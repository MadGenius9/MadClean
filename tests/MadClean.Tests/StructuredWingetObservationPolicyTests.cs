using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class StructuredWingetObservationPolicyTests
{
    private static readonly DateTimeOffset Attempt = new(2026, 11, 4, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Observed = Attempt.AddSeconds(10);
    private static AppUpdate Offer => new("Example", "Example.App", "rolling-old", "rolling-new", "winget", CheckState.Available);
    private static StructuredWingetObservation Success(params AppUpdate[] rows) => new(
        new(CheckState.Available, "Catalog observations only.", rows),
        new(StructuredWingetStage.Completed, StructuredWingetOutcome.Completed, null, true,
            Attempt.AddSeconds(1), Attempt.AddSeconds(9), rows.Length, 0, true, true, true, true, true, false, false));
    private static bool Accept(StructuredWingetObservation observation, bool pending = false) =>
        StructuredWingetObservationPolicy.IsConsistent(observation, Attempt, Observed, pending);

    [Fact]
    public void EmptySuccessRequiresTheSameActualSourceAndCompletionEvidenceAsRows()
    {
        var empty = Success();
        Assert.True(Accept(empty));
        Assert.False(Accept(empty with { Diagnostic = empty.Diagnostic with { SourceMatched = null } }));
        Assert.False(Accept(empty with { Diagnostic = empty.Diagnostic with { CompletedUtc = null } }));
        Assert.False(Accept(empty, pending: true));
    }

    [Fact]
    public void APositiveRowCannotCompensateForMissingControlOrOwnershipFacts()
    {
        var good = Success(Offer);
        Assert.True(Accept(good));
        foreach (var changed in new[]
        {
            good.Diagnostic with { AgreementsDisabled = false }, good.Diagnostic with { AuthenticationNone = null },
            good.Diagnostic with { ConnectSucceeded = false }, good.Diagnostic with { FindSucceeded = null },
            good.Diagnostic with { HResult = -1 }, good.Diagnostic with { Stage = StructuredWingetStage.Projection },
            good.Diagnostic with { StartedUtc = null }, good.Diagnostic with { ProjectedCount = 0 }
        }) Assert.False(Accept(good with { Diagnostic = changed }));
        Assert.False(Accept(good, pending: true));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData(" unknown ")]
    [InlineData("-")]
    [InlineData(" - ")]
    [InlineData("")]
    public void PlaceholderVersionsNeverBecomePositiveOffers(string version)
    {
        Assert.False(Accept(Success(Offer with { InstalledVersion = version })));
        Assert.False(Accept(Success(Offer with { AvailableVersion = version })));
        if (StructuredWingetPolicy.IsKnownText(version))
            Assert.True(Accept(Success(Offer with { AvailableVersion = version, Status = CheckState.Unknown })));
    }

    [Fact]
    public void PartialCoveragePreservesPositiveRowsWithoutClaimingCompleteCoverage()
    {
        var partial = Success(Offer) with { Result = new(CheckState.Partial, "Limited coverage.", [Offer]) };
        partial = partial with { Diagnostic = partial.Diagnostic with
            { Outcome = StructuredWingetOutcome.Partial, NativeLimitExceeded = null, ProjectionLimitExceeded = true, PropertyFailureCount = 2 } };
        Assert.True(Accept(partial));
        Assert.False(Accept(partial with { Result = partial.Result with { State = CheckState.Available } }));
        var complete = Success(Offer);
        Assert.False(Accept(complete with { Diagnostic = complete.Diagnostic with { NativeLimitExceeded = null } }));
        Assert.False(Accept(complete with { Diagnostic = complete.Diagnostic with { PropertyFailureCount = 1 } }));
    }

    [Fact]
    public void CancellationRetainsHistoricalProjectionCountButPublishesNoCurrentRows()
    {
        var cancelled = Success(Offer) with
        {
            Result = CheckResult<AppUpdate>.Empty(CheckState.Cancelled, "Caller stopped waiting."),
            Diagnostic = Success(Offer).Diagnostic with { Outcome = StructuredWingetOutcome.Cancelled, CompletedUtc = null }
        };
        Assert.True(Accept(cancelled, pending: true));
        Assert.True(Accept(cancelled));
        Assert.False(Accept(cancelled with { Result = cancelled.Result with { Items = new[] { Offer } } }, pending: true));
        var timeout = cancelled with
        {
            Result = cancelled.Result with { State = CheckState.Unknown },
            Diagnostic = cancelled.Diagnostic with { Outcome = StructuredWingetOutcome.TimedOut }
        };
        Assert.True(Accept(timeout, pending: true));
    }

    [Fact]
    public void UnsupportedPlatformAndCompletedInitializationFailureRemainDistinctFromAnAttempt()
    {
        var unavailable = new StructuredWingetObservation(CheckResult<AppUpdate>.Empty(CheckState.Unsupported, "Unsupported platform."),
            new(StructuredWingetStage.NotStarted, StructuredWingetOutcome.Unsupported, null, false,
                null, null, 0, 0, null, null, null, null, null, null, null));
        Assert.True(Accept(unavailable));
        Assert.False(Accept(unavailable with { Diagnostic = unavailable.Diagnostic with { StartedUtc = Attempt.AddSeconds(1) } }));
        var initFailure = unavailable with
        {
            Result = unavailable.Result with { State = CheckState.Failed },
            Diagnostic = unavailable.Diagnostic with { Outcome = StructuredWingetOutcome.Failed, HResult = -1, CompletedUtc = Attempt.AddSeconds(2) }
        };
        Assert.True(Accept(initFailure));
    }

    [Fact]
    public void CompletedEvidenceMustBelongToThisAttemptAndUseUtc()
    {
        var good = Success(Offer);
        foreach (var changed in new[]
        {
            good.Diagnostic with { StartedUtc = Attempt.AddTicks(-1) },
            good.Diagnostic with { CompletedUtc = Observed.AddTicks(1) },
            good.Diagnostic with { CompletedUtc = Attempt },
            good.Diagnostic with { StartedUtc = Attempt.AddSeconds(1).ToOffset(TimeSpan.FromHours(1)) },
            good.Diagnostic with { CompletedUtc = default(DateTimeOffset) }
        }) Assert.False(Accept(good with { Diagnostic = changed }));
        Assert.False(StructuredWingetObservationPolicy.IsConsistent(good, Observed, Attempt, false));
    }

    [Fact]
    public void MalformedOrForeignRowsCannotReachTheCurrentCollection()
    {
        Assert.False(Accept(Success(Offer with { Source = "other-source" })));
        Assert.False(Accept(Success(Offer with { Name = "hidden\nrow" })));
        Assert.False(Accept(Success(Offer with { Id = new string('x', 513) })));
        Assert.False(Accept(Success(Offer with { Status = CheckState.Partial })));
        Assert.False(Accept(Success(new AppUpdate[] { null! })));
        var duplicate = Offer with { Id = "example.app" };
        Assert.False(Accept(Success(Offer, duplicate)));
        Assert.True(Accept(Success(Offer with { Status = CheckState.Unknown }, duplicate with { Status = CheckState.Unknown })));
    }

    [Fact]
    public void UndefinedEnumsAndOutOfBoundsCountersFailClosed()
    {
        var good = Success(Offer);
        foreach (var changed in new[]
        {
            good.Diagnostic with { Stage = (StructuredWingetStage)999 },
            good.Diagnostic with { Outcome = (StructuredWingetOutcome)999 },
            good.Diagnostic with { PropertyFailureCount = 4097 },
            good.Diagnostic with { ProjectedCount = -1 }
        }) Assert.False(Accept(good with { Diagnostic = changed }));
        Assert.False(Accept(good with { Result = good.Result with { State = (CheckState)999 } }));
        Assert.False(Accept(Success(Enumerable.Repeat(Offer with { Status = CheckState.Unknown }, 257).ToArray())));
    }

    [Fact]
    public void BusyRefusalCannotBorrowHistoricalSourceSuccessOrCompletion()
    {
        var busy = new StructuredWingetObservation(CheckResult<AppUpdate>.Empty(CheckState.Unknown, "Previous worker remains owned."),
            new(StructuredWingetStage.NotStarted, StructuredWingetOutcome.Busy, null, false,
                null, null, 0, 0, null, null, null, null, null, null, null));
        Assert.True(Accept(busy, pending: true));
        Assert.False(Accept(busy with { Diagnostic = busy.Diagnostic with { SourceMatched = true } }, pending: true));
        Assert.False(Accept(busy with { Diagnostic = busy.Diagnostic with { CompletedUtc = Observed } }, pending: true));
    }
}
