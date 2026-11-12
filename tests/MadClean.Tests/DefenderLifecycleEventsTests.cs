using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DefenderLifecycleEventsTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static Dictionary<string, object?> Row(int id = 1000, long record = 1, DateTimeOffset? time = null) => new()
    {
        ["EventId"] = id, ["RecordId"] = record, ["TimeUtc"] = (time ?? End.AddHours(-1)).ToString("O"),
        ["Channel"] = DefenderLifecyclePolicy.Channel, ["Provider"] = DefenderLifecyclePolicy.Provider
    };
    private static Dictionary<string, object?> Root(params Dictionary<string, object?>[] rows) => new()
    {
        ["Version"] = 1, ["StartUtc"] = End.AddDays(-1).ToString("O"), ["EndUtc"] = End.ToString("O"),
        ["ObservedUtc"] = End.AddSeconds(1).ToString("O"), ["Channel"] = DefenderLifecyclePolicy.Channel,
        ["Provider"] = DefenderLifecyclePolicy.Provider, ["Status"] = "Available", ["Truncated"] = false, ["Records"] = rows
    };
    private static CheckResult<DefenderLifecycleReport> Parse(Dictionary<string, object?> root) => DefenderLifecycleParser.Parse(JsonSerializer.Serialize(root));
    private static void Refused(Dictionary<string, object?> root)
    {
        var result = Parse(root);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
        Assert.DoesNotContain("private-value", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleKindsAreIndependentEventsWithoutPairedScanOrOutcomeClaims()
    {
        var result = Parse(Root(Row(1002, 3), Row(1000, 2), Row(1001, 1)));
        Assert.Equal(CheckState.Partial, result.State);
        var report = Assert.Single(result.Items);
        Assert.Equal(new[] { DefenderLifecycleEventKind.StoppedBeforeCompletion, DefenderLifecycleEventKind.Started, DefenderLifecycleEventKind.Finished }, report.Records.Select(row => row.Kind));
        Assert.Contains("not paired or attributed", result.Message, StringComparison.Ordinal);
        Assert.Contains("clean verdict", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyMatchingRetainedEventsNeverPromoteToCompleteOrNoScans()
    {
        var result = Parse(Root());
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Assert.Single(result.Items).Records);
        Assert.Contains("does not mean no scans occurred", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Disabled", CheckState.Unavailable)]
    [InlineData("Unavailable", CheckState.Unavailable)]
    [InlineData("Denied", CheckState.Failed)]
    [InlineData("Failed", CheckState.Failed)]
    public void FailedCoverageRemainsEmptyAndNeverCreatesEventEvidence(string status, CheckState expected)
    {
        var root = Root(); root["Status"] = status;
        var result = Parse(root); Assert.Equal(expected, result.State); Assert.Empty(result.Items);
        root["Records"] = new[] { Row() }; Refused(root);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("available")]
    [InlineData("private-value")]
    public void UnknownStatusCannotBecomeAnEmptySuccessfulQuery(string status)
    {
        var root = Root(); root["Status"] = status; Refused(root);
    }

    [Fact]
    public void HalfOpenWindowIncludesStartAndExcludesEnd()
    {
        Assert.Single(Assert.Single(Parse(Root(Row(time: End.AddDays(-1)))).Items).Records);
        Refused(Root(Row(time: End))); Refused(Root(Row(time: End.AddDays(-1).AddTicks(-1))));
    }

    [Fact]
    public void DuplicateRecordIdsWithDifferentOrEqualDataAreAmbiguousRatherThanLastWins()
    {
        Refused(Root(Row(1000, 1), Row(1001, 1)));
        var row = Row(); Refused(Root(row, row));
    }

    [Theory]
    [InlineData("Channel", "Application")]
    [InlineData("Provider", "private-value")]
    [InlineData("TimeUtc", "2026-11-01T11:00:00")]
    [InlineData("TimeUtc", "2026-11-01T11:00:00+01:00")]
    [InlineData("TimeUtc", "1601-01-01T00:00:00Z")]
    public void ForeignSourceAndUnqualifiedTimesRejectEntireResponse(string field, string value)
    {
        var row = Row(); row[field] = value; Refused(Root(row));
    }

    [Theory]
    [InlineData(1116, 1)]
    [InlineData(1000, 0)]
    [InlineData(1001, -1)]
    public void DetectionEventsAndInvalidRecordIdentifiersAreNotLifecycleEvidence(int id, long record)
        => Refused(Root(Row(id, record)));

    [Fact]
    public void StrictSchemaRefusesDuplicateUnknownAndMissingFieldsWithoutEchoingPayload()
    {
        var root = Root(Row()); root["Message"] = "private-value"; Refused(root);
        root = Root(); root.Remove("ObservedUtc"); Refused(root);
        string json = JsonSerializer.Serialize(Root()).Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal);
        Assert.Empty(DefenderLifecycleParser.Parse(json).Items);
        var row = Row(); row["ScanId"] = "private-value"; Refused(Root(row));
    }

    [Fact]
    public void TruncationRequiresFullBoundAndOversizedResponsesAreRefused()
    {
        var rows = Enumerable.Range(1, DefenderLifecyclePolicy.MaximumRecords).Select(i => Row(record: i)).ToArray();
        var root = Root(rows); root["Truncated"] = true;
        Assert.Equal(256, Assert.Single(Parse(root).Items).Records.Count);
        root["Records"] = rows[..^1]; Refused(root);
        root["Records"] = rows.Append(Row(record: 257)).ToArray(); Refused(root);
        Assert.Empty(DefenderLifecycleParser.Parse(new string('x', 196_609)).Items);
    }

    [Fact]
    public void ObservationMustFollowWindowAndCannotBeInFuture()
    {
        var root = Root(); root["ObservedUtc"] = End.AddTicks(-1).ToString("O"); Refused(root);
        root["ObservedUtc"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O"); Refused(root);
        root = Root(); root["StartUtc"] = End.AddDays(-2).ToString("O"); Refused(root);
    }

    [Fact]
    public void FailureCannotCarryTruncationOrMalformedBoolean()
    {
        var root = Root(); root["Status"] = "Denied"; root["Truncated"] = true; Refused(root);
        root = Root(); root["Truncated"] = "false"; Refused(root);
    }

    [Fact]
    public void PublishedContainerCannotChangeWhenOriginalListIsMutated()
    {
        var rows = new List<DefenderLifecycleEvent> { new(1000, 1, End.AddHours(-1)) };
        var report = new DefenderLifecycleReport(End.AddDays(-1), End, End.AddSeconds(1), false, rows);
        rows.Clear(); Assert.Single(report.Records);
        Assert.Throws<NotSupportedException>(() => ((IList<DefenderLifecycleEvent>)report.Records).Clear());
        Assert.True(DefenderLifecyclePolicy.IsConsistent(report, End.AddSeconds(1)));
    }

    [Fact]
    public void ResponseAdmissionRejectsDifferentRequestedWindowAndPreAttemptEvidence()
    {
        var result = Parse(Root(Row()));
        Assert.True(DefenderLifecyclePolicy.ValidateResult(result, 1, End.AddTicks(-1), End.AddSeconds(1)));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(result, 7, End.AddTicks(-1), End.AddSeconds(1)));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(result, 1, End.AddTicks(1), End.AddSeconds(1)));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(result with { State = CheckState.Available }, 1, End, End.AddSeconds(1)));
    }

    [Theory]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Cancelled)]
    public void SafeNegativeResultsPreserveGenuineStateWhileContradictoryEvidenceIsRefused(CheckState state)
    {
        var negative = CheckResult<DefenderLifecycleReport>.Empty(state, "Fixed bounded reason.");
        Assert.True(DefenderLifecyclePolicy.ValidateResult(negative, 1, End, End.AddSeconds(1)));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(negative with { Items = Parse(Root(Row())).Items }, 1, End, End.AddSeconds(1)));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(negative, 2, End, End.AddSeconds(1)));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(negative, 1, default, End.AddSeconds(1)));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(negative, 1, End.AddDays(1), End));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(negative with { Message = "unsafe\ntext" }, 1, End, End.AddSeconds(1)));
        Assert.False(DefenderLifecyclePolicy.ValidateResult(negative with { Message = new string('x', 4097) }, 1, End, End.AddSeconds(1)));
    }

    [Fact]
    public void CancellationIsNotConvertedToUnknownEvidence()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DefenderLifecycleParser.Parse(JsonSerializer.Serialize(Root(Row())), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => DefenderLifecycleParser.Parse("malformed", cancellation.Token));
    }
}
