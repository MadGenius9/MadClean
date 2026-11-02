using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class CleanupFileEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static CleanupCandidate Candidate(string path = @"C:\Users\Example\AppData\Local\Temp\cache\old.tmp", string category = "User temporary files") =>
        new(category, path, 37, Now.AddDays(-10).UtcDateTime);
    private static CleanupReport Snapshot(params CleanupCandidate[] candidates) => new(candidates, [], 0, false);
    private static CheckResult<CleanupFileEvidenceScope> Select(CleanupReport report, CleanupCandidate candidate,
        IReadOnlyList<CleanupExclusion>? exclusions = null) => CleanupFileEvidenceScope.Create(report, candidate, Now.AddMinutes(-1), exclusions ?? [], Now);

    [Fact]
    public void GenuineSelectionCapturesReadonlyCopiesWithoutTurningHistoryIntoIdentityOrPermission()
    {
        var selected = Candidate();
        var exclusions = new List<CleanupExclusion> { new(@"C:\Other\Cache\", true) };
        var report = Snapshot(selected);
        var scope = Assert.Single(Select(report, selected, exclusions).Items);
        exclusions.Clear();
        Assert.NotSame(selected, scope.Candidate); Assert.Equal(selected, scope.Candidate);
        Assert.Equal(@"C:\Other\Cache", Assert.Single(scope.CapturedExclusions).Path);
        Assert.Throws<NotSupportedException>(() => ((IList<CleanupExclusion>)scope.CapturedExclusions).Clear());
        Assert.Equal(CleanupRootKind.UserTemporary, scope.RootKind);
        var evidence = new CleanupFileEvidenceReport(scope.Candidate, scope.RootKind, CleanupEvidenceSourceKind.WindowsTemporary,
            new(selected.Path, "0000000000000001:00000000000000000000000000000001", 37, 1, Now.AddDays(-20), Now.AddDays(-10), Now.AddDays(-10), 0),
            @"C:\Users\Example\AppData\Local\Temp", "0000000000000001:00000000000000000000000000000002", Now, Now,
            true, true, true, CheckState.Available, scope.SourceCoverageState, []);
        Assert.False(evidence.CanDelete); Assert.Equal("Unknown", evidence.ActiveUse);
    }

    [Fact]
    public void OtherWellFormedButUnsupportedRowsDoNotWidenOrBlockTheExactSelection()
    {
        var selected = Candidate();
        var longPath = Candidate(@"C:\Temp\" + new string('a', 300) + ".tmp");
        var unsupported = Candidate(@"C:\Other\old.tmp", "Additional inventory source");
        var report = Snapshot(selected, longPath, unsupported);
        var result = Select(report, selected);
        var scope = Assert.Single(result.Items);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(selected, scope.Candidate);
        Assert.Empty(Select(report, longPath).Items);
        Assert.Empty(Select(report, unsupported).Items);
    }

    [Fact]
    public void EqualCloneAndAmbiguousPathCannotSelectAnotherSnapshotObject()
    {
        var selected = Candidate();
        Assert.Empty(Select(Snapshot(selected), selected with { }).Items);
        Assert.Empty(Select(Snapshot(selected, selected with { Path = selected.Path.ToUpperInvariant() }), selected).Items);
        Assert.Empty(Select(Snapshot(selected, selected), selected).Items);
        Assert.Empty(Select(Snapshot(selected with { Bytes = 100 }), selected).Items);
    }

    [Theory]
    [InlineData(@"C:relative.tmp")]
    [InlineData(@"\\server\share\old.tmp")]
    [InlineData(@"\\?\C:\Temp\old.tmp")]
    [InlineData(@"C:\Temp\old.tmp:secret")]
    [InlineData(@"C:\Temp\..\old.tmp")]
    [InlineData(@"C:\Temp\NUL.tmp")]
    [InlineData(@"C:\Temp\old.tmp.")]
    [InlineData(@"C:\Temp\\old.tmp")]
    [InlineData("C:/Temp/old.tmp")]
    [InlineData("/tmp/old.tmp")]
    [InlineData("C:\\Temp\\old\u200b.tmp")]
    public void UnsupportedWindowsSyntaxNeverBecomesPortableArbitraryPathScope(string path)
    {
        var selected = Candidate(path);
        Assert.Empty(Select(Snapshot(selected), selected).Items);
    }

    [Fact]
    public void InvalidUnicodeAndExcessivePathsAreRejectedBeforeAnyNativeRead()
    {
        foreach (var path in new[] { "C:\\Temp\\broken\ud800.tmp", "C:\\Temp\\" + new string('a', 260) })
        {
            var selected = Candidate(path); Assert.Empty(Select(Snapshot(selected), selected).Items);
        }
    }

    [Theory]
    [InlineData("Test temporary folder")]
    [InlineData("user temporary files")]
    [InlineData("Windows temporary files ")]
    public void CallerInventedOrApproximateCategoriesDoNotAuthorizeARoot(string category)
    {
        var selected = Candidate(category: category); Assert.Empty(Select(Snapshot(selected), selected).Items);
    }

    [Fact]
    public void AvailableAndPartialSourceCoverageRemainSeparateFromCurrentExclusionChecks()
    {
        var selected = Candidate(category: "Windows temporary files");
        var complete = Assert.Single(Select(Snapshot(selected), selected).Items);
        Assert.Equal(CleanupRootKind.WindowsTemporary, complete.RootKind); Assert.Equal(CheckState.Available, complete.SourceCoverageState);
        foreach (var report in new[] { new CleanupReport([selected], [new("Missing", "", "Root unavailable.")], 1, false), new CleanupReport([selected], [], 0, true) })
        {
            var result = Select(report, selected); Assert.Equal(CheckState.Partial, result.State);
            Assert.Equal(CheckState.Partial, Assert.Single(result.Items).SourceCoverageState);
        }
    }

    [Fact]
    public void ExclusionsMatchWholePathBoundariesAndNeverSilentlyDisappear()
    {
        var selected = Candidate(@"C:\Temp\cache2\old.tmp");
        Assert.Single(Select(Snapshot(selected), selected, [new(@"C:\Temp\cache", true)]).Items);
        Assert.Empty(Select(Snapshot(selected), selected, [new(@"c:\temp\CACHE2", true)]).Items);
        Assert.Empty(Select(Snapshot(selected), selected, [new(selected.Path, false)]).Items);
        var scope = Assert.Single(Select(Snapshot(selected), selected, [new(@"C:\Temp\cache2", false)]).Items);
        Assert.False(scope.MatchesCapturedExclusions(selected.Path));
        Assert.True(scope.MatchesCapturedExclusions(@"C:\Temp\cache2"));
        Assert.Empty(Select(Snapshot(selected), selected, [new("relative", true)]).Items);
        Assert.Empty(CleanupFileEvidenceScope.Create(Snapshot(selected), selected, Now, null, Now).Items);
        Assert.Empty(Select(Snapshot(selected), selected, Enumerable.Repeat(new CleanupExclusion(@"C:\Other", true), 257).ToArray()).Items);
    }

    [Theory]
    [InlineData(@"C:\Temp\password-cache.tmp")]
    [InlineData(@"C:\Temp\private.key")]
    [InlineData(@"C:\Temp\Login Data")]
    public void SelectedProtectedNamesRemainBlockedEvenWhenFabricatedIntoInventory(string path)
    {
        var selected = Candidate(path); Assert.Empty(Select(Snapshot(selected), selected).Items);
    }

    [Fact]
    public void UnknownFutureExpiredAndRecentObservationsCannotSupplyFreshScopes()
    {
        var selected = Candidate(); var snapshot = Snapshot(selected);
        foreach (var completed in new[] { default(DateTimeOffset), Now.AddMinutes(1), Now.AddMinutes(-16), Now.ToOffset(TimeSpan.FromHours(1)) })
            Assert.Empty(CleanupFileEvidenceScope.Create(snapshot, selected, completed, [], Now).Items);
        Assert.Empty(CleanupFileEvidenceScope.Create(snapshot, selected, Now, [], default).Items);
        foreach (var modified in new[] { Now.AddDays(-6).UtcDateTime, Now.AddSeconds(1).UtcDateTime, DateTime.SpecifyKind(Now.AddDays(-10).DateTime, DateTimeKind.Unspecified) })
        {
            var bad = selected with { ModifiedUtc = modified }; Assert.Empty(Select(Snapshot(bad), bad).Items);
        }
        var boundary = selected with { ModifiedUtc = Now.AddMinutes(-1).AddDays(-7).UtcDateTime };
        Assert.Single(Select(Snapshot(boundary), boundary).Items);
    }

    [Fact]
    public void MalformedCoverageIsRejectedInsteadOfDiscardingErrors()
    {
        var selected = Candidate();
        foreach (var report in new[] { new CleanupReport([selected], null!, 0, false), new CleanupReport([selected], [], -1, false),
            new CleanupReport([selected], [new("Cleanup", "", "bad\nraw")], 0, false), new CleanupReport([selected with { Bytes = -1 }], [], 0, false) })
            Assert.Empty(Select(report, selected).Items);
        Assert.Empty(Select(new([selected], Enumerable.Repeat(new ScanIssue("Cleanup", "", "Issue"), 1001).ToArray(), 0, false), selected).Items);
    }

    [Fact]
    public void CancellationStopsSelectionAndSharedLexicalPolicyRetainsInventoryRules()
    {
        var selected = Candidate(); using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => CleanupFileEvidenceScope.Create(Snapshot(selected), selected, Now, [], Now, source.Token));
        Assert.True(CleanupSafetyPolicy.IsProtectedRelativePath(@"Saves\old.tmp"));
        Assert.True(CleanupSafetyPolicy.IsProtectedRelativePath(@"cache\session-data.tmp"));
        Assert.True(CleanupSafetyPolicy.IsProtectedRelativePath(@"cache\private.key"));
        Assert.False(CleanupSafetyPolicy.IsProtectedRelativePath(@"cache\old.tmp"));
    }
}
