using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ShortcutActionPreviewTests
{
    private const string Root = @"C:\Selected";
    private const string Link = @"C:\Selected\example.lnk";
    private static (ShortcutRecheckScope Scope, CheckResult<ShortcutObservation> Current, ShortcutFileEvidence Evidence) Fixture(CheckState source = CheckState.Available)
    {
        var prior = new ShortcutObservation(Link, @"C:\Target\absent.exe", ShortcutTargetState.LocalTargetMissing,
            "Local target missing", ShortcutTargetClassifier.Source, DateTimeOffset.UtcNow.AddMinutes(-1));
        var report = new ShortcutReport([prior], [], 1, 0, false, "Explicit selected folder coverage");
        var scope = Assert.Single(ShortcutRecheckScope.Create(Root, new(source, "", [report]), prior).Items);
        var at = DateTimeOffset.UtcNow;
        var current = prior with { CheckedAt = at };
        var evidence = new ShortcutFileEvidence(Link, 123, Guid.NewGuid(), 42, 1, new string('A', 64), at, at,
            ShortcutReadConsistency.ObservedConsistent);
        return (scope, new(CheckState.Available, "private-provider-message", [current]), evidence);
    }
    private static ShortcutActionPreview Preview(CheckResult<ShortcutActionPreview> result) => Assert.Single(result.Items);

    [Fact]
    public void CompleteMissingTargetEvidenceIsDocumentaryAndNeverMovePermission()
    {
        var f = Fixture();
        var result = ShortcutActionPreviewPolicy.Create(f.Scope, f.Current, f.Evidence, ShortcutProtectionState.OutsideKnownProtectedFolders);
        var preview = Preview(result);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(ShortcutActionReviewState.Preview, preview.ReviewState);
        Assert.False(preview.CanMove);
        Assert.Equal(f.Evidence, preview.Evidence);
        Assert.Contains("NotChecked", preview.DaclAssessment);
        Assert.Contains("not immutability", preview.Coverage);
        Assert.Contains("identity continuity is unknown", preview.Coverage);
        Assert.Contains("No lease survives", preview.Coverage);
        Assert.DoesNotContain("private-provider", preview.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ShortcutProtectionState.Unknown, ShortcutActionReviewState.Unknown)]
    [InlineData(ShortcutProtectionState.Protected, ShortcutActionReviewState.Blocked)]
    public void ProtectionStopsBeforeCurrentMetadataAndContents(ShortcutProtectionState protection, ShortcutActionReviewState expected)
    {
        var f = Fixture();
        var preview = Preview(ShortcutActionPreviewPolicy.Create(f.Scope, null, f.Evidence, protection));
        Assert.Equal(expected, preview.ReviewState);
        Assert.Null(preview.Current); Assert.Null(preview.Evidence); Assert.False(preview.CanMove);
    }

    [Theory]
    [InlineData(ShortcutTargetState.TargetExists, ShortcutActionReviewState.Blocked)]
    [InlineData(ShortcutTargetState.Unsupported, ShortcutActionReviewState.Blocked)]
    [InlineData(ShortcutTargetState.Unknown, ShortcutActionReviewState.Unknown)]
    [InlineData(ShortcutTargetState.UnresolvedMetadata, ShortcutActionReviewState.Unknown)]
    public void HistoricalMissingTargetNeverOverridesCurrentState(ShortcutTargetState target, ShortcutActionReviewState expected)
    {
        var f = Fixture();
        var current = f.Current with { Items = [f.Current.Items[0] with { State = target }] };
        var preview = Preview(ShortcutActionPreviewPolicy.Create(f.Scope, current, f.Evidence, ShortcutProtectionState.OutsideKnownProtectedFolders));
        Assert.Equal(expected, preview.ReviewState); Assert.Null(preview.Evidence); Assert.False(preview.CanMove);
    }

    [Fact]
    public void EvidenceMustMatchExactSelectedPathIdentityAndSingleLink()
    {
        var f = Fixture();
        ShortcutFileEvidence[] invalid =
        [
            f.Evidence with { ShortcutPath = @"C:\Selected\other.lnk" }, f.Evidence with { VolumeSerialNumber = 0 },
            f.Evidence with { FileId = Guid.Empty }, f.Evidence with { LinkCount = 2 }, f.Evidence with { LinkCount = 0 },
            f.Evidence with { Bytes = 0 }, f.Evidence with { Bytes = ShortcutActionPreviewPolicy.MaximumBytes + 1 },
            f.Evidence with { Sha256 = new string('G', 64) }, f.Evidence with { Sha256 = new string('A', 63) },
            f.Evidence with { Consistency = ShortcutReadConsistency.Unknown }
        ];
        foreach (var evidence in invalid)
        {
            var preview = Preview(ShortcutActionPreviewPolicy.Create(f.Scope, f.Current, evidence, ShortcutProtectionState.OutsideKnownProtectedFolders));
            Assert.Equal(ShortcutActionReviewState.Unknown, preview.ReviewState);
            Assert.Null(preview.Evidence); Assert.False(preview.CanMove);
        }
    }

    [Fact]
    public void FutureNonUtcExpiredAndMismatchedInspectionTimesAreRejected()
    {
        var f = Fixture();
        ShortcutFileEvidence[] invalid =
        [
            f.Evidence with { ReadStartedUtc = default },
            f.Evidence with { ReadCompletedUtc = DateTimeOffset.UtcNow.AddDays(1) },
            f.Evidence with { ReadStartedUtc = f.Evidence.ReadStartedUtc.ToOffset(TimeSpan.FromHours(1)) },
            f.Evidence with { ReadStartedUtc = f.Scope.ValidatedUtc.AddTicks(-1) },
            f.Evidence with { ReadCompletedUtc = f.Current.Items[0].CheckedAt.AddTicks(-1) }
        ];
        foreach (var evidence in invalid)
            Assert.Null(Preview(ShortcutActionPreviewPolicy.Create(f.Scope, f.Current, evidence,
                ShortcutProtectionState.OutsideKnownProtectedFolders)).Evidence);
        var expired = f.Current with { Items = [f.Current.Items[0] with { CheckedAt = DateTimeOffset.UtcNow.AddMinutes(-3) }] };
        Assert.Equal(ShortcutActionReviewState.Unknown, Preview(ShortcutActionPreviewPolicy.Create(f.Scope, expired,
            f.Evidence, ShortcutProtectionState.OutsideKnownProtectedFolders)).ReviewState);
    }

    [Fact]
    public void PlausibleCurrentRowsWithFailedOrAmbiguousQueryAreNotEvidence()
    {
        var f = Fixture();
        CheckResult<ShortcutObservation>[] invalid =
        [ f.Current with { State = CheckState.Unknown }, f.Current with { State = CheckState.Failed },
          f.Current with { Items = [f.Current.Items[0], f.Current.Items[0]] },
          f.Current with { Items = [f.Current.Items[0] with { Source = "untrusted" }] },
          f.Current with { Items = [f.Current.Items[0] with { TargetPath = @"\\server\share\target.exe" }] },
          f.Current with { Items = [f.Current.Items[0] with { ShortcutPath = @"C:\Selected\other.lnk" }] } ];
        foreach (var current in invalid)
        {
            var preview = Preview(ShortcutActionPreviewPolicy.Create(f.Scope, current, f.Evidence, ShortcutProtectionState.OutsideKnownProtectedFolders));
            Assert.Equal(ShortcutActionReviewState.Unknown, preview.ReviewState);
            Assert.Null(preview.Current); Assert.Null(preview.Evidence);
        }
    }

    [Fact]
    public void HistoricalAndCurrentPartialCoverageIsNeverPromoted()
    {
        var f = Fixture(CheckState.Partial);
        Assert.Equal(CheckState.Partial, ShortcutActionPreviewPolicy.Create(f.Scope, f.Current, f.Evidence, ShortcutProtectionState.OutsideKnownProtectedFolders).State);
        var complete = Fixture();
        Assert.Equal(CheckState.Partial, ShortcutActionPreviewPolicy.Create(complete.Scope, complete.Current with { State = CheckState.Partial },
            complete.Evidence, ShortcutProtectionState.OutsideKnownProtectedFolders).State);
    }

    [Fact]
    public void PartialHistoryCannotMakeAnUnperformedBlockedOrFailedInspectionPositive()
    {
        var f = Fixture(CheckState.Partial);
        var unknown = ShortcutActionPreviewPolicy.Create(f.Scope, null, null, ShortcutProtectionState.Unknown);
        var blocked = ShortcutActionPreviewPolicy.Create(f.Scope, null, null, ShortcutProtectionState.Protected);
        var failed = ShortcutActionPreviewPolicy.Create(f.Scope, f.Current with { State = CheckState.Failed },
            f.Evidence, ShortcutProtectionState.OutsideKnownProtectedFolders);
        Assert.Equal(CheckState.Unknown, unknown.State);
        Assert.Equal(CheckState.Unavailable, blocked.State);
        Assert.Equal(CheckState.Failed, failed.State);
        foreach (var result in new[] { unknown, blocked, failed })
        {
            var preview = Preview(result);
            Assert.Equal(CheckState.Partial, preview.SourceCoverageState);
            Assert.Null(preview.Evidence);
            Assert.False(preview.CanMove);
        }
    }

    [Theory]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    public void GenuineCurrentProviderFailureRetainsItsDistinctStateDespitePartialHistory(CheckState state)
    {
        var f = Fixture(CheckState.Partial);
        var current = f.Current with { State = state };
        var result = ShortcutActionPreviewPolicy.Create(f.Scope, current, f.Evidence,
            ShortcutProtectionState.OutsideKnownProtectedFolders);
        Assert.Equal(state, result.State);
        Assert.Equal(ShortcutActionReviewState.Unknown, Preview(result).ReviewState);
        Assert.Null(Preview(result).Current); Assert.Null(Preview(result).Evidence);
        Assert.Equal(CheckState.Unknown, ShortcutActionPreviewPolicy.Create(f.Scope, current, f.Evidence,
            ShortcutProtectionState.Unknown).State);
    }

    [Fact]
    public void CancellationDoesNotPublishAnEvidencePreview()
    {
        var f = Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ShortcutActionPreviewPolicy.Create(f.Scope, f.Current, f.Evidence,
            ShortcutProtectionState.OutsideKnownProtectedFolders, cancellation.Token));
    }

    [Fact]
    public void OptionalDaclFactsAreBoundToThisReadAndGrantNoActionPermission()
    {
        var f = Fixture();
        var dacl = new ShortcutDaclObservation(CheckState.Available, ShortcutDaclReason.Observed, 0,
            f.Evidence.ReadStartedUtc, f.Evidence.ReadCompletedUtc, true, false, true, false, 1, 16, 2,
            ShortcutDescriptorReleaseState.Released, null);
        var evidence = f.Evidence with { Dacl = dacl };
        var preview = Preview(ShortcutActionPreviewPolicy.Create(f.Scope, f.Current, evidence,
            ShortcutProtectionState.OutsideKnownProtectedFolders));
        Assert.Equal(CheckState.Available, preview.Evidence!.Dacl!.State);
        Assert.Equal(ShortcutActionReviewState.Preview, preview.ReviewState);
        Assert.False(preview.CanMove);
        Assert.Contains("remain unverified", preview.DaclAssessment);
        Assert.Contains("ownership", preview.DaclAssessment);
    }

    [Fact]
    public void RejectedDaclFactsDoNotBecomeAPermissionsVerdictOrEraseIndependentFileEvidence()
    {
        var f = Fixture();
        var evidence = f.Evidence with
        {
            Dacl = new(CheckState.Available, ShortcutDaclReason.Observed, 0,
                f.Evidence.ReadStartedUtc.AddSeconds(-1), f.Evidence.ReadCompletedUtc,
                true, false, true, false, 1, 16, 2, ShortcutDescriptorReleaseState.Released, null)
        };
        var result = ShortcutActionPreviewPolicy.Create(f.Scope, f.Current, evidence,
            ShortcutProtectionState.OutsideKnownProtectedFolders);
        var preview = Preview(result);
        Assert.Equal(CheckState.Available, result.State); // Describes file evidence, not DACL/effective access.
        Assert.Equal(ShortcutActionReviewState.Preview, preview.ReviewState);
        Assert.Equal(CheckState.Unknown, preview.Evidence!.Dacl!.State);
        Assert.Null(preview.Evidence.Dacl.DaclPresent);
        Assert.False(preview.CanMove);
    }
}
