using MadClean.Core;
using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

public sealed class ModernJournalSyntheticSeedTests
{
    private static readonly Guid ExecutionId = Guid.Parse("9afb69ad-b364-48ce-85b5-c3ed65ea6871");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static SystemToolJournalEntry Seed() => new(ExecutionId, SystemToolOperation.DismCheckHealth, 1,
        Now.AddMinutes(-1), SystemToolJournalPhase.DispatchIntent, null, null, null, null);
    private static SystemToolJournalReview Claimed(SystemToolJournalEntry entry) =>
        new(SystemToolJournalState.Unresolved, Array.AsReadOnly(new[] { entry }), "A caller-supplied message grants no authority.");

    [Fact]
    public void CanonicalSameRequestSeedSurvivesFullSnapshotDecodeWithoutBecomingTerminal()
    {
        var seed = Seed();
        Assert.True(SystemToolJournal.TryEncode([seed], Now, out var encoded));
        byte[] bytes = [.. encoded!, (byte)'\n'];
        var actual = SystemToolJournal.DecodeSnapshots(bytes, Now);
        Assert.True(ModernJournalSyntheticSeed.IsExpected(actual, ExecutionId, Now));
        Assert.Equal(SystemToolJournalState.Unresolved, actual.State);
        Assert.Equal(seed, Assert.Single(actual.Entries));
        Assert.Null(actual.Entries[0].ProcessId);
        Assert.Null(actual.Entries[0].ExitCode);
    }

    [Theory]
    [InlineData(SystemToolOperation.DismScanHealth)]
    [InlineData(SystemToolOperation.DismRestoreHealth)]
    [InlineData(SystemToolOperation.SfcVerifyOnly)]
    [InlineData(SystemToolOperation.SfcScanNow)]
    [InlineData((SystemToolOperation)987)]
    public void AnUnresolvedOtherOperationCannotBeSubstitutedForTheFixedSeed(SystemToolOperation operation)
        => Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed() with { Operation = operation }), ExecutionId, Now));

    [Theory]
    [InlineData(SystemToolJournalPhase.ClientObserved)]
    [InlineData(SystemToolJournalPhase.ClientExited)]
    [InlineData(SystemToolJournalPhase.ObservationLost)]
    [InlineData(SystemToolJournalPhase.LaunchDenied)]
    [InlineData((SystemToolJournalPhase)987)]
    public void CallerClaimOfUnresolvedCannotHideAnotherPhase(SystemToolJournalPhase phase)
        => Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed() with { Phase = phase }), ExecutionId, Now));

    [Fact]
    public void ClientOrResultFactsNeverQualifyEvenWhenZeroOrCallerClaimsUnresolved()
    {
        foreach (var altered in new[]
        {
            Seed() with { ProcessId = 0 }, Seed() with { ProcessId = 37 },
            Seed() with { ClientStartUtc = Now.AddMinutes(-2) },
            Seed() with { ExitCode = 0 }, Seed() with { NativeError = 0 }
        })
            Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(altered), ExecutionId, Now));
    }

    [Fact]
    public void WrongOrEmptyExecutionAndNoninitialSequenceDoNotMatch()
    {
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed()), Guid.Empty, Now));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed()), Guid.Parse("404b068e-12c4-46fa-b0b2-97b2fb30f631"), Now));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed() with { ExecutionId = Guid.Empty }), ExecutionId, Now));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed() with { Sequence = 2 }), ExecutionId, Now));
    }

    [Fact]
    public void TimeMustBeObservedUtcAndCannotComeFromTheFuture()
    {
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed()), ExecutionId, default));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed()), ExecutionId, Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed() with { TimestampUtc = default }), ExecutionId, Now));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed() with { TimestampUtc = Now.AddTicks(1) }), ExecutionId, Now));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed() with { TimestampUtc = Now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(-6)) }), ExecutionId, Now));
        // No invented expiry or fresh-file provenance follows from an old but valid seed.
        Assert.True(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed() with { TimestampUtc = Now.AddYears(-1) }), ExecutionId, Now));
    }

    [Fact]
    public void MissingUnknownOrTerminalReviewIsNotSeedEvidence()
    {
        Assert.False(ModernJournalSyntheticSeed.IsExpected(null, ExecutionId, Now));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(new(SystemToolJournalState.Unresolved, [], ""), ExecutionId, Now));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(new(SystemToolJournalState.Unresolved, null!, ""), ExecutionId, Now));
        Assert.False(ModernJournalSyntheticSeed.IsExpected(new(SystemToolJournalState.Unresolved, [null!], ""), ExecutionId, Now));
        foreach (var state in new[] { SystemToolJournalState.Unknown, SystemToolJournalState.RecordedClientExit, SystemToolJournalState.RecordedLaunchDenied })
            Assert.False(ModernJournalSyntheticSeed.IsExpected(Claimed(Seed()) with { State = state }, ExecutionId, Now));
    }

    [Fact]
    public void AValidUnresolvedHistoryWithLaterObservationCannotBeReducedToItsSeed()
    {
        var seed = Seed();
        var observed = seed with { Sequence = 2, Phase = SystemToolJournalPhase.ClientObserved,
            TimestampUtc = seed.TimestampUtc.AddSeconds(1), ProcessId = 37 };
        var review = SystemToolJournal.Review([seed, observed], Now);
        Assert.Equal(SystemToolJournalState.Unresolved, review.State);
        Assert.False(ModernJournalSyntheticSeed.IsExpected(review, ExecutionId, Now));
    }

    [Fact]
    public void MalformedOrTruncatedWholeSnapshotCannotBeRepairedByAValidSeedPrefix()
    {
        Assert.True(SystemToolJournal.TryEncode([Seed()], Now, out var encoded));
        byte[] noTerminator = encoded!;
        byte[] malformedTail = [.. encoded!, (byte)'\n', (byte)'{'];
        foreach (byte[] bytes in new[] { noTerminator, malformedTail })
        {
            var review = SystemToolJournal.DecodeSnapshots(bytes, Now);
            Assert.Equal(SystemToolJournalState.Unknown, review.State);
            Assert.False(ModernJournalSyntheticSeed.IsExpected(review, ExecutionId, Now));
        }
    }
}
