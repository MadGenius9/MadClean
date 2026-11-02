using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void DefenderScanHistoryFixture(DefenderInfo data, DateTimeOffset before,
        DateTimeOffset after, SmokeReport report)
    {
        const string check = "defender-scan-history-contract";
        if (before == default || after == default || before.Offset != TimeSpan.Zero || after.Offset != TimeSpan.Zero || before > after)
        {
            report.Unknown(check, "The shared read's UTC observation interval was unavailable or inconsistent. No timestamp compatibility, scan state or outcome is inferred.");
            return;
        }
        if (data.ScanHistory is not { } history)
        {
            report.Fail(check, "The qualified actual Defender projection omitted its scan-history observation object. No timestamp values or provider data are exported.");
            return;
        }
        DateTimeOffset?[] values = [history.QuickScanStartUtc, history.QuickScanEndUtc, history.FullScanStartUtc, history.FullScanEndUtc];
        int known = values.Count(value => value.HasValue);
        CheckState expected = known == 4 ? CheckState.Available : known > 0 ? CheckState.Partial : CheckState.Unknown;
        if (history.State != expected || history.Coverage == null || history.Coverage.Length is 0 or > 2048 ||
            history.Coverage.Any(char.IsControl) ||
            !history.Coverage.StartsWith($"{known} of 4 reported timestamps qualified. ", StringComparison.Ordinal) ||
            !history.Coverage.Contains("Get-MpComputerStatus reported quick/full scan timestamps only.", StringComparison.Ordinal) ||
            !history.Coverage.Contains("Start/end fields may refer to different scans", StringComparison.Ordinal) ||
            !history.Coverage.Contains("No scan or security action was requested.", StringComparison.Ordinal))
        {
            report.Fail(check, "The actual scan-history state/count or bounded fixed coverage contract was inconsistent. No security timestamps, coverage text or provider messages are exported.");
            return;
        }
        if (values.Any(value => value is { } timestamp && (timestamp == default || timestamp.Offset != TimeSpan.Zero ||
            timestamp > after || timestamp == DateTimeOffset.MaxValue || timestamp.UtcDateTime == DateTime.FromFileTimeUtc(0))))
        {
            report.Fail(check, "A qualified history timestamp violated UTC, sentinel or shared observation-clock bounds. Actual times and any cause remain withheld; no scan outcome is inferred.");
            return;
        }
        if (known == 0)
        {
            report.Unknown(check, "historyState=Unknown; qualifiedTimestampCount=0; nullableTimestampSchemaValid=true. The real shared status read supplied no qualified scan timestamp, so native timestamp compatibility remains unverified. Unknown does not mean never scanned; no second query or scan ran.");
            return;
        }
        report.Pass(check, $"historyState={history.State}; qualifiedTimestampCount={known}; nullableTimestampSchemaValid=true; utcAndObservationBoundsValid=true; fixedCoverageValid=true. " +
            "Reused the single actual Defender status read. This validates reported timestamp compatibility only: fields are independent, ordering is not scan ownership/running/success, and detections are not attributed. No timestamps, flags, paths, IDs, signatures or provider text exported; no scan, elevation or security change requested.");
    }
}
