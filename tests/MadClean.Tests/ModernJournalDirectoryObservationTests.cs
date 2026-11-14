using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

public sealed class ModernJournalDirectoryObservationTests
{
    private static readonly Guid Id = Guid.Parse("c9d858ad-aaee-407d-9110-c1921982b859");
    private static readonly string Journal = Id.ToString("N") + ".jsonl";
    private static ModernJournalDirectoryName File(string name) => new(name, 0x80);
    private static ModernJournalDirectoryName Dot(string name) => new(name, 0x10);
    private static ModernJournalDirectoryObservation New() => new(Id);
    private static void Continue(ModernJournalDirectoryObservation observation, params ModernJournalDirectoryName[] entries)
        => Assert.Equal(ModernJournalDirectoryDecision.Continue, observation.Observe(ModernJournalDirectoryQuery.Batch, entries));
    private static void Refused(ModernJournalDirectoryObservation observation, ModernJournalDirectoryQuery outcome,
        IReadOnlyList<ModernJournalDirectoryName>? entries)
    {
        Assert.Equal(ModernJournalDirectoryDecision.Refused, observation.Observe(outcome, entries));
        Assert.Equal(ModernJournalDirectoryDecision.Refused, observation.Observe(ModernJournalDirectoryQuery.Batch, [File("lease.bin"), File(Journal)]));
        Assert.Equal(ModernJournalDirectoryDecision.Refused, observation.Observe(ModernJournalDirectoryQuery.End, null));
    }

    [Fact]
    public void EmptyExecutionCannotDefineAScope() => Assert.Throws<ArgumentException>(() => new ModernJournalDirectoryObservation(Guid.Empty));

    [Fact]
    public void TwoFilesAcrossBatchesRequireAnActualLaterEofAndCompleteOnlyOnce()
    {
        var observation = New();
        Continue(observation, File(Journal));
        Continue(observation, File("lease.bin"));
        Assert.Equal(ModernJournalDirectoryDecision.Complete, observation.Observe(ModernJournalDirectoryQuery.End, null));
        Refused(observation, ModernJournalDirectoryQuery.End, null);
    }

    [Fact]
    public void OptionalDotsMayAppearOnceInAnyBatchOrderAndFourRecordsNeedOnlyFiveCalls()
    {
        var observation = New();
        Continue(observation, Dot(".."));
        Continue(observation, File(Journal));
        Continue(observation, Dot("."));
        Continue(observation, File("lease.bin"));
        Assert.Equal(ModernJournalDirectoryDecision.Complete, observation.Observe(ModernJournalDirectoryQuery.End, null));
    }

