using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ShortcutDaclObservationTests
{
    private static readonly DateTimeOffset End = DateTimeOffset.UtcNow.AddMinutes(-1);
    private static readonly DateTimeOffset Start = End.AddSeconds(-2);
    private static ShortcutDaclObservation Allocated => new(CheckState.Available, ShortcutDaclReason.Observed, 0,
        Start, End, true, false, true, false, 0, 8, 2, ShortcutDescriptorReleaseState.Released, null);
    private static ShortcutDaclObservation Normalize(ShortcutDaclObservation value) =>
        Assert.IsType<ShortcutDaclObservation>(ShortcutDaclObservationPolicy.Normalize(value, Start, End));
    private static void NoFacts(ShortcutDaclObservation value)
    {
        Assert.Null(value.DaclPresent); Assert.Null(value.NullDacl); Assert.Null(value.DaclProtected);
        Assert.Null(value.DaclDefaulted); Assert.Null(value.AceCount); Assert.Null(value.AclBytes); Assert.Null(value.Revision);
    }

    [Fact]
    public void AbsentNullAndAllocatedEmptyDaclsRemainDistinct()
    {
        var absent = Normalize(Allocated with { DaclPresent = false, NullDacl = null, DaclDefaulted = null,
            AceCount = null, AclBytes = null, Revision = null });
        var nullDacl = Normalize(Allocated with { NullDacl = true, AceCount = null, AclBytes = null, Revision = null });
        var empty = Normalize(Allocated);
        Assert.All(new[] { absent, nullDacl, empty }, value => Assert.Equal(CheckState.Available, value.State));
        Assert.False(absent.DaclPresent); Assert.Null(absent.NullDacl);
        Assert.True(nullDacl.DaclPresent); Assert.True(nullDacl.NullDacl);
        Assert.False(empty.NullDacl); Assert.Equal(0, empty.AceCount);
        Assert.True(absent.DaclProtected); Assert.False(nullDacl.DaclDefaulted);
    }

    [Fact]
    public void MissingOptionalSourceStaysMissing()
    {
        Assert.Null(ShortcutDaclObservationPolicy.Normalize(null, Start, End));
        var unsupported = new ShortcutDaclObservation(CheckState.Unsupported, ShortcutDaclReason.Unsupported,
            null, null, null, null, null, null, null, null, null, null, ShortcutDescriptorReleaseState.NotAttempted, null);
        var observed = Normalize(unsupported);
        Assert.Equal(CheckState.Unsupported, observed.State); Assert.Null(observed.NativeResult); NoFacts(observed);
    }

    [Fact]
    public void NativeFailureKeepsActualResultWithoutExposingPlausibleFacts()
    {
        var value = Normalize(Allocated with { State = CheckState.Unavailable, Reason = ShortcutDaclReason.NativeQueryFailed, NativeResult = 5 });
        Assert.Equal(CheckState.Unavailable, value.State); Assert.Equal(5U, value.NativeResult);
        Assert.Equal(Start, value.QueryStartedUtc); NoFacts(value);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    public void NonpositiveStatusCannotRetainAnObservedSuccessExplanation(CheckState state)
    {
        var value = Normalize(Allocated with { State = state, DescriptorRelease = ShortcutDescriptorReleaseState.NotAttempted });
        Assert.Equal(state, value.State);
        Assert.Equal(ShortcutDaclReason.MetadataInvalid, value.Reason);
        Assert.Equal(0U, value.NativeResult);
        Assert.Equal(ShortcutDescriptorReleaseState.NotAttempted, value.DescriptorRelease);
        NoFacts(value);
    }

    [Fact]
    public void NotCheckedReasonCannotHideActualQueryOrReleaseObservations()
    {
        var value = Normalize(Allocated with { State = CheckState.Unknown, Reason = ShortcutDaclReason.NotChecked });
        Assert.Equal(CheckState.Unknown, value.State);
        Assert.Equal(ShortcutDaclReason.MetadataInvalid, value.Reason);
        Assert.Equal(0U, value.NativeResult);
        Assert.Equal(ShortcutDescriptorReleaseState.Released, value.DescriptorRelease);
        Assert.Equal(Start, value.QueryStartedUtc);
        NoFacts(value);
    }

    [Fact]
    public void ReleaseFailureRejectsFactsAndPreservesSeparateActualError()
    {
        var value = Normalize(Allocated with { DescriptorRelease = ShortcutDescriptorReleaseState.Unconfirmed, DescriptorReleaseError = 6 });
        Assert.Equal(CheckState.Unknown, value.State);
        Assert.Equal(ShortcutDaclReason.DescriptorReleaseUnconfirmed, value.Reason);
        Assert.Equal(0U, value.NativeResult); Assert.Equal(6U, value.DescriptorReleaseError); NoFacts(value);
    }

    [Fact]
    public void ClaimedSuccessRequiresActualZeroAndConfirmedRelease()
    {
        ShortcutDaclObservation[] invalid =
        [ Allocated with { NativeResult = null }, Allocated with { NativeResult = 5 },
          Allocated with { DescriptorReleaseError = 6 }, Allocated with { DescriptorRelease = ShortcutDescriptorReleaseState.NotAttempted },
          Allocated with { State = CheckState.Partial } ];
        foreach (var item in invalid)
        {
            var normalized = Normalize(item); Assert.Equal(CheckState.Unknown, normalized.State); NoFacts(normalized);
        }
    }

    [Fact]
    public void BoundedAclFactsRejectMalformedAndContradictoryShapes()
    {
        ShortcutDaclObservation[] invalid =
        [ Allocated with { AceCount = -1 }, Allocated with { AceCount = 4097 },
          Allocated with { AclBytes = 7 }, Allocated with { AclBytes = 65536 },
          Allocated with { AceCount = 2, AclBytes = 12 }, Allocated with { Revision = 1 },
          Allocated with { DaclProtected = null }, Allocated with { DaclDefaulted = null },
          Allocated with { NullDacl = null }, Allocated with { NullDacl = true },
          Allocated with { DaclPresent = false }, Allocated with { DaclPresent = null } ];
        foreach (var item in invalid)
        {
            var value = Normalize(item); Assert.Equal(CheckState.Unknown, value.State); NoFacts(value);
        }
        Assert.Equal(CheckState.Available, Normalize(Allocated with { AceCount = 4096, AclBytes = 65535, Revision = 4 }).State);
    }

    [Fact]
    public void QueriesMustFitExactUtcEvidenceIntervalWithoutFutureOrDefaultDates()
    {
        ShortcutDaclObservation[] invalid =
        [ Allocated with { QueryStartedUtc = Start.AddTicks(-1) }, Allocated with { QueryCompletedUtc = End.AddTicks(1) },
          Allocated with { QueryStartedUtc = End, QueryCompletedUtc = Start }, Allocated with { QueryStartedUtc = default(DateTimeOffset) },
          Allocated with { QueryCompletedUtc = null }, Allocated with { QueryStartedUtc = Start.ToOffset(TimeSpan.FromHours(1)) } ];
        foreach (var item in invalid)
        {
            var value = Normalize(item); Assert.Equal(CheckState.Unknown, value.State);
            Assert.Null(value.QueryStartedUtc); Assert.Null(value.QueryCompletedUtc); NoFacts(value);
        }
        var future = End.AddYears(10);
        var invalidOuter = ShortcutDaclObservationPolicy.Normalize(Allocated, Start, future)!;
        Assert.Equal(CheckState.Unknown, invalidOuter.State); NoFacts(invalidOuter);
        Assert.Equal(0U, invalidOuter.NativeResult);
    }

    [Fact]
    public void DescriptorMetadataCannotBecomeEffectiveAccessOwnershipOrActionEvidence()
    {
        var names = typeof(ShortcutDaclObservation).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Owner", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Sid", StringComparison.OrdinalIgnoreCase) || name.Contains("Effective", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("CanMove", StringComparison.OrdinalIgnoreCase) || name.Contains("Private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CancellationDoesNotPublishDaclFacts()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ShortcutDaclObservationPolicy.Normalize(Allocated, Start, End, cancellation.Token));
    }
}
