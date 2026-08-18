namespace AzToMarkdown.Core.Models;

/// <summary>
/// Shared ARM resource-id string helpers. All segment math on ARM ids lives here so the
/// vault pipeline, relationship extractor, and vault query client use the same rules
/// about how a parent id, zone name, or scope segment is derived.
/// </summary>
public static class ArmId
{
    /// <summary>
    /// Removes the last <paramref name="count"/> path segments
    /// (e.g. child resource id → parent resource id with count 2).
    /// Returns "" when the id has too few segments.
    /// </summary>
    public static string StripSegments(string armId, int count)
    {
        var parts = armId.Split('/');
        var take  = parts.Length - count;
        return take > 0 ? string.Join('/', parts[..take]) : "";
    }

    /// <summary>
    /// Returns the value following a path segment (case-insensitive), or "" when absent.
    /// <c>SegmentAfter(id, "subscriptions")</c> → the subscription GUID;
    /// <c>SegmentAfter(id, "resourceGroups")</c> → the resource-group name.
    /// </summary>
    public static string SegmentAfter(string armId, string segment)
    {
        if (string.IsNullOrEmpty(armId)) return "";
        var parts = armId.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals(segment, StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        return "";
    }

    /// <summary>
    /// Zone name for a DNS record id:
    /// <c>…/dnsZones/{zone}/{recordType}/{record}</c> → <c>{zone}</c> (index ^3).
    /// </summary>
    public static string ZoneName(string recordId)
    {
        var parts = recordId.Split('/');
        return parts.Length >= 3 ? parts[^3] : "";
    }
}
