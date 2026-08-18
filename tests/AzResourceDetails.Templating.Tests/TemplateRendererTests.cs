using System.Text.Json;

namespace AzResourceDetails.Templating.Tests;

// TemplateRenderer had no dedicated test file before this library was extracted — only indirect
// coverage via AzResourceDetailsDownloader.Core.Tests' round-trip TemplateGeneratorTests, which
// exercise it as a side effect of testing template generation, not the renderer itself.
public class TemplateRendererTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Render_SimpleTemplate_SubstitutesModelFieldsAndAppliesRegisteredFunctions()
    {
        RegionDisplayNames.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["norwayeast"] = "Norway East",
        });
        var root = Parse("""{ "name": "thing1", "location": "norwayeast", "properties": { "enabled": true } }""");
        const string template = "# {{ model.name }}\nLocation: {{ model.location | region_display_name }}\nEnabled: {{ model.props.enabled | portal_bool_enabled }}";

        var rendered = TemplateRenderer.Render(template, root, "Microsoft.Test/things");

        Assert.Contains("# thing1", rendered);
        Assert.Contains("Location: Norway East", rendered);
        Assert.Contains("Enabled: Enabled", rendered);
    }

    [Fact]
    public void Render_TemplateWithParseErrors_ThrowsWithTheArmTypeAndScribanMessages()
    {
        var root = Parse("""{ "name": "thing1" }""");
        const string malformed = "{{ this is not valid scriban {{ ";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TemplateRenderer.Render(malformed, root, "Microsoft.Test/things"));

        Assert.Contains("Microsoft.Test/things", ex.Message);
    }
}
