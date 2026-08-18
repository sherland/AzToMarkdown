using System.Text.Json;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Rendering;

namespace AzToMarkdown.Tests;

/// <summary>
/// Renders the real captured ARM payloads under <c>docs/portal-examples/</c> through
/// <see cref="VaultTemplateEngine"/>.
///
/// These are genuine Resource Graph/ARM captures, not hand-written test JSON — they exist to
/// catch real-world property casing/shape issues that inline fixtures could miss, and to keep
/// the vault templates honest against what the Azure Portal itself actually shows. Subscription
/// ID and any personal/employer-identifying values (e.g. alert email addresses) are anonymized
/// in the fixtures.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class PortalExampleFixtureTests
{
    [TestMethod]
    public void ResourceGroup_Fixture_RendersProvisioningState()
    {
        var node   = LoadFixture("Microsoft_Resources_resourceGroups");
        var engine = new VaultTemplateEngine();
        var result = engine.Render(node, [], [], [], id => $"[[{id}]]");

        Assert.AreEqual("microsoft.resources/resourcegroups", node.Type);
        StringAssert.Contains(result.Body, "NetworkWatcherRG");
        StringAssert.Contains(result.Body, "Succeeded");
        Assert.IsFalse(result.Body.Contains("## ℹ️ Details"), "must not fall back to generic");
    }

    [TestMethod]
    public void ActionGroup_Fixture_RendersRealReceiver()
    {
        var node   = LoadFixture("microsoft_insights_actiongroups");
        var engine = new VaultTemplateEngine();
        var result = engine.Render(node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result.Body, "SH");
        StringAssert.Contains(result.Body, "PrimaryEmail");
        StringAssert.Contains(result.Body, "alerts@example.com");
        Assert.IsFalse(result.Body.Contains("## ℹ️ Details"), "must not fall back to generic");
    }

    [TestMethod]
    public void NetworkWatcher_Fixture_RendersRealNameAndState()
    {
        var node   = LoadFixture("microsoft_network_networkwatchers");
        var engine = new VaultTemplateEngine();
        var result = engine.Render(node, [], [], [], id => $"[[{id}]]");

        Assert.AreEqual("NetworkWatcher_westeurope", node.Name);
        StringAssert.Contains(result.Body, "Succeeded");
        Assert.IsFalse(result.Body.Contains("## ℹ️ Details"), "must not fall back to generic");
    }

    [TestMethod]
    public void StorageAccount_Fixture_RendersRealEndpointAndSecuritySettings()
    {
        var node   = LoadFixture("microsoft_storage_storageaccounts");
        var engine = new VaultTemplateEngine();
        var result = engine.Render(node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result.Body, "csb1003200344702947.dfs.core.windows.net");
        StringAssert.Contains(result.Body, "TLS1_2");
        StringAssert.Contains(result.Body, "StorageV2");
        StringAssert.Contains(result.Body, "Hot");
        Assert.IsFalse(result.Body.Contains("## ℹ️ Details"), "must not fall back to generic");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fixture loading
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a captured ARM envelope (id/name/type/location/tags/sku/kind/properties) from
    /// <c>TestData/PortalExamples/{folder}/data.json</c> — copied there at build time from
    /// <c>docs/portal-examples/{folder}/data.json</c> (see the csproj's CopyToOutputDirectory item)
    /// — and builds a <see cref="TenantNode"/> from it.
    /// </summary>
    private static TenantNode LoadFixture(string folder)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "PortalExamples", folder, "data.json");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string Str(string prop) => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var id   = Str("id");
        var name = Str("name");
        var type = Str("type").ToLowerInvariant();

        var tags = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
            foreach (var prop in tagsEl.EnumerateObject())
                tags[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();

        // Resource group's own name is used as its resourceGroup — matches TenantEnumerator's
        // QueryResourceGroups() convention (a resource group isn't itself "in" a resource group).
        var resourceGroup = type == "microsoft.resources/resourcegroups"
            ? name
            : ExtractResourceGroupFromId(id);

        return new TenantNode
        {
            ResourceId     = id,
            Name           = name,
            Type           = type,
            SubscriptionId = ExtractSubscriptionFromId(id),
            ResourceGroup  = resourceGroup,
            Location       = Str("location"),
            Properties     = root.TryGetProperty("properties", out var propsEl) ? propsEl.Clone() : default,
            Kind           = Str("kind"),
            Sku            = root.TryGetProperty("sku", out var skuEl) ? skuEl.Clone() : default,
            Tags           = tags,
        };
    }

    private static string ExtractSubscriptionFromId(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var idx   = Array.IndexOf(parts, "subscriptions");
        return idx >= 0 && idx + 1 < parts.Length ? parts[idx + 1] : "";
    }

    private static string ExtractResourceGroupFromId(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var idx   = Array.IndexOf(parts, "resourceGroups");
        return idx >= 0 && idx + 1 < parts.Length ? parts[idx + 1] : "";
    }
}
