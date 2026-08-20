using System.Text.Json;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Rendering;
using AzToMarkdown.Core.Vault;

namespace AzToMarkdown.Tests;

/// <summary>
/// Unit tests for the Scriban-based <see cref="VaultTemplateEngine"/> and the
/// <see cref="VaultWriter"/> enrichment built on top of it.
///
/// All tests run entirely in-memory — no Azure CLI, no ARG queries.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class VaultTemplateEngineTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // TypeToKey conversion
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void TypeToKey_ConvertsDotsAndSlashesToUnderscores()
    {
        Assert.AreEqual("microsoft_web_sites",                        VaultTemplateEngine.TypeToKey("microsoft.web/sites"));
        Assert.AreEqual("microsoft_network_applicationgateways",      VaultTemplateEngine.TypeToKey("microsoft.network/applicationgateways"));
        Assert.AreEqual("microsoft_cdn_profiles_afdendpoints",        VaultTemplateEngine.TypeToKey("microsoft.cdn/profiles/afdendpoints"));
        Assert.AreEqual("microsoft_compute_virtualmachines",          VaultTemplateEngine.TypeToKey("microsoft.compute/virtualmachines"));
        Assert.AreEqual("microsoft_authorization_roleassignments",    VaultTemplateEngine.TypeToKey("microsoft.authorization/roleassignments"));
    }

    [TestMethod]
    public void TypeToKey_IsLowercase()
    {
        var key = VaultTemplateEngine.TypeToKey("Microsoft.Web/Sites");
        Assert.AreEqual("microsoft_web_sites", key);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NormaliseType
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void NormaliseType_MapsKnownTypesToCanonicalCasing()
    {
        Assert.AreEqual("Microsoft.Web/sites",             VaultTemplateEngine.NormaliseType("microsoft.web/sites"));
        Assert.AreEqual("Microsoft.Web/serverFarms",       VaultTemplateEngine.NormaliseType("microsoft.web/serverfarms"));
        Assert.AreEqual("Microsoft.Network/virtualNetworks", VaultTemplateEngine.NormaliseType("microsoft.network/virtualnetworks"));
        Assert.AreEqual("Microsoft.Cdn/profiles",          VaultTemplateEngine.NormaliseType("microsoft.cdn/profiles"));
    }

    [TestMethod]
    public void NormaliseType_PassesThroughUnknownType()
    {
        var unknown = "microsoft.unknown/someresource";
        Assert.AreEqual(unknown, VaultTemplateEngine.NormaliseType(unknown));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // JsonToScriban conversion
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void JsonToScriban_StringValue_ReturnsString()
    {
        using var doc = JsonDocument.Parse("\"hello\"");
        var result = VaultTemplateEngine.JsonToScriban(doc.RootElement);
        Assert.AreEqual("hello", result);
    }

    [TestMethod]
    public void JsonToScriban_BoolTrue_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("true");
        Assert.AreEqual(true, VaultTemplateEngine.JsonToScriban(doc.RootElement));
    }

    [TestMethod]
    public void JsonToScriban_Number_ReturnsNumeric()
    {
        using var doc = JsonDocument.Parse("42");
        var result = VaultTemplateEngine.JsonToScriban(doc.RootElement);
        Assert.IsNotNull(result);
        Assert.IsTrue(result is long || result is double, $"Expected long or double, got {result.GetType()}");
    }

    [TestMethod]
    public void JsonToScriban_Null_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("null");
        Assert.IsNull(VaultTemplateEngine.JsonToScriban(doc.RootElement));
    }

    [TestMethod]
    public void JsonToScriban_Object_KeepsNestedKeysLowercase()
    {
        using var doc = JsonDocument.Parse("""{"SKU":{"Name":"Standard_D2"}}""");
        var result = VaultTemplateEngine.JsonToScriban(doc.RootElement) as Scriban.Runtime.ScriptObject;
        Assert.IsNotNull(result);
        // Top-level key lowercased
        Assert.IsTrue(result.ContainsKey("sku"), "Expected lowercased key 'sku'");
        var nested = result["sku"] as Scriban.Runtime.ScriptObject;
        Assert.IsNotNull(nested);
        Assert.IsTrue(nested.ContainsKey("name"), "Expected lowercased nested key 'name'");
        Assert.AreEqual("Standard_D2", nested["name"]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Template rendering — output structure
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_Generic_ProducesValidYamlFrontmatter()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/res", "my-resource", "microsoft.some/unknown", "sub-1", "rg-1");
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        Assert.IsTrue(result.StartsWith("---"), "Output must start with YAML frontmatter");
        var secondDash = result.IndexOf("\n---", 3);
        Assert.IsTrue(secondDash > 0,            "Output must have a closing --- after opening ---");
    }

    [TestMethod]
    public void Render_Generic_ContainsResourceName()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/my-res", "my-res", "microsoft.custom/type", "sub-1", "rg-1");
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        Assert.IsTrue(result.Contains("my-res"), "Rendered output must contain the resource name");
        Assert.IsTrue(result.Contains("# my-res"), "Body must have an H1 heading with the resource name");
    }

    [TestMethod]
    public void Render_Generic_ContainsIdAndType()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Custom/type/res", "res", "microsoft.custom/type");
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Custom/type/res");
    }

    [TestMethod]
    public void Render_FallsBackToGeneric_ForUnknownType()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/res", "res", "microsoft.definitely/doesnotexist");
        // Should NOT throw; falls back to _generic.sbn
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        Assert.IsFalse(string.IsNullOrWhiteSpace(result), "Fallback render must produce non-empty output");
        Assert.IsTrue(result.Contains("---"),              "Fallback must still produce frontmatter");
    }

    [TestMethod]
    public void Render_NeverThrows_EvenForBadProps()
    {
        var engine = new VaultTemplateEngine();
        // Node with empty/undefined properties
        var node   = MakeNode("/sub/rg/res", "res", "microsoft.web/sites");
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tags: must appear in BOTH frontmatter and body
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_TagsAppearInFrontmatter()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithTags("/sub/rg/res", "res", "microsoft.storage/storageaccounts",
            new() { ["environment"] = "prod", ["owner"] = "team-a" });
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        // Tags block must be inside the frontmatter (before the closing ---)
        var closingFm = result.IndexOf("\n---", 3);
        var fmBlock   = result[..closingFm];
        StringAssert.Contains(fmBlock, "environment");
        StringAssert.Contains(fmBlock, "prod");
    }

    [TestMethod]
    public void Render_TagsAppearInBody()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithTags("/sub/rg/res", "res", "microsoft.storage/storageaccounts",
            new() { ["environment"] = "prod" });
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        // Tags section must also exist in the body (after frontmatter)
        var closingFm = result.IndexOf("\n---", 3);
        var body      = result[(closingFm + 4)..];
        StringAssert.Contains(body, "🏷️ Tags", "Body must contain the Tags heading");
        StringAssert.Contains(body, "environment");
        StringAssert.Contains(body, "prod");
    }

    [TestMethod]
    public void Render_NoTagsSection_WhenNodeHasNoTags()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/res", "res", "microsoft.storage/storageaccounts");
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");
        Assert.IsFalse(result.Contains("🏷️ Tags"), "Tags section must be absent when node has no tags");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Role assignments: embedded in resource file, NOT separate files
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_RoleAssignmentsAppearInFrontmatter()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/res", "res", "microsoft.keyvault/vaults");
        var roles  = new List<RoleAssignmentInfo>
        {
            new("Owner",       "aaa-111"),
            new("Contributor", "bbb-222"),
        };
        var result = RenderFull(engine,node, [], [], roles, id => $"[[{id}]]");

        var closingFm = result.IndexOf("\n---", 3);
        var fmBlock   = result[..closingFm];
        StringAssert.Contains(fmBlock, "role_assignments");
        StringAssert.Contains(fmBlock, "Owner");
        StringAssert.Contains(fmBlock, "aaa-111");
    }

    [TestMethod]
    public void Render_RoleAssignmentsAppearInBody()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/res", "res", "microsoft.keyvault/vaults");
        var roles  = new List<RoleAssignmentInfo> { new("Owner", "aaa-111") };
        var result = RenderFull(engine,node, [], [], roles, id => $"[[{id}]]");

        var closingFm = result.IndexOf("\n---", 3);
        var body      = result[(closingFm + 4)..];
        StringAssert.Contains(body, "🔑 Role Assignments", "Body must contain role assignments heading");
        StringAssert.Contains(body, "Owner");
        StringAssert.Contains(body, "aaa-111");
    }

    [TestMethod]
    public void Render_NoRoleSection_WhenNoRoles()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/res", "res", "microsoft.keyvault/vaults");
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");
        Assert.IsFalse(result.Contains("🔑 Role Assignments"), "Role Assignments section must be absent when there are none");
    }

    [TestMethod]
    [DataRow("microsoft.storage/storageaccounts")]
    [DataRow("microsoft.some/unknown")]
    public void Render_SharedFooter_IsAppendedExactlyOnce(string resourceType)
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithTags("/sub/rg/res", "res", resourceType,
            new() { ["environment"] = "prod" });
        var roles  = new List<RoleAssignmentInfo> { new("Owner", "aaa-111") };
        var result = RenderFull(engine, node, [], [], roles, id => $"[[{id}]]");

        Assert.AreEqual(1, result.Split("🏷️ Tags").Length - 1,
            "The shared Tags section must be appended exactly once.");
        Assert.AreEqual(1, result.Split("🔑 Role Assignments").Length - 1,
            "The shared Role Assignments section must be appended exactly once.");
        Assert.IsTrue(result.IndexOf("# res", StringComparison.Ordinal) <
                      result.IndexOf("🏷️ Tags", StringComparison.Ordinal),
            "The shared footer must follow the resource-specific details.");
    }

    [TestMethod]
    public void BuildVaultPaths_ExcludesRoleAssignmentNodes()
    {
        var graph    = new TenantGraph();
        var resource = MakeNode("/sub/rg/res", "my-res",    "microsoft.keyvault/vaults");
        var roleNode = MakeNode("/sub/rg/ra",  "guid-name", "microsoft.authorization/roleassignments");
        graph.AddNode(resource);
        graph.AddNode(roleNode);

        var paths = VaultWriter.BuildVaultPaths(graph, new Dictionary<string, string> { ["sub"] = "my-sub" });

        Assert.IsTrue (paths.ContainsKey(resource.ResourceId), "Regular resource must have a vault path");
        Assert.IsFalse(paths.ContainsKey(roleNode.ResourceId), "Role assignment node must NOT have a vault path");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Role-assignment edges must NOT appear in depends_on frontmatter
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_DependencyIds_ExcludeRoleAssignmentEdges()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/res", "res", "microsoft.keyvault/vaults");

        // One normal inbound edge + one role-assignment edge
        var normalEdge = new TenantEdge { FromId = "/sub/rg/other",  ToId = node.ResourceId, Label = "private link" };
        var roleEdge   = new TenantEdge { FromId = "/sub/rg/ra-id",  ToId = node.ResourceId, Label = "role:Owner" };

        var result = RenderFull(engine,node, [normalEdge, roleEdge], [], [], id => $"[[{id}]]");

        // The normal edge's source ID should be in depends_on_inbound
        var fmEnd  = result.IndexOf("\n---", 3);
        var fm     = result[..fmEnd];
        StringAssert.Contains(fm, "/sub/rg/other",  "Normal inbound dep must appear in frontmatter");
        Assert.IsFalse(fm.Contains("/sub/rg/ra-id"), "Role assignment edge source must NOT appear in depends_on");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Web App template: type-specific properties
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_WebApp_ShowsKindOsHostname()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/app", "my-app", "microsoft.web/sites", $$"""
            {
              "kind": "app",
              "reserved": false,
              "defaultHostName": "my-app.azurewebsites.net",
              "serverFarmId": "/sub/rg/plan",
              "siteConfig": {}
            }
            """);

        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "app",                       "kind should be rendered");
        StringAssert.Contains(result, "Windows",                   "OS should be Windows (reserved=false)");
        StringAssert.Contains(result, "my-app.azurewebsites.net",  "hostname should be rendered");
    }

    [TestMethod]
    public void Render_WebApp_Linux_ShowsLinuxOS()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/app", "linux-app", "microsoft.web/sites", """
            { "reserved": true, "kind": "app,linux" }
            """);
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "Linux", "Should detect Linux from reserved=true");
    }

    [TestMethod]
    public void Render_WebApp_KindAndOsInFrontmatter()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/app", "my-app", "microsoft.web/sites", """
            { "kind": "app", "reserved": false, "defaultHostName": "my-app.azurewebsites.net" }
            """);
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        var fmEnd = result.IndexOf("\n---", 3);
        var fm    = result[..fmEnd];
        StringAssert.Contains(fm, "kind",     "kind must appear in frontmatter");
        StringAssert.Contains(fm, "os",       "os must appear in frontmatter");
        StringAssert.Contains(fm, "hostname", "hostname must appear in frontmatter");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AFD Profile: tier in frontmatter and body
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_AfdProfile_ShowsSkuTier()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/afd", "afd-prod", "microsoft.cdn/profiles", """
            { "sku": { "name": "Premium_AzureFrontDoor" } }
            """);
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "Premium_AzureFrontDoor", "SKU name should appear in rendered output");
    }

    [TestMethod]
    public void Render_AfdProfile_SkuInFrontmatter()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/afd", "afd-prod", "microsoft.cdn/profiles", """
            { "sku": { "name": "Standard_AzureFrontDoor" } }
            """);
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        var fmEnd = result.IndexOf("\n---", 3);
        var fm    = result[..fmEnd];
        StringAssert.Contains(fm, "Standard_AzureFrontDoor", "SKU tier must be in frontmatter");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AFD Endpoint: hostname in config
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_AfdEndpoint_ShowsHostname()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/afd/endpoints/ep", "ep", "microsoft.cdn/profiles/afdendpoints", """
            { "hostName": "ep.z01.azurefd.net", "enabledState": "Enabled" }
            """);
        var result = RenderFull(engine,node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "ep.z01.azurefd.net", "hostname must appear in rendered output");
        StringAssert.Contains(result, "Enabled",            "enabledState must appear in rendered output");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Edges / wiki links
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_EdgeWikiLinks_AppearsInBody()
    {
        var engine  = new VaultTemplateEngine();
        var node    = MakeNode("/sub/rg/res", "res", "microsoft.network/publicipaddresses");
        var inEdge  = new TenantEdge { FromId = "/sub/rg/agw", ToId = node.ResourceId, Label = "frontend ip" };

        var result  = RenderFull(engine,node, [inEdge], [], [],
            id => id == "/sub/rg/agw" ? "[[infrastructure/sub/rg/agw|agw]]" : $"`{id}`");

        StringAssert.Contains(result, "[[infrastructure/sub/rg/agw|agw]]",
            "Edge wiki link must appear in rendered body");
    }

    [TestMethod]
    public void Render_DependencyIdsAreSortedInFrontmatter()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode("/sub/rg/agw", "agw", "microsoft.network/applicationgateways");
        var edges  = new TenantEdge[]
        {
            new() { FromId = "/sub/rg/pip-z", ToId = node.ResourceId, Label = "frontend ip" },
            new() { FromId = "/sub/rg/pip-a", ToId = node.ResourceId, Label = "frontend ip" },
        };

        var result = RenderFull(engine,node, edges, [], [], id => $"[[{id}]]");

        var fmEnd  = result.IndexOf("\n---", 3);
        var fm     = result[..fmEnd];
        var aIdx   = fm.IndexOf("pip-a", StringComparison.Ordinal);
        var zIdx   = fm.IndexOf("pip-z", StringComparison.Ordinal);
        Assert.IsTrue(aIdx < zIdx, "depends_on_inbound must be sorted alphabetically (pip-a before pip-z)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // All known types render without crash (smoke test)
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("microsoft.network/applicationgateways")]
    [DataRow("microsoft.network/frontdoors")]
    [DataRow("microsoft.cdn/profiles")]
    [DataRow("microsoft.cdn/profiles/afdendpoints")]
    [DataRow("microsoft.compute/virtualmachines")]
    [DataRow("microsoft.compute/virtualmachinescalesets")]
    [DataRow("microsoft.network/virtualnetworks")]
    [DataRow("microsoft.containerservice/managedclusters")]
    [DataRow("microsoft.app/containerapps")]
    [DataRow("microsoft.web/sites")]
    [DataRow("microsoft.web/serverfarms")]
    [DataRow("microsoft.containerregistry/registries")]
    [DataRow("microsoft.network/loadbalancers")]
    [DataRow("microsoft.network/publicipaddresses")]
    [DataRow("microsoft.network/privateendpoints")]
    [DataRow("microsoft.storage/storageaccounts")]
    [DataRow("microsoft.keyvault/vaults")]
    [DataRow("microsoft.network/networksecuritygroups")]
    [DataRow("microsoft.network/dnszones")]
    [DataRow("microsoft.compute/disks")]           // generic fallback
    [DataRow("microsoft.compute/snapshots")]        // generic fallback
    [DataRow("microsoft.insights/components")]      // generic fallback
    [DataRow("microsoft.some/completelyfaketype")]  // generic fallback
    [DataRow("microsoft.resources/resourcegroups")]
    [DataRow("microsoft.insights/actiongroups")]
    [DataRow("microsoft.network/networkwatchers")]
    public void Render_KnownAndUnknownTypes_DoNotThrow(string resourceType)
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNode($"/sub/rg/{resourceType}", resourceType.Split('/').Last(), resourceType);

        var result = RenderFull(engine,node, [], [], [], id => $"`{id}`");

        Assert.IsFalse(string.IsNullOrWhiteSpace(result), $"Render must produce output for type: {resourceType}");
        Assert.IsTrue(result.Contains("---"),              $"Render must produce frontmatter for type: {resourceType}");
        Assert.IsTrue(result.Contains("# "),               $"Render must produce an H1 heading for type: {resourceType}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Template selection: generic fallback vs type-specific
    //
    // The generic template (_generic.sbn) uniquely emits "## ℹ️ Details".
    // Every type-specific template uses a different heading (## ℹ️ Configuration,
    // ## 🌐 DNS Records, ## 🔒 Private Link Target, etc.) — never "## ℹ️ Details".
    // These tests verify that each known type uses its own template and that the
    // generic fallback is only used for unmapped types.
    // ─────────────────────────────────────────────────────────────────────────

    private const string GenericHeading     = "## ℹ️ Details";
    private const string ConfigHeading      = "## ℹ️ Configuration";

    [TestMethod]
    public void GenericTemplate_ShowsDetailsHeading_ForUnknownType()
    {
        var engine = new VaultTemplateEngine();
        var result = RenderFull(engine,
            MakeNode("/sub/rg/res", "res", "microsoft.some/completelyfaketype"),
            [], [], [], id => $"`{id}`");

        StringAssert.Contains(result, GenericHeading,
            "Unknown type must use the generic fallback template (shows '## ℹ️ Details')");
    }

    [TestMethod]
    public void GenericTemplate_NotUsed_ForKnownTypesWithDedicatedTemplates()
    {
        var engine = new VaultTemplateEngine();
        // Types whose generic-fallback use IS intentional (no dedicated .sbn file):
        var intentionalFallbacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft.compute/disks",
            "microsoft.compute/snapshots",
            "microsoft.compute/restorepointcollections",
        };

        // Types that HAVE a dedicated template — must NOT fall back to generic.
        var dedicatedTemplateTypes = new[]
        {
            "microsoft.web/sites",
            "microsoft.web/serverfarms",
            "microsoft.cdn/profiles",
            "microsoft.cdn/profiles/afdendpoints",
            "microsoft.compute/virtualmachines",
            "microsoft.network/applicationgateways",
            "microsoft.network/frontdoors",
            "microsoft.network/loadbalancers",
            "microsoft.network/publicipaddresses",
            "microsoft.network/privateendpoints",
            "microsoft.network/virtualnetworks",
            "microsoft.network/networksecuritygroups",
            "microsoft.network/dnszones",
            "microsoft.storage/storageaccounts",
            "microsoft.keyvault/vaults",
            "microsoft.containerregistry/registries",
            "microsoft.containerregistry/registries/repositories",
            "microsoft.containerservice/managedclusters",
            "microsoft.app/containerapps",
            "microsoft.resources/resourcegroups",
            "microsoft.insights/actiongroups",
            "microsoft.network/networkwatchers",
        };

        var failures = new List<string>();
        foreach (var type in dedicatedTemplateTypes)
        {
            var result = RenderFull(engine,
                MakeNode($"/sub/rg/{type.Split('/').Last()}", type.Split('/').Last(), type),
                [], [], [], id => $"`{id}`");

            if (result.Contains(GenericHeading))
                failures.Add($"{type} — output contains generic '## ℹ️ Details' (template not loading or crashing back to generic)");
        }

        Assert.AreEqual(0, failures.Count,
            $"{failures.Count} type(s) fell back to the generic template:\n" + string.Join("\n", failures));
    }

    [TestMethod]
    public void WebApp_UsesCustomTemplate_ShowsConfigurationHeading()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/app", "my-app", "microsoft.web/sites",
            """{"kind":"app","reserved":false,"defaultHostName":"my-app.azurewebsites.net"}""");
        var result = RenderFull(engine,node, [], [], [], id => $"`{id}`");

        // Must NOT use generic heading
        Assert.IsFalse(result.Contains(GenericHeading),
            $"microsoft.web/sites must not fall back to generic template. Output:\n{result}");
        // Must use the type-specific heading
        StringAssert.Contains(result, ConfigHeading,
            "microsoft.web/sites must use its dedicated template with '## ℹ️ Configuration'");
    }

    [TestMethod]
    [DataRow("microsoft.web/sites",                               ConfigHeading)]
    [DataRow("microsoft.web/serverfarms",                         ConfigHeading)]
    [DataRow("microsoft.cdn/profiles",                            ConfigHeading)]
    [DataRow("microsoft.cdn/profiles/afdendpoints",               ConfigHeading)]
    [DataRow("microsoft.compute/virtualmachines",                 ConfigHeading)]
    [DataRow("microsoft.containerservice/managedclusters",        ConfigHeading)]
    [DataRow("microsoft.containerregistry/registries",            ConfigHeading)]
    [DataRow("microsoft.keyvault/vaults",                         ConfigHeading)]
    [DataRow("microsoft.network/applicationgateways",             ConfigHeading)]
    [DataRow("microsoft.storage/storageaccounts",                 ConfigHeading)]
    [DataRow("microsoft.network/loadbalancers",                   "## 🌐 Frontend")]
    [DataRow("microsoft.network/privateendpoints",                "## 🔒 Private Link Target")]
    [DataRow("microsoft.network/virtualnetworks",                 "## 🌐 Address Space")]
    [DataRow("microsoft.network/publicipaddresses",               "## 🌐 Address")]
    [DataRow("microsoft.network/frontdoors",                      "## 🌐 Frontend Endpoints")]
    [DataRow("microsoft.network/dnszones",                        "## 🌐 DNS Records")]
    [DataRow("microsoft.containerregistry/registries/repositories","## 🖥️ Deployed To")]
    [DataRow("microsoft.app/containerapps",                       ConfigHeading)]
    [DataRow("microsoft.resources/resourcegroups",                "## ℹ️ Resource Group Details")]
    [DataRow("microsoft.insights/actiongroups",                   "## ℹ️ Action Group Details")]
    [DataRow("microsoft.network/networkwatchers",                 "## ℹ️ Network Watcher Details")]
    public void TypeSpecificTemplate_ContainsExpectedSection(string resourceType, string expectedHeading)
    {
        var engine = new VaultTemplateEngine();
        var result = RenderFull(engine,
            MakeNode($"/sub/rg/{resourceType.Split('/').Last()}", resourceType.Split('/').Last(), resourceType),
            [], [], [], id => $"`{id}`");

        // Must not use generic
        Assert.IsFalse(result.Contains(GenericHeading),
            $"{resourceType} must not fall back to generic (contains '## ℹ️ Details'). Full output:\n{result[..Math.Min(500, result.Length)]}");

        // Must use its own section
        StringAssert.Contains(result, expectedHeading,
            $"{resourceType} expected to contain '{expectedHeading}' from its dedicated template. Actual body:\n{result[..Math.Min(500, result.Length)]}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Resource Group template
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_ResourceGroup_ShowsProvisioningState()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/subscriptions/sub-1/resourceGroups/NetworkWatcherRG", "NetworkWatcherRG",
            "microsoft.resources/resourcegroups", """{"provisioningState":"Succeeded"}""");
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "Succeeded");
        StringAssert.Contains(result, "Resources in this Group");
    }

    [TestMethod]
    public void Render_ResourceGroup_NoContainmentEdges_ShowsHonestEmptyState()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg1", "rg1", "microsoft.resources/resourcegroups", """{"provisioningState":"Succeeded"}""");
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "No resource containment relationships are modeled yet");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Network Watcher template
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_NetworkWatcher_ShowsProvisioningState_NoRunningOperations()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/nw", "NetworkWatcher_westeurope", "microsoft.network/networkwatchers",
            """{"provisioningState":"Succeeded","runningOperationIds":[]}""");
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "Succeeded");
        StringAssert.Contains(result, "No running operations.");
    }

    [TestMethod]
    public void Render_NetworkWatcher_ListsRunningOperations()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/nw", "nw", "microsoft.network/networkwatchers",
            """{"provisioningState":"Succeeded","runningOperationIds":["op-1","op-2"]}""");
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "op-1");
        StringAssert.Contains(result, "op-2");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Action Group template
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_ActionGroup_RendersEmailReceiverTable()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/ag", "Action-Service-Health", "microsoft.insights/actiongroups", """
            {
                "groupShortName": "SH", "enabled": true,
                "emailReceivers": [{"name":"PrimaryEmail","emailAddress":"user@example.com","useCommonAlertSchema":true,"status":"Enabled"}],
                "smsReceivers": [], "webhookReceivers": [], "eventHubReceivers": [], "itsmReceivers": [],
                "azureAppPushReceivers": [], "automationRunbookReceivers": [], "voiceReceivers": [],
                "logicAppReceivers": [], "azureFunctionReceivers": [], "armRoleReceivers": []
            }
            """);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "SH");
        StringAssert.Contains(result, "PrimaryEmail");
        StringAssert.Contains(result, "user@example.com");
        StringAssert.Contains(result, "### Email (1)");
    }

    [TestMethod]
    public void Render_ActionGroup_HandlesAllReceiverTypesEmpty()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/ag", "ag", "microsoft.insights/actiongroups", """
            {"groupShortName":"SH","enabled":false}
            """);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "No notification receivers configured.");
    }

    [TestMethod]
    public void Render_ActionGroup_RendersWebhookAndSmsReceivers()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/ag", "ag", "microsoft.insights/actiongroups", """
            {
                "groupShortName": "SH", "enabled": true,
                "smsReceivers": [{"name":"OnCall","countryCode":"1","phoneNumber":"5551234","status":"Enabled"}],
                "webhookReceivers": [{"name":"Hook","serviceUri":"https://example.com/hook","useCommonAlertSchema":true}]
            }
            """);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "OnCall");
        StringAssert.Contains(result, "5551234");
        StringAssert.Contains(result, "https://example.com/hook");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Storage Account template enrichment (network ACLs, encryption, endpoints)
    // ─────────────────────────────────────────────────────────────────────────

    private const string StorageAccountFullProps = """
        {
            "allowCrossTenantDelegationSas": false,
            "allowCrossTenantReplication": false,
            "minimumTlsVersion": "TLS1_2",
            "allowBlobPublicAccess": false,
            "supportsHttpsTrafficOnly": true,
            "networkAcls": {"bypass":"None","virtualNetworkRules":[],"ipRules":[],"defaultAction":"Allow"},
            "encryption": {"services":{"file":{"keyType":"Account","enabled":true,"lastEnabledTime":"2026-05-25T09:05:46.011Z"},"blob":{"keyType":"Account","enabled":true,"lastEnabledTime":"2026-05-25T09:05:46.011Z"}},"keySource":"Microsoft.Storage"},
            "accessTier": "Hot",
            "provisioningState": "Succeeded",
            "creationTime": "2026-05-25T09:05:45.578Z",
            "primaryEndpoints": {
                "dfs": "https://acct.dfs.core.windows.net/",
                "web": "https://acct.z6.web.core.windows.net/",
                "blob": "https://acct.blob.core.windows.net/",
                "queue": "https://acct.queue.core.windows.net/",
                "table": "https://acct.table.core.windows.net/",
                "file": "https://acct.file.core.windows.net/"
            },
            "primaryLocation": "westeurope",
            "statusOfPrimary": "available"
        }
        """;

    [TestMethod]
    public void Render_StorageAccount_ShowsAllSixEndpoints()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/acct", "acct", "microsoft.storage/storageaccounts", StorageAccountFullProps);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "acct.dfs.core.windows.net");
        StringAssert.Contains(result, "acct.z6.web.core.windows.net");
        StringAssert.Contains(result, "acct.blob.core.windows.net");
        StringAssert.Contains(result, "acct.queue.core.windows.net");
        StringAssert.Contains(result, "acct.table.core.windows.net");
        StringAssert.Contains(result, "acct.file.core.windows.net");
    }

    [TestMethod]
    public void Render_StorageAccount_ShowsNetworkAclsDefaultAction()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/acct", "acct", "microsoft.storage/storageaccounts", """
            {"networkAcls": {"bypass":"AzureServices","defaultAction":"Deny","ipRules":[{"value":"1.2.3.4","action":"Allow"}],"virtualNetworkRules":[]}}
            """);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "Deny");
        StringAssert.Contains(result, "AzureServices");
        StringAssert.Contains(result, "1.2.3.4");
    }

    [TestMethod]
    public void Render_StorageAccount_ShowsEncryptionServices()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/acct", "acct", "microsoft.storage/storageaccounts", StorageAccountFullProps);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "Microsoft.Storage");
        StringAssert.Contains(result, "Account");
    }

    [TestMethod]
    public void Render_StorageAccount_ShowsSecurityFlags()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/acct", "acct", "microsoft.storage/storageaccounts", StorageAccountFullProps);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "TLS1_2");
        StringAssert.Contains(result, "Allow Blob Public Access");
        StringAssert.Contains(result, "Allow Cross-Tenant Replication");
    }

    [TestMethod]
    public void Render_StorageAccount_NewExtraFmKeysAppearInFrontmatter()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/acct", "acct", "microsoft.storage/storageaccounts", StorageAccountFullProps);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        var fmEnd = result.IndexOf("\n---", 3);
        var fm    = result[..fmEnd];
        StringAssert.Contains(fm, "minimum_tls_version");
        StringAssert.Contains(fm, "https_only");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Generic fallback template — property flattening
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Render_Generic_FlattensScalarProperties()
    {
        var engine = new VaultTemplateEngine();
        // Must be a type with neither a hand-crafted nor a portal-fallback template, or this
        // would exercise that template instead of _generic.sbn.
        var node   = MakeNodeWithProps("/sub/rg/res", "res", "microsoft.some.faketype/widgets",
            """{"sslPort":6380,"enableNonSslPort":false,"redisVersion":"6.0"}""");
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "## 📄 Properties");
        StringAssert.Contains(result, "sslPort");
        StringAssert.Contains(result, "6380");
        StringAssert.Contains(result, "redisVersion");
        StringAssert.Contains(result, "6.0");
    }

    [TestMethod]
    public void Render_Generic_ListsComplexPropertiesWithoutDumpingContent()
    {
        var engine = new VaultTemplateEngine();
        // Must be a type with neither a hand-crafted nor a portal-fallback template, or this
        // would exercise that template instead of _generic.sbn.
        var node   = MakeNodeWithProps("/sub/rg/res", "res", "microsoft.some.faketype/widgets", """
            {"redisConfiguration": {"maxmemory-policy": "allkeys-lru", "secretKeyThatShouldNotLeak": "should-not-appear-in-body"}}
            """);
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        // Front matter is deliberately lossless (the raw value legitimately appears there) —
        // only the Scriban-rendered *body* must avoid dumping nested content.
        var body = result.Split(["\n---\n"], 2, StringSplitOptions.None) is [_, var b] ? b : result;

        StringAssert.Contains(body, "redisConfiguration");
        StringAssert.Contains(body, "(object, 2)");
        Assert.IsFalse(body.Contains("should-not-appear-in-body"),
            "Nested object content must not be dumped into the body — only the key/shape hint.");
    }

    [TestMethod]
    public void Render_Generic_NoScalarProperties_ShowsEmptyState()
    {
        var engine = new VaultTemplateEngine();
        var node   = MakeNodeWithProps("/sub/rg/res", "res", "microsoft.some/emptytype", "{}");
        var result = RenderFull(engine, node, [], [], [], id => $"[[{id}]]");

        StringAssert.Contains(result, "No scalar properties.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // No silent template-error fallback for new/edited templates
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("microsoft.resources/resourcegroups")]
    [DataRow("microsoft.insights/actiongroups")]
    [DataRow("microsoft.network/networkwatchers")]
    [DataRow("microsoft.storage/storageaccounts")]
    [DataRow("microsoft.some/completelyfaketype")]
    public void Render_NewOrEditedTemplates_ReportNoTemplateErrors(string resourceType)
    {
        var messages = new List<string>();
        var engine   = new VaultTemplateEngine(new CapturingReporter(messages));
        var node     = MakeNodeWithProps($"/sub/rg/res", "res", resourceType, StorageAccountFullProps);

        engine.Render(node, [], [], [], id => $"[[{id}]]");

        Assert.IsFalse(messages.Any(m => m.Contains("Template errors") || m.Contains("Template render failed")),
            $"Unexpected template error(s) for {resourceType}: {string.Join("; ", messages)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VaultWriter end-to-end: no GUID role-assignment files, enriched output
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void WriteAll_RoleAssignmentNodesDoNotGenerateFiles()
    {
        var engine   = new VaultTemplateEngine();
        var writer   = new VaultWriter(engine);
        var graph    = new TenantGraph();
        var subNames = new Dictionary<string, string> { ["sub-1"] = "my-sub" };

        var kvId   = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv1";
        var roleId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Authorization/roleAssignments/aaa-guid";

        graph.AddNode(MakeNode(kvId,   "kv1",      "microsoft.keyvault/vaults",             "sub-1", "rg"));
        graph.AddNode(MakeNode(roleId, "aaa-guid", "microsoft.authorization/roleassignments","sub-1", "rg"));
        // Role assignment edge from role node → KV
        graph.AddEdge(roleId, kvId, "role:Owner");

        var outputDir = Path.Combine(Path.GetTempPath(), $"vault-ra-{Guid.NewGuid():N}");
        try
        {
            writer.WriteAll(graph, subNames, outputDir);

            var files = Directory.GetFiles(outputDir, "*.md", SearchOption.AllDirectories)
                                  .Where(f => !Path.GetFileName(f).StartsWith("_summary", StringComparison.OrdinalIgnoreCase))
                                  .ToArray();
            // Only the KV file should exist; no GUID-named role assignment file
            Assert.AreEqual(1, files.Length, $"Expected exactly 1 resource file but found: {string.Join(", ", files.Select(Path.GetFileName))}");
            Assert.IsTrue(files[0].EndsWith("kv1.md"), "The only file should be the Key Vault file");
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [TestMethod]
    public void WriteAll_RoleAssignmentAppearsInTargetResourceFile()
    {
        var engine   = new VaultTemplateEngine();
        var writer   = new VaultWriter(engine);
        var graph    = new TenantGraph();
        var subNames = new Dictionary<string, string> { ["sub-1"] = "my-sub" };

        var kvId   = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv1";
        var roleId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Authorization/roleAssignments/aaa-guid";
        var kvNode = MakeNode(kvId, "kv1", "microsoft.keyvault/vaults", "sub-1", "rg");

        // Synthetic role assignment node with principalId in properties
        var roleNode = MakeNodeWithProps(roleId, "aaa-guid", "microsoft.authorization/roleassignments",
            """{"principalId":"owner-principal-id"}""", "sub-1", "rg");

        graph.AddNode(kvNode);
        graph.AddNode(roleNode);
        graph.AddEdge(roleId, kvId, "role:Owner");

        var outputDir = Path.Combine(Path.GetTempPath(), $"vault-ra2-{Guid.NewGuid():N}");
        try
        {
            writer.WriteAll(graph, subNames, outputDir);

            var kvFile  = Path.Combine(outputDir, "infrastructure", "my-sub", "rg", "kv1.md");
            Assert.IsTrue(File.Exists(kvFile), "Key Vault file must exist");
            var content = File.ReadAllText(kvFile);

            StringAssert.Contains(content, "Owner",                "Role name must appear in KV file");
            StringAssert.Contains(content, "owner-principal-id",   "Principal ID must appear in KV file");
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [TestMethod]
    public void WriteAll_Summary_LabelsPortalFallbackTemplateDistinctlyFromDedicatedAndGeneric()
    {
        var engine   = new VaultTemplateEngine();
        var writer   = new VaultWriter(engine);
        var graph    = new TenantGraph();
        var subNames = new Dictionary<string, string> { ["sub-1"] = "my-sub" };

        // Dedicated: has a hand-crafted Rendering/Templates template.
        var kvId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv1";
        graph.AddNode(MakeNode(kvId, "kv1", "microsoft.keyvault/vaults"));

        // Portal fallback: only a mirrored ARDL template, no hand-crafted one.
        var natId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/natGateways/nat1";
        graph.AddNode(MakeNode(natId, "nat1", "microsoft.network/natgateways"));

        // Generic: neither tier has a template for this made-up type.
        var fakeId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Some.Faketype/widgets/w1";
        graph.AddNode(MakeNode(fakeId, "w1", "microsoft.some.faketype/widgets"));

        var outputDir = Path.Combine(Path.GetTempPath(), $"vault-summary-{Guid.NewGuid():N}");
        try
        {
            writer.WriteAll(graph, subNames, outputDir);

            var summary = File.ReadAllText(Path.Combine(outputDir, "_summary.md"));

            StringAssert.Contains(summary, "`microsoft_keyvault_vaults`",
                "Dedicated-template types must show their key with no fallback qualifier");
            Assert.IsFalse(summary.Contains("microsoft_keyvault_vaults` (portal fallback)"),
                "A type with a hand-crafted template must not be reported as portal fallback");

            StringAssert.Contains(summary, "`microsoft_network_natgateways` (portal fallback)",
                "A type with only a mirrored ARDL template must be labeled portal fallback");

            StringAssert.Contains(summary, "`_generic` (fallback)",
                "A type with neither tier must still show the plain generic fallback label");
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [TestMethod]
    public void WriteAll_WebApp_FrontmatterContainsEnrichedProps()
    {
        var engine   = new VaultTemplateEngine();
        var writer   = new VaultWriter(engine);
        var graph    = new TenantGraph();
        var subNames = new Dictionary<string, string> { ["sub-1"] = "my-sub" };

        var appId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Web/sites/app1";
        graph.AddNode(MakeNodeWithProps(appId, "app1", "microsoft.web/sites",
            """{"kind":"app","reserved":false,"defaultHostName":"app1.azurewebsites.net"}""",
            "sub-1", "rg"));

        var outputDir = Path.Combine(Path.GetTempPath(), $"vault-webapp-{Guid.NewGuid():N}");
        try
        {
            writer.WriteAll(graph, subNames, outputDir);

            var appFile = Path.Combine(outputDir, "infrastructure", "my-sub", "rg", "app1.md");
            Assert.IsTrue(File.Exists(appFile));
            var content = File.ReadAllText(appFile);

            // Frontmatter
            var fmEnd = content.IndexOf("\n---", 3);
            var fm    = content[..fmEnd];
            StringAssert.Contains(fm, "kind",     "kind must be in frontmatter");
            StringAssert.Contains(fm, "os",       "os must be in frontmatter");
            StringAssert.Contains(fm, "hostname", "hostname must be in frontmatter");

            // Body also has those properties
            var body = content[(fmEnd + 4)..];
            StringAssert.Contains(body, "app1.azurewebsites.net", "hostname must be in body");
            StringAssert.Contains(body, "Windows",                "OS must be in body");
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Composes front-matter + body the same way <see cref="VaultWriter"/> does, so tests can
    /// assert on the complete document. Relationship names/types are unresolved (no graph here).
    /// </summary>
    private static string RenderFull(
        VaultTemplateEngine       engine,
        TenantNode                node,
        IReadOnlyList<TenantEdge> inbound,
        IReadOnlyList<TenantEdge> outbound,
        List<RoleAssignmentInfo>  roles,
        Func<string, string>      wikiLink)
    {
        var note = engine.Render(node, inbound, outbound, roles, wikiLink);

        static bool IsRoleEdge(TenantEdge e) => e.Label.StartsWith("role:", StringComparison.OrdinalIgnoreCase);
        var relationships = inbound.Where(e => !IsRoleEdge(e))
                .Select(e => new VaultRelationship(e.FromId, null, null, "inbound", e.Label))
            .Concat(outbound.Where(e => !IsRoleEdge(e))
                .Select(e => new VaultRelationship(e.ToId, null, null, "outbound", e.Label)))
            .OrderBy(r => r.Direction, StringComparer.Ordinal)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roleAssignments = roles
            .Select(r => new VaultRoleAssignment($"/role-assignments/{r.PrincipalId}", r.Role, r.PrincipalId, default))
            .ToList();

        var ctx = new FrontMatterContext(
            node, node.SubscriptionId, relationships, roleAssignments,
            VaultTemplateEngine.ExtractVersion(node), note.ExtraFlatKeys);

        return new FrontMatterSerializer("0.0.0-test").Serialize(ctx) + note.Body;
    }

    private static TenantNode MakeNode(
        string resourceId,
        string name,
        string type,
        string subscriptionId = "sub-1",
        string resourceGroup  = "rg")
        => new()
        {
            ResourceId     = resourceId,
            Name           = name,
            Type           = type,
            SubscriptionId = subscriptionId,
            ResourceGroup  = resourceGroup,
            Location       = "westeurope",
        };

    private static TenantNode MakeNodeWithTags(
        string resourceId,
        string name,
        string type,
        Dictionary<string, string> tags,
        string subscriptionId = "sub-1",
        string resourceGroup  = "rg")
        => new()
        {
            ResourceId     = resourceId,
            Name           = name,
            Type           = type,
            SubscriptionId = subscriptionId,
            ResourceGroup  = resourceGroup,
            Location       = "westeurope",
            Tags           = tags,
        };

    private static TenantNode MakeNodeWithProps(
        string resourceId,
        string name,
        string type,
        string propsJson,
        string subscriptionId = "sub-1",
        string resourceGroup  = "rg")
    {
        using var doc   = JsonDocument.Parse(propsJson);
        var       props = doc.RootElement.Clone();
        return new()
        {
            ResourceId     = resourceId,
            Name           = name,
            Type           = type,
            SubscriptionId = subscriptionId,
            ResourceGroup  = resourceGroup,
            Location       = "westeurope",
            Properties     = props,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test helpers
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class CapturingReporter(List<string> messages) : AzToMarkdown.Core.Abstractions.IProgressReporter
    {
        public void Report(string message, AzToMarkdown.Core.Abstractions.ProgressLevel level = AzToMarkdown.Core.Abstractions.ProgressLevel.Info)
            => messages.Add($"[{level}] {message}");
    }
}
