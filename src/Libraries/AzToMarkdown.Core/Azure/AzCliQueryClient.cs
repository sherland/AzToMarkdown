using System.Diagnostics;
using System.Text.Json;
using System.Threading.RateLimiting;
using AzToMarkdown.Core.Abstractions;
using CliWrap;
using CliWrap.Buffered;

namespace AzToMarkdown.Core.Azure;

/// <summary>
/// Thin wrapper around the Azure CLI (az) for authentication, extension management,
/// and Azure Resource Graph queries. Uses CliWrap for correct cross-platform
/// argument quoting (no cmd.exe string escaping required).
/// </summary>
public class AzCliQueryClient : IArgQueryClient
{
    /// <summary>
    /// The <see cref="ActivitySource"/> name emitted for every live az call.
    /// Register with OpenTelemetry via <c>tracing.AddSource(AzCliQueryClient.ActivitySourceName)</c>.
    /// </summary>
    public const string ActivitySourceName = "AzToMarkdown.AzCli";

    // One static ActivitySource per component — standard OTel .NET pattern.
    private static readonly ActivitySource s_source = new(ActivitySourceName);

    /// <summary>
    /// Process-wide token-bucket rate limiter for Azure Resource Graph queries.
    /// ARG enforces 15 reads per 5-second rolling window per user token (3/sec average).
    /// We use a token bucket with those exact parameters so we never exceed the burst
    /// limit, avoiding the Retry-After penalty imposed on throttled callers.
    /// Static so it is shared across all <see cref="AzCliQueryClient"/> instances —
    /// all instances in a process share the same ARG quota.
    /// </summary>
    private static readonly TokenBucketRateLimiter _argRateLimiter = new(
        new TokenBucketRateLimiterOptions
        {
            // TokenLimit = no burst beyond 1 second's worth (3 queries).
            // ARG enforces 15 per rolling 5-second window; a burst of 15 would
            // exhaust the window in <1 s and trigger throttling immediately.
            // Keeping TokenLimit == TokensPerPeriod ensures we never issue more
            // than 3 simultaneous queries, staying safely under the 3/s average.
            TokenLimit            = 3,
            QueueProcessingOrder  = QueueProcessingOrder.OldestFirst,
            QueueLimit            = 2000,                    // queue depth before dropping
            ReplenishmentPeriod   = TimeSpan.FromSeconds(1), // refill every second
            TokensPerPeriod       = 3,                       // 3/sec = 15/5s
            AutoReplenishment     = true,
        });

    /// <summary>Maximum number of retries when ARG responds with a throttling error.</summary>
    private const int ArgMaxRetries = 4;

    private readonly string?           _subscription;
    private readonly IProgressReporter _reporter;

    public AzCliQueryClient(
        string?           subscription = null,
        IProgressReporter? reporter    = null)
    {
        _subscription = subscription;
        _reporter     = reporter ?? NullProgressReporter.Instance;
    }

    // -------------------------------------------------------------------------
    // Pre-flight checks (static — called by CLI before the first request)
    // -------------------------------------------------------------------------

    /// <summary>Ensures az CLI is installed and accessible. Throws if not found.</summary>
    public static void CheckAzAvailable()
    {
        try
        {
            var result = Az("--version")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync().GetAwaiter().GetResult();

            if (result.ExitCode != 0) throw new Exception(result.StandardError);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("az CLI not found. Install from: https://aka.ms/installazurecliwindows", ex);
        }
    }

    /// <summary>Verifies az is authenticated. Throws if not logged in.</summary>
    public static async Task EnsureLoggedInAsync()
    {
        var result = await Az("account", "show", "--output", "none")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
            throw new InvalidOperationException("Not logged in to Azure CLI. Run 'az login' to authenticate.");
    }

