using System.Diagnostics;

namespace AzToMarkdown.Core.Diagnostics;

/// <summary>
/// Shared <see cref="ActivitySource"/> for internal AzToMarkdown.Core operations.
/// Emits <c>ActivityKind.Internal</c> spans for orchestration and discovery methods,
/// complementing the <c>AzToMarkdown.AzCli</c> source which covers outbound Azure CLI calls.
/// Register in OpenTelemetry via
/// <c>tracing.AddSource(CoreActivity.SourceName)</c>.
/// </summary>
internal static class CoreActivity
{
    public const string SourceName = "AzToMarkdown.Core";
    internal static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Sets span status to <c>Error</c> and records an <c>exception</c> event using the same
    /// format as <c>AzCliQueryClient</c> for consistent span-viewer display.
    /// </summary>
    internal static void RecordException(Activity? activity, Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.AddEvent(new ActivityEvent("exception",
            tags: new ActivityTagsCollection
            {
                { "exception.type",       ex.GetType().FullName ?? ex.GetType().Name },
                { "exception.message",    ex.Message },
                { "exception.stacktrace", ex.StackTrace ?? string.Empty },
            }));
    }
}
