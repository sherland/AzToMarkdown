namespace AzResourceDetails.Templating.Tests;

// RegionDisplayNames ships an embedded default (baked from AzResourceDetailsDownloader's own
// config/azure-locations.json — see the csproj's EmbeddedResource item and this class' own comment)
// so a host gets correct behavior without calling Configure at all; Configure/ResetToEmbeddedDefault
// are the explicit override/revert seam. Every test here calls one or the other at its own start
// rather than relying on execution order, since RegionDisplayNames' backing table is shared mutable
// static state across the whole assembly (see AssemblyInfo.cs' DisableTestParallelization).
public class RegionDisplayNamesTests
{
    [Fact]
    public void TryGetDisplayName_ConfiguredCode_ReturnsTrueAndTheDisplayName()
    {
        RegionDisplayNames.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["norwayeast"] = "Norway East",
        });

        var found = RegionDisplayNames.TryGetDisplayName("norwayeast", out var displayName);

        Assert.True(found);
        Assert.Equal("Norway East", displayName);
    }

    [Fact]
    public void TryGetDisplayName_UnknownCode_ReturnsFalseAndEmptyString()
    {
        RegionDisplayNames.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["norwayeast"] = "Norway East",
        });

        var found = RegionDisplayNames.TryGetDisplayName("not-a-real-region", out var displayName);

        Assert.False(found);
        Assert.Equal("", displayName);
    }

    // A code that genuinely isn't a real Azure region should just miss gracefully, never throw —
    // true of the embedded default just as it always was of an empty/unconfigured table.
    [Fact]
    public void TryGetDisplayName_UnknownCode_AgainstEmbeddedDefault_MissesGracefullyRatherThanThrowing()
    {
        RegionDisplayNames.ResetToEmbeddedDefault();

        var found = RegionDisplayNames.TryGetDisplayName("not-a-real-region", out var displayName);

        Assert.False(found);
        Assert.Equal("", displayName);
    }

    // The whole point of embedding a default: a consumer that never calls Configure at all still
    // gets correct, portal-matching region names. "norwayeast" is a real, stable entry in
    // config/azure-locations.json (fetched from the ARM Locations API, not guessed).
    [Fact]
    public void TryGetDisplayName_KnownCode_AgainstEmbeddedDefault_ResolvesWithoutAnyConfigureCall()
    {
        RegionDisplayNames.ResetToEmbeddedDefault();

        var found = RegionDisplayNames.TryGetDisplayName("norwayeast", out var displayName);

        Assert.True(found);
        Assert.Equal("Norway East", displayName);
    }

    [Fact]
    public void ResetToEmbeddedDefault_UndoesAPreviousConfigureCall()
    {
        RegionDisplayNames.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["westeurope"] = "West Europe",
        });
        Assert.False(RegionDisplayNames.TryGetDisplayName("norwayeast", out _));

        RegionDisplayNames.ResetToEmbeddedDefault();

        Assert.True(RegionDisplayNames.TryGetDisplayName("norwayeast", out var displayName));
        Assert.Equal("Norway East", displayName);
    }

    [Fact]
    public void Configure_CalledAgain_ReplacesThePreviousTableEntirely()
    {
        RegionDisplayNames.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["norwayeast"] = "Norway East",
        });
        RegionDisplayNames.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["westeurope"] = "West Europe",
        });

        Assert.False(RegionDisplayNames.TryGetDisplayName("norwayeast", out _));
        Assert.True(RegionDisplayNames.TryGetDisplayName("westeurope", out var displayName));
        Assert.Equal("West Europe", displayName);
    }

    [Fact]
    public void Configure_NullTable_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RegionDisplayNames.Configure(null!));
    }

    // Locks in the concurrency guarantee documented on Configure: reassigning the backing table is
    // atomic, so a reader on another thread always sees one complete table or the other, never an
    // exception and never a value that doesn't belong to either. Runs many concurrent readers
    // against a thread that keeps swapping between two distinguishable tables; any exception, or
    // any (found=true) result whose display name doesn't match ITS OWN table, fails the test.
    [Fact]
    public async Task Configure_And_TryGetDisplayName_AreSafeUnderConcurrentAccess()
    {
        var tableA = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["regiona"] = "Region A Display" };
        var tableB = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["regionb"] = "Region B Display" };
        using var stop = new CancellationTokenSource();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writer = Task.Run(() =>
        {
            var useA = true;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    RegionDisplayNames.Configure(useA ? tableA : tableB);
                    useA = !useA;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (RegionDisplayNames.TryGetDisplayName("regiona", out var a) && a != "Region A Display")
                    {
                        throw new InvalidOperationException($"Corrupted read for 'regiona': got \"{a}\"");
                    }
                    if (RegionDisplayNames.TryGetDisplayName("regionb", out var b) && b != "Region B Display")
                    {
                        throw new InvalidOperationException($"Corrupted read for 'regionb': got \"{b}\"");
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        await Task.Delay(500);
        stop.Cancel();
        await Task.WhenAll([writer, .. readers]);

        Assert.Empty(exceptions);
    }
}
