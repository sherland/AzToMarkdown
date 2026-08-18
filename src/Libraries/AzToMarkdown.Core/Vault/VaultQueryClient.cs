using System.Text.Json;
using System.Text.RegularExpressions;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Models;

namespace AzToMarkdown.Core.Vault;

/// <summary>
/// Offline <see cref="IArgQueryClient"/> backed by a schema-v1 vault folder instead of Azure.
///
/// <para>
/// KQL strategy: every ARG query in this codebase is a constant template with interpolated
/// parameters (see docs/metadata-reference.md). This client matches the
/// whitespace-normalized query text against an ordered list of pattern handlers and evaluates
/// the equivalent lookup over the in-memory <see cref="VaultIndex"/>. Unmatched queries return
/// an empty result with a warning — the same graceful degradation callers already handle for
/// live ARG misses. This is NOT a general KQL engine.
/// </para>
///
/// <para>Not supported offline: <see cref="RunAksCommandAsync"/> (kubectl requires Azure).</para>
/// </summary>
public sealed class VaultQueryClient : IArgQueryClient
{
    private readonly string            _vaultRoot;
    private readonly IProgressReporter _reporter;
    private readonly Lazy<VaultIndex>  _index;

    public VaultQueryClient(string vaultRoot, IProgressReporter? reporter = null)
    {
        _vaultRoot = vaultRoot;
        _reporter  = reporter ?? NullProgressReporter.Instance;
        _index     = new Lazy<VaultIndex>(() =>
        {
            _reporter.Report($"[vault] Loading vault from {_vaultRoot}…");
            var idx = VaultIndex.Load(_vaultRoot, _reporter);
            _reporter.Report($"[vault] {idx.Nodes.Count} node(s) loaded.", ProgressLevel.Success);
            return idx;
        });
    }

    private VaultIndex Index => _index.Value;

    // ─────────────────────────────────────────────────────────────────────────
    // IArgQueryClient — KQL
    // ─────────────────────────────────────────────────────────────────────────

    public Task<List<JsonElement>> RunQueryAsync(string kql)
    {
        var normalized = Normalize(kql);

        foreach (var (pattern, evaluate) in _handlers)
        {
            var match = pattern.Match(normalized);
            if (match.Success)
                return Task.FromResult(evaluate(match, Index));
        }

        _reporter.Report($"[vault] Unsupported KQL query (returning empty): {Truncate(normalized, 160)}", ProgressLevel.Warn);
        return Task.FromResult(new List<JsonElement>());
    }

    /// <summary>Collapses whitespace and tightens parentheses so patterns can be written compactly.</summary>
    internal static string Normalize(string kql) =>
        Regex.Replace(Regex.Replace(kql, @"\s+", " ").Trim(), @"\( | \)", m => m.Value.Trim());

    // ─────────────────────────────────────────────────────────────────────────
    // IArgQueryClient — ARM GET / batch / AKS / ACR / subscriptions
    // ─────────────────────────────────────────────────────────────────────────

    public Task<JsonElement> GetResourceByIdAsync(string resourceId) =>
        GetResourceByIdAsync(resourceId, useRestPath: false);

    public Task<JsonElement> GetResourceByIdAsync(string resourceId, bool useRestPath)
    {
        var path = StripToArmPath(resourceId);

        var node = Index.ById(path);
        if (node is not null)
            return Task.FromResult(NodeToArmJson(node));

        // Child-collection URL: {parentId}/{segment} → {"value":[…direct children…]}
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash > 0)
        {
            var parentId = path[..lastSlash];
            var segment  = path[(lastSlash + 1)..];
            if (Index.ById(parentId) is not null)
                return Task.FromResult(ChildCollectionJson(Index.DirectChildren(parentId, segment)));
        }

