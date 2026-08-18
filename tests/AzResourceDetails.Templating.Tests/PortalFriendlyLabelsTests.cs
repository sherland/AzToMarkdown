using System.Text.Json;

namespace AzResourceDetails.Templating.Tests;

// PortalFriendlyLabels had no dedicated test file before this review round — only indirect coverage
// via AzResourceDetailsDownloader.Core.Tests' FieldRecipeResolverTests (JsonElement path only) and
// ScribanModelBuilderTests' general equivalence test (one domain, Storage). One representative
// method per domain here, through BOTH overloads, proving they delegate to the same *Core
// implementation rather than each having their own copy of the logic that could quietly diverge.
public class PortalFriendlyLabelsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static TemplateResource ResourceFor(JsonElement root, string armType) =>
        ScribanModelBuilder.ToTemplateResource(root, armType);

    [Fact]
    public void StorageReplicationLabel_BothOverloads_AgreeAndMatchExpectedText()
    {
        var root = Parse("""{ "kind": "StorageV2", "sku": { "name": "Standard_GRS" } }""");
        var resource = ResourceFor(root, "Microsoft.Storage/storageAccounts");

        var fromJson = PortalFriendlyLabels.StorageReplicationLabel(root);
        var fromResource = PortalFriendlyLabels.StorageReplicationLabel(resource);

        Assert.Equal("Geo-redundant storage (GRS)", fromJson);
        Assert.Equal(fromJson, fromResource);
    }

    [Fact]
    public void DiskStorageTypeLabel_BothOverloads_AgreeAndMatchExpectedText()
    {
        var root = Parse("""{ "sku": { "name": "Premium_LRS" } }""");
        var resource = ResourceFor(root, "Microsoft.Compute/disks");

        var fromJson = PortalFriendlyLabels.DiskStorageTypeLabel(root);
        var fromResource = PortalFriendlyLabels.DiskStorageTypeLabel(resource);

        Assert.Equal("Premium SSD LRS", fromJson);
        Assert.Equal(fromJson, fromResource);
    }

    [Fact]
    public void MongoClusterTierLabel_BothOverloads_AgreeAndMatchExpectedText()
    {
        var root = Parse("""{ "properties": { "compute": { "tier": "M30" } } }""");
        var resource = ResourceFor(root, "Microsoft.DocumentDB/mongoClusters");

        var fromJson = PortalFriendlyLabels.MongoClusterTierLabel(root);
        var fromResource = PortalFriendlyLabels.MongoClusterTierLabel(resource);

        Assert.Equal("M30 tier, 2 vCores, 8 GiB RAM", fromJson);
        Assert.Equal(fromJson, fromResource);
    }

    [Fact]
    public void MongoStorageEncryptionLabel_BothOverloads_AgreeAndMatchExpectedText()
    {
        var root = Parse("""{ "identity": { "type": "UserAssigned" } }""");
        var resource = ResourceFor(root, "Microsoft.DocumentDB/mongoClusters");

        var fromJson = PortalFriendlyLabels.MongoStorageEncryptionLabel(root);
        var fromResource = PortalFriendlyLabels.MongoStorageEncryptionLabel(resource);

        Assert.Equal("Customer-managed key", fromJson);
        Assert.Equal(fromJson, fromResource);
    }

    // No identity block at all — the documented default, not a missing-data failure.
    [Fact]
    public void MongoStorageEncryptionLabel_NoIdentity_DefaultsToServiceManagedKey_ForBothOverloads()
    {
        var root = Parse("""{ "name": "thing1" }""");
        var resource = ResourceFor(root, "Microsoft.DocumentDB/mongoClusters");

        Assert.Equal("Service-managed key", PortalFriendlyLabels.MongoStorageEncryptionLabel(root));
        Assert.Equal("Service-managed key", PortalFriendlyLabels.MongoStorageEncryptionLabel(resource));
        Assert.Null(resource.IdentityType);
    }

    [Fact]
    public void LogicWorkflowDefinitionLabel_BothOverloads_AgreeAndMatchExpectedText()
    {
        var root = Parse("""
            { "properties": { "definition": { "triggers": { "t1": {} }, "actions": { "a1": {}, "a2": {} } } } }
            """);
        var resource = ResourceFor(root, "Microsoft.Logic/workflows");

        var fromJson = PortalFriendlyLabels.LogicWorkflowDefinitionLabel(root);
        var fromResource = PortalFriendlyLabels.LogicWorkflowDefinitionLabel(resource);

        Assert.Equal("1 trigger, 2 actions", fromJson);
        Assert.Equal(fromJson, fromResource);
    }

    [Fact]
    public void AppConfigPricingTierLabel_BothOverloads_AgreeAndMatchExpectedText()
    {
        var root = Parse("""{ "sku": { "name": "standard" } }""");
        var resource = ResourceFor(root, "Microsoft.AppConfiguration/configurationStores");

        var fromJson = PortalFriendlyLabels.AppConfigPricingTierLabel(root);
        var fromResource = PortalFriendlyLabels.AppConfigPricingTierLabel(resource);

        Assert.Equal("Standard (Click to upgrade)", fromJson);
        Assert.Equal(fromJson, fromResource);
    }

    [Fact]
    public void AksPowerStateLabel_BothOverloads_AgreeAndMatchExpectedText()
    {
        var root = Parse("""{ "properties": { "powerState": { "code": "Running" } } }""");
        var resource = ResourceFor(root, "Microsoft.ContainerService/managedClusters");

        var fromJson = PortalFriendlyLabels.AksPowerStateLabel(root);
        var fromResource = PortalFriendlyLabels.AksPowerStateLabel(resource);

        Assert.Equal("Running", fromJson);
        Assert.Equal(fromJson, fromResource);
    }

    [Fact]
    public void AksNodePoolsLabel_BothOverloads_AgreeAndMatchExpectedText()
    {
        var root = Parse("""
            { "properties": { "agentPoolProfiles": [ { "provisioningState": "Succeeded" }, { "provisioningState": "Failed" } ] } }
            """);
        var resource = ResourceFor(root, "Microsoft.ContainerService/managedClusters");

        var fromJson = PortalFriendlyLabels.AksNodePoolsLabel(root);
        var fromResource = PortalFriendlyLabels.AksNodePoolsLabel(resource);

        Assert.Equal("2 node pools - 1 failed", fromJson);
        Assert.Equal(fromJson, fromResource);
    }

    // The fallback case the review specifically asked to cover explicitly: no root-level sku, only
    // a properties-nested one (Key Vault/App Gateway-style types) — proves SkuAndVersion.ResolveSku's
    // fallback is what a TemplateResource's Sku is expected to already reflect, and that the
    // JsonElement overload resolves it the same way internally before constructing one.
    [Fact]
    public void ResolveSku_FallsBackToPropertiesNestedSku_WhenNoRootLevelSkuExists()
    {
        var root = Parse("""{ "properties": { "sku": { "name": "standard", "tier": "Standard" } } }""");

        var resolved = SkuAndVersion.ResolveSku(root);

        Assert.Equal(JsonValueKind.Object, resolved.ValueKind);
        Assert.Equal("standard", resolved.GetProperty("name").GetString());
        // name ("standard") and tier ("Standard") differ only by casing, so SkuLabel's combined
        // "Tier (Name)" format applies rather than collapsing to just one of them — this is
        // SkuLabel's own documented behavior (see its class comment), not what this test is
        // actually verifying; the point here is that the fallback found the sku object at all.
        Assert.Equal("Standard (standard)", SkuAndVersion.SkuLabel(root));

        var resource = ScribanModelBuilder.ToTemplateResource(root, "Microsoft.KeyVault/vaults");
        Assert.Equal("Standard (standard)", SkuAndVersion.SkuLabel(resource));
    }

    [Fact]
    public void ResolveSku_NoSkuAnywhere_ReturnsUndefinedElement_NotNull()
    {
        var root = Parse("""{ "name": "thing1" }""");

        var resolved = SkuAndVersion.ResolveSku(root);

        Assert.Equal(JsonValueKind.Undefined, resolved.ValueKind);
        Assert.Null(SkuAndVersion.SkuLabel(root));
    }
}
