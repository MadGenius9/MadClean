using System.Globalization;
using System.Text.Json;

namespace MadClean.Core;

public enum DefenderLifecycleEventKind { Started, Finished, StoppedBeforeCompletion }

/// <summary>One retained event observation, never a correlated engine scan or MadClean request.</summary>
public sealed record DefenderLifecycleEvent(int EventId, long RecordId, DateTimeOffset TimeUtc)
{
    public DefenderLifecycleEventKind Kind => EventId switch
    {
        1000 => DefenderLifecycleEventKind.Started,
        1001 => DefenderLifecycleEventKind.Finished,
        1002 => DefenderLifecycleEventKind.StoppedBeforeCompletion,
        _ => throw new InvalidOperationException("Unsupported lifecycle event identifier.")
    };
}

public sealed record DefenderLifecycleReport
{
    public DefenderLifecycleReport(DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset ObservedUtc,
        bool Truncated, IReadOnlyList<DefenderLifecycleEvent> Records)
    {
        this.StartUtc = StartUtc; this.EndUtc = EndUtc; this.ObservedUtc = ObservedUtc;
        this.Truncated = Truncated;
        this.Records = Array.AsReadOnly(Records.ToArray());
    }
    public DateTimeOffset StartUtc { get; }
    public DateTimeOffset EndUtc { get; }
    public DateTimeOffset ObservedUtc { get; }
    public bool Truncated { get; }
    public IReadOnlyList<DefenderLifecycleEvent> Records { get; }
}

public interface IDefenderLifecycleInventory
{
    Task<CheckResult<DefenderLifecycleReport>> ReadAsync(int days, CancellationToken token);
}

public static class DefenderLifecyclePolicy
{
    public const string Channel = "Microsoft-Windows-Windows Defender/Operational";
    public const string Provider = "Microsoft-Windows-Windows Defender";
    public const int MaximumRecords = 256;
    public const string Limits = "Retained Defender Operational event metadata only: 1000 started, 1001 finished, 1002 stopped before completion. " +
        "Events are not paired or attributed to MadClean. Scan ID, messages, payloads and detections are not read. " +
        "Logs may be cleared, overwritten, disabled or incomplete; no matching events does not mean no scans occurred. " +
        "No running-scan state, scan coverage, successful cancellation, clean verdict or protection status is established. No scan or security action was requested.";

    internal static bool Time(DateTimeOffset value) => value.Offset == TimeSpan.Zero &&
        value > new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero) && value < DateTimeOffset.MaxValue;
    public static bool IsConsistent(DefenderLifecycleReport? report, DateTimeOffset nowUtc)
    {
        if (report is null) return false;
        var duration = report.EndUtc - report.StartUtc;
        if (!Time(nowUtc) || !Time(report.StartUtc) || !Time(report.EndUtc) || !Time(report.ObservedUtc) ||
            report.EndUtc > report.ObservedUtc || report.ObservedUtc > nowUtc ||
            (duration != TimeSpan.FromDays(1) && duration != TimeSpan.FromDays(7) && duration != TimeSpan.FromDays(30)) ||
            report.Records.Count > MaximumRecords || report.Truncated && report.Records.Count != MaximumRecords) return false;
        var ids = new HashSet<long>();
        foreach (var row in report.Records)
            if (row is null || row.EventId is not (1000 or 1001 or 1002) || row.RecordId <= 0 || !ids.Add(row.RecordId) ||
                !Time(row.TimeUtc) || row.TimeUtc < report.StartUtc || row.TimeUtc >= report.EndUtc) return false;
        return true;
    }
    public static bool ValidateResult(CheckResult<DefenderLifecycleReport>? result, int days,
        DateTimeOffset attemptUtc, DateTimeOffset nowUtc)
    {
        if (result is null || result.Items is null || days is not (1 or 7 or 30) || !Time(attemptUtc) ||
            !Time(nowUtc) || attemptUtc > nowUtc || string.IsNullOrWhiteSpace(result.Message) ||
            result.Message.Length > 4096 || result.Message.Any(character => character < ' ' || character > '~')) return false;
        if (result.State is CheckState.Unsupported or CheckState.Unavailable or CheckState.Failed or CheckState.Unknown or CheckState.Cancelled)
            return result.Items.Count == 0;
        return result.State == CheckState.Partial && result.Items.Count == 1 &&
            IsConsistent(result.Items[0], nowUtc) && result.Items[0].EndUtc >= attemptUtc &&
            result.Items[0].EndUtc - result.Items[0].StartUtc == TimeSpan.FromDays(days);
    }

}

