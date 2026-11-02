using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemToolJournalReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);
    private static SystemToolJournalReview Journal(bool terminal, Guid? identity = null)
    {
        var first = new SystemToolJournalEntry(identity ?? Guid.NewGuid(), SystemToolOperation.DismCheckHealth, 1,
            Now.AddSeconds(-2), SystemToolJournalPhase.DispatchIntent, null, null, null, null);
        var denied = first with { Sequence = 2, TimestampUtc = Now.AddSeconds(-1), Phase = SystemToolJournalPhase.LaunchDenied, NativeError = 1223 };
        return SystemToolJournal.Review(terminal ? [first, denied] : [first], Now);
    }
    [Fact]
    public void EmptyCompleteStoreIsAnObservationNeverPermissionOrGlobalAbsence()
    {
        var result = SystemToolJournalReconciliation.Review([], true, Now);
        Assert.Equal(SystemToolJournalHistoryState.NoRecordsObserved, result.State);
        Assert.True(result.EnumerationComplete); Assert.Empty(result.Journals);
        Assert.Contains("not global absence", result.Message); Assert.Contains("permission", result.Message);
    }
    [Fact]
    public void ARecordedTerminalDoesNotHideAnotherUnresolvedIntent()
    {
        var result = SystemToolJournalReconciliation.Review([Journal(true), Journal(false)], true, Now);
        Assert.Equal(SystemToolJournalHistoryState.UnresolvedPresent, result.State); Assert.Equal(2, result.Journals.Count);
        Assert.Contains("do not prove a process is running", result.Message);
    }
    [Fact]
    public void TerminalLookingCallerStateCannotOverrideStrictRecordReview()
    {
        var intent = Journal(false);
        var forged = intent with { State = SystemToolJournalState.RecordedClientExit, Message = "private-sentinel" };
        var result = SystemToolJournalReconciliation.Review([Journal(true), forged], true, Now);
        Assert.Equal(SystemToolJournalHistoryState.UnknownEvidence, result.State);
        Assert.Empty(result.Journals); Assert.DoesNotContain("private-sentinel", result.Message);
    }
    [Fact]
    public void IncompleteDuplicateMalformedAndOverBoundHistoriesRemainUnknown()
    {
        var same = Journal(true);
        foreach (var records in new IReadOnlyList<SystemToolJournalReview>[]
        { [same, same], [SystemToolJournal.Review([], Now)], Enumerable.Range(0, 129).Select(_ => Journal(true)).ToArray() })
            Assert.Equal(SystemToolJournalHistoryState.UnknownEvidence, SystemToolJournalReconciliation.Review(records, true, Now).State);
        Assert.Equal(SystemToolJournalHistoryState.UnknownEvidence, SystemToolJournalReconciliation.Review([same], false, Now).State);
        Assert.Equal(SystemToolJournalHistoryState.UnknownEvidence, SystemToolJournalReconciliation.Review(null, true, Now).State);
        Assert.Equal(SystemToolJournalHistoryState.UnknownEvidence, SystemToolJournalReconciliation.Review([], true, default).State);
    }
    [Fact]
    public void ExactRetentionBudgetRetainsOnlyRevalidatedImmutableCopies()
    {
        var source = Enumerable.Range(0, 128).Select(_ => Journal(true)).ToArray();
        var result = SystemToolJournalReconciliation.Review(source, true, Now);
        Assert.Equal(SystemToolJournalHistoryState.RecordedTerminalOnly, result.State); Assert.Equal(128, result.Journals.Count);
        source[0] = Journal(false);
        Assert.All(result.Journals, row => Assert.Equal(SystemToolJournalState.RecordedLaunchDenied, row.State));
        var list = Assert.IsAssignableFrom<IList<SystemToolJournalReview>>(result.Journals);
        Assert.Throws<NotSupportedException>(() => list.Add(Journal(false)));
        Assert.Contains("removes one historical block only", result.Message);
    }
}
