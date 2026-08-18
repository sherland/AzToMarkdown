using System.Text;
using System.Text.RegularExpressions;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Vault;

namespace AzToMarkdown.Core.Rendering;

/// <summary>
/// Renders a <see cref="TenantGraph"/> as an Obsidian-compatible Markdown vault:
/// one <c>.md</c> file per <see cref="TenantNode"/>, using Scriban templates.
/// Role-assignment nodes are suppressed from vault output — their data is embedded
/// in the scoped resource's own file instead.
/// </summary>
public sealed class VaultWriter
{
    private readonly VaultTemplateEngine   _engine;
    private readonly FrontMatterSerializer _serializer;
    private readonly IProgressReporter     _reporter;

    public VaultWriter(VaultTemplateEngine engine, IProgressReporter? reporter = null, FrontMatterSerializer? serializer = null)
    {
        _engine     = engine;
        _reporter   = reporter ?? NullProgressReporter.Instance;
        _serializer = serializer ?? new FrontMatterSerializer(reporter: reporter);
    }

    /// <summary>Creates a writer with the default template engine.</summary>
    public VaultWriter(IProgressReporter? reporter = null)
        : this(new VaultTemplateEngine(reporter), reporter) { }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes one <c>.md</c> file per node in <paramref name="graph"/> into
    /// <paramref name="outputRoot"/>.  Existing files are overwritten.
    /// </summary>
    /// <param name="graph">The fully-populated tenant graph.</param>
    /// <param name="subscriptionNames">
    ///   Map of subscription ID → display name; used as the top-level folder name.
    /// </param>
    /// <param name="outputRoot">Root directory of the vault.</param>
    public void WriteAll(
        TenantGraph                      graph,
        Dictionary<string, string>       subscriptionNames,
        string                           outputRoot)
    {
        _reporter.Report($"VaultWriter: writing {graph.Nodes.Count} file(s) to {outputRoot}…");

        // Pre-compute vault-relative paths for every node (needed by WikiLink builder).
        var vaultPaths = BuildVaultPaths(graph, subscriptionNames);

        // Track per-type statistics (count + template) AND the sorted node list.
        var typeStats = new Dictionary<string, (string Template, int Count)>(StringComparer.OrdinalIgnoreCase);
        var typeNodes = new Dictionary<string, List<(TenantNode Node, string VaultPath)>>(StringComparer.OrdinalIgnoreCase);

        int written = 0;
        // Enumerate in deterministic order (same order reused inside per-type summaries).
        foreach (var node in graph.Nodes.Values
                                  .OrderBy(n => n.SubscriptionId, StringComparer.OrdinalIgnoreCase)
                                  .ThenBy (n => n.ResourceGroup,  StringComparer.OrdinalIgnoreCase)
                                  .ThenBy (n => n.Name,           StringComparer.OrdinalIgnoreCase))
        {
            if (!vaultPaths.TryGetValue(node.ResourceId, out var vaultRelPath))
                continue;

            var filePath = Path.Combine(outputRoot, vaultRelPath.Replace('/', Path.DirectorySeparatorChar) + ".md");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var content = RenderNode(node, graph, vaultPaths, subscriptionNames);
            File.WriteAllText(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            written++;

            // Accumulate statistics + per-type node list.
            var canonical = VaultTemplateEngine.NormaliseType(node.Type);
            var tmplKey   = _engine.ResolveTemplateKey(node.Type);
            if (typeStats.TryGetValue(canonical, out var existing))
                typeStats[canonical] = (existing.Template, existing.Count + 1);
            else
                typeStats[canonical] = (tmplKey, 1);

            if (!typeNodes.TryGetValue(canonical, out var list))
                typeNodes[canonical] = list = [];
            list.Add((node, vaultRelPath));
        }

        _reporter.Report($"VaultWriter: {written} file(s) written.", ProgressLevel.Success);

        // Write per-type summaries first, then the main summary (which links to them).
        foreach (var (canonical, nodes) in typeNodes)
            WriteTypeSummary(outputRoot, canonical, nodes, subscriptionNames);

        WriteOrphanRoleAssignments(outputRoot, graph);
        WriteSummary(outputRoot, written, typeStats, subscriptionNames);
    }

    /// <summary>
    /// Writes <c>_role_assignments.md</c> containing role assignments whose scope has no vault
    /// file of its own (subscription/RG-scoped or unknown targets), stored losslessly.
    /// Assignments scoped to a vault resource are embedded in that resource's file instead.
    /// </summary>
    private void WriteOrphanRoleAssignments(string outputRoot, TenantGraph graph)
    {
        var orphans = new List<(VaultRoleAssignment Assignment, string Scope)>();
        foreach (var node in graph.Nodes.Values
                                  .Where(n => n.Type == "microsoft.authorization/roleassignments")
                                  .OrderBy(n => n.ResourceId, StringComparer.OrdinalIgnoreCase))
        {
            // An assignment with an outbound edge is embedded in the target resource's file.
            if (graph.GetOutbound(node.ResourceId).Count > 0) continue;

            var props     = node.Properties;
            var role      = Azure.RelationshipExtractor.GetRoleName(props);
            var principal = JsonPath.GetString(props, "principalId") ?? "";
            var scope     = JsonPath.GetString(props, "scope") ?? "";

            orphans.Add((new VaultRoleAssignment(node.ResourceId, role, principal, props), scope));
        }

        if (orphans.Count == 0) return;

        var sb = new StringBuilder();
        sb.Append(_serializer.SerializeRoleAssignmentsFile(orphans));
        sb.Append("# Role Assignments (subscription / resource-group scoped)\n\n");
        sb.Append("Role assignments whose scope has no vault file. Assignments scoped to a specific resource are embedded in that resource's own file.\n");

        File.WriteAllText(
            Path.Combine(outputRoot, "_role_assignments.md"),
            sb.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _reporter.Report($"VaultWriter: {orphans.Count} orphan role assignment(s) written to _role_assignments.md.", ProgressLevel.Success);
    }

    private void WriteSummary(
        string                                      outputRoot,
        int                                         totalWritten,
        Dictionary<string, (string Template, int Count)> typeStats,
        Dictionary<string, string>                  subscriptionNames)
    {
        var sb = new System.Text.StringBuilder();
        var now = DateTimeOffset.UtcNow;

        sb.Append(_serializer.SerializeSummary(now, totalWritten, typeStats.Count, subscriptionNames));
        sb.AppendLine();
        sb.AppendLine("# Vault Summary");
        sb.AppendLine();
        sb.AppendLine($"Generated on **{now:yyyy-MM-dd}** — **{totalWritten:N0} files** across **{typeStats.Count} resource types**.");
        sb.AppendLine();
        sb.AppendLine("## Resource Type Breakdown");
        sb.AppendLine();
        sb.AppendLine("| Resource Type | Count | Template |");
        sb.AppendLine("|---|---:|---|");

        foreach (var (type, (tmpl, count)) in typeStats
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var tmplLabel   = tmpl == "_generic" ? "`_generic` (fallback)" : $"`{tmpl}`";
            var typeFileKey = VaultTemplateEngine.TypeToKey(type);
            // Standard Markdown link (not WikiLink) so the | in [[...|...]] doesn't break the table
            sb.AppendLine($"| {MdLink(type, $"_summary_{typeFileKey}.md")} | {count:N0} | {tmplLabel} |");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Role assignments are embedded in the target resource files and not counted here.*");

        var summaryPath = Path.Combine(outputRoot, "_summary.md");
        // Normalize to LF: the serializer emits LF, StringBuilder.AppendLine emits CRLF.
        File.WriteAllText(summaryPath, sb.ToString().Replace("\r\n", "\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _reporter.Report($"VaultWriter: summary written to _summary.md ({typeStats.Count} types).", ProgressLevel.Success);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-resource-type summary files
    // ─────────────────────────────────────────────────────────────────────────

    private void WriteTypeSummary(
        string                                     outputRoot,
        string                                     canonicalType,
        List<(TenantNode Node, string VaultPath)>  entries,
        Dictionary<string, string>                 subscriptionNames)
    {
        // Detect which optional columns have at least one non-empty value.
        bool showKind    = entries.Any(e => KindOf(e.Node)                            is { Length: > 0 });
        bool showSku     = entries.Any(e => VaultTemplateEngine.SkuLabel(e.Node)      is { Length: > 0 });
        bool showVersion = entries.Any(e => VaultTemplateEngine.ExtractVersion(e.Node) is { Length: > 0 });
        bool showOs      = entries.Any(e => GetOsLabel(e.Node)                        is { Length: > 0 });

        var sb  = new StringBuilder();
        var now = DateTimeOffset.UtcNow;

        sb.Append(_serializer.SerializeTypeSummary(canonicalType, entries.Count, now));
        sb.AppendLine();
        sb.AppendLine($"# {canonicalType} ({entries.Count:N0} resources)");
        sb.AppendLine();
        sb.AppendLine("← [[_summary|Vault Summary]]");
        sb.AppendLine();

        // Table header — always: Resource, Subscription, Resource Group, Location
        var hdr = new StringBuilder("| Resource | Subscription | Resource Group | Location");
        var aln = new StringBuilder("|---|---|---|---");
        if (showKind)    { hdr.Append(" | Kind");    aln.Append("|---"); }
        if (showSku)     { hdr.Append(" | SKU");     aln.Append("|---"); }
        if (showVersion) { hdr.Append(" | Version"); aln.Append("|---"); }
        if (showOs)      { hdr.Append(" | OS");      aln.Append("|---"); }
        hdr.Append(" |");
        aln.Append("|");
        sb.AppendLine(hdr.ToString());
        sb.AppendLine(aln.ToString());

        foreach (var (node, vaultPath) in entries)
        {
            var subName = subscriptionNames.TryGetValue(node.SubscriptionId, out var sn) ? sn : node.SubscriptionId;
            var link    = MdLink(EscapeCell(node.Name), $"{vaultPath}.md");

            var row = new StringBuilder(
                $"| {link} | {EscapeCell(subName)} | {EscapeCell(node.ResourceGroup)} | {EscapeCell(node.Location)}");
            if (showKind)    row.Append($" | {EscapeCell(KindOf(node) ?? "")}");
            if (showSku)     row.Append($" | {EscapeCell(VaultTemplateEngine.SkuLabel(node) ?? "")}");
            if (showVersion) row.Append($" | {EscapeCell(VaultTemplateEngine.ExtractVersion(node) ?? "")}");
            if (showOs)      row.Append($" | {EscapeCell(GetOsLabel(node) ?? "")}");
            row.Append(" |");
            sb.AppendLine(row.ToString());
        }

        var typeFileKey = VaultTemplateEngine.TypeToKey(canonicalType);
        File.WriteAllText(
            Path.Combine(outputRoot, $"_summary_{typeFileKey}.md"),
            sb.ToString().Replace("\r\n", "\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-type summary column extraction — thin wrappers over the shared helpers
    // (JsonPath navigation; version/sku switches live in VaultTemplateEngine).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Kind: the top-level ARG column first, then a properties-nested fallback.</summary>
    private static string? KindOf(TenantNode node) =>
        node.Kind.Length > 0 ? node.Kind : JsonPath.GetString(node.Properties, "kind");

    /// <summary>Extracts OS type for compute and hosting resource types.</summary>
    private static string? GetOsLabel(TenantNode node) => node.Type switch
    {
        "microsoft.compute/virtualmachines"
            => JsonPath.GetString(node.Properties, "storageProfile", "osDisk", "osType"),
        "microsoft.compute/virtualmachinescalesets"
            => JsonPath.GetString(node.Properties, "virtualMachineProfile", "storageProfile", "osDisk", "osType"),
        "microsoft.web/sites" or "microsoft.web/serverfarms" or "microsoft.web/sites/slots"
            => JsonPath.GetBool(node.Properties, "reserved") == true ? "Linux" : "Windows",
        "microsoft.devtestlab/labs/virtualmachines"
            => JsonPath.GetString(node.Properties, "osType"),
        "microsoft.compute/galleries/images"
            => JsonPath.GetString(node.Properties, "osType"),
        _ => null,
    };

    /// <summary>Escapes pipe characters so they don't break Markdown table cells.</summary>
    private static string EscapeCell(string value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace("|", "\\|");

    /// <summary>
    /// Generates a Markdown link that works even when <paramref name="url"/> contains spaces.
    /// Uses the angle-bracket form <c>[text](&lt;url&gt;)</c> which is safe regardless of spaces.
    /// </summary>
    private static string MdLink(string text, string url) => $"[{text}](<{url}>)";

    // ─────────────────────────────────────────────────────────────────────────
    // Vault-path builder
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a dictionary of resourceId → vault-relative path (forward-slash, no leading slash,
    /// no <c>.md</c> extension) for every node.
    /// </summary>
    public static Dictionary<string, string> BuildVaultPaths(
        TenantGraph                graph,
        Dictionary<string, string> subscriptionNames)
    {
        // Pass 1 — compute naive paths for every node.
        var naive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Nodes.Values)
        {
            // Role assignments are embedded in the target resource's file; no separate vault file.
            if (node.Type == "microsoft.authorization/roleassignments") continue;

            var path = ComputeVaultPath(node, subscriptionNames);
            if (!string.IsNullOrEmpty(path))
                naive[node.ResourceId] = path;
        }

        // Pass 2 — disambiguate cross-type collisions (same name, different type, same RG).
        // Append '--{type-suffix}' to every member of a collision group.
        var paths = AppendTypeSuffixForCollisions(naive, graph);

        // Pass 3 — any remaining same-type collisions (e.g., VM extensions sharing a name across
        // multiple VMs in the same RG) get a numeric counter suffix: '--2', '--3', …
        AppendCounterForRemainingCollisions(paths);

        return paths;
    }

    private static Dictionary<string, string> AppendTypeSuffixForCollisions(
        Dictionary<string, string> naive,
        TenantGraph                graph)
    {
        var colliding = naive.Values
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (resourceId, path) in naive)
        {
            if (colliding.Contains(path))
            {
                var node = graph.FindByResourceId(resourceId)!;
                result[resourceId] = $"{path}--{Sanitize(node.TypeSuffix)}";
            }
            else
            {
                result[resourceId] = path;
            }
        }
        return result;
    }

    private static void AppendCounterForRemainingCollisions(Dictionary<string, string> paths)
    {
        // Find paths that are still duplicated after the type-suffix pass.
        var seen    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordered = paths.Keys.ToList(); // stable order for deterministic numbering

        foreach (var resourceId in ordered)
        {
            var path = paths[resourceId];
            if (!seen.TryGetValue(path, out var count))
            {
                seen[path] = 1;
            }
            else
            {
                seen[path] = count + 1;
                paths[resourceId] = $"{path}--{seen[path]}";
            }
        }
    }

    private static string ComputeVaultPath(
        TenantNode                 node,
        Dictionary<string, string> subscriptionNames)
    {
        // DNS zones → routes/{zoneName}
        if (node.Type is "microsoft.network/dnszones"
                      or "microsoft.network/privatednszones")
            return $"routes/{Sanitize(node.Name)}";

        // DNS record sets live under routes as well
        if (node.Type.StartsWith("microsoft.network/dnszones/")
         || node.Type.StartsWith("microsoft.network/privatednszones/"))
        {
            // e.g. microsoft.network/dnszones/a  →  record type = "a"
            var recordType = node.Type.Contains('/') ? node.Type[(node.Type.LastIndexOf('/') + 1)..] : "record";
            var zoneName   = ExtractZoneNameFromRecordId(node.ResourceId);
            return $"routes/{Sanitize(zoneName)}/{recordType}/{Sanitize(node.Name)}";
        }

        // ACR repositories → infrastructure/{sub}/{rg}/{acr}/repos/{repo}
        if (node.Type == "microsoft.containerregistry/registries/repositories")
        {
            var subName = ResolveSubName(node.SubscriptionId, subscriptionNames);
            // ResourceId: {registryId}/repositories/{repoName}
            var repoName     = node.Name;
            var registryId   = node.ResourceId[..node.ResourceId.LastIndexOf("/repositories/", StringComparison.OrdinalIgnoreCase)];
            // Get the registry name from the registry node if possible, otherwise extract from ID.
            var registryName = registryId.Split('/').Last();
            return $"infrastructure/{Sanitize(subName)}/{Sanitize(node.ResourceGroup)}/{Sanitize(registryName)}/repos/{Sanitize(repoName)}";
        }

        // Everything else → infrastructure/{sub}/{rg}/{name}
        {
            var subName = ResolveSubName(node.SubscriptionId, subscriptionNames);
            return $"infrastructure/{Sanitize(subName)}/{Sanitize(node.ResourceGroup)}/{Sanitize(node.Name)}";
        }
    }

    private static string ResolveSubName(string subId, Dictionary<string, string> subNames) =>
        subNames.TryGetValue(subId, out var name) && !string.IsNullOrEmpty(name)
            ? name
            : subId;

    private static string ExtractZoneNameFromRecordId(string recordId)
    {
        var zone = ArmId.ZoneName(recordId);
        return zone.Length > 0 ? zone : "unknown-zone";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Node renderer
    // ─────────────────────────────────────────────────────────────────────────

    private string RenderNode(
        TenantNode                  node,
        TenantGraph                 graph,
        Dictionary<string, string>  vaultPaths,
        Dictionary<string, string>  subscriptionNames)
    {
        try
        {
            return RenderNodeInternal(node, graph, vaultPaths, subscriptionNames);
        }
        catch (Exception ex)
        {
            _reporter.Report($"  [Warn] Skipped rendering '{node.Name}' ({node.Type}): {ex.Message}", ProgressLevel.Warn);
            // Never return null — emit a minimal (but still lossless) stub so the vault is complete
            return _serializer.SerializeMinimal(node)
                 + $"# {node.Name}\n\n*Rendering error: {ex.Message}*\n";
        }
    }

    private string RenderNodeInternal(
        TenantNode                  node,
        TenantGraph                 graph,
        Dictionary<string, string>  vaultPaths,
        Dictionary<string, string>  subscriptionNames)
    {
        var inbound  = graph.GetInbound(node.ResourceId);
        var outbound = graph.GetOutbound(node.ResourceId);

        // Collect role assignments from inbound edges (edge FROM assignment node TO this resource).
        // Full assignment properties are persisted losslessly in the front-matter.
        var vaultRoleAssignments = inbound
            .Where(e => e.Label.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
            .Select(e =>
            {
                var roleName   = e.Label[5..];
                var assignNode = graph.FindByResourceId(e.FromId);
                var principal  = assignNode?.Properties.ValueKind == System.Text.Json.JsonValueKind.Object
                    && assignNode.Properties.TryGetProperty("principalId", out var pid)
                        ? pid.GetString() ?? e.FromId
                        : e.FromId;
                return new VaultRoleAssignment(e.FromId, roleName, principal, assignNode?.Properties ?? default);
            })
            .OrderBy(r => r.Role, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roleAssignmentInfos = vaultRoleAssignments
            .Select(r => new RoleAssignmentInfo(r.Role, r.PrincipalId))
            .ToList();

        Func<string, string> wikiLink = id => BuildWikiLink(id, graph, vaultPaths);

        var note = _engine.Render(node, inbound, outbound, roleAssignmentInfos, wikiLink);

        var ctx = new FrontMatterContext(
            node,
            ResolveSubName(node.SubscriptionId, subscriptionNames),
            BuildRelationships(inbound, outbound, graph),
            vaultRoleAssignments,
            VaultTemplateEngine.ExtractVersion(node),
            note.ExtraFlatKeys);

        return _serializer.Serialize(ctx) + note.Body;
    }

    /// <summary>
    /// Normalized cross-resource references from the graph edges (role edges excluded — they are
    /// persisted under <c>role_assignments</c>). Deterministic order: direction, label, id.
    /// </summary>
    private static List<VaultRelationship> BuildRelationships(
        IReadOnlyList<TenantEdge> inbound,
        IReadOnlyList<TenantEdge> outbound,
        TenantGraph               graph)
    {
        var relationships = new List<VaultRelationship>();

        foreach (var e in inbound.Where(e => !e.Label.StartsWith("role:", StringComparison.OrdinalIgnoreCase)))
            relationships.Add(MakeRelationship(e.FromId, "inbound", e.Label, graph));
        foreach (var e in outbound.Where(e => !e.Label.StartsWith("role:", StringComparison.OrdinalIgnoreCase)))
            relationships.Add(MakeRelationship(e.ToId, "outbound", e.Label, graph));

        return relationships
            .OrderBy(r => r.Direction, StringComparer.Ordinal)
            .ThenBy(r => r.Label,      StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id,         StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static VaultRelationship MakeRelationship(string id, string direction, string label, TenantGraph graph)
    {
        var target = graph.FindByResourceId(id);
        return new VaultRelationship(
            id,
            target?.Name,
            target is null ? null : VaultTemplateEngine.NormaliseType(target.Type),
            direction,
            label);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WikiLink helper
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildWikiLink(
        string                      resourceId,
        TenantGraph                 graph,
        Dictionary<string, string>  vaultPaths)
    {
        var node = graph.FindByResourceId(resourceId);
        if (node is null) return $"`{resourceId}`";

        if (!vaultPaths.TryGetValue(resourceId, out var vaultPath))
            return $"`{node.Name}`";

        var displayName = Sanitize(node.Name);
        return $"[[{vaultPath}|{displayName}]]";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sanitisation
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Regex _illegalRe = new(
        @"[#|^:%\[\]\\*?""<>]|%%",
        RegexOptions.Compiled);

    public static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return _illegalRe.Replace(input, "").Trim();
    }
}
