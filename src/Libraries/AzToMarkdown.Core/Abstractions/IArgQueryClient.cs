using System.Text.Json;

namespace AzToMarkdown.Core.Abstractions;

/// <summary>
/// Abstraction over Azure CLI data operations exposed by AzToMarkdown.Core.
/// </summary>
public interface IArgQueryClient
{
    /// <summary>Executes a KQL ARG query with automatic paging; returns all result rows.</summary>
    Task<List<JsonElement>> RunQueryAsync(string kql);

    /// <summary>Fetches the full ARM resource JSON by its resource ID via <c>az resource show</c>.</summary>
    Task<JsonElement> GetResourceByIdAsync(string resourceId);

    /// <summary>
    /// Fetches a resource by ID. When <paramref name="useRestPath"/> is true, uses
    /// <c>az rest --method GET</c> instead of <c>az resource show</c>.
    /// </summary>
    Task<JsonElement> GetResourceByIdAsync(string resourceId, bool useRestPath);

    /// <summary>Sends multiple ARM REST GET requests as one ARM batch request.</summary>
    Task<Dictionary<string, JsonElement>> BatchArmGetAsync(IReadOnlyList<string> urls);

    /// <summary>Runs a kubectl command inside an AKS cluster via <c>az aks command invoke</c>.</summary>
    Task<JsonElement> RunAksCommandAsync(string resourceGroup, string clusterName, string command);

    /// <summary>Lists repository names in an Azure Container Registry.</summary>
    Task<List<string>> ListAcrRepositoriesAsync(string registryName, string subscriptionId);

    /// <summary>
    /// Returns a mapping of subscription ID → display name for all subscriptions
    /// visible in the current az login context.
    /// </summary>
    Task<Dictionary<string, string>> FetchSubscriptionNamesAsync();
}
