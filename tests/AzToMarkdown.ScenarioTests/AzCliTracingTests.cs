using System.Diagnostics;
using AzToMarkdown.Core.Azure;

namespace AzToMarkdown.ScenarioTests;

/// <summary>
/// Verifies that AzCliQueryClient emits properly configured Activity (OTel spans)
/// for every live Azure call.
/// Important async ordering note:
///  For async methods that complete normally, the C# async state machine finalises
///  local variables AFTER the Task is marked complete, so ActivityStopped can fire
///  after the caller continuation.  We therefore assert on 'started' (fires during
///  StartActivity, before any await) for happy-path tests, and use
///  WaitForActivityStoppedAsync for tags set after awaits.
///  Exception-path tests are unaffected since finally blocks run before the exception
///  reaches the caller.
/// Requires: az CLI, az login, resource-graph extension.
/// Filter: TestCategory=Integration
/// </summary>
[TestClass]
public class AzCliTracingTests
{
    private const string EmptyKql            = "Resources | where 1 == 0 | take 1";
    private const string InvalidKql          = "THISISINVALIDKQL!!!";
    private const string NonExistentResourceId =
        "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/fake-rg" +
        "/providers/Microsoft.Network/publicIPAddresses/fake-pip";

    private static (ActivityListener Listener, List<Activity> Started, List<Activity> Stopped)
        InstallListener()
    {
        var started = new List<Activity>();
        var stopped = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo       = src => src.Name == AzCliQueryClient.ActivitySourceName,
            Sample               = (ref ActivityCreationOptions<ActivityContext> _)
                                    => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId  = (ref ActivityCreationOptions<string> _)
                                    => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted      = a => started.Add(a),
            ActivityStopped      = a => stopped.Add(a),
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, started, stopped);
    }

    private static async Task WaitForActivityStoppedAsync(List<Activity> stopped,
        int expectedCount, int timeoutMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (stopped.Count < expectedCount && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private static AzCliQueryClient LiveClient()
        => new(subscription: null);

    [ClassInitialize]
    public static async Task PreFlight(TestContext _)
    {
        try
        {
            AzCliQueryClient.CheckAzAvailable();
            await AzCliQueryClient.EnsureLoggedInAsync();
            await AzCliQueryClient.EnsureExtensionAsync("resource-graph");
        }
        catch (InvalidOperationException ex)
        {
            Assert.Inconclusive($"Azure pre-flight failed: {ex.Message}");
        }
    }

    [TestMethod, TestCategory("Integration")]
    public async Task RunQueryAsync_LiveCall_EmitsClientActivity()
    {
        var (listener, started, stopped) = InstallListener();
        using (listener) { await LiveClient().RunQueryAsync(EmptyKql); }

        Assert.AreEqual(1, started.Count, "Expected one started activity.");
        var span = started[0];
        Assert.AreEqual("azure_resource_graph query", span.OperationName);
        Assert.AreEqual(ActivityKind.Client,           span.Kind);
        Assert.AreEqual("azure_resource_graph",        span.GetTagItem("db.system")?.ToString());
        Assert.AreEqual("query",                       span.GetTagItem("db.operation.name")?.ToString());
        Assert.AreEqual("management.azure.com",        span.GetTagItem("server.address")?.ToString());
        Assert.AreEqual("443",                         span.GetTagItem("server.port")?.ToString());
        Assert.IsNotNull(span.GetTagItem("db.query.text"), "db.query.text must be set");

        await WaitForActivityStoppedAsync(stopped, 1);
        Assert.AreEqual(1, stopped.Count, "Activity must be stopped after method returns.");
        Assert.IsNotNull(stopped[0].GetTagItem("az.result.count"), "az.result.count must be set");
        Assert.AreEqual(ActivityStatusCode.Unset, stopped[0].Status);
    }

    [TestMethod, TestCategory("Integration")]
    public async Task RunQueryAsync_LongKql_TruncatesQueryTextTag()
    {
        var longKql = "Resources | where 1 == 0 // " + new string('x', 2_000);
        var (listener, started, _) = InstallListener();
        using (listener) { try { await LiveClient().RunQueryAsync(longKql); } catch { } }

        Assert.IsTrue(started.Count > 0, "Expected at least one started activity.");
        var tag = started[0].GetTagItem("db.query.text")?.ToString() ?? "";
        Assert.IsTrue(tag.Length <= 1_001, $"db.query.text exceeds 1 000 chars: {tag.Length}");
    }

    [TestMethod, TestCategory("Integration")]
    public async Task RunQueryAsync_FailedQuery_SetsErrorStatus_AndRecordsException()
    {
        var (listener, _, stopped) = InstallListener();
        using (listener)
        {
            try { await LiveClient().RunQueryAsync(InvalidKql); }
            catch (InvalidOperationException) { }
        }

        Assert.AreEqual(1, stopped.Count, "Expected one stopped activity for the failed query.");
        var span = stopped[0];
        Assert.AreEqual(ActivityStatusCode.Error, span.Status, "Status must be Error.");
        Assert.IsFalse(string.IsNullOrEmpty(span.StatusDescription));

        var exEvent = span.Events.FirstOrDefault(e => e.Name == "exception");
        Assert.IsNotNull(exEvent.Name, "exception event must be recorded.");
        Assert.AreEqual(typeof(InvalidOperationException).FullName,
            exEvent.Tags.FirstOrDefault(t => t.Key == "exception.type").Value?.ToString());
    }

    [TestMethod, TestCategory("Integration")]
    public async Task GetResourceByIdAsync_NotFound_SetsErrorStatus()
    {
        var (listener, _, stopped) = InstallListener();
        using (listener)
        {
            try { await LiveClient().GetResourceByIdAsync(NonExistentResourceId); }
            catch (InvalidOperationException) { }
        }

        Assert.AreEqual(1, stopped.Count);
        var span = stopped[0];
        Assert.AreEqual("az resource show",      span.OperationName);
        Assert.AreEqual(ActivityKind.Client,      span.Kind);
        Assert.AreEqual("GET",                    span.GetTagItem("http.request.method")?.ToString());
        Assert.AreEqual("management.azure.com",   span.GetTagItem("server.address")?.ToString());
        Assert.AreEqual(NonExistentResourceId,    span.GetTagItem("az.resource.id")?.ToString());
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        Assert.IsNotNull(span.Events.FirstOrDefault(e => e.Name == "exception").Name,
            "exception event must be recorded.");
    }

    [TestMethod, TestCategory("Integration")]
    public async Task GetResourceByIdAsync_RestPath_EmitsClientActivity()
    {
        const string rgUrl =
            "https://management.azure.com/subscriptions/deafd2fb-c3d6-47f5-9645-cc34d54d4317" +
            "/resourceGroups/rg-aztomarkdown-tracing?api-version=2021-04-01";

        var (listener, started, stopped) = InstallListener();
        using (listener) { await LiveClient().GetResourceByIdAsync(rgUrl, useRestPath: true); }

        Assert.AreEqual(1, started.Count);
        var span = started[0];
        Assert.AreEqual("az rest GET",          span.OperationName);
        Assert.AreEqual(ActivityKind.Client,     span.Kind);
        Assert.AreEqual("GET",                   span.GetTagItem("http.request.method")?.ToString());
        Assert.AreEqual("management.azure.com",  span.GetTagItem("server.address")?.ToString());
        Assert.IsNotNull(span.GetTagItem("url.full"));

        await WaitForActivityStoppedAsync(stopped, 1);
        Assert.AreEqual(1, stopped.Count);
        Assert.AreEqual("200",                    stopped[0].GetTagItem("http.response.status_code")?.ToString());
        Assert.AreEqual(ActivityStatusCode.Unset, stopped[0].Status);
    }

    [TestMethod, TestCategory("Integration")]
    public async Task FetchSubscriptionNamesAsync_EmitsClientActivity()
    {
        var (listener, started, stopped) = InstallListener();
        using (listener) { await LiveClient().FetchSubscriptionNamesAsync(); }

        Assert.AreEqual(1, started.Count);
        var span = started[0];
        Assert.AreEqual("az account list",      span.OperationName);
        Assert.AreEqual(ActivityKind.Client,     span.Kind);
        Assert.AreEqual("management.azure.com",  span.GetTagItem("server.address")?.ToString());

        await WaitForActivityStoppedAsync(stopped, 1);
        Assert.AreEqual(1, stopped.Count);
        Assert.IsNotNull(stopped[0].GetTagItem("az.account.count"));
        Assert.AreEqual(ActivityStatusCode.Unset, stopped[0].Status);
    }
}
