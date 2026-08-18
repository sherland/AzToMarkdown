using System.Text.Json;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Azure;

namespace AzToMarkdown.Tests;

/// <summary>
/// Unit tests for <see cref="TenantEnumerator"/>, in particular the resource-group discovery
/// query added on top of the plain "Resources" ARG query (resource groups live in the separate
/// ARG "ResourceContainers" table, never in "Resources").
///
/// All tests run entirely in-memory against a stub <see cref="IArgQueryClient"/> — no Azure CLI.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TenantEnumeratorTests
{
    [TestMethod]
    public async Task FetchAllAsync_IssuesResourceContainersQuery()
    {
        var client = new StubArgQueryClient();
        var enumerator = new TenantEnumerator(client);

        await enumerator.FetchAllAsync();

        Assert.IsTrue(client.QueriesReceived.Any(q => q.Contains("ResourceContainers", StringComparison.Ordinal)),
            "Expected a KQL query against the ResourceContainers table.");
        Assert.IsTrue(client.QueriesReceived.Any(q => q.StartsWith("Resources ", StringComparison.Ordinal)
            && q.Contains(", identity", StringComparison.Ordinal)),
            "The resource query must project top-level identity for shared template bindings.");
    }

    [TestMethod]
    public async Task FetchAllAsync_MapsResourceGroupRow_ToTenantNode()
    {
        var client = new StubArgQueryClient();
        client.ResourceGroupRows.Add(MakeRgRow(
            "/subscriptions/sub-1/resourceGroups/NetworkWatcherRG", "NetworkWatcherRG", "sub-1", "westeurope"));

        var enumerator = new TenantEnumerator(client);
        var (nodes, _) = await enumerator.FetchAllAsync();

        var rgNode = nodes.SingleOrDefault(n => n.ResourceId == "/subscriptions/sub-1/resourceGroups/NetworkWatcherRG");
        Assert.IsNotNull(rgNode, "Resource-group row should map to a TenantNode.");
        Assert.AreEqual("microsoft.resources/resourcegroups", rgNode!.Type);
        Assert.AreEqual("NetworkWatcherRG", rgNode.Name);
        Assert.AreEqual("NetworkWatcherRG", rgNode.ResourceGroup, "resourceGroup should self-reference the group's own name.");
    }

    [TestMethod]
    public async Task FetchAllAsync_MixesResourceGroupAndNormalResource_NoDedupCollision()
    {
        var client = new StubArgQueryClient();
        client.ResourceGroupRows.Add(MakeRgRow(
            "/subscriptions/sub-1/resourceGroups/rg1", "rg1", "sub-1", "westeurope"));
        client.ResourceRows.Add(MakeResourceRow(
            "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/acct1",
            "acct1", "microsoft.storage/storageaccounts", "sub-1", "rg1", "westeurope"));

        var enumerator = new TenantEnumerator(client);
        var (nodes, _) = await enumerator.FetchAllAsync();

        Assert.AreEqual(2, nodes.Count);
        Assert.IsTrue(nodes.Any(n => n.Type == "microsoft.resources/resourcegroups"));
        Assert.IsTrue(nodes.Any(n => n.Type == "microsoft.storage/storageaccounts"));
        Assert.AreEqual("SystemAssigned", nodes.Single(n => n.Type == "microsoft.storage/storageaccounts").IdentityType);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static JsonElement MakeRgRow(string id, string name, string subscriptionId, string location)
    {
        var json = $$"""
            {
                "id": "{{id}}", "name": "{{name}}", "type": "Microsoft.Resources/resourceGroups",
                "subscriptionId": "{{subscriptionId}}", "resourceGroup": "{{name}}", "location": "{{location}}",
                "properties": { "provisioningState": "Succeeded" }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement MakeResourceRow(string id, string name, string type, string subscriptionId, string resourceGroup, string location)
    {
        var json = $$"""
            {
                "id": "{{id}}", "name": "{{name}}", "type": "{{type}}",
                "subscriptionId": "{{subscriptionId}}", "resourceGroup": "{{resourceGroup}}", "location": "{{location}}",
                "properties": {},
                "identity": { "type": "SystemAssigned", "principalId": "principal-1" }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Minimal in-memory <see cref="IArgQueryClient"/> stub: routes <see cref="RunQueryAsync"/>
    /// by which ARG table the KQL targets, records every query string received.
    /// </summary>
    private sealed class StubArgQueryClient : IArgQueryClient
    {
        public List<string> QueriesReceived { get; } = [];
        public List<JsonElement> ResourceRows { get; } = [];
        public List<JsonElement> ResourceGroupRows { get; } = [];

        public Task<List<JsonElement>> RunQueryAsync(string kql)
        {
            QueriesReceived.Add(kql);
            if (kql.Contains("ResourceContainers", StringComparison.Ordinal))
                return Task.FromResult(new List<JsonElement>(ResourceGroupRows));
            if (kql.Contains("AuthorizationResources", StringComparison.Ordinal))
                return Task.FromResult(new List<JsonElement>());
            return Task.FromResult(new List<JsonElement>(ResourceRows));
        }

        public Task<JsonElement> GetResourceByIdAsync(string resourceId) => throw new NotSupportedException();
        public Task<JsonElement> GetResourceByIdAsync(string resourceId, bool useRestPath) => throw new NotSupportedException();
        public Task<Dictionary<string, JsonElement>> BatchArmGetAsync(IReadOnlyList<string> urls) => throw new NotSupportedException();
        public Task<JsonElement> RunAksCommandAsync(string resourceGroup, string clusterName, string command) => throw new NotSupportedException();
        public Task<List<string>> ListAcrRepositoriesAsync(string registryName, string subscriptionId) => Task.FromResult(new List<string>());
        public Task<Dictionary<string, string>> FetchSubscriptionNamesAsync() => Task.FromResult(new Dictionary<string, string>());
    }
}
