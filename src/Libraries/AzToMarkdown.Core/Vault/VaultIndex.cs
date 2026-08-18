using System.Text.Json;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Models;

namespace AzToMarkdown.Core.Vault;

/// <summary>
/// In-memory index over a schema-v1 vault, built once (lazily) by <see cref="VaultQueryClient"/>.
/// Provides the lookups the KQL handlers need: by id, by type, children-by-parent, and the raw
/// properties JSON text (for KQL <c>tostring(properties) contains</c> semantics).
/// All secondary indexes are built once in the constructor — lookups allocate nothing.
/// </summary>
public sealed class VaultIndex
{
    private readonly Dictionary<string, TenantNode> _byId;
    private readonly Dictionary<string, List<TenantNode>> _byType;          // sorted by ResourceId
    private readonly Dictionary<string, List<TenantNode>> _childrenByParent; // parent id → direct children, sorted
    private readonly Dictionary<string, string> _rawPropsText = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TenantNode> Nodes { get; }
    public Dictionary<string, string> SubscriptionNames { get; }

    private VaultIndex(List<TenantNode> nodes, Dictionary<string, string> subscriptionNames)
    {
        Nodes             = nodes;
        SubscriptionNames = subscriptionNames;

        _byId             = new Dictionary<string, TenantNode>(StringComparer.OrdinalIgnoreCase);
        _byType           = new Dictionary<string, List<TenantNode>>(StringComparer.OrdinalIgnoreCase);
        _childrenByParent = new Dictionary<string, List<TenantNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            _byId.TryAdd(node.ResourceId, node);

            if (!_byType.TryGetValue(node.Type, out var typeList))
                _byType[node.Type] = typeList = [];
            typeList.Add(node);

            // Child resources are indexed by their COLLECTION path ({parentId}/{segment} — the
            // id minus the trailing name), matching the ARM child-collection URL shape.
            var collectionPath = ArmId.StripSegments(node.ResourceId, 1);
            if (collectionPath.Length > 0)
            {
                if (!_childrenByParent.TryGetValue(collectionPath, out var childList))
                    _childrenByParent[collectionPath] = childList = [];
                childList.Add(node);
            }
        }

        foreach (var list in _byType.Values)
            list.Sort(static (a, b) => string.Compare(a.ResourceId, b.ResourceId, StringComparison.OrdinalIgnoreCase));
        foreach (var list in _childrenByParent.Values)
            list.Sort(static (a, b) => string.Compare(a.ResourceId, b.ResourceId, StringComparison.OrdinalIgnoreCase));
    }

    public static VaultIndex Load(string vaultRoot, IProgressReporter? reporter = null)
    {
        var result = new VaultReader(reporter).ReadAll(vaultRoot);
        return new VaultIndex(result.Nodes, result.SubscriptionNames);
    }

    public TenantNode? ById(string resourceId) =>
        _byId.TryGetValue(resourceId, out var n) ? n : null;

    /// <summary>Nodes of one lowercased ARM type, pre-sorted by resource id.</summary>
    public IReadOnlyList<TenantNode> ByType(string lowerType) =>
        _byType.TryGetValue(lowerType, out var list) ? list : [];

    /// <summary>Nodes of any of the given lowercased types, ordered by type then name (KQL order-by shape).</summary>
    public IEnumerable<TenantNode> ByTypes(IEnumerable<string> lowerTypes) =>
        lowerTypes.SelectMany(ByType)
                  .OrderBy(n => n.Type, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Direct children of <paramref name="parentId"/> under the given child segment (e.g. "apis").</summary>
    public IEnumerable<TenantNode> DirectChildren(string parentId, string childSegment)
    {
        if (!_childrenByParent.TryGetValue($"{parentId.TrimEnd('/')}/{childSegment}", out var children))
            return [];
        return children;
    }

    /// <summary>The raw JSON text of a node's properties bag — mirrors KQL <c>tostring(properties)</c>.</summary>
    public string RawPropertiesText(TenantNode node)
    {
        if (_rawPropsText.TryGetValue(node.ResourceId, out var cached)) return cached;
        var text = node.Properties.ValueKind == JsonValueKind.Undefined ? "" : node.Properties.GetRawText();
        _rawPropsText[node.ResourceId] = text;
        return text;
    }
}
