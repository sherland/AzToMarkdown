using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AzToMarkdown.Core.Vault;

/// <summary>
/// Lossless JSON ⇄ YAML conversion for vault front-matter.
///
/// Emit rules (see docs/ARCHITECTURE.md §7):
///   • strings  → always double-quoted scalars (kills the "norway problem", numeric-looking
///                strings, multi-line/special characters with one rule)
///   • numbers  → the exact JSON raw text as a plain scalar (preserves 1 vs 1.0 vs 1e5)
///   • booleans/null → plain scalars
///   • empty object/array → flow-style {} / []
///   • key order → JSON document order
///
/// Read rules: quoted scalar ⇒ string; plain scalar ⇒ null/bool/number (fallback string for
/// hand-edited files). Typing is therefore unambiguous for everything this converter emitted.
/// </summary>
public static class YamlJsonConverter
{
    /// <summary>YAML-spec maximum for simple keys; prevents long keys becoming "? key" complex keys.</summary>
    private const int MaxSimpleKeyLength = 1024;

    // ─────────────────────────────────────────────────────────────────────────
    // JSON → YAML
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Converts a JSON element to a YAML node tree following the lossless emit rules.</summary>
    public static YamlNode ToYaml(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonObjectToYaml(element),
        JsonValueKind.Array  => JsonArrayToYaml(element),
        JsonValueKind.String => QuotedScalar(element.GetString() ?? ""),
        JsonValueKind.Number => PlainScalar(element.GetRawText()),
        JsonValueKind.True   => PlainScalar("true"),
        JsonValueKind.False  => PlainScalar("false"),
        _                    => PlainScalar("null"), // Null + Undefined
    };

    /// <summary>Always-double-quoted string scalar (values and dynamic keys).</summary>
    public static YamlScalarNode QuotedScalar(string value) =>
        new(value) { Style = ScalarStyle.DoubleQuoted };

    /// <summary>Plain scalar — reserved for numbers, booleans, null, and fixed literal keys we control.</summary>
    public static YamlScalarNode PlainScalar(string value) =>
        new(value) { Style = ScalarStyle.Plain };

