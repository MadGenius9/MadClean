using System.Buffers;
using System.Globalization;
using System.Text;

namespace MadClean.Core;

public sealed record DiagnosticRecurrenceSummary(CheckState State, long? ObservedRecords, int? ObservedGroups,
    int? RepeatedGroups, long? RecordsInRepeatedGroups, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc,
    DateTimeOffset CollectedUtc, string Explanation);

/// <summary>Summarizes a bounded existing Event Log report; performs no discovery or incident correlation.</summary>
public static class DiagnosticRecurrenceSummaryBuilder
{
    private const string Limits = "Counts describe observed event records grouped by channel, provider, event ID and schema version, not unique crashes, causes or PC health. Source filters, truncation, inaccessible channels and log retention can omit history. Separate WER evidence is never added or correlated.";

    public static DiagnosticRecurrenceSummary Build(CheckResult<DiagnosticReport> result, DateTimeOffset collectedUtc,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        DiagnosticRecurrenceSummary Empty(CheckState state, string reason)
        {
            token.ThrowIfCancellationRequested();
            return new(state, null, null, null, null, null, null, collectedUtc, reason + " " + Limits);
        }

        if (collectedUtc == default || collectedUtc.Offset != TimeSpan.Zero || collectedUtc > DateTimeOffset.UtcNow)
            return Empty(CheckState.Unknown, "The collection timestamp is invalid or in the future; no recurrence counts were established.");
        if (result is null)
            return Empty(CheckState.Unknown, "No typed diagnostic result was supplied; recurrence is unknown.");
        if (result.State is not (CheckState.Available or CheckState.Partial))
            return Empty(Enum.IsDefined(result.State) ? result.State : CheckState.Unknown, result.State switch
            {
                CheckState.Unavailable => "Diagnostic evidence is unavailable; recurrence was not established.",
                CheckState.Unsupported => "Diagnostic evidence is unsupported; recurrence was not established.",
                CheckState.Failed => "The diagnostic check failed; recurrence was not established.",
                CheckState.Cancelled => "The diagnostic check was cancelled; recurrence was not established.",
                _ => "Diagnostic evidence is unknown or unchecked; recurrence was not established."
            });
        DiagnosticRecurrenceSummary Invalid() => Empty(CheckState.Unknown,
            "The typed diagnostic report is missing, ambiguous or outside the supported metadata bounds; no recurrence counts were established.");
        if (result.Items is null || result.Items.Count != 1 || result.Items[0] is not { } report ||
            report.StartUtc == default || report.StartUtc.Offset != TimeSpan.Zero || report.EndUtc.Offset != TimeSpan.Zero ||
            report.EndUtc > collectedUtc ||
            !IsSupportedWindow(report.EndUtc - report.StartUtc) ||
            report.Findings is null || report.Findings.Count > 256 || report.Coverage is null || report.Coverage.Count is 0 or > 64)
            return Invalid();
        foreach (string coverage in report.Coverage)
        {
            token.ThrowIfCancellationRequested();
            if (!SafeText(coverage, 4096)) return Invalid();
        }
        HashSet<(string Channel, string Provider, int EventId, int Version)> groups = [];
        HashSet<(string Channel, long Id)> records = [];
        Dictionary<string, int> channelCounts = new(StringComparer.Ordinal);
        long total = 0, repeatedRecords = 0;
        int repeatedGroups = 0;
        foreach (var finding in report.Findings)
        {
            token.ThrowIfCancellationRequested();
            if (finding is null || finding.Channel is not ("Application" or "System") ||
                !SafeText(finding.Provider, 256) || finding.EventId is < 0 or > 65535 || finding.Version is < 0 or > 255 ||
                !SafeText(finding.Level, 128) || !SafeText(finding.Observation, 2048) ||
                !SafeText(finding.Uncertainty, 2048) || !SafeText(finding.Impact, 2048) || !SafeText(finding.NextStep, 2048) ||
                finding.Count is <= 0 or > 2000 || finding.RecordIds is null || finding.RecordIds.Count != finding.Count ||
                finding.FirstUtc.Offset != TimeSpan.Zero || finding.LastUtc.Offset != TimeSpan.Zero ||
                finding.FirstUtc < report.StartUtc || finding.FirstUtc > finding.LastUtc || finding.LastUtc >= report.EndUtc ||
                (finding.Count == 1 && finding.FirstUtc != finding.LastUtc) ||
                !groups.Add((finding.Channel, finding.Provider, finding.EventId, finding.Version)))
                return Invalid();
            total += finding.Count;
            if (total > 2000) return Invalid();
            int channelTotal = channelCounts.GetValueOrDefault(finding.Channel) + finding.Count;
            if (channelTotal > 1000) return Invalid();
            channelCounts[finding.Channel] = channelTotal;
            foreach (long id in finding.RecordIds)
            {
                token.ThrowIfCancellationRequested();
                if (id <= 0 || !records.Add((finding.Channel, id))) return Invalid();
            }
            if (finding.Count >= 2) { repeatedGroups++; repeatedRecords += finding.Count; }
        }
        token.ThrowIfCancellationRequested();
        return new(result.State, total, groups.Count, repeatedGroups, repeatedRecords, report.StartUtc, report.EndUtc,
            collectedUtc, (result.State == CheckState.Partial
                ? "Partial observed evidence only; missing records remain unknown, including when no usable records were returned. "
                : "Summary of the existing bounded diagnostic report only; zero matching records is not a healthy-system determination. ") + Limits);
    }

    private static bool IsSupportedWindow(TimeSpan window) => window == TimeSpan.FromDays(1) ||
        window == TimeSpan.FromDays(7) || window == TimeSpan.FromDays(30);

    private static bool SafeText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) return false;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
