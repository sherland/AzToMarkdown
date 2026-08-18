using System.Text.Json;
using AzToMarkdown.Core.Azure;
using AzToMarkdown.Core.Models;
using AzToMarkdown.Core.Rendering;
using AzToMarkdown.Core.Vault;

namespace AzToMarkdown.Tests;

/// <summary>
/// Tests for the offline vault-backed <c>IArgQueryClient</c>. The KQL tests deliberately run the
/// REAL production query emitter (<see cref="TenantEnumerator"/>) against the vault client
/// wherever possible, so that drift in the production KQL constants breaks these tests instead
/// of silently breaking vault mode.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class VaultQueryClientTests
{
    private const string Sub = "11111111-1111-1111-1111-111111111111";

    private static string? _vaultDir;
    private static TenantGraph _graph = null!;

    // ─────────────────────────────────────────────────────────────────────────
    // Fixture vault (written once for the class)
    // ─────────────────────────────────────────────────────────────────────────

    private static string Id(string type, string name) =>
        $"/subscriptions/{Sub}/resourceGroups/rg-vault/providers/{type}/{name}";

    private static TenantNode Node(string type, string name, string propsJson = "{}")
    {
        using var doc = JsonDocument.Parse(propsJson);
        return new TenantNode
        {
            ResourceId     = Id(type, name),
            Name           = name,
            Type           = type.ToLowerInvariant(),
            SubscriptionId = Sub,
            ResourceGroup  = "rg-vault",
            Location       = "norwayeast",
            Properties     = doc.RootElement.Clone(),
        };
    }

    [ClassInitialize]
    public static void WriteVault(TestContext _)
    {
        _graph = new TenantGraph();

        var agw = Node("Microsoft.Network/applicationGateways", "agw1", """
            { "httpListeners": [ { "name": "l1", "properties": { "protocol": "Https", "hostName": "www.example.com" } } ] }
            """);

        var pip = Node("Microsoft.Network/publicIPAddresses", "pip1", $$"""
            { "ipAddress": "20.100.9.9",
              "ipConfiguration": { "id": "{{agw.ResourceId}}/frontendIPConfigurations/fe" },
              "dnsSettings": { "fqdn": "gw.norwayeast.cloudapp.azure.com" } }
            """);

        var apim = Node("Microsoft.ApiManagement/service", "apim1", """
            { "gatewayUrl": "https://apim1.azure-api.net", "hostnameConfigurations": [] }
            """);

        // APIM child API (a real ARG resource type — enables offline child-collection fetches)
        using var apiDoc = JsonDocument.Parse("""
            { "displayName": "Orders", "serviceUrl": "https://orders.internal", "path": "orders" }
            """);
        var apimApi = new TenantNode
        {
            ResourceId     = $"{apim.ResourceId}/apis/orders",
            Name           = "orders",
            Type           = "microsoft.apimanagement/service/apis",
            SubscriptionId = Sub,
            ResourceGroup  = "rg-vault",
            Location       = "norwayeast",
            Properties     = apiDoc.RootElement.Clone(),
        };

        var site = Node("Microsoft.Web/sites", "web1", """
            { "defaultHostName": "web1.azurewebsites.net", "hostNames": ["web1.azurewebsites.net", "www.example.com"] }
            """);

        var lb = Node("Microsoft.Network/loadBalancers", "lb1", """
            { "frontendIPConfigurations": [ { "properties": { "privateIPAddress": "10.10.0.5" } } ],
              "backendAddressPools": [], "loadBalancingRules": [ { "name": "https", "properties": { "protocol": "Tcp", "frontendPort": 443, "backendPort": 443 } } ] }
            """);

        var nic = Node("Microsoft.Network/networkInterfaces", "nic1", """
            { "ipConfigurations": [ { "properties": { "privateIPAddress": "10.10.0.7" } } ],
              "virtualMachine": { "id": "/subscriptions/x/virtualMachines/vm1" } }
            """);

        var aks = Node("Microsoft.ContainerService/managedClusters", "aks1", """
            { "nodeResourceGroup": "MC_rg-vault_aks1_norwayeast", "kubernetesVersion": "1.31.1" }
            """);

        var acr = Node("Microsoft.ContainerRegistry/registries", "acr1");
        var repository = new TenantNode
        {
            ResourceId = $"{acr.ResourceId}/repositories/team/app",
            Name = "team/app",
            Type = "microsoft.containerregistry/registries/repositories",
            SubscriptionId = Sub,
            ResourceGroup = "rg-vault",
            Location = "norwayeast",
        };

        // DNS zone + records (zone child ids follow the ARM shape .../dnszones/{zone}/{type}/{record})
        var zoneId = Id("Microsoft.Network/dnsZones", "example.com");
        using var cnameDoc = JsonDocument.Parse("""{ "CNAMERecord": { "cname": "web1.azurewebsites.net" } }""");
        var cname = new TenantNode
        {
            ResourceId = $"{zoneId}/CNAME/www", Name = "www",
            Type = "microsoft.network/dnszones/cname",
            SubscriptionId = Sub, ResourceGroup = "rg-vault", Location = "global",
            Properties = cnameDoc.RootElement.Clone(),
        };
        using var aDoc = JsonDocument.Parse("""{ "ARecords": [ { "ipv4Address": "20.100.9.9" } ] }""");
        var aRec = new TenantNode
        {
            ResourceId = $"{zoneId}/A/gw", Name = "gw",
            Type = "microsoft.network/dnszones/a",
            SubscriptionId = Sub, ResourceGroup = "rg-vault", Location = "global",
            Properties = aDoc.RootElement.Clone(),
        };
        var zone = Node("Microsoft.Network/dnsZones", "example.com");

        foreach (var n in new[] { pip, agw, apim, apimApi, site, lb, nic, aks, acr, repository, zone, cname, aRec })
            _graph.AddNode(n);
        _graph.AddEdge(nic.ResourceId, lb.ResourceId, "backend");

        _vaultDir = Path.Combine(Path.GetTempPath(), $"vault-qc-{Guid.NewGuid():N}");
        new VaultWriter(new VaultTemplateEngine(), serializer: new FrontMatterSerializer("0.0.0-test"))
            .WriteAll(_graph, new Dictionary<string, string> { [Sub] = "Vault Sub" }, _vaultDir);
    }

    [ClassCleanup]
    public static void DeleteVault()
    {
        if (_vaultDir is not null && Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    private static VaultQueryClient Client() => new(_vaultDir!);

    // ─────────────────────────────────────────────────────────────────────────
    // Flagship: real TenantEnumerator over the vault client
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task TenantEnumerator_OverVaultQueryClient_ReproducesOriginalNodes()
    {
        var (nodes, subNames) = await new TenantEnumerator(Client()).FetchAllAsync();

        Assert.AreEqual("Vault Sub", subNames[Sub]);
        Assert.AreEqual(_graph.Nodes.Count, nodes.Count, "enumerating the vault must reproduce every node");

        foreach (var original in _graph.Nodes.Values)
        {
            var read = nodes.SingleOrDefault(n => n.ResourceId.Equals(original.ResourceId, StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(read, $"missing after vault enumeration: {original.ResourceId}");
            Assert.IsTrue(YamlJsonConverter.JsonDeepEquals(original.Properties, read.Properties, out var diff),
                $"properties differ for {original.ResourceId} at {diff}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Direct handler coverage
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task RunQuery_IdInList_ReturnsProperties()
    {
        var agwId = Id("Microsoft.Network/applicationGateways", "agw1").ToLowerInvariant();
        var rows = await Client().RunQueryAsync(
            $"Resources | where tolower(id) in ('{agwId}') | project id, name, type, properties");

        Assert.HasCount(1, rows);
        Assert.AreEqual("agw1", rows[0].GetProperty("name").GetString());
        Assert.IsTrue(rows[0].GetProperty("properties").TryGetProperty("httpListeners", out _));
    }

    [TestMethod]
    public async Task RunQuery_FqdnCombined_MatchesHostNamesArray()
    {
        var rows = await Client().RunQueryAsync(
            "Resources" +
            " | where type in ('microsoft.web/sites', 'microsoft.web/staticsites', 'microsoft.apimanagement/service')" +
            " | where tostring(properties.defaultHostName) =~ 'www.example.com'" +
            "   or tostring(properties.hostNames) contains 'www.example.com'" +
            "   or tostring(properties.defaultHostname) =~ 'www.example.com'" +
            "   or tostring(properties.customDomains) contains 'www.example.com'" +
            "   or tostring(properties.gatewayUrl) contains 'www.example.com'" +
            "   or tostring(properties.hostnameConfigurations) contains 'www.example.com'" +
            " | project id, name, type");

        Assert.HasCount(1, rows);
        Assert.AreEqual("web1", rows[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task RunQuery_LbNicIpUnion_FindsNicWithVmId()
    {
        var rows = await Client().RunQueryAsync(
            "Resources" +
            " | where type == 'microsoft.network/loadbalancers'" +
            " | mv-expand fe = properties.frontendIPConfigurations" +
            " | where tostring(fe.properties.privateIPAddress) == '10.10.0.7'" +
            " | project id, name, type, rg = resourceGroup, vmId = ''" +
            " | union (Resources" +
            " | where type == 'microsoft.network/networkinterfaces'" +
            " | mv-expand ipConfig = properties.ipConfigurations" +
            " | where tostring(ipConfig.properties.privateIPAddress) == '10.10.0.7'" +
            " | project id, name, type, rg = '', vmId = tostring(properties.virtualMachine.id))");

        Assert.HasCount(1, rows);
        Assert.AreEqual("nic1", rows[0].GetProperty("name").GetString());
        Assert.AreEqual("/subscriptions/x/virtualMachines/vm1", rows[0].GetProperty("vmId").GetString());
    }

    [TestMethod]
    public async Task RunQuery_AksByNodeResourceGroup_Matches()
    {
        var rows = await Client().RunQueryAsync(
            "Resources | where type == 'microsoft.containerservice/managedclusters'" +
            " | where tolower(tostring(properties.nodeResourceGroup)) == 'mc_rg-vault_aks1_norwayeast'" +
            " | project id, name, type");

        Assert.HasCount(1, rows);
        Assert.AreEqual("aks1", rows[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task RunQuery_Unsupported_ReturnsEmpty()
    {
        var rows = await Client().RunQueryAsync("Resources | summarize count() by type");
        Assert.IsEmpty(rows);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Non-KQL surface
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetResourceById_ExactNode_ReturnsArmShapedJson()
    {
        var agwId = Id("Microsoft.Network/applicationGateways", "agw1");
        var json  = await Client().GetResourceByIdAsync(agwId);

        Assert.AreEqual(agwId, json.GetProperty("id").GetString());
        Assert.AreEqual("Microsoft.Network/applicationGateways", json.GetProperty("type").GetString());
        Assert.IsTrue(json.GetProperty("properties").TryGetProperty("httpListeners", out _));
    }

    [TestMethod]
    public async Task GetResourceById_ChildCollection_ReturnsValueArray()
    {
        var apimId = Id("Microsoft.ApiManagement/service", "apim1");
        var json   = await Client().GetResourceByIdAsync($"{apimId}/apis?api-version=2022-08-01", useRestPath: true);

        var value = json.GetProperty("value");
        Assert.AreEqual(1, value.GetArrayLength());
        Assert.AreEqual("orders", value[0].GetProperty("name").GetString());
        Assert.AreEqual("Orders", value[0].GetProperty("properties").GetProperty("displayName").GetString());
    }

    [TestMethod]
    public async Task GetResourceById_Missing_Throws()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Client().GetResourceByIdAsync("/subscriptions/x/notThere"));
    }

    [TestMethod]
    public async Task BatchArmGet_SkipsMissingUrls()
    {
        var apimId = Id("Microsoft.ApiManagement/service", "apim1");
        var urls = new[]
        {
            $"https://management.azure.com{apimId}?api-version=2023-05-01",
            "https://management.azure.com/subscriptions/x/missing?api-version=2023-05-01",
        };
        var results = await Client().BatchArmGetAsync(urls);

        Assert.HasCount(1, results);
        Assert.AreEqual("apim1", results[urls[0]].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task RunAksCommand_ReturnsNonZeroExit()
    {
        var result = await Client().RunAksCommandAsync("rg", "cluster", "kubectl get ingress");
        Assert.AreEqual(-1, result.GetProperty("exitCode").GetInt32());
    }

    [TestMethod]
    public async Task FetchSubscriptionNames_ComesFromSummaryFrontMatter()
    {
        var subs = await Client().FetchSubscriptionNamesAsync();
        Assert.AreEqual("Vault Sub", subs[Sub]);
    }
}
