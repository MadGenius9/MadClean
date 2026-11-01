using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ShortcutRecheckTests
{
    private const string Root = @"C:\Selected";
    private static ShortcutObservation Entry(string path = @"C:\Selected\Example.lnk", ShortcutTargetState state = ShortcutTargetState.LocalTargetMissing) =>
        new(path, @"C:\Apps\Example.exe", state, "Historical metadata only; target could move.", ShortcutTargetClassifier.Source,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static CheckResult<ShortcutReport> Snapshot(ShortcutObservation entry, CheckState state = CheckState.Available) =>
        new(state, "PRIVATE provider output", [new([entry], [], 1, 0, false, "Bounded local metadata coverage")]);

    [Theory]
    [InlineData(ShortcutTargetState.LocalTargetMissing)]
    [InlineData(ShortcutTargetState.TargetExists)]
    [InlineData(ShortcutTargetState.Unknown)]
    [InlineData(ShortcutTargetState.Unsupported)]
    [InlineData(ShortcutTargetState.UnresolvedMetadata)]
    public void BindsExactHistoricalSelectionWithoutTurningItIntoAnAction(ShortcutTargetState state)
    {
        var entry = Entry(state: state);
        var result = ShortcutRecheckScope.Create(Root, Snapshot(entry, CheckState.Partial), entry);
        var scope = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(CheckState.Partial, scope.SourceCoverageState);
        Assert.Equal(Root, scope.RootPath);
        Assert.Equal(entry.ShortcutPath, scope.ShortcutPath);
        Assert.Equal(entry, scope.PriorObservation);
        Assert.NotSame(entry, scope.PriorObservation);
        Assert.True(scope.ValidatedUtc >= entry.CheckedAt);
        Assert.Contains("does not establish file identity continuity", result.Message);
        Assert.DoesNotContain("PRIVATE", result.Message);
    }
    [Fact]
    public void RejectsEqualButUnselectedCopiesAndDuplicateReferenceMembership()
    {
        var entry = Entry(); var snapshot = Snapshot(entry);
        Assert.Empty(ShortcutRecheckScope.Create(Root, snapshot, entry with { }).Items);
        var duplicate = snapshot with { Items = [snapshot.Items[0] with { Entries = [entry, entry], VisitedFiles = 2 }] };
        Assert.Empty(ShortcutRecheckScope.Create(Root, duplicate, entry).Items);
    }
    [Theory]
    [InlineData(CheckState.Unknown)][InlineData(CheckState.Unavailable)][InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Cancelled)][InlineData(CheckState.Failed)]
    public void UnsuccessfulSourceNeverScopesARead(CheckState state)
    {
        var entry = Entry(); var result = ShortcutRecheckScope.Create(Root, Snapshot(entry, state), entry);
        Assert.Equal(CheckState.Unavailable, result.State); Assert.Empty(result.Items);
    }
    [Theory]
    [InlineData("C:\\")][InlineData("relative")][InlineData(@"\\server\share")][InlineData(@"\\?\C:\Selected")]
    [InlineData(@"C:\Selected\..\Other")][InlineData("C:\\Sel\u202eected")]
    public void RejectsUnsupportedOrUnsafeRootsWithoutIO(string root)
    {
        var entry = Entry(); Assert.Empty(ShortcutRecheckScope.Create(root, Snapshot(entry), entry).Items);
    }
    [Theory]
    [InlineData(@"C:\Selected-other\Example.lnk")][InlineData(@"C:\Selected.lnk")]
    [InlineData(@"C:\Selected\Example.txt")][InlineData(@"C:\Selected\Example.lnk.")]
    [InlineData(@"C:\Selected\..\Example.lnk")]
    [InlineData("C:\\Selected\\bad\u202e.lnk")]
    public void SelectedLinkMustBeASafeStrictDescendant(string path)
    {
        var entry = Entry(path); Assert.Empty(ShortcutRecheckScope.Create(Root, Snapshot(entry), entry).Items);
    }
    [Fact]
    public void NormalizesLocalSeparatorsAndCaseWithoutResolvingAnything()
    {
        var entry = Entry("c:/selected/Sub/Example.LNK");
        var scope = Assert.Single(ShortcutRecheckScope.Create("c:/Selected/", Snapshot(entry), entry).Items);
        Assert.Equal(@"C:\Selected", scope.RootPath);
        Assert.Equal(@"C:\selected\Sub\Example.LNK", scope.ShortcutPath);
    }
    [Fact]
    public void RequiresGenuineSourceValidEnumAndPastUtcObservation()
    {
        foreach (var entry in new[]
        {
            Entry() with { Source = "OTHER" }, Entry() with { State = (ShortcutTargetState)999 },
            Entry() with { CheckedAt = default }, Entry() with { CheckedAt = DateTimeOffset.UtcNow.AddDays(1) },
            Entry() with { CheckedAt = DateTimeOffset.UtcNow.AddDays(-1).ToOffset(TimeSpan.FromHours(1)) }
        }) Assert.Empty(ShortcutRecheckScope.Create(Root, Snapshot(entry), entry).Items);
    }
    [Fact]
    public void BoundedCoverageRejectsInvalidShapeButPreservesLegitimateOverlappingSkipCounts()
    {
        var entry = Entry(); var snapshot = Snapshot(entry);
        var valid = snapshot.Items[0];
        foreach (var report in new[]
        {
            valid with { Entries = null! }, valid with { Issues = null! }, valid with { Coverage = "" },
            valid with { Entries = Enumerable.Repeat(entry, 1001).ToArray(), VisitedFiles = 1001 },
            valid with { Issues = Enumerable.Repeat(new FileReviewIssue(Root, "Issue"), 101).ToArray() },
            valid with { VisitedFiles = 10001 }, valid with { VisitedFiles = 0 }, valid with { SkippedFiles = -1 },
            valid with { SkippedFiles = 2 }, valid with { Truncated = true },
            valid with { Issues = [new(Root, "Coverage issue")] }
        }) Assert.Empty(ShortcutRecheckScope.Create(Root, snapshot with { Items = [report] }, entry).Items);
        Assert.Single(ShortcutRecheckScope.Create(Root, snapshot with { Items = [valid with { SkippedFiles = 1 }] }, entry).Items);
        Assert.Single(ShortcutRecheckScope.Create(Root, snapshot with { State = CheckState.Partial, Items = [valid with { Truncated = true, Issues = [new(Root, "Issue")] }] }, entry).Items);
    }
    [Fact]
    public void CancellationHasPriorityOverMalformedSelectionAndNeverProducesScope()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var result = ShortcutRecheckScope.Create("bad", null, null, stop.Token);
        Assert.Equal(CheckState.Cancelled, result.State); Assert.Empty(result.Items);
    }
    [Theory]
    [InlineData("C:\\Apps\\bad\u202e.exe")][InlineData("C:\\Apps\\bad\u2028.exe")]
    public void MalformedUnicodeTargetsNeverReachProbe(string target)
    {
        int probes = 0;
        var observation = ShortcutTargetClassifier.Classify(@"C:\Selected\Example.lnk", new(target),
            _ => { probes++; throw new InvalidOperationException("No target probe permitted"); }, DateTimeOffset.UtcNow);
        Assert.Equal(0, probes);
        Assert.Equal(ShortcutTargetState.UnresolvedMetadata, observation.State);
        Assert.Null(ShortcutPathPolicy.NormalizeLocalPath(target));
    }
    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDFFF)]
    public void RuntimeMalformedUtf16PathsRejectScopeAndNeverProbe(int surrogate)
    {
        // xUnit serializes string theory arguments and replaces malformed UTF-16.
        // Construct the actual unpaired code unit after deserialization instead.
        string invalid = new((char)surrogate, 1);
        var validEntry = Entry();
        Assert.Empty(ShortcutRecheckScope.Create("C:\\Selected" + invalid, Snapshot(validEntry), validEntry).Items);
        var badEntry = Entry("C:\\Selected\\bad" + invalid + ".lnk");
        Assert.Empty(ShortcutRecheckScope.Create(Root, Snapshot(badEntry), badEntry).Items);
        MalformedUnicodeTargetsNeverReachProbe("C:\\Apps\\bad" + invalid + ".exe");
    }
    [Fact]
    public void ValidSupplementaryUnicodePathsRemainSupported()
    {
        foreach (string name in new[] { "\U0001F4BB", "\uFFFD" })
        {
            var entry = Entry("C:\\Selected\\" + name + ".lnk");
            Assert.Single(ShortcutRecheckScope.Create(Root, Snapshot(entry), entry).Items);
        }
    }
}
