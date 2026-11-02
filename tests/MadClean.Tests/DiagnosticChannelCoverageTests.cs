using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DiagnosticChannelCoverageTests
{
    private const string Start = "2026-10-20T00:00:00Z", End = "2026-10-27T00:00:00Z";
    private static Dictionary<string, object?> Event(long record, string channel = "Application", int id = 1000) => new()
    {
        ["Channel"] = channel, ["Provider"] = "Owned metadata fixture", ["EventId"] = id,
        ["Version"] = 0, ["Level"] = 2, ["RecordId"] = record, ["TimeUtc"] = "2026-10-25T12:00:00Z"
    };
    private static Dictionary<string, object?> Channel(string name, params Dictionary<string, object?>[] records) => new()
    {
        ["Name"] = name, ["Status"] = "Available", ["Truncated"] = false, ["RecordCount"] = 10000L,
        ["OldestRecordId"] = 1L, ["OldestUtc"] = "2026-10-01T00:00:00Z", ["NewestUtc"] = End,
        ["BoundsStatus"] = "Available", ["Records"] = records
    };
    private static CheckResult<DiagnosticReport> Parse(params Dictionary<string, object?>[] sources) =>
        DiagnosticEvidenceParser.Parse(JsonSerializer.Serialize(new { Version = 1, StartUtc = Start, EndUtc = End, Channels = sources }));
    private static DiagnosticChannelCoverage Source(DiagnosticReport report, string channel) =>
        Assert.Single(report.ChannelCoverage, row => row.Channel == channel);
    private static void UnknownCounts(DiagnosticChannelCoverage row)
    {
        Assert.Null(row.UsableMatchingRecords); Assert.Null(row.WholeLogRecordCount); Assert.Null(row.Truncated);
        Assert.Null(row.RejectedRecords); Assert.Null(row.DuplicateRecords);
        Assert.Null(row.OldestRetainedUtc); Assert.Null(row.NewestRetainedUtc);
        Assert.Equal(DiagnosticRetentionState.Unknown, row.RetentionState);
    }

    [Fact]
    public void AccessDeniedDoesNotBecomeEmptyOrHideTheOtherSuccessfulChannel()
    {
        var application = Channel("Application"); application["Status"] = "Denied";
        var result = Parse(application, Channel("System", Event(4, "System")));
        var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        var denied = Source(report, "Application");
        Assert.Equal(CheckState.Unavailable, denied.State);
        Assert.Equal(DiagnosticChannelCoverageReason.AccessDenied, denied.Reason); UnknownCounts(denied);
        var observed = Source(report, "System");
        Assert.Equal(CheckState.Available, observed.State); Assert.Equal(1, observed.UsableMatchingRecords);
        Assert.Equal(10000L, observed.WholeLogRecordCount);
    }

    [Theory]
    [InlineData("Disabled", CheckState.Unavailable, DiagnosticChannelCoverageReason.Disabled)]
    [InlineData("Unavailable", CheckState.Unavailable, DiagnosticChannelCoverageReason.Unavailable)]
    [InlineData("Failed", CheckState.Failed, DiagnosticChannelCoverageReason.Failed)]
    [InlineData("unsupported-private-detail", CheckState.Unknown, DiagnosticChannelCoverageReason.UnknownStatus)]
    public void UnreadStatusesKeepCountsUnknownAndDoNotEchoUnknownProviderStatus(string status, CheckState state,
        DiagnosticChannelCoverageReason reason)
    {
        var application = Channel("Application", Event(7)); application["Status"] = status;
        var row = Source(Assert.Single(Parse(application, Channel("System")).Items), "Application");
        Assert.Equal(state, row.State); Assert.Equal(reason, row.Reason); UnknownCounts(row);
        Assert.DoesNotContain("private-detail", row.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingAndAmbiguousSourcesDifferFromACompletedQueryWithNoMatchingRecords()
    {
        var missing = Source(Assert.Single(Parse(Channel("System")).Items), "Application");
        Assert.Equal(DiagnosticChannelCoverageReason.MissingResponse, missing.Reason); UnknownCounts(missing);
        var duplicate = Source(Assert.Single(Parse(Channel("Application", Event(1)), Channel("Application", Event(2)), Channel("System")).Items), "Application");
        Assert.Equal(DiagnosticChannelCoverageReason.AmbiguousResponse, duplicate.Reason); UnknownCounts(duplicate);
        var empty = Source(Assert.Single(Parse(Channel("Application"), Channel("System")).Items), "Application");
        Assert.Equal(CheckState.Available, empty.State); Assert.Equal(0, empty.UsableMatchingRecords);
        Assert.False(empty.Truncated); Assert.Equal(10000L, empty.WholeLogRecordCount);
    }

    [Fact]
    public void MalformedAvailableChannelDoesNotProduceAFalseZeroCount()
    {
        var application = Channel("Application"); application["Records"] = "not an array";
        var result = Parse(application, Channel("System"));
        var row = Source(Assert.Single(result.Items), "Application");
        Assert.Equal(CheckState.Unknown, row.State); Assert.Equal(DiagnosticChannelCoverageReason.MalformedResponse, row.Reason);
        UnknownCounts(row); Assert.Equal(CheckState.Partial, result.State);
    }

    [Fact]
    public void RejectedPrivateFieldsAndDuplicateReferencesKeepPerChannelCoveragePartial()
    {
        var privateRow = Event(2); privateRow["Message"] = "owned-private-payload-must-not-appear";
        var result = Parse(Channel("Application", Event(1), Event(1), privateRow), Channel("System"));
        var report = Assert.Single(result.Items); var row = Source(report, "Application");
        Assert.Equal(CheckState.Partial, row.State); Assert.Equal(1, row.UsableMatchingRecords);
        Assert.Equal(1, row.RejectedRecords); Assert.Equal(1, row.DuplicateRecords);
        Assert.Equal(CheckState.Available, Source(report, "System").State);
        Assert.DoesNotContain("owned-private-payload", row.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRetentionProbeKeepsMatchingCountKnownButWholeLogBoundsUnknown()
    {
        var application = Channel("Application"); application["BoundsStatus"] = "Failed";
        application["RecordCount"] = null;
        var row = Source(Assert.Single(Parse(application, Channel("System")).Items), "Application");
        Assert.Equal(CheckState.Partial, row.State); Assert.Equal(0, row.UsableMatchingRecords);
        Assert.Equal(DiagnosticRetentionState.Unknown, row.RetentionState);
        Assert.Null(row.WholeLogRecordCount); Assert.Null(row.OldestRetainedUtc); Assert.Null(row.NewestRetainedUtc);
    }

    [Fact]
    public void EmptyWholeLogProbeIsDistinctFromAnUnreadSource()
    {
        var application = Channel("Application"); application["BoundsStatus"] = "Empty";
        application["RecordCount"] = 0; application["OldestRecordId"] = null;
        application["OldestUtc"] = null; application["NewestUtc"] = null;
        var row = Source(Assert.Single(Parse(application, Channel("System")).Items), "Application");
        Assert.Equal(CheckState.Available, row.State); Assert.Equal(0, row.UsableMatchingRecords);
        Assert.Equal(0L, row.WholeLogRecordCount); Assert.Equal(DiagnosticRetentionState.Empty, row.RetentionState);
        Assert.Null(row.OldestRetainedUtc); Assert.Null(row.NewestRetainedUtc);
    }

    [Fact]
    public void ReturnedRecordLimitIsVisibleAndDoesNotInflateAcceptedCounts()
    {
        var application = Channel("Application", Enumerable.Range(1, 1001).Select(i => Event(i)).ToArray());
        var result = Parse(application, Channel("System"));
        var row = Source(Assert.Single(result.Items), "Application");
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Partial, row.State);
        Assert.Equal(1000, row.UsableMatchingRecords); Assert.True(row.Truncated); Assert.Equal(0, row.RejectedRecords);
    }

    [Fact]
    public void SourceCountsStayExplicitlyBeforeFindingGroupLimit()
    {
        var result = Parse(Channel("Application", Enumerable.Range(1, 257).Select(i => Event(i, id: i)).ToArray()), Channel("System"));
        var report = Assert.Single(result.Items); var row = Source(report, "Application");
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Available, row.State);
        Assert.Equal(257, row.UsableMatchingRecords); Assert.Equal(256, report.Findings.Sum(finding => finding.Count));
        Assert.Contains("before the finding-group limit", row.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void AdditionalChannelCannotBecomeAnExtraTrustedCoverageRow()
    {
        var result = Parse(Channel("Application"), Channel("System"), Channel("Security", Event(1, "Security")));
        var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(2, report.ChannelCoverage.Count);
        Assert.DoesNotContain(report.ChannelCoverage, row => row.Channel == "Security");
    }
    [Theory]
    [InlineData("OldestRecordId", 1L)]
    [InlineData("OldestRecordId", 0L)]
    [InlineData("OldestRecordId", "malformed-private-retention")]
    [InlineData("OldestUtc", "malformed-private-retention")]
    [InlineData("NewestUtc", "malformed-private-retention")]
    [InlineData("OldestUtc", "2026-10-01T00:00:00Z")]
    public void EmptyRetentionRequiresExplicitNullReferencesInsteadOfFailedParsing(string field, object nonNull)
    {
        var application = Channel("Application");
        application["BoundsStatus"] = "Empty"; application["RecordCount"] = 0;
        application["OldestRecordId"] = null; application["OldestUtc"] = null; application["NewestUtc"] = null;
        application[field] = nonNull;
        var result = Parse(application, Channel("System"));
        var row = Source(Assert.Single(result.Items), "Application");
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Partial, row.State);
        Assert.Equal(0, row.UsableMatchingRecords); Assert.Equal(0L, row.WholeLogRecordCount);
        Assert.Equal(DiagnosticRetentionState.Unknown, row.RetentionState);
        Assert.Null(row.OldestRetainedUtc); Assert.Null(row.NewestRetainedUtc);
        Assert.DoesNotContain("malformed-private-retention", row.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroWholeLogCountCannotQualifyAvailableRetainedRecordBoundsEvenWithNoMatches()
    {
        var application = Channel("Application"); application["RecordCount"] = 0;
        var result = Parse(application, Channel("System"));
        var report = Assert.Single(result.Items); var row = Source(report, "Application");
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Partial, row.State);
        Assert.Equal(0L, row.WholeLogRecordCount); Assert.Equal(0, row.UsableMatchingRecords);
        Assert.Equal(DiagnosticRetentionState.Unknown, row.RetentionState);
        Assert.Null(row.OldestRetainedUtc); Assert.Null(row.NewestRetainedUtc);
        Assert.Equal(CheckState.Available, Source(report, "System").State);
    }

}
