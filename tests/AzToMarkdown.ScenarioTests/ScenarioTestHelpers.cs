using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Azure;

namespace AzToMarkdown.ScenarioTests;

/// <summary>
/// IProgressReporter that accumulates all messages in memory for post-run inspection.
/// </summary>
internal sealed class CapturingProgressReporter : IProgressReporter
{
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages => _messages;

    public void Report(string message, ProgressLevel level = ProgressLevel.Info)
        => _messages.Add($"[{level}] {message}");

    public string GetLog() => string.Join("\n", _messages);
}

/// <summary>
/// Shared helpers for scenario comparison tests.
/// </summary>
internal static class ScenarioTestHelpers
{
    /// <summary>
    /// Runs az pre-flight checks. Calls <see cref="Assert.Inconclusive"/> when the
    /// environment is not set up (no az CLI, not logged in, extension missing), so that
    /// the test is skipped rather than hard-failing in CI.
    /// </summary>
    internal static async Task EnsureAzPrerequisitesAsync()
    {
        try
        {
            AzCliQueryClient.CheckAzAvailable();
            await AzCliQueryClient.EnsureLoggedInAsync();
            await AzCliQueryClient.EnsureExtensionAsync("resource-graph");
        }
        catch (InvalidOperationException ex)
        {
            Assert.Inconclusive($"Azure pre-flight failed — skipping test: {ex.Message}");
        }
    }
}
