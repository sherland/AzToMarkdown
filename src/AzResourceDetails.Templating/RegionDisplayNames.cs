using System.Text.Json;

namespace AzResourceDetails.Templating;

// The authoritative Azure region code -> portal display name map ("norwayeast" -> "Norway East").
// Region display names are effectively permanent once a region exists — the only way this data
// goes stale is a BRAND NEW region not yet in whatever snapshot is loaded, never a wrong answer for
// an existing one — so shipping a baked-in default (embedded at build time from
// AzResourceDetailsDownloader's own config/azure-locations.json, refreshed periodically via
// fetch-azure-reference-data.ps1) gives every consumer correct, consistent portal-matching text out
// of the box, with no host-side wiring required. Configure remains available for a consumer that
// wants to override it entirely — e.g. with a fresher fetch, without waiting on a new package
// version of this library.
public static class RegionDisplayNames
{
    private static IReadOnlyDictionary<string, string> _map = LoadEmbeddedDefault();

    /// <summary>
    /// Replaces the region-code -&gt; display-name table this library uses for
    /// <c>region_display_name</c>/<see cref="TryGetDisplayName"/>. Not required for correct
    /// out-of-box behavior (see the embedded default this class ships with) — only needed to
    /// override it, e.g. with data fresher than whatever was embedded at build time. Safe to call
    /// more than once; whichever call completes last wins.
    /// </summary>
    /// <remarks>
    /// Concurrency: safe to call from any thread at any time, including while other threads are
    /// calling <see cref="TryGetDisplayName"/> — reassigning a reference-typed field is atomic in
    /// .NET, so a concurrent reader always sees either the complete old table or the complete new
    /// one, never a partially-updated or corrupted one. What isn't guaranteed is exactly WHEN a
    /// concurrent reader on another thread observes the switch (ordinary memory-visibility timing,
    /// not a correctness issue) or which table wins if two threads call <see cref="Configure"/> at
    /// the same time (whichever assignment lands last, same as any unsynchronized field write). For
    /// predictable behavior, call this once during host startup, before rendering begins — the
    /// concurrency safety above means you don't strictly have to, just that doing so removes any
    /// ambiguity about which table is in effect.
    /// </remarks>
    public static void Configure(IReadOnlyDictionary<string, string> regionDisplayNamesByCode)
    {
        ArgumentNullException.ThrowIfNull(regionDisplayNamesByCode);
        _map = regionDisplayNamesByCode;
    }

    /// <summary>
    /// Restores the table embedded in this assembly, undoing any previous <see cref="Configure"/>
    /// call. Mainly useful for tests; a host has no other reason to call this unless it wants to
    /// deliberately revert to the shipped default.
    /// </summary>
    public static void ResetToEmbeddedDefault() => Configure(LoadEmbeddedDefault());

    public static bool TryGetDisplayName(string regionCode, out string displayName)
    {
        if (_map.TryGetValue(regionCode, out var found))
        {
            displayName = found;
            return true;
        }
        displayName = "";
        return false;
    }

    // "AzResourceDetails.Templating.azure-locations.json" is set explicitly via LogicalName in the
    // csproj rather than relied upon as an SDK-computed default, so this string and the actual
    // embedded name can never silently drift apart.
    private static IReadOnlyDictionary<string, string> LoadEmbeddedDefault()
    {
        try
        {
            using var stream = typeof(RegionDisplayNames).Assembly
                .GetManifestResourceStream("AzResourceDetails.Templating.azure-locations.json");
            if (stream is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            using var doc = JsonDocument.Parse(stream);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString();
                var displayName = entry.GetProperty("displayName").GetString();
                if (name is { Length: > 0 } && displayName is { Length: > 0 })
                {
                    map[name] = displayName;
                }
            }
            return map;
        }
        catch
        {
            // Best-effort — a malformed/missing embedded resource shouldn't break every resolution
            // that touches Location, just fall back to "no lookup available" the same as before
            // this default existed.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
