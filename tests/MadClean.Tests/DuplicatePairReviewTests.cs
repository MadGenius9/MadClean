using System.Collections;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DuplicatePairReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MadCleanPairTests-" + Guid.NewGuid().ToString("N"));
    public DuplicatePairReviewTests() => Directory.CreateDirectory(root);
    private CheckResult<DuplicateReport> Snapshot(bool alias = false, bool otherGroup = false)
    {
        var paths = new List<string>();
        void Add(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); paths.Add(path); }
        Add("a", [1, 2, 3]); Add("b", [1, 2, 3]);
        if (alias) Add("alias", [1, 2, 3]);
        if (otherGroup) { Add("c", [8, 9]); Add("d", [8, 9]); }
        return new DuplicateDiscoveryEngine().Scan(root, new TestOnlyAccess(paths, alias), default);
    }
    private static DuplicateReport Report(CheckResult<DuplicateReport> source) => Assert.Single(source.Items);
    private static DuplicateEntry Entry(CheckResult<DuplicateReport> source, string name) => Report(source).Entries.Single(e => Path.GetFileName(e.Path) == name);
    private static DuplicatePairReview Review(CheckResult<DuplicateReport> source, DateTimeOffset? now = null) =>
        DuplicatePairReviewBuilder.Build(source, Report(source).Entries.FirstOrDefault(e => Path.GetFileName(e.Path) == "a") ?? Report(source).Entries[0],
            Report(source).Entries.FirstOrDefault(e => Path.GetFileName(e.Path) == "b") ?? Report(source).Entries[1], now ?? DateTimeOffset.UtcNow);
    private static CheckResult<DuplicateReport> Replace(CheckResult<DuplicateReport> source, Func<DuplicateEntry, DuplicateEntry> transform) =>
        source with { Items = [Report(source) with { Entries = Report(source).Entries.Select(transform).ToArray() }] };
    private static void Blocked(DuplicatePairReview result)
    {
        Assert.Equal(DuplicatePairReviewState.Blocked, result.State);
        Assert.Null(result.Candidate); Assert.Null(result.RetainedEntry);
        Assert.False(result.ExecutionSupported);
    }

    [Fact]
    public void EngineProducedPairIsCopiedIntoReadOnlyWorksheetWithoutAFileRecheck()
    {
        var source = Snapshot(); var a = Entry(source, "a"); var b = Entry(source, "b");
        File.Delete(a.Path); File.Delete(b.Path); // Owned fixture only: the worksheet intentionally performs no I/O.
        var result = DuplicatePairReviewBuilder.Build(source, a, b, DateTimeOffset.UtcNow);
        Assert.Equal(DuplicatePairReviewState.Preview, result.State);
        Assert.Equal(CheckState.Available, result.SourceCoverageState);
        Assert.Equal(a, result.Candidate); Assert.Equal(b, result.RetainedEntry);
        Assert.NotSame(a, result.Candidate); Assert.NotSame(b, result.RetainedEntry);
        Assert.False(result.ExecutionSupported);
        Assert.Contains("Current file", result.Reason);
    }

    [Fact]
    public void PartialCoverageIsRetainedWithoutClaimingCompleteDiscovery()
    {
        var source = Snapshot() with { State = CheckState.Partial };
        var result = Review(source);
        Assert.Equal(DuplicatePairReviewState.Preview, result.State);
        Assert.Equal(CheckState.Partial, result.SourceCoverageState);
        Assert.False(result.ExecutionSupported);
    }

    [Fact]
    public void StaleSelectionsAndEqualClonesAreNotCurrentReferenceMembership()
    {
        var old = Snapshot(); var current = Snapshot();
        Blocked(DuplicatePairReviewBuilder.Build(current, Entry(old, "a"), Entry(current, "b"), DateTimeOffset.UtcNow));
        Blocked(DuplicatePairReviewBuilder.Build(current, Entry(current, "a") with { }, Entry(current, "b"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CrossGroupSameIdentityAndSameSelectionCannotFormAReviewPair()
    {
        var source = Snapshot(otherGroup: true);
        Blocked(DuplicatePairReviewBuilder.Build(source, Entry(source, "a"), Entry(source, "c"), DateTimeOffset.UtcNow));
        Blocked(DuplicatePairReviewBuilder.Build(source, Entry(source, "a"), Entry(source, "a"), DateTimeOffset.UtcNow));
        var sameId = Replace(source, e => Path.GetFileName(e.Path) == "b" ? e with { Identity = Entry(source, "a").Identity } : e);
        Blocked(Review(sameId));
    }

    [Fact]
    public void CandidateWithMultipleLinksIsBlockedWhileKnownKeeperAliasesRemainObservations()
    {
        var source = Snapshot(alias: true);
        Blocked(Review(source));
        var reversed = DuplicatePairReviewBuilder.Build(source, Entry(source, "b"), Entry(source, "a"), DateTimeOffset.UtcNow);
        Assert.Equal(DuplicatePairReviewState.Preview, reversed.State);
        Assert.Equal(2u, reversed.RetainedEntry!.ObservedLinkCount);
        Assert.False(reversed.ExecutionSupported);
        Blocked(DuplicatePairReviewBuilder.Build(source, Entry(source, "alias"), Entry(source, "b"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TextClaimsCannotReplaceTypedEvidenceOrKnownLinkCounts()
    {
        var source = Snapshot();
        foreach (var changed in new[]
        {
            Replace(source, e => e with { EvidenceKind = DuplicateEvidenceKind.Unknown }),
            Replace(source, e => e with { EvidenceKind = DuplicateEvidenceKind.AliasAnchor }),
            Replace(source, e => e with { ObservedLinkCount = null }),
            Replace(source, e => e with { ObservedUtc = null })
        }) Blocked(Review(changed));
        // Presentation words are not parsed as authorization; correct typed observations remain review-only.
        var text = Replace(source, e => e with { Verification = "arbitrary display text", Relationship = "not a permission" });
        Assert.Equal(DuplicatePairReviewState.Preview, Review(text).State);
    }

    [Fact]
    public void ObservationAgeBoundDoesNotTurnIntoAnExecutionPermission()
    {
        var source = Snapshot(); var observed = Entry(source, "a").ObservedUtc!.Value;
        var boundary = Review(source, observed + DuplicatePairReviewBuilder.MaximumObservationAge);
        Assert.Equal(DuplicatePairReviewState.Preview, boundary.State);
        Assert.False(boundary.ExecutionSupported);
        Blocked(Review(source, observed + DuplicatePairReviewBuilder.MaximumObservationAge + TimeSpan.FromTicks(1)));
        Blocked(Review(Replace(source, e => e with { ObservedUtc = observed.AddHours(1) }), observed));
        Blocked(Review(Replace(source, e => e with { ObservedUtc = observed.ToOffset(TimeSpan.FromHours(1)) })));
    }

    [Fact]
    public void ContradictoryGroupBytesTimestampsAndAliasCountsAreRejected()
    {
        var source = Snapshot();
        Blocked(Review(Replace(source, e => Path.GetFileName(e.Path) == "b" ? e with { Bytes = e.Bytes + 1 } : e)));
        Blocked(Review(Replace(source, e => Path.GetFileName(e.Path) == "b" ? e with { ObservedUtc = e.ObservedUtc!.Value.AddTicks(-1) } : e)));
        var aliases = Snapshot(alias: true);
        var contradictory = Replace(aliases, e => e.Identity == Entry(aliases, "a").Identity ? e with { ObservedLinkCount = 1 } : e);
        Blocked(DuplicatePairReviewBuilder.Build(contradictory, Entry(contradictory, "b"), Entry(contradictory, "a"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RepeatedCaseInsensitivePathsAndIdentitiesAcrossGroupsAreRejected()
    {
        var source = Snapshot(otherGroup: true);
        Blocked(Review(Replace(source, e => Path.GetFileName(e.Path) == "b" ? e with { Path = Entry(source, "a").Path.ToUpperInvariant() } : e)));
        Blocked(Review(Replace(source, e => Path.GetFileName(e.Path) == "c" ? e with { Identity = Entry(source, "a").Identity } : e)));
    }

    [Theory]
    [InlineData(CheckState.Unknown)] [InlineData(CheckState.Failed)] [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Unavailable)] [InlineData(CheckState.Unsupported)]
    public void UncompletedChecksCannotBecomePreviewsDespitePlausibleEntries(CheckState state)
    {
        var result = Review(Snapshot() with { State = state });
        Assert.Equal(DuplicatePairReviewState.Incomplete, result.State);
        Assert.Null(result.Candidate); Assert.Null(result.RetainedEntry); Assert.False(result.ExecutionSupported);
        Assert.Equal(state, result.SourceCoverageState);
    }

    [Fact]
    public void ExcessEntriesMalformedCoverageAndImpossibleCountsAreBlocked()
    {
        var source = Snapshot(); var report = Report(source);
        foreach (var malformed in new[]
        {
            report with { Entries = Enumerable.Repeat(Entry(source, "a"), 513).ToArray(), VisitedFiles = 513 },
            report with { VisitedFiles = 1 }, report with { SkippedFiles = -1 }, report with { SkippedFiles = 2 },
            report with { Issues = null! }, report with { Coverage = "" }, report with { Truncated = true },
            report with { Issues = [new("", "metadata gap")] }
        }) Blocked(Review(source with { Items = [malformed] }));
    }

    [Theory]
    [InlineData(@"C:relative")]
    [InlineData(@"\\server\share\file")]
    [InlineData(@"\\?\C:\folder\file")]
    [InlineData(@"\\.\C:\folder\file")]
    [InlineData("//server/share/file")]
    public void RelativeNetworkAndDevicePathsCannotEnterAWorksheet(string invalidPath)
    {
        var source = Replace(Snapshot(), e => Path.GetFileName(e.Path) == "a" ? e with { Path = invalidPath } : e);
        Blocked(Review(source));
    }

    [Fact]
    public void MalformedUtf16AndSupplementaryFormatCharactersCannotEnterSafeFields()
    {
        var source = Snapshot();
        foreach (string invalid in new[] { "\uD800", "\uDC00", char.ConvertFromUtf32(0xE0001) })
        {
            Blocked(Review(Replace(source, e => e with { Identity = e.Identity + invalid })));
            Blocked(Review(Replace(source, e => e with { Path = e.Path + invalid })));
            Blocked(Review(Replace(source, e => e with { GroupId = e.GroupId + invalid })));
            Blocked(Review(Replace(source, e => e with { Verification = e.Verification + invalid })));
        }
    }

    [Fact]
    public void MissingSelectionAndMissingSnapshotRemainIncomplete()
    {
        var source = Snapshot();
        Assert.Equal(DuplicatePairReviewState.Incomplete, DuplicatePairReviewBuilder.Build(source, null, Entry(source, "b"), DateTimeOffset.UtcNow).State);
        Assert.Equal(DuplicatePairReviewState.Incomplete, DuplicatePairReviewBuilder.Build(null, null, null, DateTimeOffset.UtcNow).State);
    }

    [Fact]
    public void CancellationBeforeAndDuringValidationCannotPublishAWorksheet()
    {
        var source = Snapshot(); var a = Entry(source, "a"); var b = Entry(source, "b");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DuplicatePairReviewBuilder.Build(source, a, b, DateTimeOffset.UtcNow, cancelled.Token));
        using var during = new CancellationTokenSource();
        var modified = source with { Items = [Report(source) with { Entries = new CancelOnEnumeration(Report(source).Entries, during) }] };
        Assert.Throws<OperationCanceledException>(() => DuplicatePairReviewBuilder.Build(modified, a, b, DateTimeOffset.UtcNow, during.Token));
    }

    private sealed class CancelOnEnumeration(IReadOnlyList<DuplicateEntry> entries, CancellationTokenSource cancellation) : IReadOnlyList<DuplicateEntry>
    {
        public int Count => entries.Count;
        public DuplicateEntry this[int index] => entries[index];
        public IEnumerator<DuplicateEntry> GetEnumerator() { foreach (var entry in entries) { cancellation.Cancel(); yield return entry; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    // Explicit test-only identity adapter; actual fixture content goes through the real hash/byte engine.
    private sealed class TestOnlyAccess(IReadOnlyList<string> paths, bool alias) : IDuplicateFileAccess
    {
        public IEnumerable<DuplicateScanItem> Enumerate(string scope, CancellationToken token) => paths.Select(p => new DuplicateScanItem(p, true));
        public IDuplicateReadHandle Open(string path, bool content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path); string identity = name == "alias" && alias ? "a" : name;
            return new TestOnlyHandle(path, new("TEST-ONLY:" + identity, new FileInfo(path).Length, 1, 1, alias && identity == "a" ? 2u : 1u), content);
        }
    }
    private sealed class TestOnlyHandle(string path, DuplicateFileStamp stamp, bool content) : IDuplicateReadHandle
    {
        private readonly FileStream? file = content ? File.OpenRead(path) : null;
        public DuplicateFileStamp Stamp => stamp;
        public Stream Content => file ?? throw new InvalidOperationException();
        public bool IsStable() => new FileInfo(path).Length == stamp.Bytes;
        public void Dispose() => file?.Dispose();
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
