using System.Security.Cryptography;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

// These adapters exercise the portable algorithm using real fixture contents and explicit TEST-ONLY
// identity/stream policies. They do not validate Windows file IDs, ADS, sharing or native APIs.
public sealed class DuplicateDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MadCleanDuplicateTests-" + Guid.NewGuid());
    public DuplicateDiscoveryTests() => Directory.CreateDirectory(root);
    private string FileAt(string name, byte[] contents)
    {
        var path = Path.Combine(root, name); File.WriteAllBytes(path, contents); return path;
    }
    private static DuplicateReport Report(CheckResult<DuplicateReport> result) => Assert.Single(result.Items);

    [Fact]
    public void EqualContentsWithDifferentNamesAreGroupedButSameSizeDifferentTailIsNot()
    {
        var bytes = new byte[131_100]; Array.Fill(bytes, (byte)7);
        var first = FileAt("one.txt", bytes); var second = FileAt("different-name.bin", bytes);
        bytes[^1] = 8; var different = FileAt("same-size-other.txt", bytes);
        var access = new TestOnlyAccess([first, second, different]);
        var result = new DuplicateDiscoveryEngine().Scan(root, access, default);
        var entries = Report(result).Entries;
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(2, entries.Count);
        Assert.Equal(entries[0].GroupId, entries[1].GroupId);
        Assert.NotEqual(entries[0].Identity, entries[1].Identity);
        Assert.DoesNotContain(entries, entry => entry.Path == different);
        Assert.All(entries, entry => Assert.Contains("complete byte comparison", entry.Verification));
        Assert.Equal(0, access.ActiveHandles);
    }

    [Fact]
    public void ForcedDigestCollisionStillRequiresCompleteByteComparison()
    {
        var one = new byte[80_000]; var other = new byte[80_000]; other[^1] = 1;
        var a = FileAt("a.txt", one); var b = FileAt("b.txt", other);
        static byte[] ConstantDigestAfterFullRead(Stream stream, CancellationToken token)
        { stream.CopyTo(Stream.Null); token.ThrowIfCancellationRequested(); return [42]; }
        var result = new DuplicateDiscoveryEngine(hashForTests: ConstantDigestAfterFullRead).Scan(root, new TestOnlyAccess([a, b]), default);
        Assert.Empty(Report(result).Entries);
        Assert.DoesNotContain("no duplicates", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameFileAliasIsNotAnIndependentCopyOrReadAgain()
    {
        var a = FileAt("a.txt", [1, 2, 3]); var b = FileAt("b.txt", [1, 2, 3]);
        var alias = FileAt("alias.txt", [1, 2, 3]);
        var access = new TestOnlyAccess([a, b, alias]);
        access.AliasSources[alias] = a;
        var entries = Report(new DuplicateDiscoveryEngine().Scan(root, access, default)).Entries;
        var first = Assert.Single(entries, entry => entry.Path == a);
        var aliasEntry = Assert.Single(entries, entry => entry.Path == alias);
        Assert.Equal(first.Identity, aliasEntry.Identity);
        Assert.Equal(first.GroupId, aliasEntry.GroupId);
        Assert.Contains("not an independent copy", aliasEntry.Relationship);
        Assert.DoesNotContain(alias, access.ContentOpened);
    }

    [Fact]
    public void AliasOnlyGroupDoesNotClaimIndependentDuplicateVerification()
    {
        var a = FileAt("a.txt", [1]); var alias = FileAt("alias.txt", [1]);
        var access = new TestOnlyAccess([a, alias]); access.AliasSources[alias] = a;
        var entries = Report(new DuplicateDiscoveryEngine().Scan(root, access, default)).Entries;
        Assert.Equal(2, entries.Count); Assert.All(entries, entry => Assert.StartsWith("A", entry.GroupId));
        Assert.DoesNotContain(entries, entry => entry.Verification.Contains("complete byte comparison", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyIndependentFilesCanBeObservedWithoutInventingReadBytes()
    {
        var a = FileAt("a.txt", []); var b = FileAt("b.txt", []);
        var access = new TestOnlyAccess([a, b]);
        var result = new DuplicateDiscoveryEngine(maxReadBytes: 0).Scan(root, access, default);
        Assert.Equal(2, Report(result).Entries.Count);
        Assert.Equal(0, access.BytesRead);
    }

    [Fact]
    public void UniqueSizesDoNotTriggerContentReads()
    {
        var a = FileAt("a.txt", [1]); var b = FileAt("b.txt", [1, 2]);
        var access = new TestOnlyAccess([a, b]);
        Assert.Empty(Report(new DuplicateDiscoveryEngine().Scan(root, access, default)).Entries);
        Assert.Empty(access.ContentOpened);
    }

    [Fact]
    public void MissingIdentityAndUnverifiableStreamPolicyRemainPartial()
    {
        var a = FileAt("a.txt", [1]); var b = FileAt("b.txt", [1]); var c = FileAt("c.txt", [1]);
        var access = new TestOnlyAccess([a, b, c]); access.UnknownIdentities.Add(a); access.StreamRejected.Add(b);
        var result = new DuplicateDiscoveryEngine().Scan(root, access, default);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Entries);
        Assert.Equal(2, Report(result).SkippedFiles); Assert.Empty(access.ContentOpened);
    }

    [Fact]
    public void LockedFixtureIsNotForcedOpenForContentComparison()
    {
        var a = FileAt("a.txt", [1]); var b = FileAt("b.txt", [1]);
        using var locked = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = new DuplicateDiscoveryEngine().Scan(root, new TestOnlyAccess([a, b]), default);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Entries);
    }

    [Fact]
    public void ReferenceChangedDuringWorkInvalidatesTheAffectedGroup()
    {
        var a = FileAt("a.txt", [1, 2, 3]); var b = FileAt("b.txt", [1, 2, 3]); int hashes = 0;
        byte[] HashWithTestMutation(Stream stream, CancellationToken token)
        {
            var digest = SHA256.HashData(stream);
            if (++hashes == 2) { File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddDays(1)); }
            token.ThrowIfCancellationRequested(); return digest;
        }
        var result = new DuplicateDiscoveryEngine(hashForTests: HashWithTestMutation).Scan(root, new TestOnlyAccess([a, b]), default);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Entries);
        Assert.Contains(Report(result).Issues, issue => issue.Reason.Contains("discarded", StringComparison.Ordinal));
    }

    [Fact]
    public void DataChangedBetweenMetadataAndContentOpenIsNotReadAsTheOldObject()
    {
        var a = FileAt("a.txt", [1, 2]); var b = FileAt("b.txt", [1, 2]);
        var access = new TestOnlyAccess([a, b]);
        access.BeforeContent = path => { if (path == a) File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddDays(1)); };
        var result = new DuplicateDiscoveryEngine().Scan(root, access, default);
        Assert.Equal(CheckState.Partial, result.State); Assert.Empty(Report(result).Entries);
    }

    [Fact]
    public void FileCountLimitStillReportsVerifiedObservationsFromTheCollectedSubset()
    {
        var a = FileAt("a.txt", [1]); var b = FileAt("b.txt", [1]); var c = FileAt("c.txt", [1]);
        var result = new DuplicateDiscoveryEngine(maxFiles: 2).Scan(root, new TestOnlyAccess([a, b, c]), default);
        Assert.Equal(CheckState.Partial, result.State); Assert.True(Report(result).Truncated);
        Assert.Equal(2, Report(result).VisitedFiles); Assert.Equal(2, Report(result).Entries.Count);
    }

    [Fact]
    public void ContentBudgetIsEnforcedBeforeReadingMoreFixtureData()
    {
        var a = FileAt("a.txt", new byte[8]); var b = FileAt("b.txt", new byte[8]);
        var access = new TestOnlyAccess([a, b]);
        var result = new DuplicateDiscoveryEngine(maxReadBytes: 10).Scan(root, access, default);
        Assert.Equal(CheckState.Partial, result.State); Assert.True(Report(result).Truncated);
        Assert.Empty(Report(result).Entries); Assert.InRange(access.BytesRead, 0, 10);
        Assert.Equal(0, access.ActiveHandles);
    }

    [Fact]
    public void PerFileLimitPreventsOversizedContentReads()
    {
        var a = FileAt("a.txt", new byte[8]); var b = FileAt("b.txt", new byte[8]);
        var access = new TestOnlyAccess([a, b]);
        var result = new DuplicateDiscoveryEngine(maxFileBytes: 4).Scan(root, access, default);
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(2, Report(result).SkippedFiles);
        Assert.Empty(access.ContentOpened);
    }

    [Fact]
    public void StableOpenLimitRetainsKnownGroupsButDoesNotClaimCompleteCoverage()
    {
        var files = Enumerable.Range(0, 3).Select(i => FileAt($"{i}.txt", [1])).ToArray();
        var access = new TestOnlyAccess(files);
        var result = new DuplicateDiscoveryEngine(maxOpenFiles: 2).Scan(root, access, default);
        Assert.Equal(CheckState.Partial, result.State); Assert.True(Report(result).Truncated);
        Assert.Equal(2, Report(result).Entries.Count); Assert.InRange(access.PeakContentHandles, 0, 2);
        Assert.Equal(0, access.ActiveHandles);
    }

    [Fact]
    public void CancellationDuringHashingClosesAllHandlesAndDoesNotReturnAGroup()
    {
        var a = FileAt("a.txt", new byte[100_000]); var b = FileAt("b.txt", new byte[100_000]);
        using var source = new CancellationTokenSource();
        byte[] CancelHash(Stream stream, CancellationToken token)
        {
            stream.ReadExactly(new byte[1024]); source.Cancel(); stream.ReadExactly(new byte[1024]);
            return [];
        }
        var access = new TestOnlyAccess([a, b]);
        Assert.ThrowsAny<OperationCanceledException>(() => new DuplicateDiscoveryEngine(hashForTests: CancelHash).Scan(root, access, source.Token));
        Assert.Equal(0, access.ActiveHandles);
    }

    [Fact]
    public void CancellationBeforeEnumerationDoesNotOpenFixtureFiles()
    {
        var file = FileAt("a.txt", [1]); using var source = new CancellationTokenSource(); source.Cancel();
        var access = new TestOnlyAccess([file]);
        Assert.ThrowsAny<OperationCanceledException>(() => new DuplicateDiscoveryEngine().Scan(root, access, source.Token));
        Assert.Empty(access.ContentOpened); Assert.Equal(0, access.ActiveHandles);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class TestOnlyAccess(IReadOnlyList<string> paths) : IDuplicateFileAccess
    {
        public Dictionary<string, string> AliasSources { get; } = [];
        public HashSet<string> UnknownIdentities { get; } = [];
        public HashSet<string> StreamRejected { get; } = [];
        public List<string> ContentOpened { get; } = [];
        public Action<string>? BeforeContent { get; set; }
        public int ActiveHandles { get; private set; }
        public int ActiveContentHandles { get; private set; }
        public int PeakContentHandles { get; private set; }
        public long BytesRead { get; private set; }
        public IEnumerable<DuplicateScanItem> Enumerate(string root, CancellationToken token) => paths.Select(path => new DuplicateScanItem(path, true));
        private DuplicateFileStamp ReadStamp(string path)
        {
            string canonical = AliasSources.GetValueOrDefault(path, path);
            var info = new FileInfo(canonical);
            return new(UnknownIdentities.Contains(path) ? "" : "TEST-ONLY-IDENTITY:" + canonical, info.Length,
                info.LastWriteTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, AliasSources.Values.Contains(canonical) ? 2u : 1u);
        }
        public IDuplicateReadHandle Open(string path, bool content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (StreamRejected.Contains(path)) throw new DuplicateReadException("TEST-ONLY adapter: stream scope unavailable; file excluded.");
            if (content) BeforeContent?.Invoke(path);
            var handle = new TestOnlyHandle(this, path, content);
            ActiveHandles++;
            if (content) { ContentOpened.Add(path); ActiveContentHandles++; PeakContentHandles = Math.Max(PeakContentHandles, ActiveContentHandles); }
            return handle;
        }
        private sealed class TestOnlyHandle : IDuplicateReadHandle
        {
            private readonly TestOnlyAccess owner; private readonly string path; private readonly FileStream? file;
            private readonly Stream? counted; private bool disposed;
            public DuplicateFileStamp Stamp { get; }
            public Stream Content => counted ?? throw new InvalidOperationException();
            public TestOnlyHandle(TestOnlyAccess owner, string path, bool content)
            {
                this.owner = owner; this.path = path; Stamp = owner.ReadStamp(path);
                if (content)
                {
                    file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    counted = new CountingStream(file, count => owner.BytesRead += count);
                }
            }
            public bool IsStable() => Stamp == owner.ReadStamp(path);
            public void Dispose()
            {
                if (disposed) return; disposed = true; file?.Dispose(); owner.ActiveHandles--;
                if (file != null) owner.ActiveContentHandles--;
            }
        }
        private sealed class CountingStream(Stream source, Action<int> count) : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
            public override long Length => source.Length;
            public override long Position { get => source.Position; set => source.Position = value; }
            public override int Read(byte[] buffer, int offset, int length) => Read(buffer.AsSpan(offset, length));
            public override int Read(Span<byte> buffer) { int read = source.Read(buffer); count(read); return read; }
            public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        }
    }
}
