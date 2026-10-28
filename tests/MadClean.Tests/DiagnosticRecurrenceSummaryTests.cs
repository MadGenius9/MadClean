using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DiagnosticRecurrenceSummaryTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static DiagnosticFinding Finding(long[]? ids = null, string channel = "Application", string provider = "Application Error", int version = 0) =>
        new(channel, provider, 1000, version, "Error (Windows-reported)", ids?.Length ?? 1,
            End.AddHours(-1), End.AddHours(-1), ids ?? [1], "Observed metadata", "Cause unknown", "Review evidence", "Review Windows logs");
    private static DiagnosticReport Report(params DiagnosticFinding[] findings) => new(End.AddDays(-7), End, findings, ["Bounded source coverage"]);
    private static DiagnosticRecurrenceSummary Build(DiagnosticReport report, CheckState state = CheckState.Available) =>
        DiagnosticRecurrenceSummaryBuilder.Build(new(state, "PRIVATE provider message must not be echoed", [report]), End);

    [Fact]
    public void CountsRecordsAndRepeatedGroupsWithoutMergingProviderSchemasOrChannels()
    {
        var result = Build(Report(Finding([1, 2, 3]), Finding([4], version: 1), Finding([1, 2], channel: "System"),
            Finding([5], provider: "Windows Error Reporting")));
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(7L, result.ObservedRecords);
        Assert.Equal(4, result.ObservedGroups);
        Assert.Equal(2, result.RepeatedGroups);
        Assert.Equal(5L, result.RecordsInRepeatedGroups);
        Assert.Equal(End.AddDays(-7), result.StartUtc);
        Assert.Equal(End, result.CollectedUtc);
        Assert.Contains("not unique crashes", result.Explanation);
        Assert.Contains("WER evidence is never added", result.Explanation);
        Assert.DoesNotContain("PRIVATE", result.Explanation);
    }

    [Theory]
    [InlineData(CheckState.Available)]
    [InlineData(CheckState.Partial)]
    public void EmptyTypedEvidencePreservesObservedZeroAndItsCoverageState(CheckState state)
    {
        var result = Build(Report(), state);
        Assert.Equal(state, result.State);
        Assert.Equal(0L, result.ObservedRecords);
        Assert.Equal(0, result.RepeatedGroups);
        Assert.Contains(state == CheckState.Partial ? "missing records remain unknown" : "not a healthy-system", result.Explanation);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    public void UncheckedOrFailedEvidenceNeverBecomesZeroEvenWithAnAttachedReport(CheckState state)
    {
        var result = Build(Report(), state);
        Assert.Equal(state, result.State);
        Assert.Null(result.ObservedRecords);
        Assert.Null(result.ObservedGroups);
        Assert.Null(result.StartUtc);
        Assert.DoesNotContain("PRIVATE", result.Explanation);
    }

    [Fact]
    public void RejectsMissingAmbiguousOrNullTypedContainers()
    {
        foreach (var input in new CheckResult<DiagnosticReport>[]
        {
            new(CheckState.Available, "", []), new(CheckState.Partial, "", [Report(), Report()]),
            new(CheckState.Available, "", null!), new(CheckState.Available, "", [null!]),
            new(CheckState.Available, "", [Report() with { Findings = null! }]),
            new(CheckState.Available, "", [Report() with { Coverage = null! }])
        })
            AssertUnknown(DiagnosticRecurrenceSummaryBuilder.Build(input, End));
    }

    [Fact]
    public void RejectsDuplicateGroupAndChannelScopedRecordIdentities()
    {
        AssertUnknown(Build(Report(Finding([1, 1]))));
        AssertUnknown(Build(Report(Finding([1]), Finding([1], version: 1))));
        AssertUnknown(Build(Report(Finding([1]), Finding([2]))));
        Assert.Equal(2L, Build(Report(Finding([1]), Finding([1], channel: "System"))).ObservedRecords);
    }

    [Fact]
    public void RejectsUnsupportedWindowsAndOutOfWindowFindings()
    {
        var valid = Report(Finding());
        foreach (var report in new[]
        {
            valid with { StartUtc = End.AddDays(-2) }, valid with { StartUtc = valid.StartUtc.AddTicks(1) },
            valid with { EndUtc = End.AddHours(1) }, valid with { StartUtc = valid.StartUtc.ToOffset(TimeSpan.FromHours(1)) },
            Report(Finding() with { LastUtc = End }), Report(Finding() with { FirstUtc = End.AddDays(-8) }),
            Report(Finding() with { FirstUtc = End, LastUtc = End.AddHours(-1) }),
            Report(Finding() with { LastUtc = End.AddMinutes(-30) }),
            Report(Finding() with { FirstUtc = End.AddHours(-1).ToOffset(TimeSpan.FromHours(1)) })
        }) AssertUnknown(Build(report));
        foreach (int days in new[] { 1, 7, 30 })
            Assert.Equal(CheckState.Available, Build(valid with { StartUtc = End.AddDays(-days) }).State);
    }

    [Fact]
    public void RejectsInvalidCollectionTimestampWithoutNormalizingIt()
    {
        var input = new CheckResult<DiagnosticReport>(CheckState.Available, "", [Report()]);
        foreach (var time in new[] { default(DateTimeOffset), End.AddSeconds(-1), End.ToOffset(TimeSpan.FromHours(1)), DateTimeOffset.UtcNow.AddDays(1) })
        {
            var result = DiagnosticRecurrenceSummaryBuilder.Build(input, time);
            AssertUnknown(result);
            Assert.Equal(time, result.CollectedUtc);
        }
    }

    [Fact]
    public void RejectsInvalidCountsBoundsAndPrivateOrUnsafeText()
    {
        foreach (var finding in new[]
        {
            Finding() with { Count = 0 }, Finding() with { Count = 2 }, Finding([-1]), Finding() with { RecordIds = null! },
            Finding() with { Channel = "Security" }, Finding() with { EventId = 65536 }, Finding() with { Version = -1 },
            Finding() with { Provider = "\u202Eprovider" }, Finding() with { Provider = "bad\ud800" },
            Finding() with { NextStep = "line\ncommand" }, Finding() with { Uncertainty = new string('x', 2049) },
            Finding() with { Impact = null! }, Finding() with { Level = " " }
        }) AssertUnknown(Build(Report(finding)));
        AssertUnknown(Build(Report() with { Coverage = [null!] }));
        AssertUnknown(Build(Report() with { Coverage = [] }));
        AssertUnknown(Build(Report() with { Coverage = Enumerable.Repeat("coverage", 65).ToArray() }));
        AssertUnknown(Build(Report(Enumerable.Range(0, 257).Select(i => Finding([i + 1L], provider: "Provider" + i)).ToArray())));
        AssertUnknown(Build(Report(Finding(Enumerable.Range(1, 2000).Select(i => (long)i).ToArray()), Finding([2001], version: 1))));
        AssertUnknown(Build(Report(Finding(Enumerable.Range(1, 1001).Select(i => (long)i).ToArray()))));
        AssertUnknown(Build(Report(Finding(Enumerable.Range(1, 1000).Select(i => (long)i).ToArray()), Finding([1001], version: 1))));
        var thousandIds = Enumerable.Range(1, 1000).Select(i => (long)i).ToArray();
        Assert.Equal(2000L, Build(Report(Finding(thousandIds), Finding(thousandIds, channel: "System"))).ObservedRecords);
        Assert.Equal(CheckState.Available, Build(Report(Finding(provider: "Provider \U0001F4BB"))).State);
    }

    [Fact]
    public void CancellationThrowsRatherThanReturningAnEmptyOrSuccessfulSummary()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => DiagnosticRecurrenceSummaryBuilder.Build(new(CheckState.Available, "", [Report()]), End, stop.Token));
        Assert.Throws<OperationCanceledException>(() => DiagnosticRecurrenceSummaryBuilder.Build(null!, End, stop.Token));
    }

    [Fact]
    public void CancellationDuringEvidenceEnumerationCannotPublishCompletedCounts()
    {
        using var stop = new CancellationTokenSource();
        var report = Report() with { Findings = new CancelAtEndList(stop) };
        Assert.Throws<OperationCanceledException>(() => DiagnosticRecurrenceSummaryBuilder.Build(new(CheckState.Available, "", [report]), End, stop.Token));
    }

    private sealed class CancelAtEndList(CancellationTokenSource stop) : IReadOnlyList<DiagnosticFinding>
    {
        public int Count => 1;
        public DiagnosticFinding this[int index] => index == 0 ? Finding() : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<DiagnosticFinding> GetEnumerator()
        {
            yield return Finding();
            stop.Cancel();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void AssertUnknown(DiagnosticRecurrenceSummary result)
    {
        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Null(result.ObservedRecords);
        Assert.Null(result.ObservedGroups);
        Assert.Null(result.RepeatedGroups);
        Assert.Null(result.RecordsInRepeatedGroups);
    }
}
