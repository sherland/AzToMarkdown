using System.Text.Json;
using AzToMarkdown.Core.Abstractions;
using AzToMarkdown.Core.Diagnostics;

namespace AzToMarkdown.Core.Azure;

/// <summary>
/// A decorator over <see cref="IArgQueryClient"/> that transparently handles Azure Resource Graph
/// (ARG) and ARM rate-limiting errors by retrying with exponential back-off and jitter.
///
/// <para><b>How Azure Resource Graph throttling works</b><br/>
/// ARG enforces a per-user quota of roughly 15 queries per 5-second window. When the quota is
/// exceeded, every subsequent query returns HTTP 429 with error code <c>"RateLimiting"</c> until
/// the window resets. The REST response normally carries two headers that allow precise back-off:
/// <list type="bullet">
///   <item><c>x-ms-user-quota-remaining</c> — remaining query slots in the current window.</item>
///   <item><c>x-ms-user-quota-resets-after</c> — time (hh:mm:ss) until the window resets.</item>
/// </list>
/// Because this codebase uses the <c>az</c> CLI rather than the REST SDK, those headers are
/// <b>not</b> surfaced. The CLI only exposes the JSON error body as stderr, which contains
/// <c>"code":"RateLimiting"</c>. We therefore rely on blind exponential back-off seeded to the
/// documented 5-second reset window.</para>
///
/// <para><b>Back-off formula</b><br/>
/// On attempt <em>n</em> (1-based) the delay is:
/// <code>
///   delay = BaseDelay × 2^(n−1) × jitter
///   jitter ∈ [0.75, 1.25]  (uniformly random)
/// </code>
/// With the defaults this yields approximately 5 s, 10 s, and 20 s, giving the quota window
/// multiple chances to reset before the final throw.</para>
///
/// <para><b>Proactive concurrency cap</b><br/>
/// A process-wide <see cref="SemaphoreSlim"/> limits the number of simultaneous Azure CLI calls
/// to <see cref="ThrottlingOptions.MaxConcurrentQueries"/>. With a cap of 5, the client issues
/// at most ~10–15 queries per 5-second window under normal load, staying well within the quota
/// and reducing the frequency of rate-limit errors. The semaphore is static so the cap is shared
/// across all <see cref="ThrottlingRetryQueryClient"/> instances in a process (e.g. when multiple
/// callers issue concurrent queries).</para>
///
/// <para><b>References</b><br/>
/// <see href="https://aka.ms/resourcegraph-throttling">Guidance for throttled requests in Azure Resource Graph</see></para>
/// </summary>
public sealed class ThrottlingRetryQueryClient : IArgQueryClient
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Tuning knobs for the retry and concurrency behaviour.
    /// Pass a custom instance to the constructor to override defaults.
    /// </summary>
    public sealed record ThrottlingOptions
    {
        /// <summary>
        /// Maximum number of retry attempts <em>after</em> the first failure.
        /// Total attempts = MaxRetries + 1.  Default: 3.
        /// </summary>
        public int MaxRetries { get; init; } = 3;

        /// <summary>
        /// Base delay for the first retry. Subsequent retries double this value.
        /// Defaults to 5 seconds — the documented ARG quota-reset window.
        /// </summary>
        public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
    /// Maximum number of Azure CLI calls that may run concurrently, process-wide.
        /// Keeping this at 5 ensures that under normal load the process stays within the
        /// 15 queries/5 s ARG quota, reducing the chance of hitting throttling at all.
        /// Default: 5.
        /// </summary>
        public int MaxConcurrentQueries { get; init; } = 5;
    }

    // -----------------------------------------------------------------------
    // Fields
    // -----------------------------------------------------------------------

    private readonly IArgQueryClient   _inner;
    private readonly IProgressReporter _reporter;
    private readonly ThrottlingOptions _options;

    /// <summary>
    /// Process-wide concurrency gate.  Lazily initialised on first construction so that the
    /// semaphore size is determined by the first <see cref="ThrottlingOptions"/> instance used.
    /// Subsequent instances reuse the same semaphore regardless of their own options value.
    /// </summary>
    private static SemaphoreSlim? s_concurrency;
    private static readonly object s_concurrencyLock = new();

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initialises a new <see cref="ThrottlingRetryQueryClient"/>.
    /// </summary>
    /// <param name="inner">
    ///   The underlying <see cref="IArgQueryClient"/> implementation that performs the actual
    ///   Azure CLI calls.
    /// </param>
    /// <param name="reporter">
    ///   Progress sink; receives human-readable messages when a retry is triggered.
    /// </param>
    /// <param name="options">
    ///   Optional tuning; uses <see cref="ThrottlingOptions"/> defaults when null.
    /// </param>
    public ThrottlingRetryQueryClient(
        IArgQueryClient    inner,
        IProgressReporter  reporter,
        ThrottlingOptions? options = null)
    {
        _inner    = inner;
        _reporter = reporter;
        _options  = options ?? new ThrottlingOptions();

        // Initialise the process-wide semaphore exactly once.
        if (s_concurrency is null)
        {
            lock (s_concurrencyLock)
            {
                s_concurrency ??= new SemaphoreSlim(
                    _options.MaxConcurrentQueries,
                    _options.MaxConcurrentQueries);
            }
        }
    }

    // -----------------------------------------------------------------------
    // IArgQueryClient implementation — each method is wrapped by the retry loop
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<List<JsonElement>> RunQueryAsync(string kql)
        => ExecuteWithRetryAsync(() => _inner.RunQueryAsync(kql), "RunQueryAsync");

    /// <inheritdoc/>
    public Task<JsonElement> GetResourceByIdAsync(string resourceId)
        => ExecuteWithRetryAsync(() => _inner.GetResourceByIdAsync(resourceId), "GetResourceByIdAsync");

    /// <inheritdoc/>
    public Task<JsonElement> GetResourceByIdAsync(string resourceId, bool useRestPath)
        => ExecuteWithRetryAsync(() => _inner.GetResourceByIdAsync(resourceId, useRestPath), "GetResourceByIdAsync(rest)");

    /// <inheritdoc/>
    public Task<Dictionary<string, JsonElement>> BatchArmGetAsync(IReadOnlyList<string> urls)
        => ExecuteWithRetryAsync(() => _inner.BatchArmGetAsync(urls), "BatchArmGetAsync");

    /// <inheritdoc/>
    public Task<JsonElement> RunAksCommandAsync(string resourceGroup, string clusterName, string command)
        => ExecuteWithRetryAsync(() => _inner.RunAksCommandAsync(resourceGroup, clusterName, command), "RunAksCommandAsync");

    /// <inheritdoc/>
    public Task<List<string>> ListAcrRepositoriesAsync(string registryName, string subscriptionId)
        => ExecuteWithRetryAsync(
            () => _inner.ListAcrRepositoriesAsync(registryName, subscriptionId),
            "ListAcrRepositoriesAsync");

    /// <inheritdoc/>
    public Task<Dictionary<string, string>> FetchSubscriptionNamesAsync()
        => ExecuteWithRetryAsync(() => _inner.FetchSubscriptionNamesAsync(), "FetchSubscriptionNamesAsync");

    // -----------------------------------------------------------------------
    // Core retry logic
    // -----------------------------------------------------------------------

    /// <summary>
    /// Acquires the concurrency semaphore, invokes <paramref name="action"/>, and retries on
    /// rate-limit errors using exponential back-off with jitter.
    /// </summary>
    /// <typeparam name="T">The result type returned by the underlying call.</typeparam>
    /// <param name="action">Factory that starts the Azure CLI call.</param>
    /// <param name="operationName">Human-readable name used in log messages and OTel tags.</param>
    /// <param name="ct">Cancellation token forwarded to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>>    action,
        string           operationName,
        CancellationToken ct = default)
    {
        var semaphore  = s_concurrency!;
        var lastEx     = default(Exception);

        // MaxRetries is the number of *additional* attempts after the first failure.
        // Total maximum attempts = MaxRetries + 1.
        for (int attempt = 1; attempt <= _options.MaxRetries + 1; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan delay;
            await semaphore.WaitAsync(ct);
            try
            {
                return await action();
            }
            catch (InvalidOperationException ex) when (IsRateLimitError(ex))
            {
                lastEx = ex;

                if (attempt > _options.MaxRetries)
                    break; // exhausted — fall through to rethrow

                delay = CalculateDelay(attempt);

                _reporter.Report(
                    $"ARG/ARM rate limit hit ({operationName}) — " +
                    $"retrying in {delay.TotalSeconds:F1} s (attempt {attempt}/{_options.MaxRetries})…",
                    ProgressLevel.Warn);

                // Emit an OTel span so retries are visible in the configured trace backend.
                using var retryActivity = CoreActivity.Source.StartActivity("arg.throttle.retry");
                retryActivity?.SetTag("arg.retry.operation", operationName);
                retryActivity?.SetTag("arg.retry.attempt",   attempt);
                retryActivity?.SetTag("arg.retry.delay_ms",  (int)delay.TotalMilliseconds);
            }
            finally
            {
                // Release the concurrency slot BEFORE the back-off delay so other queued
                // queries can proceed while this one waits out the quota window. Holding
                // the slot across Task.Delay would serialize the whole client under throttling.
                semaphore.Release();
            }

            await Task.Delay(delay, ct);
        }

        // All retries exhausted — propagate the last rate-limit exception.
        throw lastEx!;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> represents an Azure
    /// rate-limiting response.
    ///
    /// <para>ARG error — JSON contains <c>"code": "RateLimiting"</c>:
    /// <code>
    /// ERROR: { "code": "RateLimiting", "message": "…", "details": […] }
    /// </code>
    /// </para>
    /// <para>ARM throttle errors typically contain <em>TooManyRequests</em> or a
    /// retry-after value.</para>
    /// </summary>
    private static bool IsRateLimitError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("RateLimiting",    StringComparison.OrdinalIgnoreCase)
            || msg.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("RetryAfter",      StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes the back-off delay for the given 1-based <paramref name="attempt"/> number.
    /// Formula: <c>BaseDelay × 2^(attempt−1) × jitter</c> where jitter ∈ [0.75, 1.25].
    /// </summary>
    private TimeSpan CalculateDelay(int attempt)
    {
        // Exponential base: 5s → 10s → 20s for attempts 1, 2, 3
        var baseMs  = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);

        // Jitter ±25% to spread retries when multiple parallel calls are throttled simultaneously,
        // matching the "1 to 5× the resetAfter" pattern from the ARG throttling guidance.
        var jitter  = 0.75 + (Random.Shared.NextDouble() * 0.5);  // [0.75, 1.25]
        var delayMs = (int)(baseMs * jitter);

        return TimeSpan.FromMilliseconds(delayMs);
    }
}