    [Fact]
    public void EofWithoutBothFilesOrWithAnyEntriesNeverEstablishesCompletion()
    {
        Refused(New(), ModernJournalDirectoryQuery.End, null);
        var observation = New();
        Continue(observation, File("lease.bin"), Dot("."));
        Refused(observation, ModernJournalDirectoryQuery.End, null);
        foreach (var supplied in new IReadOnlyList<ModernJournalDirectoryName>[] { [], [File("extra")] })
        {
            observation = New();
            Continue(observation, File("lease.bin"), File(Journal));
            Refused(observation, ModernJournalDirectoryQuery.End, supplied);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(99)]
    public void LateFailureNeverPromotesEarlierExpectedNamesEvenWithPlausibleFailureOutput(int outcome)
    {
        foreach (var data in new IReadOnlyList<ModernJournalDirectoryName>?[] { null, [], [File("lease.bin"), File(Journal)] })
        {
            var observation = New();
            Continue(observation, File("lease.bin"), File(Journal));
            Refused(observation, (ModernJournalDirectoryQuery)outcome, data);
        }
    }

    [Fact]
    public void NullEmptyOversizeOrNullElementBatchesFailClosed()
    {
        foreach (var data in new IReadOnlyList<ModernJournalDirectoryName>?[]
        {
            null, [], [null!], [File("lease.bin"), null!],
            Enumerable.Repeat(File("lease.bin"), ModernJournalDirectoryBatch.MaxRecords + 1).ToArray()
        }) Refused(New(), ModernJournalDirectoryQuery.Batch, data);
    }

    [Theory]
    [InlineData("Lease.bin")]
    [InlineData("lease.bin ")]
    [InlineData("LEASE.BIN")]
    [InlineData("other.jsonl")]
    [InlineData("")]
    [InlineData("../lease.bin")]
    public void ExactOrdinalNamespaceNeverAdoptsSimilarOrUnexpectedNames(string name)
        => Refused(New(), ModernJournalDirectoryQuery.Batch, [File("lease.bin"), File(name)]);

    [Fact]
    public void UppercaseOrForeignGuidIsNotTheExpectedJournal()
    {
        Refused(New(), ModernJournalDirectoryQuery.Batch, [File(Journal.ToUpperInvariant())]);
        Refused(New(), ModernJournalDirectoryQuery.Batch, [File("552606746db945fa9e45189d80b80b25.jsonl")]);
        Refused(New(), ModernJournalDirectoryQuery.Batch, [new(null!, 0x80)]);
    }

    [Fact]
    public void DuplicatesWithinOrAcrossBatchesRefuseEvenIfBothFilesWereAlreadyObserved()
    {
        foreach (var duplicate in new[] { File("lease.bin"), File(Journal), Dot("."), Dot("..") })
        {
            Refused(New(), ModernJournalDirectoryQuery.Batch, [duplicate, duplicate]);
            var observation = New();
            Continue(observation, File("lease.bin"), File(Journal), Dot("."), Dot(".."));
            Refused(observation, ModernJournalDirectoryQuery.Batch, [duplicate]);
        }
    }

    [Fact]
    public void DirectoryAndReparseKindsCannotSubstituteForRequiredFilesOrDots()
    {
        foreach (string name in new[] { "lease.bin", Journal, ".", ".." })
        {
            foreach (uint attributes in new[] { 0x400u, 0x410u })
                Refused(New(), ModernJournalDirectoryQuery.Batch, [new(name, attributes)]);
            uint wrongKind = name is "." or ".." ? 0x80u : 0x10u;
            Refused(New(), ModernJournalDirectoryQuery.Batch, [new(name, wrongKind)]);
        }
    }

    [Fact]
    public void CallerMutationAfterACompletedBatchCannotRewriteTheObservedNames()
    {
        var observation = New();
        ModernJournalDirectoryName[] first = [File("lease.bin")];
        Continue(observation, first);
        first[0] = File("foreign");
        Continue(observation, File(Journal));
        Assert.Equal(ModernJournalDirectoryDecision.Complete, observation.Observe(ModernJournalDirectoryQuery.End, null));
    }

    [Fact]
    public void AnInvalidLaterRecordCannotBeRepairedByReplacingTheCallerArray()
    {
        var observation = New();
        ModernJournalDirectoryName[] batch = [File("lease.bin"), File("foreign")];
        Assert.Equal(ModernJournalDirectoryDecision.Refused, observation.Observe(ModernJournalDirectoryQuery.Batch, batch));
        batch[1] = File(Journal);
        Refused(observation, ModernJournalDirectoryQuery.Batch, batch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThrowingBatchAccessCannotReuseEarlierExpectedNames(bool throwFromCount)
    {
        var observation = New();
        Continue(observation, File("lease.bin"), File(Journal));
        Assert.Throws<InvalidOperationException>(() =>
            observation.Observe(ModernJournalDirectoryQuery.Batch, new ThrowingList(throwFromCount)));
        Refused(observation, ModernJournalDirectoryQuery.End, null);
    }

    private sealed class ThrowingList(bool throwFromCount) : IReadOnlyList<ModernJournalDirectoryName>
    {
        public int Count => throwFromCount ? throw new InvalidOperationException("Controlled count failure.") : 1;
        public ModernJournalDirectoryName this[int index] => throw new InvalidOperationException("Controlled index failure.");
        public IEnumerator<ModernJournalDirectoryName> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new NotSupportedException();
    }
}
