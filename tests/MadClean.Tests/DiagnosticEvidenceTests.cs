using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DiagnosticEvidenceTests
{
    private const string Start = "2026-10-20T00:00:00Z";
    private const string End = "2026-10-27T00:00:00Z";
    private static Dictionary<string, object?> Event(long record = 10, string provider = "Application Error", int id = 1000, int version = 0,
        string channel = "Application", int level = 2, string time = "2026-10-25T12:00:00Z") => new()
    { ["Channel"] = channel, ["Provider"] = provider, ["EventId"] = id, ["Version"] = version, ["Level"] = level, ["RecordId"] = record, ["TimeUtc"] = time };
    private static Dictionary<string, object?> Channel(string name, params Dictionary<string, object?>[] records) => new()
    {
        ["Name"] = name, ["Status"] = "Available", ["Truncated"] = false, ["RecordCount"] = 10000L,
        ["OldestRecordId"] = 1L, ["OldestUtc"] = "2026-10-01T00:00:00Z", ["NewestUtc"] = End, ["BoundsStatus"] = "Available", ["Records"] = records
    };
    private static Dictionary<string, object?> Envelope(params Dictionary<string, object?>[] application) => new()
    { ["Version"] = 1, ["StartUtc"] = Start, ["EndUtc"] = End, ["Channels"] = new[] { Channel("Application", application), Channel("System") } };
    private static Dictionary<string, object?>[] Sources(Dictionary<string, object?> value) => (Dictionary<string, object?>[])value["Channels"]!;
    private static CheckResult<DiagnosticReport> Parse(Dictionary<string, object?> value) => DiagnosticEvidenceParser.Parse(JsonSerializer.Serialize(value));

    [Fact]
    public void GroupingUsesProviderIdVersionAndChannelAndPreservesActualRecordReferences()
    {
        var envelope = Envelope(Event(), Event(11, time: "2026-10-26T12:00:00Z"), Event(12, version: 1), Event(13, provider: "Different provider"));
        Sources(envelope)[1] = Channel("System", Event(14, channel: "System"));

        var report = Assert.Single(Parse(envelope).Items);

        Assert.Equal(4, report.Findings.Count);
        var recurring = Assert.Single(report.Findings, f => f.Channel == "Application" && f.Provider == "Application Error" && f.Version == 0);
        Assert.Equal(2, recurring.Count);
        Assert.Equal(new long[] { 10, 11 }, recurring.RecordIds);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T12:00:00Z"), recurring.FirstUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T12:00:00Z"), recurring.LastUtc);
        Assert.All(report.Findings, finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Observation)); Assert.False(string.IsNullOrWhiteSpace(finding.Uncertainty));
            Assert.False(string.IsNullOrWhiteSpace(finding.Impact)); Assert.False(string.IsNullOrWhiteSpace(finding.NextStep));
        });
    }

    [Fact]
    public void WerAndCrashRecordsAreNotInventedUniqueCrashesOrDeduplicatedByGuessingIncidentIdentity()
    {
        var result = Parse(Envelope(Event(), Event(11, "Windows Error Reporting", 1001, level: 4)));

        var report = Assert.Single(result.Items);
        Assert.Equal(2, report.Findings.Count);
        var wer = Assert.Single(report.Findings, f => f.Provider == "Windows Error Reporting");
        Assert.Contains("not another proven unique crash", wer.Uncertainty);
        Assert.Contains("Information (Windows-reported)", wer.Level);
        Assert.Contains(report.Coverage, line => line.Contains("not guessed distinct crashes", StringComparison.Ordinal));
    }

    [Fact]
    public void KernelPowerObservationDoesNotBlameHardwareOrOfferRepair()
    {
        var envelope = Envelope();
        Sources(envelope)[1] = Channel("System", Event(20, "Microsoft-Windows-Kernel-Power", 41, channel: "System", level: 1));

        var finding = Assert.Single(Assert.Single(Parse(envelope).Items).Findings);

        Assert.Contains("without a clean shutdown", finding.Observation);
        Assert.Contains("does not identify", finding.Uncertainty);
        Assert.Contains("No stress test", finding.NextStep);
    }

    [Fact]
    public void EmptyMatchingResultsAreCoverageNotAHealthySystemVerdict()
    {
        var result = Parse(Envelope());

        Assert.Equal(CheckState.Available, result.State);
        var report = Assert.Single(result.Items);
        Assert.Empty(report.Findings);
        Assert.Contains(report.Coverage, line => line.Contains("not a health verdict", StringComparison.Ordinal));
        Assert.Contains(report.Coverage, line => line.Contains("no matching usable records", StringComparison.Ordinal));
    }

    [Fact]
    public void ChannelAccessDenialPreservesOtherChannelsAndExplainsMissingCoverage()
    {
        var envelope = Envelope(Event()); Sources(envelope)[1]["Status"] = "Denied";

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        var report = Assert.Single(result.Items);
        Assert.Single(report.Findings);
        Assert.Contains(report.Coverage, line => line.StartsWith("System: access denied", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Denied", CheckState.Unavailable)]
    [InlineData("Failed", CheckState.Failed)]
    [InlineData("not-a-status", CheckState.Unknown)]
    public void EntirelyUnreadChannelsCannotBecomeSuccessfulEmptyEvidence(string status, CheckState expected)
    {
        var envelope = Envelope(); foreach (var source in Sources(envelope)) source["Status"] = status;

        var result = Parse(envelope);

        Assert.Equal(expected, result.State);
        Assert.Empty(Assert.Single(result.Items).Findings);
    }

    [Fact]
    public void MissingOrDuplicateChannelsAreNotSilentlyChosenOrTreatedAsEmpty()
    {
        var envelope = Envelope(Event());
        envelope["Channels"] = new[] { Sources(envelope)[0], Sources(envelope)[0] };

        var result = Parse(envelope);

        Assert.NotEqual(CheckState.Available, result.State);
        var report = Assert.Single(result.Items);
        Assert.Empty(report.Findings);
        Assert.Contains(report.Coverage, line => line.Contains("duplicate/ambiguous", StringComparison.Ordinal));
        Assert.Contains(report.Coverage, line => line.StartsWith("System: missing", StringComparison.Ordinal));
    }

    [Fact]
    public void TimeWindowUsesInclusiveStartAndExclusiveEndRatherThanAcceptingOutOfScopeEvents()
    {
        var result = Parse(Envelope(Event(1, time: Start), Event(2, time: End), Event(3, time: "2026-10-19T23:59:59Z")));

        Assert.Equal(CheckState.Partial, result.State);
        var finding = Assert.Single(Assert.Single(result.Items).Findings);
        Assert.Equal(new long[] { 1 }, finding.RecordIds);
        Assert.Equal(DateTimeOffset.Parse(Start), finding.FirstUtc);
    }

    [Fact]
    public void IncorrectChannelsFiltersAndMissingVersionAreNotInventedAsValidEvidence()
    {
        var missing = Event(3); missing["Version"] = null;
        var result = Parse(Envelope(Event(1, channel: "Security"), Event(2, level: 4), missing, Event(4, version: 1)));

        Assert.Equal(CheckState.Partial, result.State);
        var finding = Assert.Single(Assert.Single(result.Items).Findings);
        Assert.Equal(1, finding.Version);
        Assert.Equal(new long[] { 4 }, finding.RecordIds);
    }

    [Fact]
    public void UnminimizedPrivatePayloadFieldsAreRejectedAndNeverEnterReportOrMessages()
    {
        var privateEvent = Event(1);
        privateEvent["Message"] = "private-command-user-and-file-path";
        privateEvent["UserId"] = "private-user-identity";
        privateEvent["Properties"] = new[] { "private-dump-value" };

        var result = Parse(Envelope(privateEvent, Event(2)));
        string serialized = JsonSerializer.Serialize(result);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(new long[] { 2 }, Assert.Single(Assert.Single(result.Items).Findings).RecordIds);
        Assert.DoesNotContain("private-command-user-and-file-path", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user-identity", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-dump-value", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void IdenticalRecordReferencesDoNotDoubleCountAndConflictingReferencesAreDiscarded()
    {
        var duplicate = Parse(Envelope(Event(1), Event(1)));
        var conflict = Parse(Envelope(Event(1), Event(1, provider: "Conflicting provider")));

        Assert.Equal(1, Assert.Single(Assert.Single(duplicate.Items).Findings).Count);
        Assert.Equal(CheckState.Partial, duplicate.State);
        Assert.Empty(Assert.Single(conflict.Items).Findings);
        Assert.Equal(CheckState.Partial, conflict.State);
    }

    [Fact]
    public void RetentionStartingLaterThanRequestedWindowIsExplicitlyIncomplete()
    {
        var envelope = Envelope(Event()); Sources(envelope)[0]["OldestUtc"] = "2026-10-23T00:00:00Z";

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Contains(Assert.Single(result.Items).Coverage, line => line.Contains("earlier history may be absent or overwritten", StringComparison.Ordinal));
    }

    [Fact]
    public void EventAndFindingBoundsKeepRealEvidenceButDiscloseOmission()
    {
        var envelope = Envelope(Enumerable.Range(1, 1005).Select(i => Event(i, provider: "Provider " + i)).ToArray());

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        var report = Assert.Single(result.Items);
        Assert.Equal(256, report.Findings.Count);
        Assert.Contains(report.Coverage, line => line.Contains("TRUNCATED", StringComparison.Ordinal));
        Assert.Contains(report.Coverage, line => line.Contains("Finding-group limit reached", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitTruncationAndUnknownRetentionAreNotHiddenByASmallReturnedArray()
    {
        var envelope = Envelope(Event()); Sources(envelope)[0]["Truncated"] = true;
        Sources(envelope)[0]["BoundsStatus"] = "Unknown";
        Sources(envelope)[0]["OldestUtc"] = null; Sources(envelope)[0]["NewestUtc"] = null;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Contains(Assert.Single(result.Items).Coverage, line => line.Contains("retention bounds unknown", StringComparison.Ordinal) && line.Contains("TRUNCATED", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026-10-19T00:00:00Z")]
    [InlineData("2026-10-20T00:00:00+01:00")]
    [InlineData("not-a-date")]
    public void UnsupportedOrNonUtcQueryWindowsAreNotGuessed(string start)
    {
        var envelope = Envelope(Event()); envelope["StartUtc"] = start;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"Version\":2}")]
    public void MalformedEnvelopeCannotCreateFindingsOrAHealthVerdict(string json)
    {
        var result = DiagnosticEvidenceParser.Parse(json);

        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void CancellationRemainsCancellationRatherThanAnEmptyEvidenceSuccess()
    {
        using var source = new CancellationTokenSource(); source.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DiagnosticEvidenceParser.Parse(JsonSerializer.Serialize(Envelope(Event())), source.Token));
    }
}
