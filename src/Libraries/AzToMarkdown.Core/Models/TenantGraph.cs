using System.Text.Json;

namespace AzToMarkdown.Core.Models;

/// <summary>
/// A single Azure resource node in the tenant-wide topology graph.
/// </summary>
public sealed class TenantNode
{
    public string ResourceId     { get; init; } = "";
    public string Name           { get; init; } = "";
    /// <summary>Lowercased ARM resource type, e.g. "microsoft.network/applicationgateways".</summary>
    public string Type           { get; init; } = "";
    public string SubscriptionId { get; init; } = "";
    public string ResourceGroup  { get; init; } = "";
    public string Location       { get; init; } = "";
    /// <summary>Cloned JsonElement containing the full resource properties bag.</summary>
    public JsonElement Properties { get; init; }
    /// <summary>
    /// Top-level ARG <c>kind</c> column (e.g. "app,linux" for web sites, "StorageV2").
    /// NOT part of the properties bag — ARG projects it as a sibling column.
    /// </summary>
    public string Kind { get; init; } = "";
    /// <summary>
    /// Top-level ARG <c>sku</c> column ({name, tier, …}). NOT part of the properties bag for
    /// most types (storage, load balancers, registries, plans, CDN profiles) — some types
    /// additionally nest a sku inside properties; consumers should check this first.
    /// </summary>
    public JsonElement Sku { get; init; }
    /// <summary>
    /// Complete top-level ARM <c>identity</c> object. Kept separate from properties for lossless
    /// vault round-tripping and shared template bindings.
    /// </summary>
    public JsonElement Identity { get; init; }
    /// <summary>Top-level managed-identity type, when present.</summary>
    public string? IdentityType => JsonPath.GetString(Identity, "type");
    /// <summary>Resource tags, sorted by key for deterministic output.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ── convenience helpers ───────────────────────────────────────────────────

    /// <summary>Short ARM type suffix after the last '/', e.g. "applicationgateways".</summary>
    public string TypeSuffix =>
        Type.Contains('/') ? Type[(Type.LastIndexOf('/') + 1)..] : Type;

    /// <summary>Top-level provider namespace, e.g. "microsoft.network".</summary>
    public string Provider =>
        Type.Contains('/') ? Type[..Type.IndexOf('/')] : Type;
}

/// <summary>
/// A directed relationship edge between two <see cref="TenantNode"/> resource IDs.
/// Edges are stored normalised as outbound (From → To).
/// </summary>
public sealed class TenantEdge
{
    public string FromId { get; init; } = "";
    public string ToId   { get; init; } = "";
    /// <summary>Human-readable description of the relationship, e.g. "backend pool", "subnet".</summary>
    public string Label  { get; init; } = "";
}

/// <summary>
/// Tenant-wide directed graph of Azure resources and their relationships.
/// Maintains bidirectional indices so both inbound and outbound lookups are O(1).
/// </summary>
public sealed class TenantGraph
{
    private readonly Dictionary<string, TenantNode>        _nodes    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TenantEdge>>  _outbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TenantEdge>>  _inbound  = new(StringComparer.OrdinalIgnoreCase);

    // ── write ─────────────────────────────────────────────────────────────────

    /// <summary>Adds a node; silently ignores duplicates (first registration wins).</summary>
    public void AddNode(TenantNode node)
    {
        _nodes.TryAdd(node.ResourceId, node);
    }

