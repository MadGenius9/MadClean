using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class CleanupReviewSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static CleanupCandidate File(int index) => new("User temporary files", $@"C:\Temp\old{index}.tmp", 17 + index, Now.AddDays(-10).UtcDateTime);
    private static CleanupReport Report(params CleanupCandidate[] candidates) => new(candidates, [], 0, false);
    private static CheckResult<CleanupReviewSelection> Select(CleanupReport report, IReadOnlyList<CleanupCandidate> selected,
        IReadOnlyList<CleanupExclusion>? exclusions = null, DateTimeOffset? completed = null) =>
        CleanupReviewSelection.Create(report, selected, completed ?? Now.AddMinutes(-1), exclusions ?? [], Now);

    [Fact]
    public void ExplicitSelectionKeepsOriginalReferencesAndFreezesOnlyItsChoiceAndPreferenceLists()
    {
        var first = File(0); var second = File(1); var source = Report(first, second);
        var choices = new[] { first }; var preferences = new List<CleanupExclusion> { new(@"C:\Other\", true) };
        var selection = Assert.Single(Select(source, choices, preferences).Items);
        choices[0] = second; preferences.Clear();
        Assert.Same(source, selection.SourceSnapshot); Assert.Same(first, Assert.Single(selection.Selections));
        Assert.Equal(@"C:\Other", Assert.Single(selection.CapturedExclusions).Path);
        Assert.Throws<NotSupportedException>(() => ((IList<CleanupCandidate>)selection.Selections).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CleanupExclusion>)selection.CapturedExclusions).Clear());
        Assert.False(selection.CanDelete); Assert.True(selection.IsFresh(Now));
        Assert.Single(selection.Prepare(first, Now).Items);
        Assert.Empty(selection.Prepare(second, Now).Items); // Actual source member, but never explicitly chosen.
    }

    [Fact]
    public void ClonesForeignSnapshotChoicesDuplicateReferencesAndAmbiguousPathsAreNotChoices()
    {
        var first = File(0); var second = File(1); var source = Report(first, second);
        Assert.Empty(Select(source, [first with { }]).Items);
        var foreign = File(9); var foreignSource = Report(foreign);
        Assert.Single(foreignSource.Candidates);
        Assert.Empty(Select(source, [first, foreign]).Items);
        Assert.Empty(Select(source, [first, first]).Items);
        var samePath = first with { Path = first.Path.ToUpperInvariant() };
        Assert.Empty(Select(Report(first, samePath), [first]).Items);
        var selection = Assert.Single(Select(source, [first]).Items);
        Assert.Empty(selection.Prepare(first with { }, Now).Items);
    }

    [Fact]
    public void NoDefaultsAndEightIsAnActualSelectionBound()
    {
        var candidates = Enumerable.Range(0, 9).Select(File).ToArray(); var source = Report(candidates);
        Assert.Empty(Select(source, []).Items);
        Assert.Empty(Select(source, candidates).Items);
        var selection = Assert.Single(Select(source, candidates.Take(8).ToArray()).Items);
        Assert.Equal(8, selection.Selections.Count);
        Assert.Empty(selection.Prepare(candidates[8], Now).Items);
    }

    [Fact]
    public void SourceLifetimeAndActualMembershipAreRecheckedForEachPreparedRead()
    {
        var first = File(0); var candidates = new[] { first }; var source = Report(candidates);
        var selection = Assert.Single(Select(source, [first]).Items);
        Assert.False(selection.IsFresh(Now.AddMinutes(15)));
        Assert.False(selection.IsFresh(default)); Assert.False(selection.IsFresh(Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.Empty(selection.Prepare(first, Now.AddMinutes(15)).Items);
        candidates[0] = first with { }; // Same record value cannot preserve exact source membership.
        Assert.Empty(selection.Prepare(first, Now).Items);
        foreach (var completed in new[] { default(DateTimeOffset), Now.AddMinutes(1), Now.AddMinutes(-16) })
            Assert.Empty(Select(source, [candidates[0]], completed: completed).Items);
    }

    [Fact]
    public void ExclusionComparisonIsExactNormalizedSetAndNeverIgnoresUnsupportedPreferences()
    {
        var file = File(0); var source = Report(file);
        var selection = Assert.Single(Select(source, [file], [new(@"C:\Other\", true), new(@"D:\Keep.tmp", false)]).Items);
        Assert.True(selection.MatchesExclusions([new(@"d:\KEEP.tmp", false), new(@"c:\OTHER", true), new(@"D:\Keep.tmp", false)]));
        Assert.False(selection.MatchesExclusions([new(@"C:\Other", false), new(@"D:\Keep.tmp", false)]));
        Assert.False(selection.MatchesExclusions([])); Assert.False(selection.MatchesExclusions(null));
        Assert.False(selection.MatchesExclusions([new("relative", true)]));
        Assert.Empty(Select(source, [file], [new("relative", true)]).Items);
        Assert.Empty(Select(source, [file], [new(@"C:\Temp", true)]).Items);
    }

    [Fact]
    public void PartialCoverageStaysPartialAndMalformedSourceCannotBePrepared()
    {
        var file = File(0); var source = new CleanupReport([file], [new("Cleanup", "", "A location was inaccessible.")], 1, true);
        var result = Select(source, [file]); var selection = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Partial, selection.SourceCoverageState);
        Assert.Equal(CheckState.Partial, selection.Prepare(file, Now).State);
        Assert.Empty(Select(source with { Issues = null! }, [file]).Items);
        Assert.Empty(Select(source with { ExcludedCount = -1 }, [file]).Items);
    }

    [Fact]
    public void CancellationCannotCreateOrRefreshAWorksheet()
    {
        var file = File(0); var source = Report(file); var selection = Assert.Single(Select(source, [file]).Items);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => CleanupReviewSelection.Create(source, [file], Now, [], Now, stop.Token));
        Assert.Throws<OperationCanceledException>(() => selection.Prepare(file, Now, stop.Token));
        Assert.Throws<OperationCanceledException>(() => selection.MatchesExclusions([], stop.Token));
    }
}
