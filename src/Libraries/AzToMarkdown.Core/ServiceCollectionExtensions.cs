using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Azure;
using AzToMarkdown.Core.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace AzToMarkdown.Core;

/// <summary>
/// DI registration helpers for AzToMarkdown.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all AzToMarkdown Core services with their default implementations.
    /// Call this in <c>builder.Services</c> from both the CLI and the API.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="subscriptionId">
    /// Optional Azure subscription ID to scope all ARG queries.
    /// If null, queries run across all subscriptions visible to the current az login context.
    /// </param>
    public static IServiceCollection AddAzToMarkdownCore(
        this IServiceCollection services,
        string? subscriptionId = null)
    {
        services.AddSingleton<IProgressReporter, NullProgressReporter>();

        services.AddSingleton<IArgQueryClient>(sp =>
        {
            var reporter = sp.GetRequiredService<IProgressReporter>();
            var rawClient = new AzCliQueryClient(subscriptionId, reporter);
            // Wrap with rate-limit retry decorator. All Azure CLI calls are funnelled through
            // ThrottlingRetryQueryClient, which enforces a process-wide concurrency cap and
            // retries on ARG/ARM RateLimiting errors with exponential back-off and jitter.
            // See ThrottlingRetryQueryClient for the full throttling model documentation.
            return new ThrottlingRetryQueryClient(rawClient, reporter);
        });

        // AzToMarkdown services
        services.AddSingleton<TenantEnumerator>(sp =>
            new TenantEnumerator(
                sp.GetRequiredService<IArgQueryClient>(),
                sp.GetRequiredService<IProgressReporter>()));

        services.AddSingleton<RelationshipExtractor>();

        services.AddSingleton<VaultTemplateEngine>(sp =>
            new VaultTemplateEngine(sp.GetRequiredService<IProgressReporter>()));

        services.AddSingleton<VaultWriter>(sp =>
            new VaultWriter(
                sp.GetRequiredService<VaultTemplateEngine>(),
                sp.GetRequiredService<IProgressReporter>()));

        return services;
    }
}
