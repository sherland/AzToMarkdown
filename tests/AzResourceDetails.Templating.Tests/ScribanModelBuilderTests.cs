using System.Text.Json;

namespace AzResourceDetails.Templating.Tests;

// ScribanModelBuilder had no dedicated test file before this library was extracted — only indirect
// coverage through AzResourceDetailsDownloader.Core.Tests' FieldRecipeResolverTests/
// TemplateGeneratorTests, which exercise it as a side effect of testing something else. These are
// the library's own direct tests for the one public entry point, BuildModel.
public class ScribanModelBuilderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void BuildModel_PopulatesTheFirstClassFieldsFromRootLevelArmProperties()
    {
        var root = Parse("""
            {
              "id": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-example/providers/Microsoft.Test/things/thing1",
              "name": "thing1",
              "location": "norwayeast",
              "tags": { "env": "test" },
              "properties": { "foo": "bar" }
            }
            """);

        var model = ScribanModelBuilder.BuildModel(root, "Microsoft.Test/things");

        Assert.Equal(root.GetProperty("id").GetString(), model["id"]);
        Assert.Equal("thing1", model["name"]);
        Assert.Equal("Microsoft.Test/things", model["type"]);
        Assert.Equal("norwayeast", model["location"]);
        Assert.Equal("rg-example", model["resource_group"]);
    }

    [Fact]
    public void BuildModel_Props_ExposesTheWholePropertiesTreeIncludingNestedArrays()
    {
        var root = Parse("""
            {
              "name": "thing1",
              "properties": { "items": [ { "count": 3 }, { "count": 5 } ], "nested": { "flag": true } }
            }
            """);

        var model = ScribanModelBuilder.BuildModel(root, "Microsoft.Test/things");
        var props = Assert.IsType<Scriban.Runtime.ScriptObject>(model["props"]);
        var items = Assert.IsType<Scriban.Runtime.ScriptArray>(props["items"]);
        var nested = Assert.IsType<Scriban.Runtime.ScriptObject>(props["nested"]);

        Assert.Equal(2, items.Count);
        Assert.Equal(true, nested["flag"]);
    }

    // model.tags is deliberately NOT part of the shared model (see PopulateSharedFields' doc
    // comment) — no generated template references it, and the two known consumers of this library
    // want different shapes for it. A host that wants a tags field populates it itself, before or
    // after calling PopulateSharedFields.
    [Fact]
    public void BuildModel_NeverPopulatesTags_HostOwnsThatFieldEntirely()
    {
        var root = Parse("""{ "name": "thing1", "tags": { "env": "test" } }""");

        var model = ScribanModelBuilder.BuildModel(root, "Microsoft.Test/things");

        Assert.False(model.ContainsKey("tags"));
    }

    [Fact]
    public void BuildModel_SkuFields_MirrorSkuAndVersionExactly()
    {
        var root = Parse("""{ "name": "thing1", "sku": { "tier": "Standard", "name": "Standard_LRS", "capacity": 3 } }""");

        var model = ScribanModelBuilder.BuildModel(root, "Microsoft.Test/things");

        Assert.Equal("Standard (Standard_LRS)", model["sku_label"]);
        Assert.Equal("Standard_LRS", model["sku_name"]);
        Assert.Equal("Standard", model["sku_tier"]);
        Assert.Equal(3L, model["sku_capacity"]);
    }

    [Fact]
    public void BuildModel_NoSkuObjectAtAll_SkuFieldsAreNull()
    {
        var root = Parse("""{ "name": "thing1" }""");

        var model = ScribanModelBuilder.BuildModel(root, "Microsoft.Test/things");

        Assert.Null(model["sku_label"]);
        Assert.Null(model["sku_name"]);
        Assert.Null(model["sku_tier"]);
        Assert.Null(model["sku_capacity"]);
    }

    // Confirms the friendly-label fields actually reach ScribanModelBuilder's output (not just
    // PortalFriendlyLabels in isolation) — storage_replication_label is a representative example.
    [Fact]
    public void BuildModel_FriendlyLabelFields_AreComputedFromPortalFriendlyLabels()
    {
        var root = Parse("""{ "name": "thing1", "sku": { "name": "Standard_LRS" } }""");

        var model = ScribanModelBuilder.BuildModel(root, "Microsoft.Storage/storageAccounts");

        Assert.Equal("Locally redundant storage (LRS)", model["storage_replication_label"]);
    }

    // The equivalence guarantee a decomposed-input consumer (e.g. AzToMd's TenantNode) depends on:
    // building from a full ARM document and building from that SAME document's manually-decomposed
    // TemplateResource must produce identical values for every field this library declares shared.
    // Exercises id/name/location/resource_group (from the id), kind + sku (StorageReplicationLabel
    // needs both), and identity.type (MongoStorageEncryptionLabel) — the two root-level fields most
    // at risk of being missed by a hand-rolled decomposition, since they sit outside properties/sku.
    [Fact]
    public void BuildModel_JsonElementOverload_AndTemplateResourceOverload_ProduceTheSameSharedFields()
    {
        var root = Parse("""
            {
              "id": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-example/providers/Microsoft.Storage/storageAccounts/thing1",
              "name": "thing1",
              "location": "norwayeast",
              "kind": "StorageV2",
              "identity": { "type": "UserAssigned" },
              "sku": { "name": "Standard_GRS", "tier": "Standard", "capacity": 1 },
              "properties": { "foo": "bar" }
            }
            """);
        const string armType = "Microsoft.Storage/storageAccounts";

        var fromJson = ScribanModelBuilder.BuildModel(root, armType);

        var resource = new TemplateResource(
            Id: root.GetProperty("id").GetString(),
            Name: "thing1",
            ArmType: armType,
            Location: "norwayeast",
            ResourceGroup: "rg-example",
            Kind: "StorageV2",
            IdentityType: "UserAssigned",
            Properties: root.GetProperty("properties"),
            Sku: root.GetProperty("sku"));
        var fromResource = ScribanModelBuilder.BuildModel(resource);

        foreach (var field in TemplateRuntimeContract.SupportedModelFields)
        {
            Assert.Equal(fromJson[field], fromResource[field]);
        }
    }

    // PopulateSharedFields must never clobber fields the host already added — this is the whole
    // point of exposing it separately from BuildModel, so a host can populate its own vault-specific
    // fields into the same ScriptObject before/after calling this. Covers the specific field names
    // AzToMd's own vault renderer actually owns, not just one representative example.
    [Fact]
    public void PopulateSharedFields_LeavesEveryHostOwnedKeyUntouched()
    {
        var target = new Scriban.Runtime.ScriptObject
        {
            ["relationships"] = "host-relationships-value",
            ["tags"] = "host-tags-value",
            ["role_assignments"] = "host-role-assignments-value",
            ["wiki_links"] = "host-wiki-links-value",
        };
        var resource = MinimalResource();

        ScribanModelBuilder.PopulateSharedFields(target, resource);

        Assert.Equal("host-relationships-value", target["relationships"]);
        Assert.Equal("host-tags-value", target["tags"]);
        Assert.Equal("host-role-assignments-value", target["role_assignments"]);
        Assert.Equal("host-wiki-links-value", target["wiki_links"]);
        Assert.Equal("thing1", target["name"]);
    }

    // The other half of the overwrite/preserve contract: shared fields ARE unconditionally
    // refreshed to the current resource's values, not merged with whatever was there before — this
    // is a refresh for the fields this library owns, not a "populate once" or "fill gaps only".
    [Fact]
    public void PopulateSharedFields_CalledTwiceWithDifferentResources_SecondCallsSharedValuesWin()
    {
        var target = new Scriban.Runtime.ScriptObject();
        ScribanModelBuilder.PopulateSharedFields(target, MinimalResource() with { Name = "first-name" });

        ScribanModelBuilder.PopulateSharedFields(target, MinimalResource() with { Name = "second-name" });

        Assert.Equal("second-name", target["name"]);
    }

    // Derived, symmetric version of the "shared fields populated" guarantee — compares the actual
    // set of keys PopulateSharedFields writes against what TemplateRuntimeContract declares, in
    // both directions, rather than eyeballing two separately-maintained lists.
    [Fact]
    public void PopulateSharedFields_PopulatesExactlyTheContractDeclaredFields_PlusProps()
    {
        var target = new Scriban.Runtime.ScriptObject();

        ScribanModelBuilder.PopulateSharedFields(target, MinimalResource());

        var expected = TemplateRuntimeContract.SupportedModelFields.Append("props").ToHashSet();
        Assert.Equal(expected, target.Keys.ToHashSet());
    }

    [Fact]
    public void PopulateSharedFields_NullTarget_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ScribanModelBuilder.PopulateSharedFields(null!, MinimalResource()));
    }

    [Fact]
    public void PopulateSharedFields_NullResource_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ScribanModelBuilder.PopulateSharedFields(new Scriban.Runtime.ScriptObject(), null!));
    }

    [Fact]
    public void BuildModel_TemplateResourceOverload_NullResource_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ScribanModelBuilder.BuildModel((TemplateResource)null!));
    }

    // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException specifically for null (a
    // subtype of ArgumentException) and plain ArgumentException for empty — ThrowsAny accepts
    // either, since both are the same class of "invalid caller argument" this test is about.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildModel_JsonElementOverload_NullOrEmptyArmType_ThrowsArgumentException(string? armType)
    {
        var root = Parse("""{ "name": "thing1" }""");

        Assert.ThrowsAny<ArgumentException>(() => ScribanModelBuilder.BuildModel(root, armType!));
    }

    // Missing/absent source data (as opposed to an invalid caller argument, covered above) must
    // never throw — Undefined Properties/Sku, no identity, empty strings are all ordinary,
    // expected shapes for a resource this library knows nothing else about.
    [Fact]
    public void PopulateSharedFields_UndefinedPropertiesAndSku_DoesNotThrow_AndYieldsNullOrEmptyFields()
    {
        var target = new Scriban.Runtime.ScriptObject();
        var resource = MinimalResource();

        ScribanModelBuilder.PopulateSharedFields(target, resource);

        Assert.IsType<Scriban.Runtime.ScriptObject>(target["props"]);
        Assert.Empty((Scriban.Runtime.ScriptObject)target["props"]!);
        Assert.Null(target["sku_label"]);
        Assert.Null(target["version"]);
    }

    [Fact]
    public void PopulateSharedFields_EmptyStringFields_DoesNotThrow()
    {
        var target = new Scriban.Runtime.ScriptObject();
        var resource = MinimalResource() with { Id = "", Name = "", Location = "", ResourceGroup = "", Kind = "" };

        var ex = Record.Exception(() => ScribanModelBuilder.PopulateSharedFields(target, resource));

        Assert.Null(ex);
        Assert.Equal("", target["name"]);
    }

    // A malformed shape (Properties given as a JSON string instead of an object) is still not a
    // throwing condition — JsonToScriban converts whatever ValueKind it's actually handed rather
    // than assuming Object, so model.props just becomes a Scriban string instead of a ScriptObject
    // for a resource shaped like this, not an exception.
    [Fact]
    public void PopulateSharedFields_PropertiesShapedAsAStringNotAnObject_DoesNotThrow()
    {
        using var malformed = JsonDocument.Parse(""" "not-an-object" """);
        var target = new Scriban.Runtime.ScriptObject();
        var resource = MinimalResource() with { Properties = malformed.RootElement };

        var ex = Record.Exception(() => ScribanModelBuilder.PopulateSharedFields(target, resource));

        Assert.Null(ex);
        Assert.Equal("not-an-object", target["props"]);
    }

    private static TemplateResource MinimalResource() => new(
        Id: null, Name: "thing1", ArmType: "Microsoft.Test/things", Location: null, ResourceGroup: null,
        Kind: null, IdentityType: null, Properties: default, Sku: default);
}
