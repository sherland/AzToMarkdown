using System.Globalization;
using System.Text.Json;
using AzToMarkdown.Core.Azure;
using AzToMarkdown.Core.Rendering;
using AzToMarkdown.Core.Vault;
using CliWrap;
using CliWrap.Buffered;

namespace AzToMarkdown.ScenarioTests;

/// <summary>
/// Live end-to-end validation of the schema-v1 lossless vault against REAL Azure resources.
///
/// The test creates a dedicated resource group (region: AZTOMARKDOWN_LIVE_LOCATION env var, default
/// <c>westeurope</c>) containing only free /
/// near-free resources (NSG, VNet+subnet with the NSG attached, NIC, user-assigned managed
/// identity, Standard_LRS storage account — no compute, no public IPs), runs the AzToMarkdown
/// pipeline against it, asserts byte-level value parity between the live ARG payloads and the
/// YAML front-matter, proves offline consumption via VaultReader + VaultQueryClient, and then
/// deletes the resource group.
///
/// Cleanup guarantees: the RG is tagged <c>aztomarkdown-live-test=1</c> + <c>created-utc</c>;
/// deletion runs in <c>finally</c>, and every run begins by sweeping expired orphaned
/// resource groups (tag older than 2 hours).
///
/// Run: dotnet test tests/AzToMarkdown.ScenarioTests --filter TestCategory=AzureLive
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AzToMarkdownLiveVaultTests
{
    // Overridable because subscriptions can restrict regions by policy.
    private static readonly string Location =
        Environment.GetEnvironmentVariable("AZTOMARKDOWN_LIVE_LOCATION") ?? "westeurope";
    private const string SweepTag    = "aztomarkdown-live-test";
    private const string CreatedTag  = "created-utc";
    private const string TimeFormat  = "yyyyMMddTHHmmssZ";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("AzureLive")]
    [Timeout(600_000)]
    public async Task LiveAzure_CreateResources_RunAzToMarkdown_ValidateLossless_DeleteRg()
    {
        await ScenarioTestHelpers.EnsureAzPrerequisitesAsync();
        await EnsureProvidersRegisteredAsync();

        var subscriptionId = await GetSubscriptionIdAsync();
        await SweepOrphanedResourceGroupsAsync();

        var rg = $"rg-aztomarkdown-live-test-{Guid.NewGuid():N}"[..38];
        var vaultDir = Path.Combine(Path.GetTempPath(), $"vault-live-{Guid.NewGuid():N}");

        try
        {
            // ── 1. Create the low-cost resource set ──────────────────────────
            var createdUtc = DateTime.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture);
            await AzOk("group", "create", "-n", rg, "-l", Location,
                       "--tags", $"{SweepTag}=1", $"{CreatedTag}={createdUtc}");
            Log($"Created resource group {rg}");

            await AzOk("network", "nsg", "create", "-g", rg, "-n", "nsg-aztomarkdown-test", "-l", Location);
            await AzOk("network", "vnet", "create", "-g", rg, "-n", "vnet-aztomarkdown-test", "-l", Location,
                       "--address-prefix", "10.42.0.0/16",
                       "--subnet-name", "snet-test", "--subnet-prefixes", "10.42.1.0/24",
                       "--network-security-group", "nsg-aztomarkdown-test");
            await AzOk("network", "nic", "create", "-g", rg, "-n", "nic-aztomarkdown-test", "-l", Location,
                       "--vnet-name", "vnet-aztomarkdown-test", "--subnet", "snet-test");
            await AzOk("identity", "create", "-g", rg, "-n", "id-aztomarkdown-test", "-l", Location);
            // Locked-down networking satisfies common storage security policies; the test reads
            // ARM metadata only and never accesses the data plane.
            var storageName = $"staztomarkdown{Guid.NewGuid():N}"[..22];
            await AzOk("storage", "account", "create", "-g", rg, "-n", storageName, "-l", Location,
                       "--sku", "Standard_LRS", "--kind", "StorageV2",
                       "--allow-blob-public-access", "false",
                       "--public-network-access", "Disabled",
                       "--default-action", "Deny",
                       "--min-tls-version", "TLS1_2",
                       "--https-only", "true");
            Log("Created NSG, VNet+subnet (NSG attached), NIC, managed identity, storage account.");

            // ── 2. Wait for ARG eventual consistency ──────────────────────────
            var reporter = new CapturingProgressReporter();
            var argClient = new AzCliQueryClient(subscriptionId, reporter);

            const int expectedResources = 5; // nsg, vnet, nic, identity, storage
            var deadline = DateTime.UtcNow.AddMinutes(5);
            int visible = 0;
            while (DateTime.UtcNow < deadline)
            {
                var rows = await argClient.RunQueryAsync(
                    $"Resources | where resourceGroup =~ '{rg}' | project id");
                visible = rows.Count;
                if (visible >= expectedResources) break;
                Log($"ARG shows {visible}/{expectedResources} resources — waiting for eventual consistency…");
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
            if (visible < expectedResources)
                Assert.Inconclusive($"ARG only showed {visible}/{expectedResources} resources within 5 minutes — ARG replication lag, not a product bug.");

            // ── 3. Run the AzToMarkdown pipeline, filtered to the test RG ────
            var (allNodes, subNames) = await new TenantEnumerator(argClient, reporter).FetchAllAsync();
            var rgNodes = allNodes
                .Where(n => n.ResourceGroup.Equals(rg, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.IsTrue(rgNodes.Count >= expectedResources,
                $"expected at least {expectedResources} nodes in {rg}; got {rgNodes.Count}: {string.Join(", ", rgNodes.Select(n => n.Name))}");

            var graph = new RelationshipExtractor().Build(rgNodes);
            new VaultWriter(new VaultTemplateEngine(reporter), reporter).WriteAll(graph, subNames, vaultDir);
            Log($"Vault written to {vaultDir} ({rgNodes.Count} nodes).");

            // ── 4a. One .md per resource, schema v1, valid YAML ──────────────
            var subName = subNames.TryGetValue(subscriptionId, out var sn) ? sn : subscriptionId;
            foreach (var expected in new[] { "nsg-aztomarkdown-test", "vnet-aztomarkdown-test", "nic-aztomarkdown-test", "id-aztomarkdown-test", storageName })
            {
                var file = Path.Combine(vaultDir, "infrastructure",
                    VaultWriter.Sanitize(subName), rg, $"{expected}.md");
                Assert.IsTrue(File.Exists(file), $"vault file missing for {expected}: {file}");

                var parsed = VaultReader.ParseFile(file);
                Assert.IsNotNull(parsed, $"front-matter unparseable for {expected}");
                Assert.AreEqual(FrontMatterSerializer.SchemaVersion, parsed.SchemaVersion);
                Assert.AreEqual(expected, parsed.Node.Name);

                // 4b. Byte-level value parity: YAML round-trip vs the live ARG payload
                var live = rgNodes.Single(n => n.Name.Equals(expected, StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(
                    YamlJsonConverter.JsonDeepEquals(live.Properties, parsed.Node.Properties, out var diff),
                    $"lossless parity failed for {expected} at: {diff}");
            }
            Log("All 5 resource files exist with schema v1 front-matter and lossless properties.");

            // ── 4c. Cross-resource relationship: NIC → subnet edge persisted ─
            var nicParsed = VaultReader.ParseFile(Path.Combine(vaultDir, "infrastructure",
                VaultWriter.Sanitize(subName), rg, "nic-aztomarkdown-test.md"))!;
            Assert.IsTrue(
                nicParsed.Relationships.Any(r =>
                    r.Direction == "outbound" && r.Label == "subnet"
                    && r.Id.EndsWith("/subnets/snet-test", StringComparison.OrdinalIgnoreCase)),
                $"NIC must have an outbound 'subnet' relationship to snet-test; got: {string.Join("; ", nicParsed.Relationships.Select(r => $"{r.Direction}/{r.Label}/{r.Id}"))}");

            // ── 4d. VaultReader reconstructs everything losslessly ───────────
            var readBack = new VaultReader(reporter).ReadAll(vaultDir);
            foreach (var live in rgNodes)
            {
                var read = readBack.Nodes.SingleOrDefault(n =>
                    n.ResourceId.Equals(live.ResourceId, StringComparison.OrdinalIgnoreCase));
                Assert.IsNotNull(read, $"node missing after ReadAll: {live.ResourceId}");
                Assert.IsTrue(YamlJsonConverter.JsonDeepEquals(live.Properties, read.Properties, out var diff),
                    $"ReadAll parity failed for {live.Name} at: {diff}");
            }

            // ── 4e. Offline proof: TenantEnumerator over VaultQueryClient ────
            var vaultClient = new VaultQueryClient(vaultDir, reporter);
            var (offlineNodes, offlineSubs) = await new TenantEnumerator(vaultClient, reporter).FetchAllAsync();
            Assert.AreEqual(subName, offlineSubs[subscriptionId]);
            foreach (var live in rgNodes)
                Assert.IsTrue(
                    offlineNodes.Any(n => n.ResourceId.Equals(live.ResourceId, StringComparison.OrdinalIgnoreCase)),
                    $"offline enumeration missing {live.ResourceId}");
            Log("Offline vault enumeration reproduced the live node set. All assertions passed.");
        }
        finally
        {
            // Guaranteed cleanup — --no-wait keeps the test fast; the orphan sweep at the start
            // of every run is the backstop should this request fail.
            var (exit, _, err) = await AzAsync("group", "delete", "-n", rg, "--yes", "--no-wait");
            Log(exit == 0
                ? $"Deletion of {rg} accepted (running asynchronously in Azure)."
                : $"WARNING: could not delete {rg}: {err} — the next run's orphan sweep will retry.");

            if (Directory.Exists(vaultDir))
                Directory.Delete(vaultDir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Orphan sweep
    // ─────────────────────────────────────────────────────────────────────────

    private async Task SweepOrphanedResourceGroupsAsync()
    {
        var (exit, stdout, _) = await AzAsync("group", "list",
            "--tag", $"{SweepTag}=1", "--query", "[].{name:name, tags:tags}", "-o", "json");
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return;

        using var doc = JsonDocument.Parse(stdout);
        foreach (var group in doc.RootElement.EnumerateArray())
        {
            var name = group.GetProperty("name").GetString() ?? "";
            var createdText = group.TryGetProperty("tags", out var tags)
                           && tags.ValueKind == JsonValueKind.Object
                           && tags.TryGetProperty(CreatedTag, out var created)
                ? created.GetString() : null;

            var isStale = createdText is null
                || !DateTime.TryParseExact(createdText, TimeFormat, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdUtc)
                || createdUtc < DateTime.UtcNow.AddHours(-2);

            if (isStale && name.StartsWith("rg-aztomarkdown-live-test-", StringComparison.OrdinalIgnoreCase))
            {
                Log($"Sweeping expired orphaned test resource group: {name}");
                await AzAsync("group", "delete", "-n", name, "--yes", "--no-wait");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // az helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly string[] RequiredProviders =
        ["Microsoft.Network", "Microsoft.ManagedIdentity", "Microsoft.Storage"];

    /// <summary>
    /// Registers any of <see cref="RequiredProviders"/> that aren't already registered on the
    /// target subscription. A brand-new subscription starts with most non-core providers
    /// unregistered, which otherwise surfaces as a confusing failure deep into resource creation
    /// (e.g. <c>az storage account create</c> failing with a generic <c>SubscriptionNotFound</c>
    /// instead of a clear registration error). Registration is idempotent and near-instant when
    /// already registered, so this is cheap to run every time.
    /// </summary>
    private async Task EnsureProvidersRegisteredAsync()
    {
        foreach (var ns in RequiredProviders)
        {
            var state = await ProviderStateAsync(ns);
            if (state == "Registered") continue;

            Log($"Resource provider {ns} is '{state}' — registering (can take a few minutes on a fresh subscription)…");
            await AzOk("provider", "register", "--namespace", ns);

            for (var attempt = 0; attempt < 20 && state != "Registered"; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                state = await ProviderStateAsync(ns);
            }
            Assert.AreEqual("Registered", state, $"{ns} did not finish registering within the polling window");
            Log($"Resource provider {ns} is now Registered.");
        }
    }

    private static async Task<string> ProviderStateAsync(string ns)
    {
        var (_, stdout, _) = await AzAsync("provider", "show", "--namespace", ns, "--query", "registrationState", "-o", "tsv");
        return stdout.Trim();
    }

    private static async Task<string> GetSubscriptionIdAsync()
    {
        var (exit, stdout, err) = await AzAsync("account", "show", "--query", "id", "-o", "tsv");
        if (exit != 0) Assert.Inconclusive($"az account show failed: {err}");
        return stdout.Trim();
    }

    /// <summary>
    /// Runs an az command and asserts it succeeded. Authorization/token problems are an
    /// environment issue (missing role, expired PIM activation, conditional access) — the test
    /// is skipped as Inconclusive rather than failed, mirroring EnsureAzPrerequisitesAsync.
    /// </summary>
    private async Task AzOk(params string[] args)
    {
        var (exit, _, err) = await AzAsync(args);
        if (exit != 0 && (err.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase)
                       || err.Contains("AADSTS", StringComparison.OrdinalIgnoreCase)
                       || err.Contains("RequestDisallowedByPolicy", StringComparison.OrdinalIgnoreCase)
                       || err.Contains("RequestDisallowedByAzure", StringComparison.OrdinalIgnoreCase)))
            Assert.Inconclusive($"Azure permissions/authentication/policy problem — skipping live test: az {string.Join(' ', args)}: {err}");
        Assert.AreEqual(0, exit, $"az {string.Join(' ', args)} failed: {err}");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> AzAsync(params string[] args)
    {
        // Same invocation shape as AzCliQueryClient: on Windows az is a .cmd, so go through cmd.exe.
        var isWindows = OperatingSystem.IsWindows();
        var result = await Cli.Wrap(isWindows ? "cmd.exe" : "az")
            .WithArguments(isWindows ? ["/c", "az", .. args] : args)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        return (result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private void Log(string message) => TestContext.WriteLine($"[live-vault] {message}");
}