    /// <summary>Checks for the named az extension and auto-installs it if missing.</summary>
    public static async Task EnsureExtensionAsync(string name)
    {
        var check = await Az("extension", "show", "--name", name, "--output", "none")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (check.ExitCode == 0) return;

        var install = await Az("extension", "add", "--name", name)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (install.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to install extension '{name}': {install.StandardError}");
    }

    // -------------------------------------------------------------------------
    // IArgQueryClient implementation
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<List<JsonElement>> RunQueryAsync(string kql)
    {
        // ActivityKind.Client: outbound call to Azure Resource Graph (external service).
        // db.* semantic conventions — ARG behaves like a query database.
        using var activity = s_source.StartActivity("azure_resource_graph query", ActivityKind.Client);
        activity?.SetTag("db.system",         "azure_resource_graph");
        activity?.SetTag("db.operation.name", "query");
        activity?.SetTag("db.query.text",      TruncateForTag(kql));
        activity?.SetTag("server.address",     "management.azure.com");
        activity?.SetTag("server.port",        443);
        if (_subscription is not null)
            activity?.SetTag("az.subscription", _subscription);

        try
        {
            var all   = new List<JsonElement>();
            int skip  = 0;
            const int batch = 1000;

            while (true)
            {
                var argList = new List<string>
                {
                    "graph", "query",
                    "-q",       kql,
                    "--first",  batch.ToString(),
                    "--skip",   skip.ToString(),
                    "--output", "json",
                };
                if (_subscription is not null)
                {
                    argList.Add("--subscriptions");
                    argList.Add(_subscription);
                }

                _reporter.Report($"Querying Azure Resource Graph (skip={skip})…");

                // Acquire a rate-limit token before calling the CLI so we stay within
                // ARG's 15-per-5s quota.  The bucket auto-replenishes at 3 tokens/sec.
                using var lease = await _argRateLimiter.AcquireAsync(permitCount: 1);
                if (!lease.IsAcquired)
                    throw new InvalidOperationException("ARG rate-limiter queue exhausted.");

                CliWrap.Buffered.BufferedCommandResult result;
                for (int attempt = 0; ; attempt++)
                {
                    result = await Az(argList)
                        .WithValidation(CommandResultValidation.None)
                        .ExecuteBufferedAsync();

                    if (result.ExitCode == 0) break;  // success

                    bool isThrottled =
                        result.StandardError.Contains("throttl",             StringComparison.OrdinalIgnoreCase) ||
                        result.StandardError.Contains("ResourcesThrottled",  StringComparison.OrdinalIgnoreCase) ||
                        result.StandardError.Contains("TooManyRequests",     StringComparison.OrdinalIgnoreCase) ||
                        result.StandardError.Contains("429",                 StringComparison.Ordinal);

                    if (!isThrottled || attempt >= ArgMaxRetries)
                        throw new InvalidOperationException($"az graph query failed: {result.StandardError}");

                    // Throttled: honour Retry-After if we can parse it, otherwise back off.
                    double delaySec = TryParseRetryAfter(result.StandardError)
                                      ?? Math.Min(60, 5 * Math.Pow(2, attempt)); // 5, 10, 20, 40 s
                    activity?.AddEvent(new ActivityEvent("throttled",
                        tags: new ActivityTagsCollection
                        {
                            { "attempt",    attempt + 1 },
                            { "delay_sec",  delaySec },
                        }));
                    _reporter.Report(
                        $"ARG throttled (attempt {attempt + 1}/{ArgMaxRetries}); " +
                        $"retrying in {delaySec:0}s…",
                        ProgressLevel.Warn);
                    await Task.Delay(TimeSpan.FromSeconds(delaySec));
                }

                using var doc = JsonDocument.Parse(result.StandardOutput);
                var root  = doc.RootElement;
                var data  = root.GetProperty("data");
                var count = root.GetProperty("count").GetInt32();

                foreach (var row in data.EnumerateArray())
                    all.Add(row.Clone());

                if (count < batch) break;
                skip += batch;
            }

            activity?.SetTag("az.result.count", all.Count);
            return all;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordExceptionEvent(activity, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<JsonElement> GetResourceByIdAsync(string resourceId)
    {
        // http.* semantic conventions — az resource show is an HTTP GET to ARM.
        using var activity = s_source.StartActivity("az resource show", ActivityKind.Client);
        activity?.SetTag("http.request.method",     "GET");
        activity?.SetTag("server.address",           "management.azure.com");
        activity?.SetTag("server.port",              443);
        activity?.SetTag("az.resource.id",           resourceId);

        try
        {
            var result = await Az("resource", "show", "--ids", resourceId, "--output", "json")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"az resource show failed for '{resourceId}': {result.StandardError}");

            activity?.SetTag("http.response.status_code", 200);
            var doc = JsonDocument.Parse(result.StandardOutput);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordExceptionEvent(activity, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<JsonElement> GetResourceByIdAsync(string resourceId, bool useRestPath)
    {
        if (!useRestPath) return await GetResourceByIdAsync(resourceId);

        var url = resourceId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            ? "https://management.azure.com" + resourceId
            : resourceId;

        // Direct ARM REST GET — url.full carries the full endpoint.
        using var activity = s_source.StartActivity("az rest GET", ActivityKind.Client);
        activity?.SetTag("http.request.method", "GET");
        activity?.SetTag("url.full",             url);
        activity?.SetTag("server.address",       "management.azure.com");
        activity?.SetTag("server.port",          443);

        try
        {
            var result = await Az("rest", "--method", "GET", "--url", url, "--output", "json")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
                throw new InvalidOperationException($"az rest failed for '{url}': {result.StandardError}");

            activity?.SetTag("http.response.status_code", 200);
            var doc = JsonDocument.Parse(result.StandardOutput);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordExceptionEvent(activity, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, JsonElement>> BatchArmGetAsync(IReadOnlyList<string> urls)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (urls.Count == 0) return result;

        // ARM batch supports ≤ 20 requests per call; chunk larger lists.
        const int BatchLimit = 20;
        using var activity = s_source.StartActivity("az rest batch GET", ActivityKind.Client);
        activity?.SetTag("http.request.method",    "POST");
        activity?.SetTag("server.address",          "management.azure.com");
        activity?.SetTag("server.port",             443);
        activity?.SetTag("az.batch.request_count", urls.Count);

        for (int offset = 0; offset < urls.Count; offset += BatchLimit)
        {
            var chunk = urls.Skip(offset).Take(BatchLimit).ToList();

            // Build batch body. Write to a temp file so the body never appears on the
            // command line, which avoids Windows cmd.exe's 8 191-char argument limit.
            var requestsJson = string.Join(",\n", chunk.Select(url =>
            {
                var full = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url
                         : "https://management.azure.com" + url;
                return $"    {{\"httpMethod\":\"GET\",\"url\":\"{full}\"}}";
            }));
            var batchBody = $"{{\"requests\":[\n{requestsJson}\n]}}";

            var tmpFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tmpFile, batchBody);

                var batchResult = await Az("rest",
                        "--method", "POST",
                        "--uri",    "https://management.azure.com/batch?api-version=2020-06-01",
                        "--body",   $"@{tmpFile}",
                        "--output", "json")
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (batchResult.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"az rest batch failed: {batchResult.StandardError}");

                activity?.SetTag("http.response.status_code", 200);

                using var batchDoc = JsonDocument.Parse(batchResult.StandardOutput);
                if (!batchDoc.RootElement.TryGetProperty("responses", out var responses)) continue;

                var responseArray = responses.EnumerateArray().ToList();
                for (int j = 0; j < responseArray.Count && j < chunk.Count; j++)
                {
                    var urlKey = chunk[j];
                    var resp   = responseArray[j];

                    if (!resp.TryGetProperty("httpStatusCode", out var sc) ||
                        sc.GetInt32() is < 200 or >= 300) continue;

                    if (!resp.TryGetProperty("content", out var content)) continue;

                    result[urlKey] = content.Clone();
                }
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                RecordExceptionEvent(activity, ex);
                throw;
            }
            finally
            {
                if (File.Exists(tmpFile)) File.Delete(tmpFile);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<JsonElement> RunAksCommandAsync(string resourceGroup, string clusterName, string command)
    {
        _reporter.Report($"Running command in AKS cluster {clusterName}…");

        using var activity = s_source.StartActivity("az aks command invoke", ActivityKind.Client);
        activity?.SetTag("server.address",        "management.azure.com");
        activity?.SetTag("server.port",           443);
        activity?.SetTag("az.aks.resource_group", resourceGroup);
        activity?.SetTag("az.aks.cluster_name",   clusterName);
        activity?.SetTag("az.aks.command",         command);

        try
        {
            var result = await Az("aks", "command", "invoke",
                    "--resource-group", resourceGroup,
                    "--name",           clusterName,
                    "--command",        command,
                    "--output",         "json")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"az aks command invoke failed for '{clusterName}': {result.StandardError}");

            var doc = JsonDocument.Parse(result.StandardOutput);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordExceptionEvent(activity, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<List<string>> ListAcrRepositoriesAsync(string registryName, string subscriptionId)
    {
        using var activity = s_source.StartActivity("az acr repository list", ActivityKind.Client);
        activity?.SetTag("az.acr.registry_name", registryName);
        activity?.SetTag("az.subscription.id", subscriptionId);

        var arguments = new List<string>
        {
            "acr", "repository", "list",
            "--name", registryName,
            "--output", "json",
        };
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            arguments.Add("--subscription");
            arguments.Add(subscriptionId);
        }

        try
        {
            var result = await Az(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"az acr repository list failed for '{registryName}': {result.StandardError.Trim()}");

            var repositories = JsonSerializer.Deserialize<List<string>>(result.StandardOutput) ?? [];
            repositories = repositories
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            activity?.SetTag("az.result.count", repositories.Count);
            return repositories;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordExceptionEvent(activity, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, string>> FetchSubscriptionNamesAsync()
    {
        using var activity = s_source.StartActivity("az account list", ActivityKind.Client);
        activity?.SetTag("server.address", "management.azure.com");
        activity?.SetTag("server.port",    443);

        var result = await Az("account", "list", "--output", "json")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            if (result.ExitCode != 0)
            {
                var ex = new InvalidOperationException($"az account list failed: {result.StandardError}");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                RecordExceptionEvent(activity, ex);
            }
            return map;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("id",   out var idEl)  &&
                    item.TryGetProperty("name", out var nameEl))
                {
                    var id   = idEl.GetString();
                    var name = nameEl.GetString();
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                        map[id] = name;
                }
            }
            activity?.SetTag("az.account.count", map.Count);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordExceptionEvent(activity, ex);
            /* best-effort */
        }

        return map;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Records a standard OpenTelemetry exception event on the activity using
    /// the base <see cref="Activity.AddEvent"/> API.  This avoids a dependency
    /// on the <c>OpenTelemetry</c> NuGet package in the Core library while still
    /// following the OTel exception semantic conventions.
    /// </summary>
    private static void RecordExceptionEvent(Activity? activity, Exception ex)
    {
        activity?.AddEvent(new ActivityEvent("exception",
            tags: new ActivityTagsCollection
            {
                { "exception.type",       ex.GetType().FullName ?? ex.GetType().Name },
                { "exception.message",    ex.Message },
                { "exception.stacktrace", ex.StackTrace ?? string.Empty },
            }));
    }

    /// <summary>
    /// Caps a string at 1 000 chars so long KQL queries don't bloat trace backends.
    /// </summary>
    private static string TruncateForTag(string value, int maxLen = 1_000)
        => value.Length <= maxLen ? value : string.Concat(value.AsSpan(0, maxLen), "\u2026");

    /// <summary>
    /// Tries to parse a <c>Retry-After</c> duration from an ARG throttle error message.
    /// Returns null when the header/value cannot be found in <paramref name="stderr"/>.
    /// </summary>
    private static double? TryParseRetryAfter(string stderr)
    {
        // az CLI sometimes surfaces "Retry after: X seconds" or "retryAfter: X" in stderr.
        foreach (var pattern in new[] { "retry after", "retryafter", "retry-after" })
        {
            var idx = stderr.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            // Skip past the keyword and any separators, then read the number.
            var rest = stderr[(idx + pattern.Length)..].TrimStart(':', ' ');
            var endIdx = 0;
            while (endIdx < rest.Length && (char.IsDigit(rest[endIdx]) || rest[endIdx] == '.'))
                endIdx++;

            if (endIdx > 0 && double.TryParse(rest[..endIdx], out var sec))
                return Math.Clamp(sec, 1, 120);
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Command builder
    // -------------------------------------------------------------------------

    private static Command Az(params string[] args) => Az((IEnumerable<string>)args);

    private static Command Az(IEnumerable<string> args)
    {
        if (OperatingSystem.IsWindows())
        {
            return Cli.Wrap("cmd.exe")
                .WithArguments(a =>
                {
                    a.Add("/c").Add("az");
                    foreach (var arg in args) a.Add(arg);
                });
        }

        return Cli.Wrap("az")
            .WithArguments(a =>
            {
                foreach (var arg in args) a.Add(arg);
            });
    }
}

