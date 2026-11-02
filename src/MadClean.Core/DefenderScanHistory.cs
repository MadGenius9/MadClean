using System.Globalization;
using System.Text.Json;

namespace MadClean.Core;

/// <summary>Reported history timestamps only. These fields do not identify one scan or establish its outcome.</summary>
public sealed record DefenderScanHistory(CheckState State, DateTimeOffset? QuickScanStartUtc,
    DateTimeOffset? QuickScanEndUtc, DateTimeOffset? FullScanStartUtc, DateTimeOffset? FullScanEndUtc, string Coverage);

public static class DefenderScanHistoryParser
{
    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
    private const string Limits = "Get-MpComputerStatus reported quick/full scan timestamps only. Missing, sentinel, unsupported-zone or future fields remain Unknown. " +
        "Start/end fields may refer to different scans; their order does not establish a running scan, ownership, success, scan scope, or attributed detections. " +
        "Provider times and the local observation clock can be wrong or change. No scan or security action was requested.";

    public static DefenderScanHistory Read(JsonElement status, DateTimeOffset observedUtc)
    {
        if (status.ValueKind != JsonValueKind.Object || observedUtc == default || observedUtc.Offset != TimeSpan.Zero)
            return new(CheckState.Unknown, null, null, null, null, Limits);
        var quickStart = Timestamp(status, "QuickScanStartTime", observedUtc);
        var quickEnd = Timestamp(status, "QuickScanEndTime", observedUtc);
        var fullStart = Timestamp(status, "FullScanStartTime", observedUtc);
        var fullEnd = Timestamp(status, "FullScanEndTime", observedUtc);
        int known = (quickStart.HasValue ? 1 : 0) + (quickEnd.HasValue ? 1 : 0) + (fullStart.HasValue ? 1 : 0) + (fullEnd.HasValue ? 1 : 0);
        return new(known == 4 ? CheckState.Available : known > 0 ? CheckState.Partial : CheckState.Unknown,
            quickStart, quickEnd, fullStart, fullEnd, $"{known} of 4 reported timestamps qualified. " + Limits);
    }

    private static DateTimeOffset? Timestamp(JsonElement status, string field, DateTimeOffset observedUtc)
    {
        // Duplicate optional properties are ambiguous; GetProperty's last-value rule
        // must not turn conflicting timestamps into qualified history.
        JsonElement value = default; int matches = 0;
        foreach (var property in status.EnumerateObject())
            if (property.NameEquals(field)) { value = property.Value; matches++; }
        if (matches != 1 || value.ValueKind != JsonValueKind.String) return null;
        string text = value.GetString()!;
        if (text.Length is < 20 or > 64) return null;
        if (text.EndsWith('Z')) text = text[..^1] + "+00:00";
        // Exact numeric offsets (or explicit Z above) only. No AssumeLocal or
        // AssumeUniversal interpretation of an unspecified provider timezone.
        if (!DateTimeOffset.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return null;
        var utc = parsed.ToUniversalTime();
        if (parsed.DateTime == DateTime.MaxValue || parsed.DateTime == DateTime.MinValue ||
            parsed.DateTime == DateTime.FromFileTimeUtc(0) || utc == default || utc == DateTimeOffset.MaxValue || utc > observedUtc ||
            utc.UtcDateTime == DateTime.FromFileTimeUtc(0)) return null;
        return utc;
    }
}
