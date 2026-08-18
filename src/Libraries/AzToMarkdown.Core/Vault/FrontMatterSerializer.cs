using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Rendering;
using YamlDotNet.RepresentationModel;

namespace AzToMarkdown.Core.Vault;

/// <summary>
/// Emits the schema-v1 YAML front-matter for a vault resource file
/// (see docs/ARCHITECTURE.md §7 for the normative schema).
///
/// The front-matter is built entirely through the YAML representation model — never string
/// concatenation — so every dynamic value is quoted/escaped correctly.
/// </summary>
public sealed partial class FrontMatterSerializer
{
    /// <summary>Bumped ONLY for breaking structural changes; additive keys are allowed within a version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Top-level keys owned by the schema; template extra keys colliding with these are dropped.</summary>
    private static readonly HashSet<string> _reservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Keep the pre-rename version key reserved so template-provided front matter cannot
        // collide with compatibility data when an existing vault is read and re-serialized.
        "schema_version", "aztomarkdown_version", "cartographer_version", "id", "name", "type",
        "resource-group", "location", "version", "resource", "azure_metadata", "relationships",
        "role_assignments", "tags", "depends_on_inbound", "depends_on_outbound",
    };

    private readonly string            _aztomarkdownVersion;
    private readonly IProgressReporter _reporter;

    /// <param name="aztomarkdownVersion">
    ///   Override for deterministic tests; defaults to the Core assembly's informational version.
    /// </param>
    public FrontMatterSerializer(string? aztomarkdownVersion = null, IProgressReporter? reporter = null)
    {
        _aztomarkdownVersion = aztomarkdownVersion ?? GetAssemblyVersion();
        _reporter            = reporter ?? NullProgressReporter.Instance;
    }

    private static string GetAssemblyVersion()
    {
        var info = typeof(FrontMatterSerializer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        // Strip source-link build metadata ("2.1.0+abc123" → "2.1.0")
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Serializes the complete schema-v1 front-matter block ("---\n…---\n").</summary>
    public string Serialize(FrontMatterContext ctx)
    {
        var root = new YamlMappingNode();
        AddEnvelope(root);
        AddFlatKeys(root, ctx.Node, ctx.Version, ctx.ExtraFlatKeys);
        AddResourceIdentity(root, ctx.Node, ctx.SubscriptionName);
        AddAzureMetadata(root, ctx.Node);

        if (ctx.Relationships.Count > 0)
            root.Add(Plain("relationships"), BuildRelationships(ctx.Relationships));

        if (ctx.RoleAssignments.Count > 0)
            root.Add(Plain("role_assignments"), BuildRoleAssignments(ctx.RoleAssignments));

        return YamlJsonConverter.EmitDocument(root);
    }

    /// <summary>
    /// Minimal front-matter for render-failure stub files: envelope + identity + lossless payload,
    /// no relationships/roles (the graph context may be unavailable in the failure path).
    /// Delegates to <see cref="Serialize"/> so stub files can never drift from the full schema.
    /// </summary>
    public string SerializeMinimal(TenantNode node) =>
        Serialize(new FrontMatterContext(node, node.SubscriptionId, [], [], Version: null, ExtraFlatKeys: []));

    /// <summary>
    /// Front-matter for the vault-root <c>_summary.md</c>: envelope, generation info, and the
    /// subscription id → display-name map (required for offline vault consumption).
    /// </summary>
    public string SerializeSummary(
        DateTimeOffset                       generated,
        int                                  totalResources,
        int                                  distinctTypes,
        IReadOnlyDictionary<string, string>  subscriptions)
    {
        var subs = new YamlMappingNode();
        if (subscriptions.Count == 0)
            subs.Style = YamlDotNet.Core.Events.MappingStyle.Flow;
        foreach (var (id, name) in subscriptions.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            subs.Add(YamlJsonConverter.QuotedScalar(id), YamlJsonConverter.QuotedScalar(name));

        var root = new YamlMappingNode();
        AddEnvelope(root);
        root.Add(Plain("generated"),       YamlJsonConverter.QuotedScalar(generated.ToString("yyyy-MM-ddTHH:mm:ssZ")));
        root.Add(Plain("total_resources"), Plain(totalResources.ToString()));
        root.Add(Plain("distinct_types"),  Plain(distinctTypes.ToString()));
        root.Add(Plain("subscriptions"),   subs);
        return YamlJsonConverter.EmitDocument(root);
    }

    /// <summary>
    /// Front-matter for the vault-root <c>_role_assignments.md</c>: role assignments whose scope
    /// has no vault file of its own (subscription/RG-scoped or unknown targets), stored losslessly.
    /// </summary>
    public string SerializeRoleAssignmentsFile(
        IReadOnlyList<(VaultRoleAssignment Assignment, string Scope)> items)
    {
        var seq = new YamlSequenceNode();
        foreach (var (ra, scope) in items)
            seq.Add(RoleAssignmentNode(ra, scope));

        var root = new YamlMappingNode();
        AddEnvelope(root);
        root.Add(Plain("role_assignments"), seq);
        return YamlJsonConverter.EmitDocument(root);
    }

    /// <summary>
    /// Front-matter for the per-type <c>_summary_{type}.md</c> files. Machine-generated like every
    /// other vault front-matter — a resource type or count must never be string-concatenated.
    /// </summary>
    public string SerializeTypeSummary(string canonicalType, int count, DateTimeOffset generated)
    {
        var root = new YamlMappingNode();
        AddEnvelope(root);
        root.Add(Plain("type"),      YamlJsonConverter.QuotedScalar(canonicalType));
        root.Add(Plain("count"),     Plain(count.ToString()));
        root.Add(Plain("generated"), YamlJsonConverter.QuotedScalar(generated.ToString("yyyy-MM-ddTHH:mm:ssZ")));
        return YamlJsonConverter.EmitDocument(root);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Section builders
    // ─────────────────────────────────────────────────────────────────────────

    private void AddEnvelope(YamlMappingNode root)
    {
        root.Add(Plain("schema_version"), Plain(SchemaVersion.ToString()));
        root.Add(Plain("aztomarkdown_version"), YamlJsonConverter.QuotedScalar(_aztomarkdownVersion));
    }

    private void AddFlatKeys(
        YamlMappingNode                              root,
        TenantNode                                   node,
        string?                                      version,
        IReadOnlyList<KeyValuePair<string, string>>  extraFlatKeys)
    {
        var canonicalType = VaultTemplateEngine.NormaliseType(node.Type);

        root.Add(Plain("id"),             SafeScalar(node.ResourceId));
        root.Add(Plain("name"),           YamlJsonConverter.QuotedScalar(node.Name));
        root.Add(Plain("type"),           SafeScalar(canonicalType));
        root.Add(Plain("resource-group"), SafeScalar(node.ResourceGroup));
        root.Add(Plain("location"),       SafeScalar(node.Location));

        if (!string.IsNullOrEmpty(version))
            root.Add(Plain("version"), YamlJsonConverter.QuotedScalar(version));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in extraFlatKeys)
        {
            if (_reservedKeys.Contains(key))
            {
                _reporter.Report($"  [Warn] Front-matter extra key '{key}' collides with a reserved key on '{node.Name}' — dropped.", ProgressLevel.Warn);
                continue;
            }
            if (!seen.Add(key))
            {
                _reporter.Report($"  [Warn] Duplicate front-matter extra key '{key}' on '{node.Name}' — dropped.", ProgressLevel.Warn);
                continue;
            }
            root.Add(SafeScalar(key), YamlJsonConverter.QuotedScalar(value));
        }
    }

    private static void AddResourceIdentity(YamlMappingNode root, TenantNode node, string subscriptionName)
    {
        var resource = new YamlMappingNode
        {
            { Plain("id"),                YamlJsonConverter.QuotedScalar(node.ResourceId) },
            { Plain("name"),              YamlJsonConverter.QuotedScalar(node.Name) },
            { Plain("type"),              YamlJsonConverter.QuotedScalar(VaultTemplateEngine.NormaliseType(node.Type)) },
            { Plain("subscription_id"),   YamlJsonConverter.QuotedScalar(node.SubscriptionId) },
            { Plain("subscription_name"), YamlJsonConverter.QuotedScalar(subscriptionName) },
            { Plain("resource_group"),    YamlJsonConverter.QuotedScalar(node.ResourceGroup) },
            { Plain("location"),          YamlJsonConverter.QuotedScalar(node.Location) },
        };
        root.Add(Plain("resource"), resource);
    }

    private static void AddAzureMetadata(YamlMappingNode root, TenantNode node)
    {
        // Undefined/absent properties round-trip as null (distinct from a real empty {} bag).
        var properties = YamlJsonConverter.ToYaml(node.Properties);

        var tags = new YamlMappingNode();
        if (node.Tags.Count == 0)
            tags.Style = YamlDotNet.Core.Events.MappingStyle.Flow;
        foreach (var (k, v) in node.Tags)
            tags.Add(YamlJsonConverter.QuotedScalar(k), YamlJsonConverter.QuotedScalar(v));

        var metadata = new YamlMappingNode
        {
            { Plain("properties"), properties },
            { Plain("tags"),       tags },
        };
        // kind/sku/identity are top-level ARG columns (siblings of properties) — persisted losslessly
        // alongside the bag, omitted when the resource type doesn't carry them.
        if (node.Kind.Length > 0)
            metadata.Add(Plain("kind"), YamlJsonConverter.QuotedScalar(node.Kind));
        if (node.Sku.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            metadata.Add(Plain("sku"), YamlJsonConverter.ToYaml(node.Sku));
        if (node.Identity.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            metadata.Add(Plain("identity"), YamlJsonConverter.ToYaml(node.Identity));
        root.Add(Plain("azure_metadata"), metadata);
    }

    private static YamlSequenceNode BuildRelationships(IReadOnlyList<VaultRelationship> relationships)
    {
        var seq = new YamlSequenceNode();
        foreach (var rel in relationships)
        {
            var map = new YamlMappingNode { { Plain("id"), YamlJsonConverter.QuotedScalar(rel.Id) } };
            if (rel.Name is { Length: > 0 })
                map.Add(Plain("name"), YamlJsonConverter.QuotedScalar(rel.Name));
            if (rel.Type is { Length: > 0 })
                map.Add(Plain("type"), YamlJsonConverter.QuotedScalar(rel.Type));
            map.Add(Plain("direction"), Plain(rel.Direction));
            map.Add(Plain("label"), YamlJsonConverter.QuotedScalar(rel.Label));
            seq.Add(map);
        }
        return seq;
    }

    private static YamlSequenceNode BuildRoleAssignments(IReadOnlyList<VaultRoleAssignment> assignments)
    {
        var seq = new YamlSequenceNode();
        foreach (var ra in assignments)
            seq.Add(RoleAssignmentNode(ra, scope: null));
        return seq;
    }

    /// <summary>
    /// The one shape of a serialized role assignment — embedded (no scope key) and orphan-file
    /// (with scope) entries must stay structurally identical apart from the scope.
    /// </summary>
    private static YamlMappingNode RoleAssignmentNode(VaultRoleAssignment ra, string? scope)
    {
        var map = new YamlMappingNode
        {
            { Plain("id"),           YamlJsonConverter.QuotedScalar(ra.Id) },
            { Plain("role"),         YamlJsonConverter.QuotedScalar(ra.Role) },
            { Plain("principal_id"), YamlJsonConverter.QuotedScalar(ra.PrincipalId) },
        };
        if (scope is not null)
            map.Add(Plain("scope"), YamlJsonConverter.QuotedScalar(scope));
        map.Add(Plain("properties"), YamlJsonConverter.ToYaml(ra.Properties));
        return map;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scalar helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static YamlScalarNode Plain(string value) => YamlJsonConverter.PlainScalar(value);

    /// <summary>
    /// Plain when unambiguously safe (keeps the Obsidian-friendly look of ids/types/locations),
    /// otherwise double-quoted. Never lets a string read back as a non-string.
    /// </summary>
    internal static YamlScalarNode SafeScalar(string value)
    {
        if (value.Length > 0
            && SafePlainRegex().IsMatch(value)
            && value is not ("true" or "false" or "null" or "~")
            && !(value[0] is '-' or >= '0' and <= '9'))
        {
            return YamlJsonConverter.PlainScalar(value);
        }
        return YamlJsonConverter.QuotedScalar(value);
    }

    [GeneratedRegex(@"^[A-Za-z_/][A-Za-z0-9/_.\-() ]*[A-Za-z0-9/_.\-()]$|^[A-Za-z_/]$")]
    private static partial Regex SafePlainRegex();
}
