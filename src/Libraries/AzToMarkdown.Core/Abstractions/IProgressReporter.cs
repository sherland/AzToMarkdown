namespace AzToMarkdown.Core.Abstractions;

public enum ProgressLevel { Info, Success, Warn, Error }

/// <summary>
/// Abstraction over progress/status reporting.
/// The CLI implements this with Spectre.Console; the API uses a no-op or ILogger bridge.
/// </summary>
public interface IProgressReporter
{
    void Report(string message, ProgressLevel level = ProgressLevel.Info);
}

/// <summary>No-op implementation — discards all messages. Used by the API and tests.</summary>
public sealed class NullProgressReporter : IProgressReporter
{
    public static readonly NullProgressReporter Instance = new();
    public void Report(string message, ProgressLevel level = ProgressLevel.Info) { }
}
