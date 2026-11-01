using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DiagnosticInspectionEvidenceTests
{
    private static readonly DateTimeOffset Completed = DateTimeOffset.UtcNow.AddMinutes(-1);
    private static readonly DateTimeOffset Attempted = Completed.AddSeconds(-5);
    private static CheckResult<DiskInfo> Disks(params DiskInfo[] rows) => new(CheckState.Available, "private-provider-sentinel", rows);
    private static DefenderInfo DefenderRow(bool? antivirus = true, bool? realtime = false, bool? service = true, string mode = "Normal") =>
        new(antivirus, realtime, service, mode, "private-version", "private-date", "private-history",
            [new("private-threat", "private-action", "private-change")]);
    private static DiagnosticInspectionEvidenceEntry Storage(CheckResult<DiskInfo> result) =>
        DiagnosticInspectionEvidence.Storage(result, Attempted, Completed);
    private static DiagnosticInspectionEvidenceEntry Defender(CheckResult<DefenderInfo> result) =>
        DiagnosticInspectionEvidence.Defender(result, Attempted, Completed);

    [Fact]
    public void StorageKeepsPerDriveFactsWithoutSummingPotentiallyOverlappingVolumes()
    {
        var evidence = Storage(Disks(new DiskInfo("c:\\", 100, 0), new("D:\\", 100, 100)));
        Assert.Equal(CheckState.Available, evidence.State);
        Assert.Contains("2 drive(s)", evidence.Observation);
        Assert.Contains("C:\\ free 0 bytes of 100", evidence.Coverage);
        Assert.Contains("D:\\ free 100 bytes of 100", evidence.Coverage);
        Assert.Contains("no global free-space sum", evidence.Uncertainty);
        Assert.DoesNotContain("private-provider", evidence.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\\\\server\\share", 100, 10)]
    [InlineData("C:\\folder", 100, 10)]
    [InlineData("C:\\", 0, 0)]
    [InlineData("C:\\", 100, -1)]
    [InlineData("C:\\", 100, 101)]
    public void InvalidDriveMetadataIsOmittedWithoutDiscardingValidDrive(string name, long total, long free)
    {
        var evidence = Storage(Disks(new DiskInfo(name, total, free), new("D:\\", 100, 20)));
        Assert.Equal(CheckState.Partial, evidence.State);
        Assert.Contains("1 drive(s)", evidence.Observation);
        Assert.DoesNotContain(name == "C:\\" ? "C:\\ free" : name, evidence.Coverage, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateDriveLettersAreAmbiguousEvenWithIdenticalMetadata()
    {
        var evidence = Storage(Disks(new DiskInfo("C:\\", 100, 50), new("c:\\", 100, 50)));
        Assert.Equal(CheckState.Unknown, evidence.State);
        Assert.DoesNotContain("free 50", evidence.Coverage);
    }

    [Fact]
    public void EmptyAndOverBoundInventoryDoNotBecomeZeroFreeSpaceFacts()
    {
        Assert.Equal(CheckState.Unknown, Storage(Disks()).State);
        var excessive = Enumerable.Range(0, 65).Select(_ => new DiskInfo("C:\\", 100, 20)).ToArray();
        var evidence = Storage(Disks(excessive));
        Assert.Equal(CheckState.Unknown, evidence.State);
        Assert.DoesNotContain("free 20", evidence.Coverage);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    public void NonpositiveSourcesCannotSmugglePlausibleValues(CheckState state)
    {
        var disk = Storage(Disks(new DiskInfo("C:\\", 100, 67)) with { State = state });
        var defender = Defender(new(state, "private-message", [DefenderRow()]));
        Assert.Equal(state, disk.State);
        Assert.Equal(state, defender.State);
        Assert.DoesNotContain("67", disk.Coverage);
        Assert.DoesNotContain("Antivirus enabled", defender.Coverage);
    }

    [Fact]
    public void DefenderRetainsOnlyFlagsAndRecognizedModeWithoutProtectionVerdict()
    {
        var evidence = Defender(new(CheckState.Available, "private-message", [DefenderRow(mode: "EDR Block Mode")]));
        Assert.Equal(CheckState.Available, evidence.State);
        Assert.Contains("real-time enabled: No", evidence.Coverage);
        Assert.Contains("mode: EDRBlock", evidence.Coverage);
        Assert.DoesNotContain("private-", evidence.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("protected", evidence.Observation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not establish protection", evidence.Uncertainty);
    }

    [Fact]
    public void UnrecognizedDefenderModeIsNotEchoedAndMissingFieldsRemainUnknown()
    {
        var evidence = Defender(new(CheckState.Available, "private-message", [DefenderRow(null, null, true, "private-mode")]));
        Assert.Equal(CheckState.Partial, evidence.State);
        Assert.Contains("mode: Unknown", evidence.Coverage);
        Assert.DoesNotContain("private-mode", evidence.ToString(), StringComparison.Ordinal);
        var unknown = Defender(new(CheckState.Available, "", [DefenderRow(null, null, null, "unknown")]));
        Assert.Equal(CheckState.Unknown, unknown.State);
        Assert.Equal(CheckState.Unknown, Defender(new(CheckState.Available, "", [])).State);
        Assert.Equal(CheckState.Unknown, Defender(new(CheckState.Available, "", [DefenderRow(), DefenderRow()])).State);
    }

    [Fact]
    public void PartialSourceIsNeverPromotedByCompleteFields()
    {
        Assert.Equal(CheckState.Partial, Storage(Disks(new DiskInfo("C:\\", 100, 10)) with { State = CheckState.Partial }).State);
        Assert.Equal(CheckState.Partial, Defender(new(CheckState.Partial, "", [DefenderRow()])).State);
    }

    [Fact]
    public void InvalidTimestampsSuppressValuesAndDoNotInventDates()
    {
        (DateTimeOffset Attempt, DateTimeOffset Complete)[] invalid =
        [ (default, Completed), (Completed, Attempted), (Attempted.ToOffset(TimeSpan.FromHours(1)), Completed),
          (Attempted, DateTimeOffset.UtcNow.AddDays(1)) ];
        foreach (var time in invalid)
        {
            var evidence = DiagnosticInspectionEvidence.Storage(Disks(new DiskInfo("C:\\", 100, 50)), time.Attempt, time.Complete);
            Assert.Equal(CheckState.Unknown, evidence.State);
            Assert.Null(evidence.AttemptedUtc); Assert.Null(evidence.CompletedUtc);
            Assert.DoesNotContain("free 50", evidence.Coverage);
        }
    }

    [Fact]
    public void SessionStatesDistinguishUnattemptedPendingAndInterruptedWithoutValues()
    {
        var notChecked = DiagnosticInspectionEvidence.NotChecked(DiagnosticEvidenceArea.Storage);
        Assert.Equal(CheckState.Unknown, notChecked.State); Assert.Null(notChecked.AttemptedUtc);
        var pending = DiagnosticInspectionEvidence.Pending(DiagnosticEvidenceArea.Defender, Attempted);
        Assert.Equal(Attempted, pending.AttemptedUtc); Assert.Null(pending.CompletedUtc);
        var cancelled = DiagnosticInspectionEvidence.Interrupted(DiagnosticEvidenceArea.Storage, CheckState.Cancelled, Attempted, Completed);
        Assert.Equal(CheckState.Cancelled, cancelled.State);
        Assert.Equal(CheckState.Unknown, DiagnosticInspectionEvidence.Interrupted(DiagnosticEvidenceArea.Storage, CheckState.Available, Attempted, Completed).State);
    }

    [Fact]
    public void PreCancelledInspectionDoesNotPublishEvidence()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DiagnosticInspectionEvidence.Storage(Disks(), Attempted, Completed, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => DiagnosticInspectionEvidence.Defender(null, Attempted, Completed, cancellation.Token));
    }
}
