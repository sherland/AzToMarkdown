using System.Text.Json;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Vault;
using YamlDotNet.RepresentationModel;

namespace AzToMarkdown.Tests;

/// <summary>
/// Unit tests for the schema-v1 lossless front-matter serializer:
/// YAML validity, value parity (round-trip), YAML edge cases (norway problem, numeric-looking
/// strings, multiline/special characters), and deterministic golden output.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class FrontMatterSerializerTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static TenantNode MakeNode(
        string propertiesJson = "{}",
        IReadOnlyDictionary<string, string>? tags = null,
        string type = "microsoft.network/networkinterfaces",
        string name = "my-nic")
    {
        using var doc = JsonDocument.Parse(propertiesJson);
        return new TenantNode
        {
            ResourceId     = $"/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/{type}/{name}",
            Name           = name,
            Type           = type,
            SubscriptionId = "00000000-0000-0000-0000-000000000000",
            ResourceGroup  = "rg-test",
            Location       = "norwayeast",
            Properties     = doc.RootElement.Clone(),
            Tags           = tags ?? new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static FrontMatterContext MakeContext(TenantNode node) =>
        new(node, "Test Subscription", [], [], Version: null, ExtraFlatKeys: []);

    private static FrontMatterSerializer MakeSerializer() =>
        new(aztomarkdownVersion: "0.0.0-test");

    /// <summary>Extracts and parses the front-matter YAML from a serialized block.</summary>
    private static YamlMappingNode ParseFrontMatter(string serialized)
    {
        Assert.IsTrue(serialized.StartsWith("---\n", StringComparison.Ordinal), "front-matter must start with ---");
        var end = serialized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        Assert.IsTrue(end > 0, "front-matter must end with ---");
        var yaml = serialized[4..end];
        var root = YamlJsonConverter.ParseDocument(yaml);
        Assert.IsNotNull(root, "front-matter must parse as a YAML mapping");
        return root;
    }

    private static YamlNode GetPath(YamlMappingNode root, params string[] path)
    {
        YamlNode cur = root;
        foreach (var key in path)
        {
            cur = ((YamlMappingNode)cur).Children[new YamlScalarNode(key)];
        }
        return cur;
    }

    /// <summary>Serializes a node, reparses the YAML, and returns azure_metadata.properties as JSON.</summary>
    private static JsonElement RoundTripProperties(TenantNode node)
    {
        var serialized = MakeSerializer().Serialize(MakeContext(node));
        var root       = ParseFrontMatter(serialized);
        var propsYaml  = GetPath(root, "azure_metadata", "properties");
        var json       = YamlJsonConverter.ToJson(propsYaml);
        Assert.IsNotNull(json);
        return JsonDocument.Parse(json.ToJsonString()).RootElement.Clone();
    }

    private static void AssertLossless(string propertiesJson)
    {
        using var original = JsonDocument.Parse(propertiesJson);
        var roundTripped = RoundTripProperties(MakeNode(propertiesJson));
        Assert.IsTrue(
            YamlJsonConverter.JsonDeepEquals(original.RootElement, roundTripped, out var diff),
            $"round-trip lost data at: {diff}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Round-trip value parity
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void RoundTrip_ValueParity_DeepNesting()
    {
        // NIC-shaped fixture, 6 levels deep with arrays of objects
        AssertLossless("""
        {
          "provisioningState": "Succeeded",
          "ipConfigurations": [
            {
              "name": "ipconfig1",
              "properties": {
                "privateIPAddress": "10.0.0.4",
                "primary": true,
                "subnet": { "id": "/subscriptions/x/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet/subnets/snet" },
                "loadBalancerBackendAddressPools": [
                  { "id": "/subscriptions/x/pools/p1", "properties": { "nested": { "deep": { "deeper": [1, 2, 3] } } } }
                ]
              }
            }
          ],
          "dnsSettings": { "dnsServers": [], "appliedDnsServers": [] },
          "enableAcceleratedNetworking": false,
          "virtualMachine": { "id": "/subscriptions/x/vms/vm1" }
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_NorwayProblem_StringsStayStrings()
    {
        AssertLossless("""
        {
          "country": "no",
          "confirm": "yes",
          "toggle": "on",
          "upper": "NO",
          "tilde": "~",
          "nullWord": "null",
          "trueWord": "true",
          "offWord": "off"
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_NumericLookingStrings_StayStrings()
    {
        AssertLossless("""
        {
          "version": "1.0",
          "padded": "007",
          "exponent": "1e5",
          "hexish": "0x1F",
          "negative": "-42",
          "runtime": "DOTNETCORE|8.0"
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_Numbers_PreserveRawFormat()
    {
        AssertLossless("""
        {
          "int": 1,
          "decimal": 1.0,
          "negative": -0.5,
          "exponent": 1e10,
          "int64max": 9223372036854775807,
          "zero": 0,
          "port": 443
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_EmptyContainers_And_Nulls()
    {
        AssertLossless("""
        {
          "emptyObject": {},
          "emptyArray": [],
          "nullValue": null,
          "emptyString": "",
          "nested": { "alsoEmpty": {}, "alsoEmptyArr": [] }
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_SpecialCharacters()
    {
        AssertLossless("""
        {
          "multiline": "line1\nline2\nline3",
          "tabs": "a\tb",
          "quotes": "she said \"hi\" and 'bye'",
          "hash": "# not a comment",
          "colonSpace": "key: value lookalike",
          "unicode": "æøå 日本語 🎉",
          "leadingSpace": "  padded  ",
          "backslash": "C:\\Windows\\System32",
          "longId": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/a-very-long-resource-group-name-with-many-characters/providers/Microsoft.Network/applicationGateways/an-application-gateway-with-an-extremely-long-name-that-must-not-be-line-folded-by-the-yaml-emitter",
          "dashLeading": "- looks like a list item",
          "bracketLeading": "[not, an, array]",
          "braceLeading": "{not: an object}"
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_OdataTypeKeys_And_ArbitraryDeepJson()
    {
        // metric-alert / portal-dashboard shaped payloads (keys with @, $, dots; deep arbitrary JSON)
        AssertLossless("""
        {
          "criteria": {
            "odata.type": "Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria",
            "allOf": [
              { "@odata.id": "x", "metricName": "Percentage CPU", "operator": "GreaterThan", "threshold": 90.0 }
            ]
          },
          "lenses": {
            "0": {
              "order": 0,
              "parts": {
                "0": { "position": { "x": 0, "y": 0, "colSpan": 6, "rowSpan": 4 },
                       "metadata": { "inputs": [], "type": "Extension/HubsExtension/PartType/MarkdownPart",
                                     "settings": { "content": { "settings": { "content": "## Hello\n**bold**" } } } } }
              }
            }
          },
          "$schema": "https://schema.management.azure.com/x.json"
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_NsgSecurityRules_Fixture()
    {
        AssertLossless("""
        {
          "securityRules": [
            {
              "name": "AllowHttps",
              "properties": {
                "protocol": "Tcp",
                "sourcePortRange": "*",
                "destinationPortRange": "443",
                "sourceAddressPrefix": "Internet",
                "destinationAddressPrefixes": ["10.0.0.0/24", "10.0.1.0/24"],
                "access": "Allow",
                "priority": 100,
                "direction": "Inbound"
              }
            }
          ],
          "defaultSecurityRules": []
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_WebSiteSiteConfig_Fixture()
    {
        AssertLossless("""
        {
          "defaultHostName": "myapp.azurewebsites.net",
          "hostNames": ["myapp.azurewebsites.net", "www.example.com"],
          "serverFarmId": "/subscriptions/x/serverfarms/plan1",
          "reserved": true,
          "siteConfig": {
            "linuxFxVersion": "DOTNETCORE|8.0",
            "netFrameworkVersion": "v4.0",
            "alwaysOn": true,
            "numberOfWorkers": null,
            "appSettings": null
          },
          "virtualNetworkSubnetId": null
        }
        """);
    }

    [TestMethod]
    public void RoundTrip_VmExtensionSettings_Fixture()
    {
        AssertLossless("""
        {
          "publisher": "Microsoft.Azure.ServiceFabric",
          "type": "ServiceFabricNode",
          "typeHandlerVersion": "1.1",
          "autoUpgradeMinorVersion": true,
          "settings": {
            "clusterEndpoint": "https://cluster.norwayeast.cloudapp.azure.com:19080",
            "nodeTypeRef": "nt1vm",
            "durabilityLevel": "Bronze",
            "nicPrefixOverride": "10.0.0.0/24"
          }
        }
        """);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tags
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void RoundTrip_TagsWithSpecialKeys()
    {
        var tags = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["environment"]        = "Production",
            ["cost center"]        = "1234",
            ["owner:team"]         = "platform",
            ["norway"]             = "no",
            ["hidden-link:/x/y"]   = "https://example.com?a=b&c=d",
        };
        var node       = MakeNode(tags: tags);
        var serialized = MakeSerializer().Serialize(MakeContext(node));
        var root       = ParseFrontMatter(serialized);
        var tagsYaml   = (YamlMappingNode)GetPath(root, "azure_metadata", "tags");

        Assert.HasCount(tags.Count, tagsYaml.Children);
        foreach (var (k, v) in tags)
        {
            var val = (YamlScalarNode)tagsYaml.Children[new YamlScalarNode(k)];
            Assert.AreEqual(v, val.Value);
            Assert.AreEqual(YamlDotNet.Core.ScalarStyle.DoubleQuoted, val.Style, $"tag '{k}' value must be quoted");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Envelope / identity / structure
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Serialize_EmitsEnvelopeAndCanonicalIdentity()
    {
        var node       = MakeNode("""{ "ipAddress": "1.2.3.4" }""");
        var serialized = MakeSerializer().Serialize(MakeContext(node));
        var root       = ParseFrontMatter(serialized);

        Assert.AreEqual("1",          ((YamlScalarNode)GetPath(root, "schema_version")).Value);
        Assert.AreEqual("0.0.0-test", ((YamlScalarNode)GetPath(root, "aztomarkdown_version")).Value);
        Assert.AreEqual(node.ResourceId, ((YamlScalarNode)GetPath(root, "id")).Value);
        Assert.AreEqual("my-nic",     ((YamlScalarNode)GetPath(root, "name")).Value);
        Assert.AreEqual("Microsoft.Network/networkInterfaces", ((YamlScalarNode)GetPath(root, "type")).Value);

        Assert.AreEqual(node.ResourceId, ((YamlScalarNode)GetPath(root, "resource", "id")).Value);
        Assert.AreEqual("Microsoft.Network/networkInterfaces", ((YamlScalarNode)GetPath(root, "resource", "type")).Value);
        Assert.AreEqual(node.SubscriptionId, ((YamlScalarNode)GetPath(root, "resource", "subscription_id")).Value);
        Assert.AreEqual("Test Subscription", ((YamlScalarNode)GetPath(root, "resource", "subscription_name")).Value);
        Assert.AreEqual("rg-test",    ((YamlScalarNode)GetPath(root, "resource", "resource_group")).Value);
        Assert.AreEqual("norwayeast", ((YamlScalarNode)GetPath(root, "resource", "location")).Value);
    }

    [TestMethod]
    public void Serialize_Relationships_AndRoleAssignments()
    {
        using var raProps = JsonDocument.Parse("""
            { "roleDefinitionId": "/subscriptions/x/roleDefinitions/abc", "principalId": "p-1", "scope": "/subscriptions/x/rg" }
            """);
        var node = MakeNode();
        var ctx = new FrontMatterContext(
            node,
            "Test Subscription",
            Relationships:
            [
                new VaultRelationship("/subscriptions/x/subnets/snet", "snet", "Microsoft.Network/virtualNetworks/subnets", "outbound", "subnet"),
                new VaultRelationship("/subscriptions/x/vms/vm1", null, null, "inbound", "nic"),
            ],
            RoleAssignments:
            [
                new VaultRoleAssignment("/subscriptions/x/roleAssignments/ra1", "Contributor", "p-1", raProps.RootElement),
            ],
            Version: "1.28.3",
            ExtraFlatKeys: [new("sku", "Standard_v2")]);

        var serialized = MakeSerializer().Serialize(ctx);
        var root       = ParseFrontMatter(serialized);

        var rels = (YamlSequenceNode)GetPath(root, "relationships");
        Assert.HasCount(2, rels.Children);
        var rel0 = (YamlMappingNode)rels.Children[0];
        Assert.AreEqual("snet",     ((YamlScalarNode)rel0.Children[new YamlScalarNode("name")]).Value);
        Assert.AreEqual("outbound", ((YamlScalarNode)rel0.Children[new YamlScalarNode("direction")]).Value);
        Assert.AreEqual("subnet",   ((YamlScalarNode)rel0.Children[new YamlScalarNode("label")]).Value);
        var rel1 = (YamlMappingNode)rels.Children[1];
        Assert.IsFalse(rel1.Children.ContainsKey(new YamlScalarNode("name")), "unknown target name must be omitted");

        var ras = (YamlSequenceNode)GetPath(root, "role_assignments");
        Assert.HasCount(1, ras.Children);
        var ra0 = (YamlMappingNode)ras.Children[0];
        Assert.AreEqual("Contributor", ((YamlScalarNode)ra0.Children[new YamlScalarNode("role")]).Value);
        var raJson = YamlJsonConverter.ToJson(ra0.Children[new YamlScalarNode("properties")]);
        Assert.IsNotNull(raJson);
        Assert.IsTrue(YamlJsonConverter.JsonDeepEquals(
            raProps.RootElement,
            JsonDocument.Parse(raJson.ToJsonString()).RootElement, out var diff), $"role assignment properties lost: {diff}");

        Assert.AreEqual("1.28.3",      ((YamlScalarNode)GetPath(root, "version")).Value);
        Assert.AreEqual("Standard_v2", ((YamlScalarNode)GetPath(root, "sku")).Value);
    }

    [TestMethod]
    public void Serialize_ExtraFlatKeys_ReservedAndDuplicatesDropped()
    {
        var node = MakeNode();
        var ctx = MakeContext(node) with
        {
            ExtraFlatKeys =
            [
                new("sku", "S1"),
                new("sku", "S2"),                 // duplicate → dropped
                new("type", "evil-override"),     // reserved → dropped
                new("azure_metadata", "evil"),    // reserved → dropped
                new("fqdn", "app.example.com"),
            ],
        };

        var root = ParseFrontMatter(MakeSerializer().Serialize(ctx));
        Assert.AreEqual("S1", ((YamlScalarNode)GetPath(root, "sku")).Value);
        Assert.AreEqual("app.example.com", ((YamlScalarNode)GetPath(root, "fqdn")).Value);
        Assert.AreEqual("Microsoft.Network/networkInterfaces", ((YamlScalarNode)GetPath(root, "type")).Value,
            "reserved key must not be overridden by template extras");
    }

    [TestMethod]
    public void Serialize_UndefinedProperties_EmitsNull_And_EmptyBagStaysEmptyMapping()
    {
        // Synthetic nodes (ACR repositories) have default/undefined Properties → null
        var node = new TenantNode
        {
            ResourceId     = "/subscriptions/x/registries/reg1/repositories/repo1",
            Name           = "repo1",
            Type           = "microsoft.containerregistry/registries/repositories",
            SubscriptionId = "x",
            ResourceGroup  = "rg",
            Location       = "norwayeast",
        };
        var root  = ParseFrontMatter(MakeSerializer().Serialize(MakeContext(node)));
        var props = (YamlScalarNode)GetPath(root, "azure_metadata", "properties");
        Assert.AreEqual("null", props.Value);
        Assert.IsNull(YamlJsonConverter.ToJson(props));

        // …whereas a genuinely empty bag stays an empty mapping ({})
        var emptyBag  = MakeNode("{}");
        var root2     = ParseFrontMatter(MakeSerializer().Serialize(MakeContext(emptyBag)));
        var props2    = (YamlMappingNode)GetPath(root2, "azure_metadata", "properties");
        Assert.IsEmpty(props2.Children);
    }

    [TestMethod]
    public void SerializeMinimal_ProducesValidSchemaV1()
    {
        var node = MakeNode("""{ "a": 1 }""");
        var root = ParseFrontMatter(MakeSerializer().SerializeMinimal(node));
        Assert.AreEqual("1", ((YamlScalarNode)GetPath(root, "schema_version")).Value);
        Assert.AreEqual(node.ResourceId, ((YamlScalarNode)GetPath(root, "resource", "id")).Value);
        var props = YamlJsonConverter.ToJson(GetPath(root, "azure_metadata", "properties"));
        Assert.IsNotNull(props);
        Assert.AreEqual(1, (int)props["a"]!.AsValue());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Determinism / golden
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Serialize_IsDeterministic_AndUsesLfLineEndings()
    {
        var node = MakeNode("""{ "b": 2, "a": 1, "nested": { "z": "last", "y": "first" } }""");
        var s1 = MakeSerializer().Serialize(MakeContext(node));
        var s2 = MakeSerializer().Serialize(MakeContext(node));
        Assert.AreEqual(s1, s2, "output must be deterministic");
        Assert.IsFalse(s1.Contains('\r'), "output must use LF line endings only");
    }

    [TestMethod]
    public void Serialize_PreservesJsonKeyOrder()
    {
        var node = MakeNode("""{ "zebra": 1, "alpha": 2, "middle": 3 }""");
        var serialized = MakeSerializer().Serialize(MakeContext(node));
        var zebra  = serialized.IndexOf("\"zebra\"", StringComparison.Ordinal);
        var alpha  = serialized.IndexOf("\"alpha\"", StringComparison.Ordinal);
        var middle = serialized.IndexOf("\"middle\"", StringComparison.Ordinal);
        Assert.IsTrue(zebra >= 0 && zebra < alpha && alpha < middle, "document order must be preserved");
    }

    [TestMethod]
    public void Serialize_Golden_MatchesApprovedSnapshot()
    {
        var tags = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["env"] = "prod",
        };
        var node = MakeNode("""{ "ipAddress": "1.2.3.4", "count": 2, "flag": true, "nothing": null, "list": ["a"], "empty": {} }""", tags);
        var ctx = new FrontMatterContext(
            node, "Test Subscription",
            [new VaultRelationship("/subscriptions/x/subnets/snet", "snet", "Microsoft.Network/virtualNetworks/subnets", "outbound", "subnet")],
            [], Version: null, ExtraFlatKeys: []);

        var expected =
            "---\n" +
            "schema_version: 1\n" +
            "aztomarkdown_version: \"0.0.0-test\"\n" +
            "id: /subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/microsoft.network/networkinterfaces/my-nic\n" +
            "name: \"my-nic\"\n" +
            "type: Microsoft.Network/networkInterfaces\n" +
            "resource-group: rg-test\n" +
            "location: norwayeast\n" +
            "resource:\n" +
            "  id: \"/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/microsoft.network/networkinterfaces/my-nic\"\n" +
            "  name: \"my-nic\"\n" +
            "  type: \"Microsoft.Network/networkInterfaces\"\n" +
            "  subscription_id: \"00000000-0000-0000-0000-000000000000\"\n" +
            "  subscription_name: \"Test Subscription\"\n" +
            "  resource_group: \"rg-test\"\n" +
            "  location: \"norwayeast\"\n" +
            "azure_metadata:\n" +
            "  properties:\n" +
            "    \"ipAddress\": \"1.2.3.4\"\n" +
            "    \"count\": 2\n" +
            "    \"flag\": true\n" +
            "    \"nothing\": null\n" +
            "    \"list\":\n" +
            "      - \"a\"\n" +
            "    \"empty\": {}\n" +
            "  tags:\n" +
            "    \"env\": \"prod\"\n" +
            "relationships:\n" +
            "  - id: \"/subscriptions/x/subnets/snet\"\n" +
            "    name: \"snet\"\n" +
            "    type: \"Microsoft.Network/virtualNetworks/subnets\"\n" +
            "    direction: outbound\n" +
            "    label: \"subnet\"\n" +
            "---\n";

        Assert.AreEqual(expected, MakeSerializer().Serialize(ctx));
    }

    [TestMethod]
    public void VaultReader_ParseFile_FallsBackToCartographerVersionKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vault-cartographer-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "resource.md");
            File.WriteAllText(path,
                "---\n" +
                "schema_version: 1\n" +
                "cartographer_version: \"1.2.3\"\n" +
                "resource:\n" +
                "  id: \"/subscriptions/x/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/nic1\"\n" +
                "  name: \"nic1\"\n" +
                "  type: \"Microsoft.Network/networkInterfaces\"\n" +
                "  subscription_id: \"x\"\n" +
                "  subscription_name: \"Test Subscription\"\n" +
                "  resource_group: \"rg\"\n" +
                "  location: \"norwayeast\"\n" +
                "---\n# nic1\n");

            var parsed = VaultReader.ParseFile(path);
            Assert.IsNotNull(parsed);
            Assert.AreEqual("1.2.3", parsed.AzToMarkdownVersion);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

}