    /// <summary>
    /// Adds a directed edge from <paramref name="fromId"/> to <paramref name="toId"/>.
    /// Duplicate edges (same From + To + Label) are silently ignored.
    /// </summary>
    public void AddEdge(string fromId, string toId, string label = "")
    {
        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId)) return;

        var edge = new TenantEdge { FromId = fromId, ToId = toId, Label = label };

        if (!_outbound.TryGetValue(fromId, out var outList))
            _outbound[fromId] = outList = [];
        if (!outList.Any(e => string.Equals(e.ToId, toId, StringComparison.OrdinalIgnoreCase)
                           && e.Label == label))
            outList.Add(edge);

        if (!_inbound.TryGetValue(toId, out var inList))
            _inbound[toId] = inList = [];
        if (!inList.Any(e => string.Equals(e.FromId, fromId, StringComparison.OrdinalIgnoreCase)
                          && e.Label == label))
            inList.Add(edge);
    }

    // ── read ──────────────────────────────────────────────────────────────────

    /// <summary>All nodes in the graph, keyed by resource ID (case-insensitive).</summary>
    public IReadOnlyDictionary<string, TenantNode> Nodes => _nodes;

    /// <summary>Returns all outbound edges from the given resource ID.</summary>
    public IReadOnlyList<TenantEdge> GetOutbound(string resourceId) =>
        _outbound.TryGetValue(resourceId, out var list) ? list : [];

    /// <summary>Returns all inbound edges into the given resource ID.</summary>
    public IReadOnlyList<TenantEdge> GetInbound(string resourceId) =>
        _inbound.TryGetValue(resourceId, out var list) ? list : [];

    /// <summary>Returns true if a node with the given resource ID exists.</summary>
    public bool HasNode(string resourceId) => _nodes.ContainsKey(resourceId);

    /// <summary>Returns the node for the given resource ID, or null.</summary>
    public TenantNode? FindByResourceId(string resourceId) =>
        _nodes.TryGetValue(resourceId, out var n) ? n : null;

    /// <summary>
    /// Returns nodes whose name matches (case-insensitive).
    /// May return multiple results when the same name exists in different resource groups.
    /// </summary>
    public IEnumerable<TenantNode> FindByName(string name) =>
        _nodes.Values.Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the first node whose properties contain the given IP address
    /// in <c>properties.ipAddress</c> or <c>properties.ipConfigurations[*].properties.privateIPAddress</c>.
    /// </summary>
    public TenantNode? FindByIpAddress(string ipAddress)
    {
        foreach (var node in _nodes.Values)
        {
            if (node.Properties.ValueKind == JsonValueKind.Undefined) continue;

            // publicIPAddresses expose ipAddress directly
            if (node.Properties.TryGetProperty("ipAddress", out var ip)
                && string.Equals(ip.GetString(), ipAddress, StringComparison.OrdinalIgnoreCase))
                return node;

            // networkInterfaces expose it under ipConfigurations
            if (node.Properties.TryGetProperty("ipConfigurations", out var cfgs)
                && cfgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var cfg in cfgs.EnumerateArray())
                {
                    if (cfg.TryGetProperty("properties", out var cfgProps)
                        && cfgProps.TryGetProperty("privateIPAddress", out var pip)
                        && string.Equals(pip.GetString(), ipAddress, StringComparison.OrdinalIgnoreCase))
                        return node;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the first node whose FQDN (properties.dnsSettings.fqdn or
    /// properties.customDomainVerificationId) matches the given hostname.
    /// Used to resolve backend FQDNs to known Azure resources.
    /// </summary>
    public TenantNode? FindByFqdn(string fqdn)
    {
        if (string.IsNullOrEmpty(fqdn)) return null;

        // Strip trailing dot for DNS normalisation
        fqdn = fqdn.TrimEnd('.');

        foreach (var node in _nodes.Values)
        {
            if (node.Properties.ValueKind == JsonValueKind.Undefined) continue;

            // Public IPs have dnsSettings.fqdn
            if (node.Properties.TryGetProperty("dnsSettings", out var dns)
                && dns.TryGetProperty("fqdn", out var dnsf)
                && string.Equals(dnsf.GetString()?.TrimEnd('.'), fqdn, StringComparison.OrdinalIgnoreCase))
                return node;

            // Web Apps / Function Apps: defaultHostName
            if (node.Properties.TryGetProperty("defaultHostName", out var dh)
                && string.Equals(dh.GetString()?.TrimEnd('.'), fqdn, StringComparison.OrdinalIgnoreCase))
                return node;

            // App Service: hostNames array
            if (node.Properties.TryGetProperty("hostNames", out var hns)
                && hns.ValueKind == JsonValueKind.Array)
            {
                foreach (var hn in hns.EnumerateArray())
                {
                    if (string.Equals(hn.GetString()?.TrimEnd('.'), fqdn, StringComparison.OrdinalIgnoreCase))
                        return node;
                }
            }
        }
        return null;
    }
}