public static class DefenderLifecycleParser
{
    private const int MaximumCharacters = 196_608;
    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
    private static readonly HashSet<string> RootFields = new(["Version", "StartUtc", "EndUtc", "ObservedUtc", "Channel", "Provider", "Status", "Truncated", "Records"], StringComparer.Ordinal);
    private static readonly HashSet<string> EventFields = new(["EventId", "RecordId", "TimeUtc", "Channel", "Provider"], StringComparer.Ordinal);

    public static CheckResult<DefenderLifecycleReport> Parse(string json, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumCharacters) return Invalid();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            if (!Shape(root, RootFields) || !root.GetProperty("Version").TryGetInt32(out int version) || version != 1 ||
                !Date(root.GetProperty("StartUtc"), out var start) || !Date(root.GetProperty("EndUtc"), out var end) ||
                !Date(root.GetProperty("ObservedUtc"), out var observed) ||
                !Source(root) || root.GetProperty("Status").ValueKind != JsonValueKind.String ||
                root.GetProperty("Truncated").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid();
            bool truncated = root.GetProperty("Truncated").GetBoolean();
            var array = root.GetProperty("Records");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > DefenderLifecyclePolicy.MaximumRecords) return Invalid();
            var rows = new List<DefenderLifecycleEvent>(array.GetArrayLength());
            foreach (var element in array.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (!Shape(element, EventFields) || !Source(element) ||
                    !element.GetProperty("EventId").TryGetInt32(out int eventId) ||
                    !element.GetProperty("RecordId").TryGetInt64(out long recordId) ||
                    !Date(element.GetProperty("TimeUtc"), out var time)) return Invalid();
                rows.Add(new(eventId, recordId, time));
            }
            var report = new DefenderLifecycleReport(start, end, observed, truncated, rows);
            if (!DefenderLifecyclePolicy.IsConsistent(report, DateTimeOffset.UtcNow)) return Invalid();
            token.ThrowIfCancellationRequested();
            string status = root.GetProperty("Status").GetString()!;
            if (status == "Available") return new(CheckState.Partial,
                $"{rows.Count} retained lifecycle event observations; {(truncated ? "the bounded result is truncated. " : "no limit truncation was reported. ")}" + DefenderLifecyclePolicy.Limits, [report]);
            if (rows.Count != 0 || truncated) return Invalid();
            return status switch
            {
                "Disabled" => CheckResult<DefenderLifecycleReport>.Empty(CheckState.Unavailable, "Defender Operational channel is disabled; it was not enabled. " + DefenderLifecyclePolicy.Limits),
                "Unavailable" => CheckResult<DefenderLifecycleReport>.Empty(CheckState.Unavailable, "Defender Operational channel/provider is unavailable. " + DefenderLifecyclePolicy.Limits),
                "Denied" => CheckResult<DefenderLifecycleReport>.Empty(CheckState.Failed, "Defender Operational read access was denied. Check existing Event Log permissions; no elevation was requested. " + DefenderLifecyclePolicy.Limits),
                "Failed" => CheckResult<DefenderLifecycleReport>.Empty(CheckState.Failed, "Defender Operational query failed. Check Windows Event Log service and read access, then retry. " + DefenderLifecyclePolicy.Limits),
                _ => Invalid()
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            token.ThrowIfCancellationRequested();
            return Invalid();
        }
    }
    private static CheckResult<DefenderLifecycleReport> Invalid() => CheckResult<DefenderLifecycleReport>.Empty(CheckState.Unknown,
        "Lifecycle response was malformed, ambiguous or inconsistent; no event observations were trusted. " + DefenderLifecyclePolicy.Limits);
    private static bool Shape(JsonElement element, HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name) || !names.Add(property.Name)) return false;
        return names.Count == expected.Count;
    }
    private static bool Source(JsonElement element) =>
        element.GetProperty("Channel").ValueKind == JsonValueKind.String && element.GetProperty("Channel").GetString() == DefenderLifecyclePolicy.Channel &&
        element.GetProperty("Provider").ValueKind == JsonValueKind.String && element.GetProperty("Provider").GetString() == DefenderLifecyclePolicy.Provider;
    private static bool Date(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: >= 20 and <= 40 } text) return false;
        return DateTimeOffset.TryParseExact(text, Formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out value) && DefenderLifecyclePolicy.Time(value);
    }
}
