using System.Text.Json;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Models;
using YamlDotNet.RepresentationModel;

namespace AzToMarkdown.Core.Vault;

/// <summary>
/// Reads a schema-v1 vault folder back into <see cref="TenantNode"/>s — the inverse of the
/// <c>VaultWriter</c>/<c>FrontMatterSerializer</c> pipeline. Role-assignment nodes are
/// reconstructed from the <c>role_assignments</c> lists embedded in resource files and from
/// the vault-root <c>_role_assignments.md</c>.
/// </summary>
public sealed class VaultReader
{
    private readonly IProgressReporter _reporter;

    public VaultReader(IProgressReporter? reporter = null)
    {
        _reporter = reporter ?? NullProgressReporter.Instance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks every <c>.md</c> file under <paramref name="vaultRoot"/> (skipping summary files)
    /// and reconstructs the tenant node list plus the subscription-name map from
    /// <c>_summary.md</c>. Files without a <c>schema_version</c> are skipped with a warning;
    /// files with a HIGHER schema version throw <see cref="NotSupportedException"/>.
    /// </summary>
    public VaultReadResult ReadAll(string vaultRoot)
    {
        if (!Directory.Exists(vaultRoot))
            throw new DirectoryNotFoundException($"Vault folder not found: {vaultRoot}");

        var nodes = new List<TenantNode>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoleAssignments(IEnumerable<VaultRoleAssignment> assignments)
        {
            foreach (var ra in assignments)
            {
                var raNode = RoleAssignmentToNode(ra);
                if (raNode is not null && seen.Add(raNode.ResourceId))
                    nodes.Add(raNode);
            }
        }

        foreach (var path in Directory.EnumerateFiles(vaultRoot, "*.md", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith("_summary", StringComparison.OrdinalIgnoreCase)) continue;

            if (fileName.Equals("_role_assignments.md", StringComparison.OrdinalIgnoreCase))
            {
                var root = LoadFrontMatterRoot(path);
                if (root is not null)
                    AddRoleAssignments(ReadRoleAssignments(root));
                continue;
            }

            ParsedVaultFile? parsed;
            try
            {
                parsed = ParseFile(path);
            }
            catch (NotSupportedException) { throw; }
            catch (Exception ex)
            {
                _reporter.Report($"  [Warn] VaultReader: failed to parse '{fileName}': {ex.Message}", ProgressLevel.Warn);
                continue;
            }

            if (parsed is null)
            {
                _reporter.Report($"  [Warn] VaultReader: '{fileName}' has no schema_version front-matter — skipped.", ProgressLevel.Warn);
                continue;
            }

            if (seen.Add(parsed.Node.ResourceId))
                nodes.Add(parsed.Node);

            // Reconstruct the role-assignment nodes embedded in this resource's front-matter.
            AddRoleAssignments(parsed.RoleAssignments);
        }

        var subscriptionNames = ReadSummarySubscriptions(vaultRoot);
        return new VaultReadResult(nodes, subscriptionNames);
    }

    /// <summary>
    /// Parses one vault resource file's front-matter. Returns null when the file carries no
    /// <c>schema_version</c>; throws <see cref="NotSupportedException"/> on a
    /// schema version newer than this reader supports.
    /// </summary>
    public static ParsedVaultFile? ParseFile(string path)
    {
        var yaml = ExtractFrontMatter(File.ReadAllText(path));
        if (yaml is null) return null;

        var root = YamlJsonConverter.ParseDocument(yaml);
        if (root is null) return null;

        var schemaVersion = GetScalar(root, "schema_version");
        if (schemaVersion is null) return null;
        if (!int.TryParse(schemaVersion, out var schema))
            throw new NotSupportedException($"Unreadable schema_version '{schemaVersion}' in {path}");
        if (schema > FrontMatterSerializer.SchemaVersion)
            throw new NotSupportedException(
                $"Vault file {path} uses schema_version {schema}; this reader supports up to {FrontMatterSerializer.SchemaVersion}. Upgrade the tooling.");

        if (root.Children.TryGetValue(new YamlScalarNode("resource"), out var resNode)
            && resNode is YamlMappingNode resource)
        {
            var node = new TenantNode
            {
                ResourceId     = GetScalar(resource, "id") ?? "",
                Name           = GetScalar(resource, "name") ?? "",
                Type           = (GetScalar(resource, "type") ?? "").ToLowerInvariant(),
                SubscriptionId = GetScalar(resource, "subscription_id") ?? "",
                ResourceGroup  = GetScalar(resource, "resource_group") ?? "",
                Location       = GetScalar(resource, "location") ?? "",
                Properties     = ReadProperties(root),
                Kind           = ReadMetadataScalar(root, "kind") ?? "",
                Sku            = ReadMetadataElement(root, "sku") ?? default,
                Identity       = ReadMetadataElement(root, "identity") ?? default,
                Tags           = ReadTags(root),
            };

            return new ParsedVaultFile(
                schema,
                GetScalar(root, "aztomarkdown_version") ?? GetScalar(root, "cartographer_version") ?? "",
                node,
                GetScalar(resource, "subscription_name") ?? "",
                ReadRelationships(root),
                ReadRoleAssignments(root),
                path);
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Front-matter extraction / section readers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Reads a file and parses its front-matter into the root YAML mapping, or null.</summary>
    private static YamlMappingNode? LoadFrontMatterRoot(string path)
    {
        var yaml = ExtractFrontMatter(File.ReadAllText(path));
        return yaml is null ? null : YamlJsonConverter.ParseDocument(yaml);
    }

    /// <summary>Returns the YAML text between the leading <c>---</c> pair, or null.</summary>
    internal static string? ExtractFrontMatter(string content)
    {
        content = content.Replace("\r\n", "\n");
        if (!content.StartsWith("---\n", StringComparison.Ordinal)) return null;
        var end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
        // The closing marker must be a full line: "\n---\n" or "\n---" at EOF.
        while (end >= 0)
        {
            var after = end + 4;
            if (after >= content.Length || content[after] == '\n')
                return content[4..end];
            end = content.IndexOf("\n---", after, StringComparison.Ordinal);
        }
        return null;
    }

    private static JsonElement ReadProperties(YamlMappingNode root)
    {
        if (!TryGet(root, "azure_metadata", out var metaNode) || metaNode is not YamlMappingNode meta)
            return default;
        if (!TryGet(meta, "properties", out var propsYaml))
            return default;

        // "properties: null" ⇒ node had undefined properties
        return YamlJsonConverter.ToJsonElement(propsYaml) ?? default;
    }

    private static string? ReadMetadataScalar(YamlMappingNode root, string key) =>
        TryGet(root, "azure_metadata", out var m) && m is YamlMappingNode meta
            ? GetScalar(meta, key)
            : null;

    private static JsonElement? ReadMetadataElement(YamlMappingNode root, string key) =>
        TryGet(root, "azure_metadata", out var m) && m is YamlMappingNode meta
            && TryGet(meta, key, out var el)
            ? YamlJsonConverter.ToJsonElement(el)
            : null;

    private static IReadOnlyDictionary<string, string> ReadTags(YamlMappingNode root)
    {
        var tags = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryGet(root, "azure_metadata", out var metaNode) && metaNode is YamlMappingNode meta
            && TryGet(meta, "tags", out var tagsNode) && tagsNode is YamlMappingNode tagsMap)
        {
            foreach (var (k, v) in tagsMap.Children)
                tags[((YamlScalarNode)k).Value ?? ""] = ((YamlScalarNode)v).Value ?? "";
        }
        return tags;
    }

    private static IReadOnlyList<VaultRelationship> ReadRelationships(YamlMappingNode root)
    {
        var list = new List<VaultRelationship>();
        if (TryGet(root, "relationships", out var relNode) && relNode is YamlSequenceNode seq)
        {
            foreach (var item in seq.Children.OfType<YamlMappingNode>())
            {
                list.Add(new VaultRelationship(
                    GetScalar(item, "id") ?? "",
                    GetScalar(item, "name"),
                    GetScalar(item, "type"),
                    GetScalar(item, "direction") ?? "",
                    GetScalar(item, "label") ?? ""));
            }
        }
        return list;
    }

    private static IReadOnlyList<VaultRoleAssignment> ReadRoleAssignments(YamlMappingNode root)
    {
        var list = new List<VaultRoleAssignment>();
        if (TryGet(root, "role_assignments", out var raNode) && raNode is YamlSequenceNode seq)
        {
            foreach (var item in seq.Children.OfType<YamlMappingNode>())
            {
                JsonElement props = default;
                if (TryGet(item, "properties", out var propsYaml))
                    props = YamlJsonConverter.ToJsonElement(propsYaml) ?? default;
                list.Add(new VaultRoleAssignment(
                    GetScalar(item, "id") ?? "",
                    GetScalar(item, "role") ?? "",
                    GetScalar(item, "principal_id") ?? "",
                    props));
            }
        }
        return list;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Role-assignment node reconstruction
    // ─────────────────────────────────────────────────────────────────────────

    private static TenantNode? RoleAssignmentToNode(VaultRoleAssignment ra)
    {
        if (string.IsNullOrEmpty(ra.Id)) return null;
        return new TenantNode
        {
            ResourceId     = ra.Id,
            Name           = ra.Id.Contains('/') ? ra.Id[(ra.Id.LastIndexOf('/') + 1)..] : ra.Id,
            Type           = "microsoft.authorization/roleassignments",
            SubscriptionId = ArmId.SegmentAfter(ra.Id, "subscriptions"),
            ResourceGroup  = ArmId.SegmentAfter(ra.Id, "resourceGroups"),
            Location       = "",
            Properties     = ra.Properties,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // _summary.md
    // ─────────────────────────────────────────────────────────────────────────

    private Dictionary<string, string> ReadSummarySubscriptions(string vaultRoot)
    {
        var result  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var summary = Path.Combine(vaultRoot, "_summary.md");
        if (!File.Exists(summary)) return result;

        try
        {
            var root = LoadFrontMatterRoot(summary);
            if (root is null) return result;

            if (TryGet(root, "subscriptions", out var subsNode) && subsNode is YamlMappingNode subs)
                foreach (var (k, v) in subs.Children)
                    result[((YamlScalarNode)k).Value ?? ""] = ((YamlScalarNode)v).Value ?? "";
        }
        catch (Exception ex)
        {
            _reporter.Report($"  [Warn] VaultReader: could not read _summary.md subscriptions: {ex.Message}", ProgressLevel.Warn);
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // YAML helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool TryGet(YamlMappingNode map, string key, out YamlNode value) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out value!);

    private static string? GetScalar(YamlMappingNode map, string key) =>
        TryGet(map, key, out var v) && v is YamlScalarNode s ? s.Value : null;
}

/// <summary>Result of reading a whole vault folder.</summary>
public sealed record VaultReadResult(
    List<TenantNode>            Nodes,
    Dictionary<string, string>  SubscriptionNames);