        throw new InvalidOperationException($"[vault] Resource not found in vault: {resourceId}");
    }

    public async Task<Dictionary<string, JsonElement>> BatchArmGetAsync(IReadOnlyList<string> urls)
    {
        var results = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            try { results[url] = await GetResourceByIdAsync(url, useRestPath: true); }
            catch (InvalidOperationException) { /* absent key = failed fetch, same as live batch */ }
        }
        return results;
    }

    public Task<JsonElement> RunAksCommandAsync(string resourceGroup, string clusterName, string command)
    {
        _reporter.Report($"[vault] AKS command invoke ('{command}') requires Azure — not supported in offline vault mode.", ProgressLevel.Warn);
        return Task.FromResult(JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["exitCode"] = -1, ["logs"] = "" }));
    }

    public Task<Dictionary<string, string>> FetchSubscriptionNamesAsync() =>
        Task.FromResult(new Dictionary<string, string>(Index.SubscriptionNames));

    public Task<List<string>> ListAcrRepositoriesAsync(string registryName, string subscriptionId)
    {
        var registry = Index.ByType("microsoft.containerregistry/registries")
            .FirstOrDefault(n =>
                n.Name.Equals(registryName, StringComparison.OrdinalIgnoreCase) &&
                n.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase));
        if (registry is null)
            return Task.FromResult(new List<string>());

        var repositoryPrefix = registry.ResourceId.TrimEnd('/') + "/repositories/";
        var repositories = Index.Nodes
            .Where(n => n.Type == "microsoft.containerregistry/registries/repositories"
                     && n.ResourceId.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(repositories);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // KQL handlers (ordered; first match wins)
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly List<(Regex Pattern, Func<Match, VaultIndex, List<JsonElement>> Evaluate)> _handlers = BuildHandlers();

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static List<(Regex, Func<Match, VaultIndex, List<JsonElement>>)> BuildHandlers() =>
    [
        // ── TenantEnumerator ────────────────────────────────────────────────
        // T1: resources
        (Rx(@"^Resources \| project id, name, type, subscriptionId, resourceGroup, location, properties, tags(?:, kind, sku, identity)?$"),
         (_, idx) => idx.Nodes
            .Where(n => n.Type is not "microsoft.authorization/roleassignments"
                                  and not "microsoft.resources/resourcegroups"
                                  and not "microsoft.containerregistry/registries/repositories")
            .OrderBy(n => n.ResourceId, StringComparer.OrdinalIgnoreCase)
            .Select(n => Row(
                ("id", n.ResourceId), ("name", n.Name), ("type", n.Type),
                ("subscriptionId", n.SubscriptionId), ("resourceGroup", n.ResourceGroup),
                ("location", n.Location), ("properties", PropsOrNull(n)), ("tags", n.Tags),
                ("kind", n.Kind.Length > 0 ? n.Kind : null),
                ("sku", n.Sku.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : (object)n.Sku),
                ("identity", n.Identity.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : (object)n.Identity)))
            .ToList()),

        // T2: resource groups
        (Rx(@"^ResourceContainers \| where type == 'microsoft\.resources/subscriptions/resourcegroups' \| extend type = 'Microsoft\.Resources/resourceGroups', resourceGroup = name \| project id, name, type, subscriptionId, resourceGroup, location, properties, tags, kind, sku, identity$"),
         (_, idx) => idx.ByType("microsoft.resources/resourcegroups")
            .Select(n => Row(
                ("id", n.ResourceId), ("name", n.Name), ("type", n.Type),
                ("subscriptionId", n.SubscriptionId), ("resourceGroup", n.ResourceGroup),
                ("location", n.Location), ("properties", PropsOrNull(n)), ("tags", n.Tags),
                ("kind", n.Kind.Length > 0 ? n.Kind : null),
                ("sku", n.Sku.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : (object)n.Sku),
                ("identity", n.Identity.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : (object)n.Identity)))
            .ToList()),

        // T3: role assignments
        (Rx(@"^AuthorizationResources \| where type == 'microsoft\.authorization/roleassignments' \| project id, name, type, subscriptionId, resourceGroup, location, properties$"),
         (_, idx) => idx.ByType("microsoft.authorization/roleassignments")
            .Select(n => Row(
                ("id", n.ResourceId), ("name", n.Name), ("type", n.Type),
                ("subscriptionId", n.SubscriptionId), ("resourceGroup", n.ResourceGroup),
                ("location", n.Location), ("properties", PropsOrNull(n))))
            .ToList()),

        // ── ResourceDiscovery ───────────────────────────────────────────────
        // R1: public IP by exact ipAddress → id, name, type, ipConfigId
        (Rx(@"^Resources \| where type == 'microsoft\.network/publicipaddresses' \| where tostring\(properties\.ipAddress\) == '(?<ip>[^']+)' \| project id, name, type, ipConfigId = tostring\(properties\.ipConfiguration\.id\)$"),
         (m, idx) => idx.ByType("microsoft.network/publicipaddresses")
            .Where(n => PropStr(n, "ipAddress") == m.Groups["ip"].Value)
            .Select(n => Row(("id", n.ResourceId), ("name", n.Name), ("type", n.Type),
                             ("ipConfigId", PropStr(n, "ipConfiguration", "id"))))
            .ToList()),

        // R14: public IPs by id in~ (…) → id, fqdn, ip
        (Rx(@"^Resources \| where type == 'microsoft\.network/publicipaddresses' \| where id in~ \((?<ids>[^)]+)\) \| project id, fqdn = tostring\(properties\.dnsSettings\.fqdn\), ip = tostring\(properties\.ipAddress\)$"),
         (m, idx) =>
         {
            var ids = ParseQuotedList(m.Groups["ids"].Value);
            return idx.ByType("microsoft.network/publicipaddresses")
                .Where(n => ids.Contains(n.ResourceId))
                .Select(n => Row(("id", n.ResourceId),
                                 ("fqdn", PropStr(n, "dnsSettings", "fqdn")),
                                 ("ip", PropStr(n, "ipAddress"))))
                .ToList();
         }),

        // R18: all public IPs → id (lower), ip, fqdn
        (Rx(@"^Resources \| where type == 'microsoft\.network/publicipaddresses' \| project id = tolower\(id\), ip = tostring\(properties\.ipAddress\), fqdn = tostring\(properties\.dnsSettings\.fqdn\)$"),
         (_, idx) => idx.ByType("microsoft.network/publicipaddresses")
            .Select(n => Row(("id", n.ResourceId.ToLowerInvariant()),
                             ("ip", PropStr(n, "ipAddress")),
                             ("fqdn", PropStr(n, "dnsSettings", "fqdn"))))
            .ToList()),

        // R2/R5: tolower(id) in (…) → id, name, type, properties
        (Rx(@"^Resources \| where tolower\(id\) in \((?<ids>[^)]+)\) \| project id, name, type, properties$"),
         (m, idx) => ParseQuotedList(m.Groups["ids"].Value)
            .Select(idx.ById)
            .Where(n => n is not null)
            .OrderBy(n => n!.ResourceId, StringComparer.OrdinalIgnoreCase)
            .Select(n => Row(("id", n!.ResourceId), ("name", n.Name), ("type", n.Type), ("properties", PropsOrNull(n))))
            .ToList()),

        // R19a: AKS cluster by id → name, resourceGroup, subscriptionId, nodeRg
        (Rx(@"^Resources \| where id =~ '(?<id>[^']+)' \| project name, resourceGroup, subscriptionId, nodeRg = tostring\(properties\.nodeResourceGroup\)$"),
         (m, idx) => idx.ById(m.Groups["id"].Value) is { } n
            ? [Row(("name", n.Name), ("resourceGroup", n.ResourceGroup),
                   ("subscriptionId", n.SubscriptionId),
                   ("nodeRg", PropStr(n, "nodeResourceGroup")))]
            : []),

        // R8: id =~ '…' → id, name, type
        (Rx(@"^Resources \| where id =~ '(?<id>[^']+)' \| project id, name, type$"),
         (m, idx) => idx.ById(m.Groups["id"].Value) is { } n ? [IdNameTypeRow(n)] : []),

        // R15: gateway list — type in (…) → id, name, type, properties | order by type, name
        (Rx(@"^Resources \| where type in \((?<types>[^)]+)\) \| project id, name, type, properties \| order by type asc, name asc$"),
         (m, idx) => idx.ByTypes(ParseQuotedList(m.Groups["types"].Value))
            .Select(n => Row(("id", n.ResourceId), ("name", n.Name), ("type", n.Type), ("properties", PropsOrNull(n))))
            .ToList()),

        // R3/R6: type in (…) + tostring(properties) contains '…' → id, name, type, properties
        (Rx(@"^Resources \| where type in \((?<types>[^)]+)\) \| where tostring\(properties\) contains '(?<needle>[^']*)' \| project id, name, type, properties$"),
         (m, idx) => idx.ByTypes(ParseQuotedList(m.Groups["types"].Value))
            .Where(n => ContainsCi(idx.RawPropertiesText(n), m.Groups["needle"].Value))
            .Select(n => Row(("id", n.ResourceId), ("name", n.Name), ("type", n.Type), ("properties", PropsOrNull(n))))
            .ToList()),

        // R13: broad contains fallback — … | project id, name, type | take 1
        (Rx(@"^Resources \| where type in \((?<types>[^)]+)\) \| where tostring\(properties\) contains '(?<needle>[^']*)' \| project id, name, type \| take 1$"),
         (m, idx) => idx.ByTypes(ParseQuotedList(m.Groups["types"].Value))
            .Where(n => ContainsCi(idx.RawPropertiesText(n), m.Groups["needle"].Value))
            .Take(1)
            .Select(IdNameTypeRow)
            .ToList()),

        // R4: AFD endpoint hostname → parent profile id
        (Rx(@"^Resources \| where type == 'microsoft\.cdn/profiles/afdendpoints' \| where tostring\(properties\.hostName\) =~ '(?<h>[^']*)' or tostring\(properties\.customDomains\) contains '(?<h2>[^']*)' \| project profileId = .+$"),
         (m, idx) => idx.ByType("microsoft.cdn/profiles/afdendpoints")
            .Where(n => EqCi(PropStr(n, "hostName"), m.Groups["h"].Value)
                     || ContainsCi(PropRawText(n, "customDomains"), m.Groups["h2"].Value))
            .Select(n => Row(("profileId", StripSegments(n.ResourceId, 2).ToLowerInvariant())))
            .ToList()),

        // R10: combined FQDN match over sites/staticsites/apim → id, name, type
        (Rx(@"^Resources \| where type in \('microsoft\.web/sites', 'microsoft\.web/staticsites', 'microsoft\.apimanagement/service'\) \| where tostring\(properties\.defaultHostName\) =~ '(?<f>[^']*)' or .+ \| project id, name, type$"),
         (m, idx) =>
         {
            var f = m.Groups["f"].Value;
            return idx.ByTypes(["microsoft.web/sites", "microsoft.web/staticsites", "microsoft.apimanagement/service"])
                .Where(n => EqCi(PropStr(n, "defaultHostName"), f)
                         || ContainsCi(PropRawText(n, "hostNames"), f)
                         || EqCi(PropStr(n, "defaultHostname"), f)
                         || ContainsCi(PropRawText(n, "customDomains"), f)
                         || ContainsCi(PropStr(n, "gatewayUrl"), f)
                         || ContainsCi(PropRawText(n, "hostnameConfigurations"), f))
                .Select(IdNameTypeRow)
                .ToList();
         }),

        // R20: preload — defaultHostName in~ (…) → id, name, type, hn
        (Rx(@"^Resources \| where type in \('microsoft\.web/sites', 'microsoft\.web/staticsites', 'microsoft\.apimanagement/service'\) \| where tostring\(properties\.defaultHostName\) in~ \((?<list>[^)]+)\) or tostring\(properties\.defaultHostname\) in~ \([^)]+\) \| project id, name, type, hn = .+$"),
         (m, idx) =>
         {
            var wanted = ParseQuotedList(m.Groups["list"].Value);
            return idx.ByTypes(["microsoft.web/sites", "microsoft.web/staticsites", "microsoft.apimanagement/service"])
                .Select(n => (Node: n, Hn: FirstNonEmpty(PropStr(n, "defaultHostName"), PropStr(n, "defaultHostname"))))
                .Where(x => wanted.Contains(x.Hn))
                .Select(x => Row(("id", x.Node.ResourceId), ("name", x.Node.Name), ("type", x.Node.Type), ("hn", x.Hn)))
                .ToList();
         }),

        // R11/R12/R16: single-type name probe → id, name, type
        (Rx(@"^Resources \| where type == '(?<type>[^']+)' \| where name =~ '(?<name>[^']*)' \| project id, name, type$"),
         (m, idx) => idx.ByType(m.Groups["type"].Value.ToLowerInvariant())
            .Where(n => EqCi(n.Name, m.Groups["name"].Value))
            .Select(IdNameTypeRow)
            .ToList()),

        // R9: AKS by nodeResourceGroup → id, name, type
        (Rx(@"^Resources \| where type == 'microsoft\.containerservice/managedclusters' \| where tolower\(tostring\(properties\.nodeResourceGroup\)\) == '(?<rg>[^']*)' \| project id, name, type$"),
         (m, idx) => idx.ByType("microsoft.containerservice/managedclusters")
            .Where(n => PropStr(n, "nodeResourceGroup").ToLowerInvariant() == m.Groups["rg"].Value)
            .Select(IdNameTypeRow)
            .ToList()),

        // R7: LB frontend / NIC ipConfig private-IP union → id, name, type, rg, vmId
        (Rx(@"^Resources \| where type == 'microsoft\.network/loadbalancers' \| mv-expand fe = properties\.frontendIPConfigurations \| where tostring\(fe\.properties\.privateIPAddress\) == '(?<ip>[^']+)' \| project id, name, type, rg = resourceGroup, vmId = '' \| union \(Resources \| where type == 'microsoft\.network/networkinterfaces' \| mv-expand ipConfig = properties\.ipConfigurations \| where tostring\(ipConfig\.properties\.privateIPAddress\) == '(?<ip2>[^']+)' \| project id, name, type, rg = '', vmId = tostring\(properties\.virtualMachine\.id\)\)$"),
         (m, idx) =>
         {
            var ip   = m.Groups["ip"].Value;
            var rows = new List<JsonElement>();
            foreach (var lb in idx.ByType("microsoft.network/loadbalancers"))
                foreach (var fe in PropArray(lb, "frontendIPConfigurations"))
                    if (GetStr(fe, "properties", "privateIPAddress") == ip)
                        rows.Add(Row(("id", lb.ResourceId), ("name", lb.Name), ("type", lb.Type),
                                     ("rg", lb.ResourceGroup), ("vmId", "")));
            foreach (var nic in idx.ByType("microsoft.network/networkinterfaces"))
                foreach (var cfg in PropArray(nic, "ipConfigurations"))
                    if (GetStr(cfg, "properties", "privateIPAddress") == m.Groups["ip2"].Value)
                        rows.Add(Row(("id", nic.ResourceId), ("name", nic.Name), ("type", nic.Type),
                                     ("rg", ""), ("vmId", PropStr(nic, "virtualMachine", "id"))));
            return rows;
         }),

        // R19b: LB frontend private IPs in an RG → distinct ip
        (Rx(@"^Resources \| where type == 'microsoft\.network/loadbalancers' \| where resourceGroup =~ '(?<rg>[^']*)' \| mv-expand fe = properties\.frontendIPConfigurations \| extend ip = tostring\(fe\.properties\.privateIPAddress\) \| where isnotempty\(ip\) \| project ip \| distinct ip$"),
         (m, idx) => idx.ByType("microsoft.network/loadbalancers")
            .Where(n => EqCi(n.ResourceGroup, m.Groups["rg"].Value))
            .SelectMany(n => PropArray(n, "frontendIPConfigurations"))
            .Select(fe => GetStr(fe, "properties", "privateIPAddress"))
            .Where(ip => ip.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ip => Row(("ip", ip)))
            .ToList()),

        // R21: APIM apis — apimId in (…) → apimId, name, displayName, serviceUrl, path
        (Rx(@"^Resources \| where type == 'microsoft\.apimanagement/service/apis' \| extend apimId = .+ \| where apimId in \((?<ids>[^)]+)\) \| project apimId, name, displayName = .+$"),
         (m, idx) =>
         {
            var ids = ParseQuotedList(m.Groups["ids"].Value);
            return idx.ByType("microsoft.apimanagement/service/apis")
                .Select(n => (Node: n, ApimId: StripSegments(n.ResourceId, 2).ToLowerInvariant()))
                .Where(x => ids.Contains(x.ApimId))
                .Select(x => Row(("apimId", x.ApimId), ("name", x.Node.Name),
                                 ("displayName", PropStr(x.Node, "displayName")),
                                 ("serviceUrl", PropStr(x.Node, "serviceUrl")),
                                 ("path", PropStr(x.Node, "path"))))
                .ToList();
         }),

        // R22: AFD child union — profileId in (…) → resourceType, id, name, profileId, parentId, hostName, patternsToMatch, originGroupId
        (Rx(@"^Resources \| where type in \('microsoft\.cdn/profiles/origingroups', 'microsoft\.cdn/profiles/origingroups/origins', 'microsoft\.cdn/profiles/afdendpoints', 'microsoft\.cdn/profiles/afdendpoints/routes'\).+\| where profileId in \((?<ids>[^)]+)\).+\| project resourceType = type, id = tolower\(id\), name, profileId, parentId, hostName = .+$"),
         (m, idx) =>
         {
            var ids       = ParseQuotedList(m.Groups["ids"].Value);
            var topLevel  = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "microsoft.cdn/profiles/origingroups", "microsoft.cdn/profiles/afdendpoints" };
            var rows = new List<JsonElement>();
            foreach (var n in idx.ByTypes([
                "microsoft.cdn/profiles/origingroups", "microsoft.cdn/profiles/origingroups/origins",
                "microsoft.cdn/profiles/afdendpoints", "microsoft.cdn/profiles/afdendpoints/routes"]))
            {
                var isTop     = topLevel.Contains(n.Type);
                var profileId = StripSegments(n.ResourceId, isTop ? 2 : 4).ToLowerInvariant();
                if (!ids.Contains(profileId)) continue;
                var parentId  = isTop ? "" : StripSegments(n.ResourceId, 2).ToLowerInvariant();
                rows.Add(Row(
                    ("resourceType", n.Type), ("id", n.ResourceId.ToLowerInvariant()), ("name", n.Name),
                    ("profileId", profileId), ("parentId", parentId),
                    ("hostName", PropStr(n, "hostName")),
                    ("patternsToMatch", PropElOrNull(n, "patternsToMatch")),
                    ("originGroupId", PropStr(n, "originGroup", "id"))));
            }
            return rows;
         }),

        // R17: DNS record union (CNAME + A, public + private) — matched on its distinctive prefix
        (Rx(@"^Resources \| where type in \('microsoft\.network/dnszones/cname', 'microsoft\.network/privatednszones/cname'\) and name != '@' \| extend .+ \| union \(.+\)$"),
         (_, idx) =>
         {
            var rows = new List<JsonElement>();
            foreach (var n in idx.ByTypes(["microsoft.network/dnszones/cname", "microsoft.network/privatednszones/cname"]))
            {
                if (n.Name == "@") continue;
                var cname = FirstNonEmpty(PropStr(n, "CNAMERecord", "cname"), PropStr(n, "cnameRecord", "cname"));
                if (cname.Length == 0) continue;
                rows.Add(DnsRow(n, "CNAME", cname: cname, ip: ""));
            }
            foreach (var n in idx.ByTypes(["microsoft.network/dnszones/a", "microsoft.network/privatednszones/a"]))
            {
                if (n.Name == "@") continue;
                // KQL coalesce(properties.ARecords, properties.aRecords) — exact-case, first non-null wins
                // (a case-insensitive lookup would enumerate the same array twice).
                JsonElement recordsEl = default;
                if (n.Properties.ValueKind == JsonValueKind.Object
                    && !n.Properties.TryGetProperty("ARecords", out recordsEl))
                    n.Properties.TryGetProperty("aRecords", out recordsEl);
                if (recordsEl.ValueKind != JsonValueKind.Array) continue;
                foreach (var rec in recordsEl.EnumerateArray())
                {
                    var ip = GetStr(rec, "ipv4Address");
                    if (ip.Length == 0) continue;
                    rows.Add(DnsRow(n, "A", cname: "", ip: ip));
                }
            }
            return rows;

            static JsonElement DnsRow(TenantNode n, string recordType, string cname, string ip) => Row(
                ("zoneId", StripSegments(n.ResourceId, 2)),
                ("zoneName", ZoneNameOf(n.ResourceId)),
                ("recordName", n.Name),
                ("recordType", recordType),
                ("cname", cname),
                ("ip", ip),
                ("isPrivate", n.Type.StartsWith("microsoft.network/privatednszones", StringComparison.OrdinalIgnoreCase)));
         }),

        // R23: Service Fabric extension on a VMSS
        (Rx(@"^resources \| where type =~ 'microsoft\.compute/virtualmachinescalesets' \| where id =~ '(?<id>[^']+)' \| mv-expand extension = properties\.virtualMachineProfile\.extensionProfile\.extensions \| where extension\.properties\.type =~ 'ServiceFabricNode' \| project clusterEndpoint = .+$"),
         (m, idx) =>
         {
            var rows = new List<JsonElement>();
            var vmss = idx.ById(m.Groups["id"].Value);
            if (vmss is null) return rows;
            foreach (var ext in PropArray(vmss, "virtualMachineProfile", "extensionProfile", "extensions"))
            {
                if (!EqCi(GetStr(ext, "properties", "type"), "ServiceFabricNode")) continue;
                rows.Add(Row(
                    ("clusterEndpoint", GetStr(ext, "properties", "settings", "clusterEndpoint")),
                    ("nodeTypeRef",     GetStr(ext, "properties", "settings", "nodeTypeRef")),
                    ("durabilityLevel", GetStr(ext, "properties", "settings", "durabilityLevel"))));
            }
            return rows;
         }),

        // R24: Service Fabric cluster by clusterEndpoint
        (Rx(@"^resources \| where type =~ 'microsoft\.servicefabric/clusters' \| where tostring\(properties\.clusterEndpoint\) =~ '(?<ep>[^']*)' \| project id, name, resourceGroup, clusterState = .+$"),
         (m, idx) => idx.ByType("microsoft.servicefabric/clusters")
            .Where(n => EqCi(PropStr(n, "clusterEndpoint"), m.Groups["ep"].Value))
            .Select(n => Row(("id", n.ResourceId), ("name", n.Name), ("resourceGroup", n.ResourceGroup),
                             ("clusterState", PropStr(n, "clusterState")),
                             ("reliabilityLevel", PropStr(n, "reliabilityLevel")),
                             ("managementEndpoint", PropStr(n, "managementEndpoint"))))
            .ToList()),

        // R25: SF load balancer whose backend NIC ids reference the VMSS
        (Rx(@"^resources \| where type =~ 'microsoft\.network/loadbalancers' \| where resourceGroup =~ '(?<rg>[^']*)' \| mv-expand bePool = properties\.backendAddressPools \| mv-expand beNic = bePool\.properties\.backendIPConfigurations \| where tolower\(tostring\(beNic\.id\)\) contains tolower\('(?<needle>[^']*)'\) \| project id, name, frontendIp = .+$"),
         (m, idx) =>
         {
            var rows = new List<JsonElement>();
            foreach (var lb in idx.ByType("microsoft.network/loadbalancers"))
            {
                if (!EqCi(lb.ResourceGroup, m.Groups["rg"].Value)) continue;
                var matches = PropArray(lb, "backendAddressPools")
                    .SelectMany(p => GetArray(p, "properties", "backendIPConfigurations"))
                    .Any(nic => ContainsCi(GetStr(nic, "id"), m.Groups["needle"].Value));
                if (!matches) continue;
                var frontendIp = PropArray(lb, "frontendIPConfigurations")
                    .Select(fe => GetStr(fe, "properties", "privateIPAddress"))
                    .FirstOrDefault() ?? "";
                rows.Add(Row(("id", lb.ResourceId), ("name", lb.Name), ("frontendIp", frontendIp)));
            }
            return rows;
         }),

        // R26: SF LB rules
        (Rx(@"^resources \| where type =~ 'microsoft\.network/loadbalancers' \| where id =~ '(?<id>[^']+)' \| mv-expand rule = properties\.loadBalancingRules \| project ruleName = .+$"),
         (m, idx) =>
         {
            var rows = new List<JsonElement>();
            var lb = idx.ById(m.Groups["id"].Value);
            if (lb is null) return rows;
            foreach (var rule in PropArray(lb, "loadBalancingRules"))
            {
                rows.Add(Row(
                    ("ruleName", GetStr(rule, "name")),
                    ("protocol", GetStr(rule, "properties", "protocol")),
                    ("frontPort", GetInt(rule, "properties", "frontendPort")),
                    ("backPort", GetInt(rule, "properties", "backendPort"))));
            }
            return rows;
         }),
    ];

    // ─────────────────────────────────────────────────────────────────────────
    // Row / value helpers (KQL semantics: tostring → "" for missing, =~ / contains case-insensitive)
    // ─────────────────────────────────────────────────────────────────────────

    private static JsonElement Row(params (string Key, object? Value)[] columns)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in columns) dict[key] = value;
        return JsonSerializer.SerializeToElement(dict);
    }

    private static JsonElement IdNameTypeRow(TenantNode n) =>
        Row(("id", n.ResourceId), ("name", n.Name), ("type", n.Type));

    private static object? PropsOrNull(TenantNode n) =>
        n.Properties.ValueKind == JsonValueKind.Undefined ? null : n.Properties;

    private static object? PropElOrNull(TenantNode n, params string[] path)
    {
        var el = JsonPath.Navigate(n.Properties, path);
        return el is { ValueKind: not JsonValueKind.Undefined } value ? (object)value : null;
    }

    // Node-level conveniences — one-line delegations so KQL semantics live in exactly one
    // place (JsonPath) for the vault writer, template engine, and this client alike.

    /// <summary>KQL tostring(properties.a.b): string value, raw JSON for non-strings, "" when missing/null.</summary>
    private static string PropStr(TenantNode n, params string[] path) =>
        JsonPath.GetKqlString(n.Properties, path);

    /// <summary>Raw JSON text of a property subtree ("" when missing) — for KQL contains over serialized values.</summary>
    private static string PropRawText(TenantNode n, params string[] path) =>
        JsonPath.Navigate(n.Properties, path) is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } v
            ? v.GetRawText() : "";

    private static IEnumerable<JsonElement> PropArray(TenantNode n, params string[] path) =>
        JsonPath.GetArray(n.Properties, path);

    private static string GetStr(JsonElement el, params string[] path) =>
        JsonPath.GetKqlString(el, path);

    private static int GetInt(JsonElement el, params string[] path) =>
        JsonPath.Navigate(el, path) is { ValueKind: JsonValueKind.Number } num && num.TryGetInt32(out var i) ? i : 0;

    private static IEnumerable<JsonElement> GetArray(JsonElement el, params string[] path) =>
        JsonPath.GetArray(el, path);

    private static bool EqCi(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCi(string text, string needle) =>
        needle.Length > 0 && text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => v.Length > 0) ?? "";

    /// <summary>Set of 'quoted', 'lists' from a KQL in(…) clause → case-insensitive hash set.</summary>
    private static HashSet<string> ParseQuotedList(string list)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(list, @"'([^']*)'"))
            set.Add(m.Groups[1].Value);
        return set;
    }

    private static string StripSegments(string armId, int count) => ArmId.StripSegments(armId, count);

    private static string ZoneNameOf(string recordId) => ArmId.ZoneName(recordId);

    /// <summary>Reduces an ARM URL ("https://management.azure.com/{id}?api-version=…") to the bare id path.</summary>
    private static string StripToArmPath(string resourceIdOrUrl)
    {
        var path = resourceIdOrUrl;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(path, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath;
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return path.TrimEnd('/');
    }

    private static JsonElement NodeToArmJson(TenantNode node) => Row(
        ("id", node.ResourceId),
        ("name", node.Name),
        ("type", Rendering.VaultTemplateEngine.NormaliseType(node.Type)),
        ("location", node.Location),
        ("properties", PropsOrNull(node)),
        ("tags", node.Tags));

    /// <summary>
    /// Builds the ARM child-collection payload <c>{"value":[…]}</c> in a single
    /// Utf8JsonWriter pass — each child's properties bag is written exactly once.
    /// </summary>
    private static JsonElement ChildCollectionJson(IEnumerable<TenantNode> children)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("value");
            foreach (var child in children)
            {
                writer.WriteStartObject();
                writer.WriteString("id", child.ResourceId);
                writer.WriteString("name", child.Name);
                writer.WriteString("type", Rendering.VaultTemplateEngine.NormaliseType(child.Type));
                writer.WriteString("location", child.Location);
                writer.WritePropertyName("properties");
                if (child.Properties.ValueKind == JsonValueKind.Undefined)
                    writer.WriteNullValue();
                else
                    child.Properties.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
