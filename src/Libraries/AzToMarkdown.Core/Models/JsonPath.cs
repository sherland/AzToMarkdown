using System.Text.Json;

namespace AzToMarkdown.Core.Models;

/// <summary>
/// The single shared implementation of case-insensitive <see cref="JsonElement"/> path
/// navigation (exact-name match first, then an OrdinalIgnoreCase scan — ARG property casing
/// is not reliable). Every consumer — vault writer, template engine, offline query client —
/// must navigate through here so they can never disagree about the same property.
/// </summary>
public static class JsonPath
{
    /// <summary>Walks <paramref name="path"/> through nested objects; null when any step is missing.</summary>
    public static JsonElement? Navigate(JsonElement element, params string[] path)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return null;
        var cur = element;
        foreach (var key in path)
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            if (!cur.TryGetProperty(key, out var next))
            {
                var found = false;
                foreach (var prop in cur.EnumerateObject())
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    { next = prop.Value; found = true; break; }
                if (!found) return null;
            }
            cur = next;
        }
        return cur;
    }

    /// <summary>The string at the path, or null when missing or not a string.</summary>
    public static string? GetString(JsonElement element, params string[] path) =>
        Navigate(element, path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    /// <summary>The boolean at the path, or null when missing or not a boolean.</summary>
    public static bool? GetBool(JsonElement element, params string[] path) =>
        Navigate(element, path) switch
        {
            { ValueKind: JsonValueKind.True }  => true,
            { ValueKind: JsonValueKind.False } => false,
            _                                  => null,
        };

    /// <summary>Enumerates the array at the path; empty when missing or not an array.</summary>
    public static IEnumerable<JsonElement> GetArray(JsonElement element, params string[] path) =>
        Navigate(element, path) is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray()
            : [];

    /// <summary>
    /// KQL <c>tostring()</c> semantics: string value verbatim, raw JSON text for other kinds,
    /// "" when missing or null.
    /// </summary>
    public static string GetKqlString(JsonElement element, params string[] path) =>
        Navigate(element, path) switch
        {
            null => "",
            { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null } => "",
            { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
            { } other => other.GetRawText(),
        };
}
