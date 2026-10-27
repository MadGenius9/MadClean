using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DiskIdentityAccountingTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "madclean-identity-test-scope"));
    private static string P(string name) => Path.Combine(Root, name);
    private static string Id(int id, int volume = 1) => $"{volume:X16}:{id:X32}";
    private static DiskUsageNode FileNode(string name, long bytes) => new(name, P(name), bytes, [], false);
    private static DiskUsageReport Map(params DiskUsageNode[] files) => new(new("scope", Root, 0, files, true), [], 0, false);
    private static DiskIdentityAccountingReport Report(CheckResult<DiskIdentityAccountingReport> result) => Assert.Single(result.Items);

    [Fact]
    public void HardlinksCountObservedIdentityOnceButIndependentEqualLengthsRemainDistinct()
    {
        var probe = new TestOnlyProbe(path => new(Id(path == P("copy") ? 2 : 1), 10, path == P("copy") ? 1u : 3u, true));
        var result = new DiskIdentityAccountingEngine(probe).Analyze(Map(FileNode("a", 10), FileNode("alias", 10), FileNode("copy", 10)), default);
        var report = Report(result);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(30, report.PathLogicalBytes);
        Assert.Equal(30, report.ObservedPathLogicalBytes);
        Assert.Equal(20, report.ObservedUniqueFileLogicalBytes);
        Assert.Equal(2, report.Observations.Single(x => x.Path == P("a")).ObservedAliasCount);
        Assert.Equal(3u, report.Observations.Single(x => x.Path == P("a")).LinkCount);
        Assert.Contains("outside", report.Coverage);
    }

    [Fact]
    public void SameFileIdOnDifferentVolumesIsNotAnAlias()
    {
        var result = new DiskIdentityAccountingEngine(new TestOnlyProbe(path => new(Id(1, path == P("a") ? 1 : 2), 7, 1, true)))
            .Analyze(Map(FileNode("a", 7), FileNode("b", 7)), default);
        Assert.Equal(14, Report(result).ObservedUniqueFileLogicalBytes);
    }

    [Fact]
    public void InconsistentAliasLengthsInvalidateEntireGroupIncludingFirstObservation()
    {
        var result = new DiskIdentityAccountingEngine(new TestOnlyProbe(path => new(Id(1), path == P("a") ? 10 : 11, 2, true)))
            .Analyze(Map(FileNode("a", 10), FileNode("b", 11)), default);
        var report = Report(result);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Empty(report.Observations);
        Assert.Equal(21, report.PathLogicalBytes);
        Assert.Equal(0, report.ObservedUniqueFileLogicalBytes);
    }

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(2u, 3u)]
    public void ImpossibleOrChangingLinkCountsAreNotTrusted(uint aLinks, uint bLinks)
    {
        var result = new DiskIdentityAccountingEngine(new TestOnlyProbe(path => new(Id(1), 10, path == P("a") ? aLinks : bLinks, true)))
            .Analyze(Map(FileNode("a", 10), FileNode("b", 10)), default);
        Assert.Empty(Report(result).Observations);
        Assert.Equal(CheckState.Partial, result.State);
    }

    [Fact]
    public void ChangedAndInaccessiblePathsRetainSnapshotBytesButCannotLookLikeZeroSizeKnownFiles()
    {
        var result = new DiskIdentityAccountingEngine(new TestOnlyProbe(path => path == P("locked")
            ? throw new UnauthorizedAccessException("private provider text") : new(Id(1), 11, 1, true)))
            .Analyze(Map(FileNode("changed", 10), FileNode("locked", 20)), default);
        var report = Report(result);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(30, report.PathLogicalBytes);
        Assert.Equal(0, report.ObservedPathLogicalBytes);
        Assert.Equal(2, report.Issues.Count);
        Assert.DoesNotContain("private provider text", string.Join(" ", report.Issues));
    }

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(true, 0u)]
    public void UnstableOrMissingLinkMetadataCannotContribute(bool stable, uint links)
    {
        var result = new DiskIdentityAccountingEngine(new TestOnlyProbe(_ => new(Id(1), 4, links, stable)))
            .Analyze(Map(FileNode("a", 4)), default);
        Assert.Empty(Report(result).Observations);
        Assert.Equal(CheckState.Partial, result.State);
    }

    [Fact]
    public void MalformedIdentityCannotReachDisplayAndRealEmptyFilesAreStillKnown()
    {
        var result = new DiskIdentityAccountingEngine(new TestOnlyProbe(path => new(path == P("bad") ? "secret/path" : Id(1), 0, 1, true)))
            .Analyze(Map(FileNode("empty", 0), FileNode("bad", 0)), default);
        Assert.Equal(P("empty"), Assert.Single(Report(result).Observations).Path);
        Assert.Equal(CheckState.Partial, result.State);
    }

    [Fact]
    public void OverflowRejectsEntireSnapshotBeforeNativeQueries()
    {
        var probe = new TestOnlyProbe(_ => throw new Exception("must not query"));
        var result = new DiskIdentityAccountingEngine(probe).Analyze(Map(FileNode("a", long.MaxValue), FileNode("b", 1)), default);
        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void DuplicatePathsAndNonchildPathsRejectBeforeAnyQuery()
    {
        var probe = new TestOnlyProbe(_ => throw new Exception("must not query"));
        var engine = new DiskIdentityAccountingEngine(probe);
        Assert.Empty(engine.Analyze(Map(FileNode("a", 1), FileNode("a", 1)), default).Items);
        Assert.Empty(engine.Analyze(Map(new DiskUsageNode("outside", Path.GetFullPath(Path.Combine(Root, "..", "outside")), 1, [], false)), default).Items);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void MissingCoverageMetadataAndNegativeSkippedCountsRejectBeforeAnyProbe()
    {
        var probe = new TestOnlyProbe(_ => throw new Exception("must not query"));
        var engine = new DiskIdentityAccountingEngine(probe);
        var valid = Map(FileNode("a", 1));
        foreach (var malformed in new[] { valid with { Issues = null! }, valid with { SkippedEntries = -1 } })
        {
            var result = engine.Analyze(malformed, default);
            Assert.Equal(CheckState.Unknown, result.State);
            Assert.Empty(result.Items);
        }
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void FileAndIssueBoundsPreventUnboundedProbeWork()
    {
        var probe = new TestOnlyProbe(_ => throw new IOException());
        var engine = new DiskIdentityAccountingEngine(probe);
        var excessive = Map(Enumerable.Range(0, DiskIdentityAccountingEngine.MaxFiles + 1).Select(i => FileNode(i.ToString(), 1)).ToArray());
        Assert.Empty(engine.Analyze(excessive, default).Items);
        Assert.Equal(0, probe.Calls);
        var bounded = Map(Enumerable.Range(0, 300).Select(i => FileNode(i.ToString(), 1)).ToArray());
        var report = Report(engine.Analyze(bounded, default));
        Assert.True(report.Truncated);
        Assert.Equal(DiskIdentityAccountingEngine.MaxIssues, probe.Calls);
        Assert.Equal(300, report.PathLogicalBytes);
    }

    [Fact]
    public void PartialOriginalMapCannotBecomeCompleteAfterSuccessfulIdentityReads()
    {
        var map = Map(FileNode("a", 5)) with { SkippedEntries = 1 };
        var result = new DiskIdentityAccountingEngine(new TestOnlyProbe(_ => new(Id(1), 5, 1, true))).Analyze(map, default);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(5, Report(result).ObservedUniqueFileLogicalBytes);
    }

    [Fact]
    public void CancellationDuringProbeStopsWithoutPublishingPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new TestOnlyProbe(_ => { cancellation.Cancel(); return new(Id(1), 5, 2, true); });
        var engine = new DiskIdentityAccountingEngine(probe);
        Assert.Throws<OperationCanceledException>(() => engine.Analyze(Map(FileNode("a", 5), FileNode("b", 5)), cancellation.Token));
        Assert.Equal(1, probe.Calls);
    }

    // Synthetic identity metadata only; these adapters do not validate Windows identity/marshalling.
    private sealed class TestOnlyProbe(Func<string, DiskIdentityAccountingStamp> read) : IDiskIdentityAccountingProbe
    {
        public int Calls { get; private set; }
        public DiskIdentityAccountingStamp Probe(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return read(path); }
    }
}
