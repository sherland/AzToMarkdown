using System.Text.Json;
using System.Text.RegularExpressions;
using AzResourceDetails.Templating;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Rendering;

namespace AzToMarkdown.Tests;

/// <summary>AzToMd-specific integration tests for the synchronized shared template runtime.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SharedTemplateRuntimeIntegrationTests
{
    [TestMethod]
    public void CreateTemplateResource_MapsIdentityAndNestedSkuFallback()
    {
        using var properties = JsonDocument.Parse("""{"sku":{"name":"nested-name","tier":"nested-tier"}}""");
        using var identity = JsonDocument.Parse("""{"type":"SystemAssigned","principalId":"principal-1"}""");
        var node = new TenantNode
        {
            ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.DocumentDB/mongoClusters/mongo",
            Name = "mongo",
            Type = "microsoft.documentdb/mongoclusters",
            ResourceGroup = "rg",
            Location = "norwayeast",
            Kind = "vCore",
            Properties = properties.RootElement.Clone(),
            Identity = identity.RootElement.Clone(),
        };

        var resource = VaultTemplateEngine.CreateTemplateResource(node);

        Assert.AreEqual(node.ResourceId, resource.Id);
        Assert.AreEqual("SystemAssigned", resource.IdentityType);
        Assert.AreEqual("nested-name", resource.Sku.GetProperty("name").GetString());
        Assert.AreEqual(node.Properties.GetRawText(), resource.Properties.GetRawText());
    }

    [TestMethod]
    public void Render_GenericTemplate_UsesSharedRegionDisplayFunction()
    {
        var node = MakeNode("microsoft.example/widgets", "norwayeast");

        var rendered = new VaultTemplateEngine().Render(node, [], [], [], id => $"`{id}`");

        StringAssert.Contains(rendered.Body, "Norway East");
    }

    [TestMethod]
    public void Render_StorageTemplate_UsesSharedTopLevelSkuField()
    {
        using var sku = JsonDocument.Parse("""{"name":"Standard_LRS","tier":"Standard","capacity":4}""");
        var node = MakeNode("microsoft.storage/storageaccounts", "norwayeast", sku.RootElement.Clone());


        var rendered = new VaultTemplateEngine().Render(node, [], [], [], id => $"`{id}`");

        StringAssert.Contains(rendered.Body, "Standard_LRS");
    }

    [TestMethod]
    public void Templates_ReferenceOnlySharedOrAzToMdModelFields()
    {
        var allowed = TemplateRuntimeContract.SupportedModelFields
            .Concat([
                "tags", "role_assignments", "inbound_ids", "outbound_ids",
                "inbound", "outbound", "props_scalars", "props_complex",
            ])
            .ToHashSet(StringComparer.Ordinal);
        var unsupported = new List<string>();

        foreach (var templatePath in Directory.EnumerateFiles(FindTemplateDirectory(), "*.sbn"))
        {
            var text = File.ReadAllText(templatePath);
            foreach (Match match in Regex.Matches(text, @"\bmodel\.([A-Za-z_][A-Za-z0-9_]*)"))
            {
                var field = match.Groups[1].Value;
                if (!allowed.Contains(field))
                    unsupported.Add($"{Path.GetFileName(templatePath)}: model.{field}");
            }
        }

        Assert.HasCount(0, unsupported,
            "Templates reference fields outside the shared + AzToMd contracts:\n" + string.Join("\n", unsupported));
    }

    private static TenantNode MakeNode(string type, string location, JsonElement sku = default)
    {
        using var properties = JsonDocument.Parse("{}");
        return new TenantNode
        {
            ResourceId = $"/subscriptions/sub/resourceGroups/rg/providers/{type}/resource",
            Name = "resource",
            Type = type,
            SubscriptionId = "sub",
            ResourceGroup = "rg",
            Location = location,
            Properties = properties.RootElement.Clone(),
            Sku = sku,
        };
    }

    private static string FindTemplateDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName,
                "src", "Libraries", "AzToMarkdown.Core", "Rendering", "Templates");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate AzToMarkdown.Core rendering templates.");
    }
}
