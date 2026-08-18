using System.Text.Json;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Rendering;
using AzToMarkdown.Core.Vault;

namespace AzToMarkdown.Tests;

/// <summary>
/// In-memory tests for shared helpers and vault serialization behavior.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CoreBehaviorTests
{
    // ── ArmId / JsonPath shared helpers ──────────────────────────────────────

    [TestMethod]
    public void ArmId_StripSegments_And_SegmentAfter_And_ZoneName()
    {
        var id = "/subscriptions/SUB/resourceGroups/RG/providers/Microsoft.Network/dnsZones/example.com/A/www";
        Assert.AreEqual("SUB", ArmId.SegmentAfter(id, "subscriptions"));
        Assert.AreEqual("RG",  ArmId.SegmentAfter(id, "resourceGroups"));
        Assert.AreEqual("",    ArmId.SegmentAfter(id, "notThere"));
        Assert.AreEqual("example.com", ArmId.ZoneName(id));
        Assert.IsTrue(ArmId.StripSegments(id, 2).EndsWith("/dnsZones/example.com"));
        Assert.AreEqual("", ArmId.StripSegments("/a/b", 5));
    }

    [TestMethod]
    public void JsonPath_IsCaseInsensitive_And_KqlStringSemantics()
    {
        using var doc = JsonDocument.Parse("""{ "Outer": { "innerName": "v", "num": 42 } }""");
        var root = doc.RootElement;
        Assert.AreEqual("v", JsonPath.GetString(root, "outer", "innername"));   // case-insensitive
        Assert.AreEqual("", JsonPath.GetKqlString(root, "missing"));            // missing → ""
        Assert.AreEqual("42", JsonPath.GetKqlString(root, "outer", "num"));     // non-string → raw text
        Assert.IsNull(JsonPath.GetString(root, "outer", "num"));               // non-string → null
    }

    // ── kind / sku top-level ARG columns persist + round-trip ────────────────

    [TestMethod]
    public void KindAndSku_RoundTripThroughVault()
    {
        using var props = JsonDocument.Parse("""{ "provisioningState": "Succeeded" }""");
        using var sku   = JsonDocument.Parse("""{ "name": "Standard_LRS", "tier": "Standard" }""");
        var node = new TenantNode
        {
            ResourceId     = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st1",
            Name           = "st1",
            Type           = "microsoft.storage/storageaccounts",
            SubscriptionId = "s",
            ResourceGroup  = "rg",
            Location       = "westeurope",
            Properties     = props.RootElement.Clone(),
            Kind           = "StorageV2",
            Sku            = sku.RootElement.Clone(),
        };

        var dir = Path.Combine(Path.GetTempPath(), $"vault-kindsku-{Guid.NewGuid():N}");
        try
        {
            var graph = new TenantGraph();
            graph.AddNode(node);
            new VaultWriter(new VaultTemplateEngine(), serializer: new FrontMatterSerializer("0.0.0-test"))
                .WriteAll(graph, new Dictionary<string, string> { ["s"] = "Sub" }, dir);

            var read = new VaultReader().ReadAll(dir).Nodes.Single(n => n.Name == "st1");
            Assert.AreEqual("StorageV2", read.Kind);
            Assert.IsTrue(YamlJsonConverter.JsonDeepEquals(node.Sku, read.Sku, out var diff), $"sku lost: {diff}");
            // SkuLabel prefers the top-level column (properties has no sku here)
            Assert.AreEqual("Standard (Standard_LRS)", VaultTemplateEngine.SkuLabel(read));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // ── FrontMatterSerializer minimal-context behavior ──────────────────────

    [TestMethod]
    public void SerializeMinimal_MatchesEmptyContextSerialize()
    {
        using var props = JsonDocument.Parse("""{ "a": 1 }""");
        var node = new TenantNode
        {
            ResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Foo/bar/x",
            Name = "x", Type = "microsoft.foo/bar",
            SubscriptionId = "s", ResourceGroup = "rg", Location = "westeurope",
            Properties = props.RootElement.Clone(),
        };
        var ser = new FrontMatterSerializer("0.0.0-test");
        var minimal = ser.SerializeMinimal(node);
        var viaCtx  = ser.Serialize(new FrontMatterContext(node, node.SubscriptionId, [], [], null, []));
        Assert.AreEqual(viaCtx, minimal);
    }
}
