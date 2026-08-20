using System.Text.Json;
using AzResourceDetails.Templating;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Models;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;

namespace AzToMarkdown.Core.Rendering;

/// <summary>
/// Renders vault Markdown files using Scriban templates loaded as embedded resources.
/// Each Azure resource type maps to a <c>.sbn</c> template file, resolved in three tiers:
/// a hand-crafted template under <c>Rendering/Templates/</c> (vault-aware — relationships,
/// wiki links, curated sections); failing that, a mechanically-generated template mirrored
/// from AzResourceDetailsDownloader under <c>Rendering/PortalTemplates/</c> (a flat
/// Portal-Essentials-style property table, no relationships); failing that, <c>_generic.sbn</c>.
///
/// Template naming: <c>microsoft.web/sites</c> → <c>microsoft_web_sites.sbn</c>
/// (dots and slashes replaced with underscores).
/// </summary>
public sealed class VaultTemplateEngine
{
    private readonly Dictionary<string, Template> _cache       = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Template> _portalCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IProgressReporter            _reporter;
    private static readonly EmbeddedLoader        _loader = new();

    public VaultTemplateEngine(IProgressReporter? reporter = null)
    {
        _reporter = reporter ?? NullProgressReporter.Instance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the vault note body for a resource node, together with the per-type flat
    /// front-matter keys the template contributed via <c>extra_fm</c>.
    /// The YAML front-matter itself is emitted by <c>FrontMatterSerializer</c>, not by templates.
    /// </summary>
    public RenderedNote Render(
        TenantNode                   node,
        IReadOnlyList<TenantEdge>    inbound,
        IReadOnlyList<TenantEdge>    outbound,
        List<RoleAssignmentInfo>     roleAssignments,
        Func<string, string>         wikiLink)
    {
        var key      = TypeToKey(node.Type);
        var template = GetTemplate(key) ?? GetPortalTemplate(key) ?? GetTemplate("_generic")!;

        var globals = BuildScriptObject(node, inbound, outbound, roleAssignments, wikiLink);

        RenderedNote details;

        try
        {
            var result = RenderTemplate(template, globals);
            if (template.HasErrors)
                _reporter.Report($"  [Warn] Template errors for '{node.Name}' ({node.Type}): {string.Join("; ", template.Messages.Select(m => m.Message))}", ProgressLevel.Warn);
            details = new RenderedNote(ExtractExtraFm(globals), result.Replace("\r\n", "\n").TrimStart('\n'));
        }
        catch (Exception ex)
        {
            _reporter.Report($"  [Warn] Template render failed for '{node.Name}' ({node.Type}): {ex.Message}", ProgressLevel.Warn);
            // Fall back to the generic details template. The shared footer is composed separately
            // below, so neither custom nor generated details templates need to include it.
            try
            {
                var generic = GetTemplate("_generic")!;
                var result  = RenderTemplate(generic, globals);
                details = new RenderedNote(ExtractExtraFm(globals), result.Replace("\r\n", "\n").TrimStart('\n'));
            }
            catch (Exception ex2)
            {
                _reporter.Report($"  [Warn] Generic template also failed for '{node.Name}': {ex2.Message}", ProgressLevel.Warn);
                // Last resort: minimal body (front-matter is added by the caller's serializer)
                details = new RenderedNote([], $"# {node.Name}\n\n*Rendering failed — see tool output for details.*\n");
            }
        }

        return AppendSharedFooter(details, globals, node);
    }

    private static string RenderTemplate(Template template, ScriptObject globals)
    {
        var context = new TemplateContext { StrictVariables = false, TemplateLoader = _loader };
        context.PushGlobal(globals);
        return template.Render(context);
    }

    private RenderedNote AppendSharedFooter(RenderedNote details, ScriptObject globals, TenantNode node)
    {
        string footer;
        try
        {
            footer = RenderTemplate(GetTemplate("_common_footer")!, globals)
                .Replace("\r\n", "\n")
                .Trim();
        }
        catch (Exception ex)
        {
            // A shared-section failure must not discard an otherwise useful resource note.
            _reporter.Report($"  [Warn] Shared footer render failed for '{node.Name}' ({node.Type}): {ex.Message}", ProgressLevel.Warn);
            footer = "";
        }

        var body = details.Body.Replace("\r\n", "\n").TrimEnd();
        if (footer.Length > 0)
            body = body.Length > 0 ? $"{body}\n\n{footer}" : footer;

        return new RenderedNote(details.ExtraFlatKeys, body + "\n");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // extra_fm flat keys
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the <c>extra_fm</c> global a template optionally assigned during rendering (e.g.
    /// <c>{{- extra_fm = "sku: \"" + model.props.sku + "\"" -}}</c>) directly off the render
    /// context — no per-template include or in-body marker required, so a template that never
    /// touches <c>extra_fm</c> at all (including one dropped in without AzToMd conventions in
    /// mind) still renders correctly, just without contributing extra front-matter keys.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ExtractExtraFm(ScriptObject globals)
    {
        if (globals["extra_fm"] is not string raw || raw.Length == 0)
            return [];

        var keys = new List<KeyValuePair<string, string>>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key   = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"');
            if (key.Length == 0 || value.Length == 0) continue;
            keys.Add(new KeyValuePair<string, string>(key, value));
        }
        return keys;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Template loading
    // ─────────────────────────────────────────────────────────────────────────

    private Template? GetTemplate(string key) => GetEmbeddedTemplate(_cache, "Templates", key);

    /// <summary>
    /// Loads a mechanically-generated fallback template mirrored from
    /// AzResourceDetailsDownloader (see <c>scripts/Sync-PortalTemplates.ps1</c>). Used only when
    /// no hand-crafted template exists for the type.
    /// </summary>
    private Template? GetPortalTemplate(string key) => GetEmbeddedTemplate(_portalCache, "PortalTemplates", key);

    private static Template? GetEmbeddedTemplate(Dictionary<string, Template> cache, string folder, string key)
    {
        if (cache.TryGetValue(key, out var cached)) return cached;

        var resourceName = $"AzToMarkdown.Core.Rendering.{folder}.{key}.sbn";
        var asm          = typeof(VaultTemplateEngine).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        var tmpl = Template.Parse(reader.ReadToEnd(), resourceName);
        cache[key] = tmpl;
        return tmpl;
    }

    /// <summary>Converts a resource type to a template file key.</summary>
    /// <example>"microsoft.web/sites" → "microsoft_web_sites"</example>
    public static string TypeToKey(string type)
        => type.Replace('.', '_').Replace('/', '_').ToLowerInvariant();

    /// <summary>
    /// Returns the template key that will actually be used to render the given resource type:
    /// a hand-crafted template's key, else a portal-fallback template's key, else
    /// <c>"_generic"</c>. Use <see cref="UsesPortalTemplate"/> to tell the first two apart.
    /// </summary>
    public string ResolveTemplateKey(string type)
    {
        var key = TypeToKey(type);
        if (GetTemplate(key) is not null) return key;
        if (GetPortalTemplate(key) is not null) return key;
        return "_generic";
    }

    /// <summary>
    /// True when the type has no hand-crafted AzToMd template and would render through the
    /// mirrored portal-fallback tier instead.
    /// </summary>
    public bool UsesPortalTemplate(string type)
    {
        var key = TypeToKey(type);
        return GetTemplate(key) is null && GetPortalTemplate(key) is not null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Script object builder
    // ─────────────────────────────────────────────────────────────────────────

    private static ScriptObject BuildScriptObject(
        TenantNode                   node,
        IReadOnlyList<TenantEdge>    inbound,
        IReadOnlyList<TenantEdge>    outbound,
        List<RoleAssignmentInfo>     roleAssignments,
        Func<string, string>         wikiLink)
    {
        // Separate role-assignment edges from dependency edges
        var realInbound  = inbound .Where(e => !e.Label.StartsWith("role:", StringComparison.OrdinalIgnoreCase)).ToList();
        var realOutbound = outbound.Where(e => !e.Label.StartsWith("role:", StringComparison.OrdinalIgnoreCase)).ToList();

        var inboundIds  = realInbound .Select(e => e.FromId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var outboundIds = realOutbound.Select(e => e.ToId  ).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

        // ── model object ─────────────────────────────────────────────────────
        var m = new ScriptObject();
        ScribanModelBuilder.PopulateSharedFields(m, CreateTemplateResource(node));

        // AzToMd owns canonical ARM casing in rendered notes; the shared runtime receives the
        // lowercased graph type for matching, then this display-facing field is refined here.
        m["type"] = NormaliseType(node.Type);

        // Tags: [{key, value}] sorted by key
        var tagsArr = new ScriptArray();
        foreach (var (k, v) in node.Tags)
        {
            var kv = new ScriptObject(); kv["key"] = k; kv["value"] = v;
            tagsArr.Add(kv);
        }
        m["tags"] = tagsArr;

        // Role assignments: [{role, principal_id}]
        var rolesArr = new ScriptArray();
        foreach (var ra in roleAssignments.OrderBy(r => r.Role))
        {
            var obj = new ScriptObject(); obj["role"] = ra.Role; obj["principal_id"] = ra.PrincipalId;
            rolesArr.Add(obj);
        }
        m["role_assignments"] = rolesArr;

        // Frontmatter dependency IDs (sorted)
        m["inbound_ids"]  = ToStringArray(inboundIds);
        m["outbound_ids"] = ToStringArray(outboundIds);

        // Full edge objects with wiki links: [{id, label, wiki}]
        m["inbound"]  = ToEdgeArray(realInbound .OrderBy(e => e.Label).ThenBy(e => e.FromId), e => e.FromId, wikiLink);
        m["outbound"] = ToEdgeArray(realOutbound.OrderBy(e => e.Label).ThenBy(e => e.ToId  ), e => e.ToId,   wikiLink);

        // Preserve the legacy model.props.kind/model.props.sku aliases used by handwritten
        // templates. Shared top-level sku_* fields remain authoritative for generated templates.
        var props = m["props"] as ScriptObject ?? new ScriptObject();
        if (node.Kind.Length > 0 && !props.ContainsKey("kind"))
            props["kind"] = node.Kind;
        if (node.Sku.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && !props.ContainsKey("sku"))
            props["sku"] = ScribanModelBuilder.JsonToScriban(node.Sku);
        m["props"] = props;

        // Retain AzToMd's additional version fallbacks while delegating all shared cases to the
        // shared runtime. This keeps front matter, summaries, and template bodies consistent.
        m["version"] = ExtractVersion(node);

        // Flattened top-level properties for display — used by _generic.sbn (and available to
        // any template) to surface the full properties bag without hand-rolling per-type field
        // extraction. Computed from the raw JsonElement (not the Scriban props object above) so
        // the object/array/scalar classification is a plain C# JsonValueKind check.
        var (propsScalars, propsComplex) = FlattenPropsForDisplay(node.Properties);
        m["props_scalars"] = propsScalars;
        m["props_complex"] = propsComplex;

        var globals = new ScriptObject();
        globals["model"] = m;
        TemplateFunctions.ImportInto(globals);

        // ── Helper functions for templates ───────────────────────────────────
        // filter_by_label(edges, label)  → ScriptArray of matching edges
        // filter_by_labels(edges, l1, l2) → ScriptArray matching either label
        // has_label(edges, label)        → bool
        globals.Import("filter_by_label", new Func<ScriptArray, string, ScriptArray>(
            (edges, label) =>
            {
                var result = new ScriptArray();
                foreach (var item in edges)
                    if (item is ScriptObject so && so["label"] as string == label)
                        result.Add(item);
                return result;
            }));

        globals.Import("filter_by_labels", new Func<ScriptArray, string, string, ScriptArray>(
            (edges, l1, l2) =>
            {
                var result = new ScriptArray();
                foreach (var item in edges)
                {
                    var lbl = (item as ScriptObject)?["label"] as string;
                    if (lbl == l1 || lbl == l2) result.Add(item);
                }
                return result;
            }));

        globals.Import("has_label", new Func<ScriptArray, string, bool>(
            (edges, label) =>
            {
                foreach (var item in edges)
                    if ((item as ScriptObject)?["label"] as string == label)
                        return true;
                return false;
            }));

        return globals;
    }

    /// <summary>
    /// Splits a resource's top-level <c>properties.*</c> bag into scalars (string/number/bool,
    /// ready to display directly) and complex entries (object/array — name + shape only, since
    /// the full nested detail already lives losslessly in front matter). Backs
    /// <c>model.props_scalars</c> / <c>model.props_complex</c> for the generic fallback template.
    /// </summary>
    private static (ScriptArray Scalars, ScriptArray Complex) FlattenPropsForDisplay(JsonElement props)
    {
        var scalars = new ScriptArray();
        var complex = new ScriptArray();
        if (props.ValueKind != JsonValueKind.Object) return (scalars, complex);

        foreach (var prop in props.EnumerateObject())
        {
            if (string.Equals(prop.Name, "kind", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(prop.Name, "sku",  StringComparison.OrdinalIgnoreCase)) continue;

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    var kv = new ScriptObject();
                    kv["key"]   = prop.Name;
                    kv["value"] = JsonToScriban(prop.Value);
                    scalars.Add(kv);
                    break;
                case JsonValueKind.Object:
                    var co = new ScriptObject();
                    co["key"]   = prop.Name;
                    co["kind"]  = "object";
                    co["count"] = prop.Value.EnumerateObject().Count();
                    complex.Add(co);
                    break;
                case JsonValueKind.Array:
                    var ca = new ScriptObject();
                    ca["key"]   = prop.Name;
                    ca["kind"]  = "array";
                    ca["count"] = prop.Value.GetArrayLength();
                    complex.Add(ca);
                    break;
                // JsonValueKind.Null / Undefined: skip.
            }
        }
        return (scalars, complex);
    }

    private static ScriptArray ToStringArray(IEnumerable<string> items)
    {
        var arr = new ScriptArray();
        foreach (var s in items) arr.Add(s);
        return arr;
    }

    private static ScriptArray ToEdgeArray(
        IEnumerable<TenantEdge>  edges,
        Func<TenantEdge, string> idSelector,
        Func<string, string>     wikiLink)
    {
        var arr = new ScriptArray();
        foreach (var e in edges)
        {
            var obj = new ScriptObject();
            obj["id"]    = idSelector(e);
            obj["label"] = e.Label;
            obj["wiki"]  = wikiLink(idSelector(e));
            arr.Add(obj);
        }
        return arr;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // JSON → Scriban conversion
    // ─────────────────────────────────────────────────────────────────────────

    public static object? JsonToScriban(JsonElement elem) =>
        ScribanModelBuilder.JsonToScriban(elem);

    // ─────────────────────────────────────────────────────────────────────────
    // Version extraction (available as model.version in all Scriban templates)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Converts a graph node into the shared template runtime's neutral resource shape.</summary>
    internal static TemplateResource CreateTemplateResource(TenantNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var sku = node.Sku.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? node.Sku
            : JsonPath.Navigate(node.Properties, "sku") ?? default;
        return new TemplateResource(
            Id: node.ResourceId,
            Name: node.Name,
            ArmType: node.Type,
            Location: node.Location,
            ResourceGroup: node.ResourceGroup,
            Kind: node.Kind.Length > 0 ? node.Kind : null,
            IdentityType: node.IdentityType,
            Properties: node.Properties,
            Sku: sku);
    }

    /// <summary>
    /// Returns the shared runtime's version with AzToMd-specific fallbacks for resource types whose
    /// useful version proxy is outside the common portal-template contract.
    /// </summary>
    public static string? ExtractVersion(TenantNode node)
    {
        var sharedVersion = SkuAndVersion.ExtractVersion(CreateTemplateResource(node));
        return node.Type switch
        {
            "microsoft.sql/servers/databases"
                => sharedVersion ?? SkuLabel(node),
            "microsoft.app/containerapps"
                => ExtractContainerAppImage(node) ?? sharedVersion,
            "microsoft.compute/galleries/images/versions"
                => node.Name.Length > 0 ? node.Name : sharedVersion,
            "microsoft.apimanagement/service"
                => SkuLabel(node) ?? sharedVersion,
            _ => sharedVersion,
        };
    }

    /// <summary>Returns the shared runtime's display-ready SKU label.</summary>
    public static string? SkuLabel(TenantNode node) =>
        SkuAndVersion.SkuLabel(CreateTemplateResource(node));

    private static string? ExtractContainerAppImage(TenantNode node)
    {
        var containers = JsonPath.Navigate(node.Properties, "template", "containers");
        if (containers is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0) return null;
        return JsonPath.GetString(arr[0], "image");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Type normalisation (canonical casing for frontmatter)
    // ─────────────────────────────────────────────────────────────────────────

    public static string NormaliseType(string type) => type switch
    {
        "microsoft.network/applicationgateways"                => "Microsoft.Network/applicationGateways",
        "microsoft.network/frontdoors"                         => "Microsoft.Network/frontDoors",
        "microsoft.cdn/profiles"                               => "Microsoft.Cdn/profiles",
        "microsoft.cdn/profiles/afdendpoints"                  => "Microsoft.Cdn/profiles/afdEndpoints",
        "microsoft.cdn/profiles/customdomains"                 => "Microsoft.Cdn/profiles/customDomains",
        "microsoft.cdn/profiles/origingroups"                  => "Microsoft.Cdn/profiles/originGroups",
        "microsoft.cdn/profiles/rulesets"                      => "Microsoft.Cdn/profiles/ruleSets",
        "microsoft.apimanagement/service"                      => "Microsoft.ApiManagement/service",
        "microsoft.network/virtualnetworks"                    => "Microsoft.Network/virtualNetworks",
        "microsoft.network/publicipaddresses"                  => "Microsoft.Network/publicIPAddresses",
        "microsoft.network/networkinterfaces"                  => "Microsoft.Network/networkInterfaces",
        "microsoft.network/loadbalancers"                      => "Microsoft.Network/loadBalancers",
        "microsoft.network/networksecuritygroups"              => "Microsoft.Network/networkSecurityGroups",
        "microsoft.network/dnszones"                           => "Microsoft.Network/dnsZones",
        "microsoft.network/privatednszones"                    => "Microsoft.Network/privateDnsZones",
        "microsoft.network/privateendpoints"                   => "Microsoft.Network/privateEndpoints",
        "microsoft.network/virtualnetworkgateways"             => "Microsoft.Network/virtualNetworkGateways",
        "microsoft.network/virtualwans"                        => "Microsoft.Network/virtualWans",
        "microsoft.compute/virtualmachines"                    => "Microsoft.Compute/virtualMachines",
        "microsoft.compute/virtualmachinescalesets"            => "Microsoft.Compute/virtualMachineScaleSets",
        "microsoft.compute/disks"                              => "Microsoft.Compute/disks",
        "microsoft.containerservice/managedclusters"           => "Microsoft.ContainerService/managedClusters",
        "microsoft.app/containerapps"                          => "Microsoft.App/containerApps",
        "microsoft.app/managedenvironments"                    => "Microsoft.App/managedEnvironments",
        "microsoft.web/sites"                                  => "Microsoft.Web/sites",
        "microsoft.web/serverfarms"                            => "Microsoft.Web/serverFarms",
        "microsoft.web/staticsites"                            => "Microsoft.Web/staticSites",
        "microsoft.containerregistry/registries"               => "Microsoft.ContainerRegistry/registries",
        "microsoft.containerregistry/registries/repositories"  => "Microsoft.ContainerRegistry/registries/repositories",
        "microsoft.storage/storageaccounts"                    => "Microsoft.Storage/storageAccounts",
        "microsoft.keyvault/vaults"                            => "Microsoft.KeyVault/vaults",
        "microsoft.servicebus/namespaces"                      => "Microsoft.ServiceBus/namespaces",
        "microsoft.sql/servers"                                => "Microsoft.Sql/servers",
        "microsoft.sql/servers/databases"                      => "Microsoft.Sql/servers/databases",
        "microsoft.documentdb/databaseaccounts"                => "Microsoft.DocumentDB/databaseAccounts",
        "microsoft.cache/redis"                                => "Microsoft.Cache/redis",
        "microsoft.eventhub/namespaces"                        => "Microsoft.EventHub/namespaces",
        "microsoft.managedidentity/userassignedidentities"     => "Microsoft.ManagedIdentity/userAssignedIdentities",
        "microsoft.authorization/roleassignments"              => "Microsoft.Authorization/roleAssignments",
        "microsoft.insights/components"                        => "Microsoft.Insights/components",
        "microsoft.operationalinsights/workspaces"             => "Microsoft.OperationalInsights/workspaces",
        "microsoft.network/applicationgatewayfirewallpolicies" => "Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies",
        "microsoft.resources/resourcegroups"                   => "Microsoft.Resources/resourceGroups",
        "microsoft.insights/actiongroups"                      => "Microsoft.Insights/actionGroups",
        "microsoft.network/networkwatchers"                    => "Microsoft.Network/networkWatchers",
        _ => type,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Embedded template loader (for {{ include 'name.sbn' }} in templates)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class EmbeddedLoader : ITemplateLoader
    {
        public string GetPath(TemplateContext ctx, SourceSpan span, string name) => name;

        public string Load(TemplateContext ctx, SourceSpan span, string name)
        {
            var resourceName = $"AzToMarkdown.Core.Rendering.Templates.{name}";
            var asm          = typeof(VaultTemplateEngine).Assembly;
            using var stream = asm.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"Embedded template not found: {resourceName}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public ValueTask<string?> LoadAsync(TemplateContext ctx, SourceSpan span, string name)
            => new(Load(ctx, span, name));
    }
}

/// <summary>Role assignment info passed to templates.</summary>
public sealed record RoleAssignmentInfo(string Role, string PrincipalId);

/// <summary>
/// Result of rendering a vault note template: the per-type flat front-matter keys the template
/// contributed via <c>extra_fm</c>, and the human-readable Markdown body.
/// </summary>
public sealed record RenderedNote(
    IReadOnlyList<KeyValuePair<string, string>> ExtraFlatKeys,
    string                                      Body);
