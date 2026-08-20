using System.Text.Json;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Models;

namespace AzToMarkdown.Core.Azure;

/// <summary>
/// Fetches Azure resources, resource groups, and role assignments across the tenant
/// (or a single subscription) using three bulk ARG KQL queries with automatic paging,
/// plus one <c>az acr repository list</c> call per Container Registry found.
/// </summary>
public sealed class TenantEnumerator
{
    private readonly IArgQueryClient   _client;
    private readonly IProgressReporter _reporter;

    public TenantEnumerator(IArgQueryClient client, IProgressReporter? reporter = null)
    {
        _client   = client;
        _reporter = reporter ?? NullProgressReporter.Instance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches all tenant resources and returns them as a flat list of
    /// <see cref="TenantNode"/> objects, ready for <c>RelationshipExtractor</c>.
    /// Also returns a subscription-name map fetched from the az context.
    /// </summary>
    public async Task<(List<TenantNode> Nodes, Dictionary<string, string> SubscriptionNames)> FetchAllAsync()
    {
        _reporter.Report("AzToMarkdown: fetching tenant topology…");

        // Resources + resource groups + role assignments + subscription names all in parallel.
        var tAll   = FetchBatchAsync("all resources",    QueryAll());
        var tRgs   = FetchBatchAsync("resource groups",  QueryResourceGroups());
        var tRoles = FetchBatchAsync("role assignments", QueryRoleAssignments());
        var tSub   = _client.FetchSubscriptionNamesAsync();

        await Task.WhenAll(tAll, tRgs, tRoles, tSub);

        var raw = new List<JsonElement>(tAll.Result.Count + tRgs.Result.Count + tRoles.Result.Count);
        raw.AddRange(tAll.Result);
        raw.AddRange(tRgs.Result);
        raw.AddRange(tRoles.Result);
        _reporter.Report($"ARG fetch complete — {tAll.Result.Count} resources + {tRgs.Result.Count} resource groups + {tRoles.Result.Count} role assignments found.");

        // Materialise into TenantNodes, deduplicating by resource ID.
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<TenantNode>(raw.Count);
        foreach (var elem in raw)
        {
            var node = ElementToNode(elem);
            if (node is null) continue;
            if (!seen.Add(node.ResourceId)) continue;
            nodes.Add(node);
        }

        // ACR repository expansion — one az call per registry, cached.
        await ExpandAcrRepositoriesAsync(nodes);

        _reporter.Report($"Tenant enumeration complete — {nodes.Count} nodes total.", ProgressLevel.Success);
        return (nodes, tSub.Result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ARG KQL query
    // ─────────────────────────────────────────────────────────────────────────

    // kind, sku, and identity are TOP-LEVEL ARG columns (siblings of properties) — omitting them loses
    // data needed by templates, summaries, and lossless managed-identity round trips.
    private const string ProjectTail =
        "| project id, name, type, subscriptionId, resourceGroup, location, properties, tags, kind, sku, identity";

    /// <summary>Single query that fetches every resource in the tenant.</summary>
    private static string QueryAll() =>
        $"Resources {ProjectTail}";

    /// <summary>
    /// Fetches resource-group container nodes from the <c>ResourceContainers</c> table — these
    /// never appear in the plain <c>Resources</c> table above. ARG represents a resource group's
    /// <c>type</c> as <c>microsoft.resources/subscriptions/resourcegroups</c>; it's rewritten here
    /// to the ARM-canonical <c>Microsoft.Resources/resourceGroups</c> so it matches the casing
    /// <see cref="ElementToNode"/> lowercases to (and what the vault template file-naming
    /// convention and <see cref="AzToMarkdown.Core.Rendering.VaultTemplateEngine.NormaliseType"/>
    /// both expect). A resource group also isn't itself "in" a resource group, so
    /// <c>ResourceContainers</c> doesn't
    /// populate a meaningful <c>resourceGroup</c> column for these rows — set it to the group's
    /// own name so <see cref="ElementToNode"/>'s required-field check passes and vault path
    /// building (which expects a non-empty resource group) works unchanged.
    /// </summary>
    private static string QueryResourceGroups() =>
        $"ResourceContainers | where type == 'microsoft.resources/subscriptions/resourcegroups' " +
        $"| extend type = 'Microsoft.Resources/resourceGroups', resourceGroup = name {ProjectTail}";

    /// <summary>
    /// Fetches all role assignments from the AuthorizationResources table.
    /// Uses a minimal projection since tags are not present on role assignments.
    /// </summary>
    private static string QueryRoleAssignments() =>
        "AuthorizationResources | where type == 'microsoft.authorization/roleassignments' | project id, name, type, subscriptionId, resourceGroup, location, properties";

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<JsonElement>> FetchBatchAsync(string label, string kql)
    {
        _reporter.Report($"  [{label}]…");
        // Collapse multi-line KQL to a single line so the az CLI honours
        // the --first/--skip paging flags correctly on all platforms.
        var singleLine = System.Text.RegularExpressions.Regex
            .Replace(kql, @"\s+", " ").Trim();
        return await _client.RunQueryAsync(singleLine);
    }

    private static TenantNode? ElementToNode(JsonElement elem)
    {
        if (!elem.TryGetProperty("id",             out var idEl))   return null;
        if (!elem.TryGetProperty("name",           out var nameEl)) return null;
        if (!elem.TryGetProperty("type",           out var typeEl)) return null;
        if (!elem.TryGetProperty("subscriptionId", out var subEl))  return null;
        if (!elem.TryGetProperty("resourceGroup",  out var rgEl))   return null;

        // Some resource types use non-string id/name (e.g. integer names on certain ARG rows).
        // Fall back to GetRawText() so we never throw on an unexpected JsonValueKind.
        var id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : idEl.GetRawText();
        if (string.IsNullOrEmpty(id)) return null;

        elem.TryGetProperty("location",   out var locEl);
        elem.TryGetProperty("properties", out var propsEl);
        elem.TryGetProperty("tags",       out var tagsEl);
        elem.TryGetProperty("kind",       out var kindEl);
        elem.TryGetProperty("sku",        out var skuEl);
        elem.TryGetProperty("identity",   out var identityEl);

        static string Str(JsonElement e) =>
            e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();

        // Parse tags into a sorted dictionary.
        var tags = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tagsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in tagsEl.EnumerateObject())
                tags[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
        }

        return new TenantNode
        {
            ResourceId     = id,
            Name           = Str(nameEl),
            Type           = Str(typeEl).ToLowerInvariant(),
            SubscriptionId = Str(subEl),
            ResourceGroup  = Str(rgEl),
            Location       = locEl.ValueKind != JsonValueKind.Undefined ? Str(locEl) : "",
            Properties     = propsEl.ValueKind != JsonValueKind.Undefined
                                ? propsEl.Clone()
                                : default,
            Kind           = kindEl.ValueKind == JsonValueKind.String ? kindEl.GetString() ?? "" : "",
            Sku            = skuEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                                ? skuEl.Clone()
                                : default,
            Identity       = identityEl.ValueKind == JsonValueKind.Object
                                ? identityEl.Clone()
                                : default,
            Tags           = tags,
        };
    }

    /// <summary>
    /// For every ACR registry node found, calls <c>az acr repository list</c>
    /// (once per registry) and adds synthetic TenantNode entries for each repo.
    /// Non-fatal — logs a warning on failure.
    /// </summary>
    private async Task ExpandAcrRepositoriesAsync(List<TenantNode> nodes)
    {
        var registries = nodes
            .Where(n => n.Type == "microsoft.containerregistry/registries")
            .ToList();

        if (registries.Count == 0) return;

        _reporter.Report($"  [ACR repositories] listing repositories for {registries.Count} registr(y/ies)…");

        foreach (var reg in registries)
        {
            try
            {
                var repos = await _client.ListAcrRepositoriesAsync(reg.Name, reg.SubscriptionId);
                foreach (var repo in repos)
                {
                    // Synthetic resource ID — not a real ARM ID but stable and unique.
                    var syntheticId = $"{reg.ResourceId}/repositories/{repo}";
                    nodes.Add(new TenantNode
                    {
                        ResourceId     = syntheticId,
                        Name           = repo,
                        Type           = "microsoft.containerregistry/registries/repositories",
                        SubscriptionId = reg.SubscriptionId,
                        ResourceGroup  = reg.ResourceGroup,
                        Location       = reg.Location,
                    });
                }
                _reporter.Report($"    {reg.Name}: {repos.Count} repo(s).");
            }
            catch (Exception ex)
            {
                _reporter.Report($"    [Warn] Could not list repos for ACR '{reg.Name}': {ex.Message}", ProgressLevel.Warn);
            }
        }
    }

}