    private static YamlMappingNode JsonObjectToYaml(JsonElement obj)
    {
        var map = new YamlMappingNode();
        if (!obj.EnumerateObject().Any())
        {
            map.Style = YamlDotNet.Core.Events.MappingStyle.Flow; // {}
            return map;
        }

        // Duplicate keys are legal JSON but not representable in a YAML mapping;
        // disambiguate with a "~N" suffix (documented lossy corner).
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            var key = prop.Name;
            if (seen.TryGetValue(key, out var count))
            {
                seen[key] = count + 1;
                key = $"{key}~{count + 1}";
            }
            else
            {
                seen[key] = 1;
            }
            map.Add(QuotedScalar(key), ToYaml(prop.Value));
        }
        return map;
    }

    private static YamlSequenceNode JsonArrayToYaml(JsonElement arr)
    {
        var seq = new YamlSequenceNode();
        if (arr.GetArrayLength() == 0)
        {
            seq.Style = YamlDotNet.Core.Events.SequenceStyle.Flow; // []
            return seq;
        }
        foreach (var item in arr.EnumerateArray())
            seq.Add(ToYaml(item));
        return seq;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // YAML → JSON
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a YAML node back to JSON. Quoted scalars become strings verbatim; plain scalars
    /// are typed as null/bool/number, with a string fallback for values this converter would not
    /// have emitted (tolerates hand-edited files).
    /// </summary>
    public static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map   => YamlMapToJson(map),
        YamlSequenceNode seq  => YamlSeqToJson(seq),
        YamlScalarNode scalar => YamlScalarToJson(scalar),
        _ => throw new NotSupportedException($"Unsupported YAML node type: {node.GetType().Name}"),
    };

    private static JsonObject YamlMapToJson(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map.Children)
        {
            var keyText = ((YamlScalarNode)key).Value ?? "";
            obj[keyText] = ToJson(value);
        }
        return obj;
    }

    private static JsonArray YamlSeqToJson(YamlSequenceNode seq)
    {
        var arr = new JsonArray();
        foreach (var item in seq.Children)
            arr.Add(ToJson(item));
        return arr;
    }

    private static JsonNode? YamlScalarToJson(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";

        // Any quoting/block style ⇒ string, verbatim.
        if (scalar.Style is ScalarStyle.DoubleQuoted or ScalarStyle.SingleQuoted
                         or ScalarStyle.Literal or ScalarStyle.Folded)
            return JsonValue.Create(text);

        // Plain scalar ⇒ null / bool / number (per emit rules).
        if (text is "null" or "~" or "") return null;
        if (text == "true")  return JsonValue.Create(true);
        if (text == "false") return JsonValue.Create(false);

        if (LooksLikeJsonNumber(text))
        {
            try { return JsonNode.Parse(text); }
            catch (JsonException) { /* fall through to string */ }
        }

        // Tolerance for hand-edited plain strings.
        return JsonValue.Create(text);
    }

    private static bool LooksLikeJsonNumber(string text)
    {
        if (text.Length == 0) return false;
        var c = text[0];
        return c == '-' || (c >= '0' && c <= '9');
    }

    /// <summary>
    /// Converts a YAML node straight to a <see cref="JsonElement"/> (null for a YAML null).
    /// Serializes into a pooled UTF-8 buffer — no intermediate JSON string, no undisposed
    /// <see cref="JsonDocument"/>. All YAML→JsonElement conversion must go through here so
    /// resource properties and role-assignment properties round-trip under identical rules.
    /// </summary>
    public static JsonElement? ToJsonElement(YamlNode node)
    {
        var json = ToJson(node);
        return json is null ? null : JsonSerializer.SerializeToElement(json);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Emission
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits a complete front-matter document: <c>---\n…\n---\n</c> with LF endings and no
    /// line folding (long ARM ids stay on one line).
    /// </summary>
    public static string EmitDocument(YamlMappingNode root)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb) { NewLine = "\n" })
        {
            var emitter = new Emitter(writer, new EmitterSettings(
                bestIndent: 2,
                bestWidth: int.MaxValue,
                isCanonical: false,
                maxSimpleKeyLength: MaxSimpleKeyLength,
                skipAnchorName: false,
                indentSequences: true));

            new YamlStream(new YamlDocument(root)).Save(emitter, assignAnchors: false);
        }

        // Normalize whatever markers YamlStream.Save chose to emit, then wrap with our own.
        var body = sb.ToString().Replace("\r\n", "\n").Trim('\n');
        if (body.StartsWith("---\n", StringComparison.Ordinal)) body = body[4..];
        if (body.EndsWith("\n...", StringComparison.Ordinal))   body = body[..^4];
        body = body.Trim('\n');
        return $"---\n{body}\n---\n";
    }

    /// <summary>
    /// Parses a front-matter YAML block (the text between the <c>---</c> markers) into its root
    /// mapping node. Returns null when the text contains no YAML document.
    /// </summary>
    public static YamlMappingNode? ParseDocument(string yamlText)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yamlText);
        stream.Load(reader);
        return stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Deep equality (value parity for tests and validation)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structural deep-equality of two JSON trees. Numbers are compared by raw text so that
    /// 1 vs 1.0 vs 1e0 are treated as DIFFERENT (byte-faithfulness, not numeric equality).
    /// On mismatch, <paramref name="firstDifferencePath"/> holds a JSON-pointer-ish path.
    /// </summary>
    public static bool JsonDeepEquals(JsonElement a, JsonElement b, out string? firstDifferencePath)
    {
        firstDifferencePath = FindFirstDifference(a, b, "$");
        return firstDifferencePath is null;
    }

    private static string? FindFirstDifference(JsonElement a, JsonElement b, string path)
    {
        // Treat Undefined as Null (TenantNode.Properties may be default for synthetic nodes).
        var aKind = a.ValueKind == JsonValueKind.Undefined ? JsonValueKind.Null : a.ValueKind;
        var bKind = b.ValueKind == JsonValueKind.Undefined ? JsonValueKind.Null : b.ValueKind;

        if (aKind != bKind)
            return $"{path} (kind: {aKind} vs {bKind})";

        switch (aKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().ToList();
                var bProps = b.EnumerateObject().ToList();
                if (aProps.Count != bProps.Count)
                    return $"{path} (property count: {aProps.Count} vs {bProps.Count})";
                // Order-insensitive on names (order preservation is asserted separately by golden tests).
                var bByName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in bProps) bByName[p.Name] = p.Value;
                foreach (var p in aProps)
                {
                    if (!bByName.TryGetValue(p.Name, out var bVal))
                        return $"{path}.{p.Name} (missing on right side)";
                    var diff = FindFirstDifference(p.Value, bVal, $"{path}.{p.Name}");
                    if (diff is not null) return diff;
                }
                return null;

            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength())
                    return $"{path} (array length: {a.GetArrayLength()} vs {b.GetArrayLength()})";
                int i = 0;
                var bItems = b.EnumerateArray().ToList();
                foreach (var aItem in a.EnumerateArray())
                {
                    var diff = FindFirstDifference(aItem, bItems[i], $"{path}[{i}]");
                    if (diff is not null) return diff;
                    i++;
                }
                return null;

            case JsonValueKind.String:
                return a.GetString() == b.GetString() ? null : $"{path} (string: \"{a.GetString()}\" vs \"{b.GetString()}\")";

            case JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText() ? null : $"{path} (number: {a.GetRawText()} vs {b.GetRawText()})";

            default: // True/False/Null — kinds already matched
                return null;
        }
    }
}
