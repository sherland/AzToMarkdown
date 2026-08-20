using System.Text.Json;
using AzToMarkdown.Core.Azure;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Rendering;

namespace AzToMarkdown.Tests;

/// <summary>
/// Unit tests for AzToMarkdown components.
/// These tests run entirely in memory — no Azure CLI, no ARG queries.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class AzToMarkdownTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // TenantGraph tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void TenantGraph_AddNode_Deduplicates()
    {
        var graph = new TenantGraph();
        var node  = MakeNode("/sub/rg/pip-1", "pip-1", "microsoft.network/publicipaddresses");
        graph.AddNode(node);
        graph.AddNode(node); // duplicate — should be silently ignored
        Assert.AreEqual(1, graph.Nodes.Count);
    }

    [TestMethod]
    public void TenantGraph_AddEdge_Deduplicates()
    {
        var graph = new TenantGraph();
        var a = MakeNode("/id/a", "a", "microsoft.network/virtualnetworks");
        var b = MakeNode("/id/b", "b", "microsoft.network/publicipaddresses");
        graph.AddNode(a);
        graph.AddNode(b);

        graph.AddEdge(a.ResourceId, b.ResourceId, "link");
        graph.AddEdge(a.ResourceId, b.ResourceId, "link"); // duplicate
        Assert.AreEqual(1, graph.GetOutbound(a.ResourceId).Count);
        Assert.AreEqual(1, graph.GetInbound(b.ResourceId).Count);
    }

    [TestMethod]
    public void TenantGraph_BidirectionalIndex_Consistent()
    {
        var graph = new TenantGraph();
        var a = MakeNode("/id/a", "a", "x");
        var b = MakeNode("/id/b", "b", "x");
        graph.AddNode(a);
        graph.AddNode(b);
        graph.AddEdge(a.ResourceId, b.ResourceId, "test");

        Assert.AreEqual(1, graph.GetOutbound(a.ResourceId).Count);
        Assert.AreEqual(0, graph.GetOutbound(b.ResourceId).Count);
        Assert.AreEqual(0, graph.GetInbound(a.ResourceId).Count);
        Assert.AreEqual(1, graph.GetInbound(b.ResourceId).Count);
        Assert.AreEqual(b.ResourceId, graph.GetOutbound(a.ResourceId)[0].ToId);
        Assert.AreEqual(a.ResourceId, graph.GetInbound(b.ResourceId)[0].FromId);
    }

    [TestMethod]
    public void TenantGraph_FindByName_ReturnsAllMatches()
    {
        var graph = new TenantGraph();
        var n1 = MakeNode("/sub1/rg1/n", "vnet-prod", "microsoft.network/virtualnetworks");
        var n2 = MakeNode("/sub2/rg2/n", "vnet-prod", "microsoft.network/virtualnetworks");
        graph.AddNode(n1);
        graph.AddNode(n2);

        var results = graph.FindByName("vnet-prod").ToList();
        Assert.AreEqual(2, results.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RelationshipExtractor tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void RelationshipExtractor_NicEdges_VmAndSubnet()
    {
        var extractor = new RelationshipExtractor();
        var vmId      = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1";
        var subnetId  = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet/subnets/sn1";
        var nicId     = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/nic1";

        var nicJson = $$"""
            {
              "virtualMachine": { "id": "{{vmId}}" },
              "ipConfigurations": [{
                "properties": {
                  "subnet": { "id": "{{subnetId}}" }
                }
              }]
            }
            """;

        var vmNode     = MakeNode(vmId,     "vm1",  "microsoft.compute/virtualmachines");
        var subnetNode = MakeNode(subnetId, "sn1",  "microsoft.network/virtualnetworks/subnets");
        var nicNode    = MakeNodeWithProps(nicId, "nic1", "microsoft.network/networkinterfaces", nicJson);

        var graph = extractor.Build([vmNode, subnetNode, nicNode]);

        // VM → NIC (vm is the source via virtualMachine.id on the NIC)
        var outVm = graph.GetOutbound(vmId);
        Assert.IsTrue(outVm.Any(e => e.ToId == nicId && e.Label == "network interface"),
            "Expected VM → NIC edge via virtualMachine.id");

        // NIC → Subnet
        var outNic = graph.GetOutbound(nicId);
        Assert.IsTrue(outNic.Any(e => e.ToId == subnetId && e.Label == "subnet"),
            "Expected NIC → Subnet edge");
    }

    [TestMethod]
    public void RelationshipExtractor_PrivateEndpointEdges_LinksToTarget()
    {
        var extractor = new RelationshipExtractor();
        var peId    = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/privateEndpoints/pe1";
        var svcId   = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/sa1";
        var subnetId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet/subnets/sn1";

        var peJson = $$"""
            {
              "subnet": { "id": "{{subnetId}}" },
              "privateLinkServiceConnections": [{
                "properties": {
                  "privateLinkServiceId": "{{svcId}}"
                }
              }]
            }
            """;

        var peNode     = MakeNodeWithProps(peId,     "pe1", "microsoft.network/privateendpoints",   peJson);
        var svcNode    = MakeNode(svcId,    "sa1",   "microsoft.storage/storageaccounts");
        var subnetNode = MakeNode(subnetId, "sn1",   "microsoft.network/virtualnetworks/subnets");

        var graph = extractor.Build([peNode, svcNode, subnetNode]);

        var out_ = graph.GetOutbound(peId);
        Assert.IsTrue(out_.Any(e => e.ToId == svcId    && e.Label == "private link"), "PE → service link");
        Assert.IsTrue(out_.Any(e => e.ToId == subnetId && e.Label == "subnet"),       "PE → subnet");
    }

    [TestMethod]
    public void RelationshipExtractor_UnresolvableBackend_NoEdgeAdded()
    {
        var extractor = new RelationshipExtractor();
        var agwId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/applicationGateways/agw1";

        var agwJson = """
            {
              "backendAddressPools": [{
                "properties": {
                  "backendAddresses": [{ "fqdn": "unknown-backend.example.com" }]
                }
              }]
            }
            """;

        var agw   = MakeNodeWithProps(agwId, "agw1", "microsoft.network/applicationgateways", agwJson);
        var graph = extractor.Build([agw]);

        // The unresolvable backend should NOT produce any outbound edge
        Assert.AreEqual(0, graph.GetOutbound(agwId).Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VaultWriter tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void VaultWriter_Sanitize_StripsForbiddenChars()
    {
        Assert.AreEqual("my-resource", VaultWriter.Sanitize("my-resource"));
        Assert.AreEqual("myresource",   VaultWriter.Sanitize("my:resource"));   // : stripped, no hyphen inserted
        Assert.AreEqual("myresource",   VaultWriter.Sanitize("my|resource"));   // | stripped
        Assert.AreEqual("sub-prod",     VaultWriter.Sanitize("sub-prod"));
        Assert.AreEqual("ab",           VaultWriter.Sanitize("a|b"));
        Assert.AreEqual("ab",           VaultWriter.Sanitize("a#b"));
    }

    [TestMethod]
    public void VaultWriter_BuildVaultPaths_InfrastructurePath()
    {
        var node = MakeNode(
            "/subscriptions/sub-123/resourceGroups/rg-net/providers/Microsoft.Network/applicationGateways/agw1",
            "agw1",
            "microsoft.network/applicationgateways",
            subscriptionId: "sub-123",
            resourceGroup: "rg-net");

        var graph = new TenantGraph();
        graph.AddNode(node);

        var subNames = new Dictionary<string, string> { ["sub-123"] = "sub-prod" };
        var paths    = VaultWriter.BuildVaultPaths(graph, subNames);

        Assert.IsTrue(paths.TryGetValue(node.ResourceId, out var path));
        Assert.AreEqual("infrastructure/sub-prod/rg-net/agw1", path);
    }

    [TestMethod]
    public void VaultWriter_BuildVaultPaths_DnsZonePath()
    {
        var node = MakeNode(
            "/subscriptions/sub-123/resourceGroups/rg/providers/Microsoft.Network/dnszones/contoso.com",
            "contoso.com",
            "microsoft.network/dnszones",
            subscriptionId: "sub-123",
            resourceGroup: "rg");

        var graph = new TenantGraph();
        graph.AddNode(node);

        var paths = VaultWriter.BuildVaultPaths(graph, new Dictionary<string, string>());
        Assert.IsTrue(paths.TryGetValue(node.ResourceId, out var path));
        Assert.AreEqual("routes/contoso.com", path);
    }

    [TestMethod]
    public void VaultWriter_BuildVaultPaths_DisambiguatesNameCollisions()
    {
        // A VM and its NIC sharing the same name in the same RG must get distinct paths.
        var subNames = new Dictionary<string, string> { ["sub-1"] = "my-sub" };
        var vmId  = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/foo";
        var nicId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/foo";

        var graph = new TenantGraph();
        graph.AddNode(MakeNode(vmId,  "foo", "microsoft.compute/virtualmachines",     "sub-1", "rg"));
        graph.AddNode(MakeNode(nicId, "foo", "microsoft.network/networkinterfaces",   "sub-1", "rg"));

        var paths = VaultWriter.BuildVaultPaths(graph, subNames);

        Assert.IsTrue(paths.TryGetValue(vmId,  out var vmPath),  "VM must have a vault path");
        Assert.IsTrue(paths.TryGetValue(nicId, out var nicPath), "NIC must have a vault path");
        Assert.AreNotEqual(vmPath, nicPath, "VM and NIC with same name in same RG must get distinct paths");
        // Both belong in the infrastructure folder for that RG.
        StringAssert.Contains(vmPath,  "infrastructure/my-sub/rg/foo",  "VM path should start with the naive path");
        StringAssert.Contains(nicPath, "infrastructure/my-sub/rg/foo",  "NIC path should start with the naive path");
        // Both should have the type suffix appended for disambiguation
        StringAssert.Contains(vmPath,  "--virtualmachines",    "VM path must have type suffix");
        StringAssert.Contains(nicPath, "--networkinterfaces",  "NIC path must have type suffix");
    }

    [TestMethod]
    public void VaultWriter_BuildVaultPaths_NoSuffixWhenNamesAreUnique()
    {
        // When there is no collision, the path should remain clean (no --type suffix).
        var subNames = new Dictionary<string, string> { ["sub-1"] = "my-sub" };
        var agwId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/applicationGateways/agw1";

        var graph = new TenantGraph();
        graph.AddNode(MakeNode(agwId, "agw1", "microsoft.network/applicationgateways", "sub-1", "rg"));

        var paths = VaultWriter.BuildVaultPaths(graph, subNames);

        Assert.IsTrue(paths.TryGetValue(agwId, out var path));
        Assert.AreEqual("infrastructure/my-sub/rg/agw1", path,
            "Non-colliding resources must not get a type suffix");
    }

    [TestMethod]
    public void VaultWriter_WriteAll_NoOverwrites_WhenNamesCollide()
    {
        // Writing a VM and a same-named NIC to disk must produce two distinct files.
        var subNames = new Dictionary<string, string> { ["sub-1"] = "my-sub" };
        var vmId  = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/srv01";
        var nicId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/srv01";

        var graph = new TenantGraph();
        graph.AddNode(MakeNode(vmId,  "srv01", "microsoft.compute/virtualmachines",   "sub-1", "rg"));
        graph.AddNode(MakeNode(nicId, "srv01", "microsoft.network/networkinterfaces", "sub-1", "rg"));

        var engine    = new AzToMarkdown.Core.Rendering.VaultTemplateEngine();
        var writer    = new AzToMarkdown.Core.Rendering.VaultWriter(engine);
        var outputDir = Path.Combine(Path.GetTempPath(), $"vault-collision-{Guid.NewGuid():N}");
        try
        {
            writer.WriteAll(graph, subNames, outputDir);

            var files = Directory.GetFiles(outputDir, "*.md", SearchOption.AllDirectories)
                                  .Where(f => !Path.GetFileName(f).StartsWith("_summary", StringComparison.OrdinalIgnoreCase))
                                  .ToArray();
            Assert.AreEqual(2, files.Length,
                $"Expected 2 distinct resource files for a VM+NIC name collision; got: {string.Join(", ", files.Select(Path.GetFileName))}");

            // Neither file should have the other's content
            var f1content = File.ReadAllText(files.OrderBy(x => x).First());
            var f2content = File.ReadAllText(files.OrderBy(x => x).Last());
            Assert.AreNotEqual(f1content, f2content, "The two collision-disambiguated files must have different contents");
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [TestMethod]
    public void VaultWriter_WriteAll_FrontmatterSorted()
    {
        var graph  = new TenantGraph();
        var subNames = new Dictionary<string, string> { ["sub-1"] = "sub-prod" };

        var agwId  = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/applicationGateways/agw1";
        var pip1Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/pip-z";
        var pip2Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/pip-a";

        var agw  = MakeNode(agwId,  "agw1",  "microsoft.network/applicationgateways",  "sub-1", "rg");
        var pip1 = MakeNode(pip1Id, "pip-z", "microsoft.network/publicipaddresses",    "sub-1", "rg");
        var pip2 = MakeNode(pip2Id, "pip-a", "microsoft.network/publicipaddresses",    "sub-1", "rg");
        graph.AddNode(agw);
        graph.AddNode(pip1);
        graph.AddNode(pip2);
        graph.AddEdge(pip1Id, agwId, "frontend ip");
        graph.AddEdge(pip2Id, agwId, "frontend ip");

        var outputDir = Path.Combine(Path.GetTempPath(), $"vault-test-{Guid.NewGuid():N}");
        try
        {
            var writer = new VaultWriter();
            writer.WriteAll(graph, subNames, outputDir);

            var agwFile    = Path.Combine(outputDir, "infrastructure", "sub-prod", "rg", "agw1.md");
            Assert.IsTrue(File.Exists(agwFile), $"Expected file: {agwFile}");
            var content = File.ReadAllText(agwFile);

            // YAML frontmatter must be sorted (pip-a before pip-z)
            var pip_aIdx = content.IndexOf("pip-a", StringComparison.Ordinal);
            var pip_zIdx = content.IndexOf("pip-z", StringComparison.Ordinal);
            Assert.IsTrue(pip_aIdx < pip_zIdx, "depends_on_inbound must be sorted alphabetically");

            // Must contain YAML delimiters
            Assert.IsTrue(content.StartsWith("---"), "File must start with YAML frontmatter");
            Assert.IsTrue(content.Contains("\n---"), "File must have closing YAML delimiter");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [TestMethod]
    public void VaultWriter_WriteAll_DeterministicOnRepeat()
    {
        var graph    = BuildSampleGraph();
        var subNames = new Dictionary<string, string> { ["sub-1"] = "sub-prod" };

        var dir1 = Path.Combine(Path.GetTempPath(), $"vault-det-1-{Guid.NewGuid():N}");
        var dir2 = Path.Combine(Path.GetTempPath(), $"vault-det-2-{Guid.NewGuid():N}");
        try
        {
            var writer = new VaultWriter();
            writer.WriteAll(graph, subNames, dir1);
            writer.WriteAll(graph, subNames, dir2);

            // _summary*.md files legitimately embed a live `generated:` UTC timestamp (second
            // precision) — excluded here since the two WriteAll calls above can straddle a
            // second boundary under CI load, and that's not the determinism this test targets:
            // resource-file front matter/body content is what must be stable across runs.
            foreach (var file1 in Directory.GetFiles(dir1, "*.md", SearchOption.AllDirectories)
                         .Where(f => !Path.GetFileName(f).StartsWith("_summary", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = Path.GetRelativePath(dir1, file1);
                var file2    = Path.Combine(dir2, relative);
                Assert.IsTrue(File.Exists(file2), $"Missing {relative} in second run");
                Assert.AreEqual(
                    File.ReadAllText(file1),
                    File.ReadAllText(file2),
                    $"File {relative} is not identical between runs");
            }
        }
        finally
        {
            foreach (var d in new[] { dir1, dir2 })
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
    }

    [TestMethod]
    public void VaultWriter_WikiLink_UsesFullVaultPathWithPipe()
    {
        var graph    = new TenantGraph();
        var subNames = new Dictionary<string, string> { ["sub-1"] = "sub-prod" };

        var agwId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/applicationGateways/agw1";
        var pipId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/pip1";
        var agw   = MakeNode(agwId, "agw1", "microsoft.network/applicationgateways", "sub-1", "rg");
        var pip   = MakeNode(pipId, "pip1", "microsoft.network/publicipaddresses",   "sub-1", "rg");
        graph.AddNode(agw);
        graph.AddNode(pip);
        graph.AddEdge(pipId, agwId, "frontend ip");

        var outputDir = Path.Combine(Path.GetTempPath(), $"vault-wikilink-{Guid.NewGuid():N}");
        try
        {
            var writer = new VaultWriter();
            writer.WriteAll(graph, subNames, outputDir);

            var agwFile = Path.Combine(outputDir, "infrastructure", "sub-prod", "rg", "agw1.md");
            var content = File.ReadAllText(agwFile);

            // Must contain a full-path WikiLink with pipe display name
            Assert.IsTrue(content.Contains("[[infrastructure/sub-prod/rg/pip1|pip1]]"),
                $"Expected full-path WikiLink in:\n{content}");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

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

    private static TenantGraph BuildSampleGraph()
    {
        var graph = new TenantGraph();
        var agw   = MakeNode("/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/applicationGateways/agw1",
                             "agw1", "microsoft.network/applicationgateways", "sub-1", "rg");
        var pip   = MakeNode("/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/pip1",
                             "pip1", "microsoft.network/publicipaddresses", "sub-1", "rg");
        var vm    = MakeNode("/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
                             "vm1", "microsoft.compute/virtualmachines", "sub-1", "rg");
        graph.AddNode(agw);
        graph.AddNode(pip);
        graph.AddNode(vm);
        graph.AddEdge(pip.ResourceId, agw.ResourceId, "frontend ip");
        graph.AddEdge(agw.ResourceId, vm.ResourceId,  "backend pool");
        return graph;
    }
}
