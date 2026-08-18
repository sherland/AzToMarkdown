using System.Text.Json;

namespace AzResourceDetails.Templating;

public sealed record JsonLeaf(string OriginalPath, string ScribanPath, JsonElement Value);

// Case-insensitive JsonElement path navigation, and a flattener that walks a whole document down
// to its scalar leaves — shared by the timestamp/boolean matchers and the field-recipe resolver so
// none of them re-implement JSON-tree walking their own way.
public static class JsonTree
{
    public static JsonElement? Navigate(JsonElement element, params string[] path)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var cur = element;
        foreach (var key in path)
        {
            if (cur.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!cur.TryGetProperty(key, out var next))
            {
                var found = false;
                foreach (var prop in cur.EnumerateObject())
                {
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        next = prop.Value;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return null;
                }
            }
            cur = next;
        }
        return cur;
    }

    public static string? GetString(JsonElement element, params string[] path) =>
        Navigate(element, path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    // Walks every scalar leaf in the document. OriginalPath preserves the ARM JSON's own casing
    // (needed for camelCase-aware label/property-name similarity scoring); ScribanPath is the same
    // path lowercased (the convention Obsidian/Scriban-style templates use for property access).
    public static IEnumerable<JsonLeaf> Flatten(JsonElement elem, string path = "")
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in elem.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? prop.Name : $"{path}.{prop.Name}";
                    foreach (var leaf in Flatten(prop.Value, childPath))
                    {
                        yield return leaf;
                    }
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in elem.EnumerateArray())
                {
                    foreach (var leaf in Flatten(item, $"{path}[{i}]"))
                    {
                        yield return leaf;
                    }
                    i++;
                }
                break;
            default:
                yield return new JsonLeaf(path, path.ToLowerInvariant(), elem);
                break;
        }
    }
}
