using System.Text.Json;
using AzResourceDetails.Templating;

namespace AzResourceDetails.Templating.Tests;

public class SkuAndVersionTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SkuLabel_RootLevelSkuWithDifferentTierAndName_CombinesAsTierParenName()
    {
        var root = Parse("""{"sku":{"tier":"Standard","name":"Standard_LRS"}}""");

        Assert.Equal("Standard (Standard_LRS)", SkuAndVersion.SkuLabel(root));
    }

    [Fact]
    public void SkuLabel_PropertiesNestedSku_FallsBackWhenNoRootSku()
    {
        var root = Parse("""{"properties":{"sku":{"tier":"Premium","name":"P1v2"}}}""");

        Assert.Equal("Premium (P1v2)", SkuAndVersion.SkuLabel(root));
    }

    [Fact]
    public void SkuLabel_TierEqualsName_ReturnsJustTheName()
    {
        var root = Parse("""{"sku":{"tier":"Standard","name":"Standard"}}""");

        Assert.Equal("Standard", SkuAndVersion.SkuLabel(root));
    }

    [Fact]
    public void SkuLabel_NoTier_ReturnsJustTheName()
    {
        var root = Parse("""{"sku":{"name":"Standard_LRS"}}""");

        Assert.Equal("Standard_LRS", SkuAndVersion.SkuLabel(root));
    }

    // Symmetric with the "no tier" case above — a sku object with only a tier and no name must not
    // fall through to the combined "Tier (Name)" format and produce "Standard ()". Live-caught: the
    // original code had no branch for this, and string-interpolating a null `name` silently renders
    // as empty rather than throwing, so the bug produced plausible-looking-but-wrong text instead of
    // an obvious crash.
    [Fact]
    public void SkuLabel_NoName_ReturnsJustTheTier()
    {
        var root = Parse("""{"sku":{"tier":"Standard"}}""");

        Assert.Equal("Standard", SkuAndVersion.SkuLabel(root));
    }

    [Fact]
    public void SkuLabel_NoSkuObjectAtAll_ReturnsNull()
    {
        var root = Parse("""{"name":"example"}""");

        Assert.Null(SkuAndVersion.SkuLabel(root));
    }

    [Fact]
    public void SkuLabel_SkuObjectWithNeitherNameNorTier_ReturnsNull()
    {
        var root = Parse("""{"sku":{"capacity":1}}""");

        Assert.Null(SkuAndVersion.SkuLabel(root));
    }

    // AKS confirmed live 2026-08-14: "Sku" = sku.name = "Base", "Pricing tier" = sku.tier = "Free" —
    // rendered as two separate direct passthroughs, never combined into SkuLabel's shape, so both
    // bare accessors need to work independently of SkuLabel.
    [Fact]
    public void SkuName_And_SkuTier_ReadIndependently()
    {
        var root = Parse("""{"sku":{"tier":"Free","name":"Base"}}""");

        Assert.Equal("Base", SkuAndVersion.SkuName(root));
        Assert.Equal("Free", SkuAndVersion.SkuTier(root));
    }

    [Fact]
    public void SkuName_NoSkuObject_ReturnsNull()
    {
        var root = Parse("""{}""");

        Assert.Null(SkuAndVersion.SkuName(root));
        Assert.Null(SkuAndVersion.SkuTier(root));
    }

    [Theory]
    [InlineData("Microsoft.ContainerService/managedClusters")]
    [InlineData("microsoft.containerservice/managedclusters")]
    public void ExtractVersion_AksType_IsCaseInsensitiveAndReadsKubernetesVersion(string armType)
    {
        var properties = Parse("""{"kubernetesVersion":"1.29.2"}""");

        Assert.Equal("1.29.2", SkuAndVersion.ExtractVersion(armType, properties));
    }

    [Fact]
    public void ExtractVersion_WebSites_FallsThroughSiteConfigStackChain()
    {
        var properties = Parse("""{"siteConfig":{"pythonVersion":"3.12"}}""");

        Assert.Equal("3.12", SkuAndVersion.ExtractVersion("Microsoft.Web/sites", properties));
    }

    [Fact]
    public void ExtractVersion_SqlDatabase_PrefersRequestedOverCurrentServiceObjective()
    {
        var properties = Parse(
            """{"requestedServiceObjectiveName":"S1","currentServiceObjectiveName":"S0"}""");

        Assert.Equal("S1", SkuAndVersion.ExtractVersion("Microsoft.Sql/servers/databases", properties));
    }

    [Fact]
    public void ExtractVersion_UnlistedArmType_FallsBackToPlainVersionProperty()
    {
        var properties = Parse("""{"version":"2.0"}""");

        Assert.Equal("2.0", SkuAndVersion.ExtractVersion("Microsoft.SomeUnlisted/type", properties));
    }

    [Fact]
    public void ExtractVersion_NoMatchingProperty_ReturnsNull()
    {
        var properties = Parse("""{}""");

        Assert.Null(SkuAndVersion.ExtractVersion("Microsoft.SomeUnlisted/type", properties));
    }
}
