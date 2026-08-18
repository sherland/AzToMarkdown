using System.Text.Json;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Rendering;
using AzToMarkdown.Core.Vault;

namespace AzToMarkdown.Tests;

/// <summary>
/// End-to-end round-trip: VaultWriter.WriteAll → VaultReader.ReadAll must reconstruct every
/// node losslessly (identity, full properties bag, tags), including role assignments (embedded
/// and orphan-scoped) and synthetic ACR repository nodes.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class VaultRoundTripTests
{
    private const string Sub = "00000000-0000-0000-0000-000000000000";

    // ─────────────────────────────────────────────────────────────────────────
    // Fixture graph
    // ─────────────────────────────────────────────────────────────────────────

    private static TenantNode MakeNode(string id, string name, string type, string propsJson = "{}",
        IReadOnlyDictionary<string, string>? tags = null, string? identityJson = null)
    {
        using var doc = JsonDocument.Parse(propsJson);
        using var identityDoc = identityJson is null ? null : JsonDocument.Parse(identityJson);
        return new TenantNode
        {
            ResourceId     = id,
            Name           = name,
            Type           = type,
            SubscriptionId = Sub,
            ResourceGroup  = "rg-test",
            Location       = "norwayeast",
            Properties     = doc.RootElement.Clone(),
            Identity       = identityDoc?.RootElement.Clone() ?? default,
            Tags           = tags ?? new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string Id(string type, string name) =>
        $"/subscriptions/{Sub}/resourceGroups/rg-test/providers/{type}/{name}";

    /// <summary>Builds a graph with a vnet, NSG (tagged), NIC→vnet edge, embedded + orphan role assignments, and an ACR synthetic repo node.</summary>
    private static (TenantGraph Graph, Dictionary<string, string> SubNames) BuildFixtureGraph()
    {
        var graph = new TenantGraph();

        var vnet = MakeNode(Id("Microsoft.Network/virtualNetworks", "vnet1"), "vnet1",
            "microsoft.network/virtualnetworks",
            """
            { "addressSpace": { "addressPrefixes": ["10.42.0.0/16"] },
              "subnets": [ { "name": "snet1", "properties": { "addressPrefix": "10.42.1.0/24" } } ] }
            """);

        var nsg = MakeNode(Id("Microsoft.Network/networkSecurityGroups", "nsg1"), "nsg1",
            "microsoft.network/networksecuritygroups",
            """
            { "securityRules": [ { "name": "AllowHttps", "properties": { "priority": 100, "access": "Allow", "destinationPortRange": "443" } } ] }
            """,
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["env"] = "test", ["norway"] = "no" },
            identityJson: """{"type":"SystemAssigned","principalId":"principal-nsg","tenantId":"tenant-1"}""");

        var nic = MakeNode(Id("Microsoft.Network/networkInterfaces", "nic1"), "nic1",
            "microsoft.network/networkinterfaces",
            """
            { "ipConfigurations": [ { "name": "ipconfig1", "properties": { "privateIPAddress": "10.42.1.4" } } ] }
            """);

        // Embedded role assignment (scoped to the NSG, which has a vault file)
        var raEmbedded = MakeNode(
            $"{nsg.ResourceId}/providers/Microsoft.Authorization/roleAssignments/aaaa1111",
            "aaaa1111", "microsoft.authorization/roleassignments",
            $$"""
            { "roleDefinitionId": "/subscriptions/{{Sub}}/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c",
              "principalId": "principal-embedded", "scope": "{{nsg.ResourceId}}" }
            """);

        // Orphan role assignment (subscription-scoped — no vault file for the scope, no resource group)
        using var orphanProps = JsonDocument.Parse($$"""
            { "roleDefinitionId": "/subscriptions/{{Sub}}/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635",
              "principalId": "principal-orphan", "scope": "/subscriptions/{{Sub}}" }
            """);
        var raOrphan = new TenantNode
        {
            ResourceId     = $"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleAssignments/bbbb2222",
            Name           = "bbbb2222",
            Type           = "microsoft.authorization/roleassignments",
            SubscriptionId = Sub,
            ResourceGroup  = "",
            Location       = "",
            Properties     = orphanProps.RootElement.Clone(),
        };

        // Synthetic ACR repository node (undefined properties, like TenantEnumerator produces)
        var acrRepo = new TenantNode
        {
            ResourceId     = Id("Microsoft.ContainerRegistry/registries", "reg1") + "/repositories/app/backend",
            Name           = "app/backend",
            Type           = "microsoft.containerregistry/registries/repositories",
            SubscriptionId = Sub,
            ResourceGroup  = "rg-test",
            Location       = "norwayeast",
        };

        foreach (var n in new[] { vnet, nsg, nic, raEmbedded, raOrphan, acrRepo })
            graph.AddNode(n);

        graph.AddEdge(nic.ResourceId, vnet.ResourceId, "subnet");
        graph.AddEdge(raEmbedded.ResourceId, nsg.ResourceId, "role:Contributor");
        // raOrphan gets NO edge — its scope has no node (mirrors RelationshipExtractor behaviour)

        return (graph, new Dictionary<string, string> { [Sub] = "Test Subscription" });
    }

    private static string WriteFixtureVault(out TenantGraph graph, out Dictionary<string, string> subNames)
    {
        (graph, subNames) = BuildFixtureGraph();
        var dir = Path.Combine(Path.GetTempPath(), $"vault-roundtrip-{Guid.NewGuid():N}");
        new VaultWriter(new VaultTemplateEngine(), serializer: new FrontMatterSerializer("0.0.0-test"))
            .WriteAll(graph, subNames, dir);
        return dir;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void WriteAll_ThenReadAll_ReconstructsAllNodesLosslessly()
    {
        var dir = WriteFixtureVault(out var graph, out _);
        try
        {
            var result = new VaultReader().ReadAll(dir);

            Assert.AreEqual(graph.Nodes.Count, result.Nodes.Count,
                $"expected all {graph.Nodes.Count} nodes back (incl. role assignments + ACR repo); got: {string.Join(", ", result.Nodes.Select(n => n.Name))}");

            foreach (var original in graph.Nodes.Values)
            {
                var read = result.Nodes.SingleOrDefault(n =>
                    n.ResourceId.Equals(original.ResourceId, StringComparison.OrdinalIgnoreCase));
                Assert.IsNotNull(read, $"node missing after round-trip: {original.ResourceId}");

                Assert.AreEqual(original.Name, read.Name, $"Name mismatch for {original.ResourceId}");
                Assert.AreEqual(original.Type, read.Type, $"Type mismatch for {original.ResourceId}");
                Assert.AreEqual(original.SubscriptionId, read.SubscriptionId, $"SubscriptionId mismatch for {original.ResourceId}");
                Assert.AreEqual(original.ResourceGroup, read.ResourceGroup, $"ResourceGroup mismatch for {original.ResourceId}");

                Assert.IsTrue(
                    YamlJsonConverter.JsonDeepEquals(original.Properties, read.Properties, out var diff),
                    $"properties lost for {original.ResourceId} at: {diff}");
                Assert.IsTrue(
                    YamlJsonConverter.JsonDeepEquals(original.Identity, read.Identity, out var identityDiff),
                    $"identity lost for {original.ResourceId} at: {identityDiff}");
                Assert.AreEqual(original.IdentityType, read.IdentityType,
                    $"IdentityType mismatch for {original.ResourceId}");

                CollectionAssert.AreEquivalent(
                    original.Tags.ToList(), read.Tags.ToList(),
                    $"tags mismatch for {original.ResourceId}");
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void WriteAll_PersistsSubscriptionNames_InSummaryFrontMatter()
    {
        var dir = WriteFixtureVault(out _, out var subNames);
        try
        {
            var result = new VaultReader().ReadAll(dir);
            Assert.AreEqual("Test Subscription", result.SubscriptionNames[Sub]);
            Assert.HasCount(subNames.Count, result.SubscriptionNames);

            var summaryText = File.ReadAllText(Path.Combine(dir, "_summary.md"));
            StringAssert.Contains(summaryText, "schema_version: 1");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void WriteAll_OrphanRoleAssignments_LandInRoleAssignmentsFile()
    {
        var dir = WriteFixtureVault(out _, out _);
        try
        {
            var orphanFile = Path.Combine(dir, "_role_assignments.md");
            Assert.IsTrue(File.Exists(orphanFile), "_role_assignments.md must exist for subscription-scoped assignments");

            var content = File.ReadAllText(orphanFile);
            StringAssert.Contains(content, "bbbb2222",           "orphan assignment id must be present");
            StringAssert.Contains(content, "principal-orphan",   "orphan principal must be present");
            StringAssert.Contains(content, "Owner",              "role name must be resolved from the GUID map");
            StringAssert.Contains(content, $"/subscriptions/{Sub}", "scope must be persisted");
            Assert.IsFalse(content.Contains("aaaa1111"), "embedded (resource-scoped) assignment must NOT be in the orphan file");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void EmbeddedRoleAssignment_IsLossless_InTargetResourceFile()
    {
        var dir = WriteFixtureVault(out var graph, out _);
        try
        {
            var nsgFile = Path.Combine(dir, "infrastructure", "Test Subscription", "rg-test", "nsg1.md");
            Assert.IsTrue(File.Exists(nsgFile));

            var parsed = VaultReader.ParseFile(nsgFile);
            Assert.IsNotNull(parsed);
            Assert.HasCount(1, parsed.RoleAssignments);

            var ra = parsed.RoleAssignments[0];
            Assert.AreEqual("Contributor", ra.Role);
            Assert.AreEqual("principal-embedded", ra.PrincipalId);
            StringAssert.Contains(ra.Id, "aaaa1111");

            var originalRa = graph.Nodes.Values.Single(n => n.Name == "aaaa1111");
            Assert.IsTrue(
                YamlJsonConverter.JsonDeepEquals(originalRa.Properties, ra.Properties, out var diff),
                $"embedded assignment properties lost at: {diff}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void Relationships_MatchGraphEdges_WithDirectionAndResolvedNames()
    {
        var dir = WriteFixtureVault(out _, out _);
        try
        {
            var nicFile = VaultReader.ParseFile(Path.Combine(dir, "infrastructure", "Test Subscription", "rg-test", "nic1.md"));
            Assert.IsNotNull(nicFile);
            Assert.HasCount(1, nicFile.Relationships);
            var outRel = nicFile.Relationships[0];
            Assert.AreEqual("outbound", outRel.Direction);
            Assert.AreEqual("subnet",   outRel.Label);
            Assert.AreEqual("vnet1",    outRel.Name, "target name must be resolved from the graph");
            Assert.AreEqual("Microsoft.Network/virtualNetworks", outRel.Type);

            var vnetFile = VaultReader.ParseFile(Path.Combine(dir, "infrastructure", "Test Subscription", "rg-test", "vnet1.md"));
            Assert.IsNotNull(vnetFile);
            Assert.HasCount(1, vnetFile.Relationships);
            Assert.AreEqual("inbound", vnetFile.Relationships[0].Direction);
            Assert.AreEqual("nic1",    vnetFile.Relationships[0].Name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void ReadAll_SkipsFilesWithoutSchemaVersion()
    {
        var dir = WriteFixtureVault(out var graph, out _);
        try
        {
            File.WriteAllText(Path.Combine(dir, "unversioned-note.md"),
                "---\nid: /notes/example\nname: \"example\"\n---\n# note\n");

            var result = new VaultReader().ReadAll(dir);
            Assert.AreEqual(graph.Nodes.Count, result.Nodes.Count, "unversioned file must be skipped");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void ReadAll_ThrowsOnNewerSchemaVersion()
    {
        var dir = WriteFixtureVault(out _, out _);
        try
        {
            File.WriteAllText(Path.Combine(dir, "unsupported-version.md"),
                "---\nschema_version: 999\nresource:\n  id: \"/x\"\n---\n# unsupported\n");

            Assert.ThrowsExactly<NotSupportedException>(() => new VaultReader().ReadAll(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
