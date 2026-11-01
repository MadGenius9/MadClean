using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DuplicatePairRecheckTests
{
    private const string Root = @"C:\Chosen";
    private const string Left = @"C:\Chosen\a.bin", Right = @"C:\Chosen\b.bin";
    private static (DuplicatePairRecheckScope Scope, DuplicateEntry Candidate, DuplicateEntry Retained, CheckResult<DuplicateReport> Snapshot) Fixture(long length = 4, CheckState state = CheckState.Available)
    {
        var time = DateTimeOffset.UtcNow;
        DuplicateEntry Row(string path, string id) => new("group", path, length, id, "Independent", "Byte compared")
        { EvidenceKind = DuplicateEvidenceKind.ByteComparedIndependentFile, ObservedUtc = time, ObservedLinkCount = 1 };
        var a = Row(Left, "A"); var b = Row(Right, "B");
        var snapshot = new CheckResult<DuplicateReport>(state, "private-message", [new([a, b], [], 2, 0, false, "Explicit folder")]);
        return (Assert.Single(DuplicatePairRecheckScope.Create(Root, snapshot, a, b).Items), a, b, snapshot);
    }
    private static DuplicatePairRecheckReport Report(CheckResult<DuplicatePairRecheckReport> result) => Assert.Single(result.Items);

    [Fact]
    public void EqualRequiresBothSimultaneouslyHeldFullStreamsAndReleasesBeforePublication()
    {
        var f = Fixture(); var access = new Access([1, 2, 3, 4], [1, 2, 3, 4]);
        var result = new DuplicatePairRecheckEngine().Read(f.Scope, access);
        var report = Report(result);
        Assert.Equal(DuplicatePairRecheckState.Equal, report.State); Assert.Equal(8, report.ComparedBytes);
        Assert.Equal(new[] { Left, Right }, access.Opened);
        Assert.True(access.BothWereOpen); Assert.True(access.Left.Disposed); Assert.True(access.Right.Disposed);
        Assert.False(report.CanRemove); Assert.Contains("No keeper lease", report.Coverage);
        Assert.Contains("do not prove uninterrupted object history", report.Coverage);
    }

    [Fact]
    public void LiteralContentDifferenceWinsWithoutAnyHashAssumptionAndBothFilesAreFullyRead()
    {
        var f = Fixture(); var access = new Access([1, 2, 3, 4], [1, 9, 3, 4]);
        var report = Report(new DuplicatePairRecheckEngine().Read(f.Scope, access));
        Assert.Equal(DuplicatePairRecheckState.Different, report.State);
        Assert.Equal(8, report.ComparedBytes); Assert.False(report.CanRemove);
    }

    [Fact]
    public void ShortReadsAreFilledRatherThanMistakenForEof()
    {
        var f = Fixture(); var access = new Access([1, 2, 3, 4], [1, 2, 3, 4], shortReads: true);
        Assert.Equal(DuplicatePairRecheckState.Equal, Report(new DuplicatePairRecheckEngine().Read(f.Scope, access)).State);
    }

    [Fact]
    public void EmptyIndependentFilesRequireBothEofObservations()
    {
        var f = Fixture(0); var access = new Access([], []);
        access.Left.Value = access.Left.Value with { Bytes = 0 };
        access.Right.Value = access.Right.Value with { Bytes = 0 };
        var report = Report(new DuplicatePairRecheckEngine().Read(f.Scope, access));
        Assert.Equal(DuplicatePairRecheckState.Equal, report.State); Assert.Equal(0, report.ComparedBytes);
        Assert.True(access.Left.Disposed); Assert.True(access.Right.Disposed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void PrematureOrExtraEofContentInvalidatesPriorLength(int bytes)
    {
        var f = Fixture(); var access = new Access(new byte[bytes], new byte[4]);
        Assert.Equal(DuplicatePairRecheckState.Changed, Report(new DuplicatePairRecheckEngine().Read(f.Scope, access)).State);
    }

    [Fact]
    public void ReplacedIdentityOrChangingMetadataNeverReuseOldDuplicateState()
    {
        var f = Fixture();
        var replaced = new Access([1, 2, 3, 4], [1, 2, 3, 4]);
        replaced.Left.Value = replaced.Left.Value with { Identity = "new-object" };
        Assert.Equal(DuplicatePairRecheckState.Changed, Report(new DuplicatePairRecheckEngine().Read(f.Scope, replaced)).State);
        var changing = new Access([1, 2, 3, 4], [1, 2, 3, 4]);
        changing.Left.ChangeOnRead = true;
        Assert.Equal(DuplicatePairRecheckState.Changed, Report(new DuplicatePairRecheckEngine().Read(f.Scope, changing)).State);
    }

    [Fact]
    public void AliasesMultiLinksAndOversizeCurrentDataAreUnknown()
    {
        var f = Fixture();
        foreach (var stamp in new[]
        {
            new DuplicateFileStamp("B", 4, 1, 1, 1), new DuplicateFileStamp("A", 4, 1, 1, 2),
            new DuplicateFileStamp("A", DuplicatePairRecheckEngine.MaximumFileBytes + 1, 1, 1, 1)
        })
        {
            var access = new Access([1, 2, 3, 4], [1, 2, 3, 4]); access.Left.Value = stamp;
            var result = new DuplicatePairRecheckEngine().Read(f.Scope, access);
            Assert.Equal(CheckState.Unknown, result.State); Assert.Equal(DuplicatePairRecheckState.Unknown, Report(result).State);
            Assert.Equal(0, Report(result).ComparedBytes);
        }
    }

    [Fact]
    public void ScopeRequiresExactSnapshotReferencesLocalDescendantsAndSingleLinks()
    {
        var f = Fixture();
        Assert.Empty(DuplicatePairRecheckScope.Create(Root, f.Snapshot, f.Candidate with { }, f.Retained).Items);
        Assert.Empty(DuplicatePairRecheckScope.Create(@"C:\Other", f.Snapshot, f.Candidate, f.Retained).Items);
        Assert.Empty(DuplicatePairRecheckScope.Create(@"\\server\share", f.Snapshot, f.Candidate, f.Retained).Items);
        var retained = f.Retained with { ObservedLinkCount = 2 };
        var changed = f.Snapshot with { Items = [f.Snapshot.Items[0] with { Entries = [f.Candidate, retained] }] };
        Assert.Empty(DuplicatePairRecheckScope.Create(Root, changed, f.Candidate, retained).Items);
    }

    [Fact]
    public void PartialSourceStaysPartialOnlyForCompleteCurrentComparison()
    {
        var f = Fixture(state: CheckState.Partial);
        var result = new DuplicatePairRecheckEngine().Read(f.Scope, new Access([1, 2, 3, 4], [1, 2, 3, 4]));
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Partial, Report(result).SourceCoverageState);
        var access = new Access([1, 2, 3, 4], [1, 2, 3, 4]) { FailSecondOpen = true };
        var failed = new DuplicatePairRecheckEngine().Read(f.Scope, access);
        Assert.Equal(CheckState.Unknown, failed.State); Assert.True(access.Left.Disposed);
        Assert.DoesNotContain("private-provider", failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PreCancellationDoesNotOpenAndDuringReadCancellationClosesBoth()
    {
        var f = Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var access = new Access([1, 2, 3, 4], [1, 2, 3, 4]);
        Assert.ThrowsAny<OperationCanceledException>(() => new DuplicatePairRecheckEngine().Read(f.Scope, access, cancellation.Token));
        Assert.Empty(access.Opened);
        using var during = new CancellationTokenSource();
        access.Left.AfterRead = during.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => new DuplicatePairRecheckEngine().Read(f.Scope, access, during.Token));
        Assert.True(access.Left.Disposed); Assert.True(access.Right.Disposed);
    }

    [Fact]
    public void ExactAggregateReadCeilingIsRespectedWithoutSpeculativeExtraByteRead()
    {
        var f = Fixture(DuplicatePairRecheckEngine.MaximumFileBytes);
        var access = new SizedAccess();
        var report = Report(new DuplicatePairRecheckEngine().Read(f.Scope, access));
        Assert.Equal(DuplicatePairRecheckState.Equal, report.State);
        Assert.Equal(DuplicatePairRecheckEngine.MaximumReadBytes, report.ComparedBytes);
        Assert.Equal(DuplicatePairRecheckEngine.MaximumReadBytes, access.Left.ContentBytes + access.Right.ContentBytes);
        Assert.True(access.Left.Disposed); Assert.True(access.Right.Disposed);
    }

    private sealed class SizedAccess : IDuplicateFileAccess
    {
        public SizedHandle Left { get; } = new("A");
        public SizedHandle Right { get; } = new("B");
        public IEnumerable<DuplicateScanItem> Enumerate(string root, CancellationToken token) => throw new InvalidOperationException();
        public IDuplicateReadHandle Open(string path, bool content, CancellationToken token) => path == DuplicatePairRecheckTests.Left ? Left : Right;
    }
    private sealed class SizedHandle(string identity) : IDuplicateReadHandle
    {
        private readonly ZeroStream stream = new();
        public DuplicateFileStamp Stamp => new(identity, DuplicatePairRecheckEngine.MaximumFileBytes, 1, 1, 1);
        public Stream Content => stream;
        public long ContentBytes => stream.ReadBytes;
        public bool Disposed { get; private set; }
        public bool IsStable() => true;
        public void Dispose() { Disposed = true; stream.Dispose(); }
    }
    private sealed class ZeroStream : Stream
    {
        public long ReadBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => DuplicatePairRecheckEngine.MaximumFileBytes;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int actual = (int)Math.Min(count, Length - Position);
            buffer.AsSpan(offset, actual).Clear(); Position += actual; ReadBytes += actual; return actual;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Access : IDuplicateFileAccess
    {
        public Handle Left { get; } public Handle Right { get; }
        public List<string> Opened { get; } = [];
        public bool BothWereOpen { get; private set; }
        public bool FailSecondOpen { get; init; }
        public Access(byte[] left, byte[] right, bool shortReads = false)
        { Left = new("A", left, shortReads); Right = new("B", right, shortReads); }
        public IEnumerable<DuplicateScanItem> Enumerate(string root, CancellationToken token) => throw new InvalidOperationException("Must never enumerate.");
        public IDuplicateReadHandle Open(string path, bool content, CancellationToken token)
        {
            Assert.True(content); Opened.Add(path);
            if (path == LeftPath) return Left;
            if (FailSecondOpen) throw new IOException("private-provider-sentinel");
            BothWereOpen = !Left.Disposed; return Right;
        }
        private const string LeftPath = DuplicatePairRecheckTests.Left;
    }
    private sealed class Handle : IDuplicateReadHandle
    {
        public DuplicateFileStamp Value { get; set; }
        public DuplicateFileStamp Stamp => Value;
        public bool Disposed { get; private set; }
        public bool ChangeOnRead { get; set; }
        public Action? AfterRead { get; set; }
        public Stream Content { get; }
        public Handle(string id, byte[] bytes, bool shortReads)
        {
            Value = new(id, 4, 1, 1, 1);
            Content = new ReadStream(bytes, shortReads, () =>
            {
                if (ChangeOnRead) Value = Value with { ChangeTime = Value.ChangeTime + 1 };
                AfterRead?.Invoke();
            });
        }
        public bool IsStable() => !ChangeOnRead || Value.ChangeTime == 1;
        public void Dispose() { Disposed = true; Content.Dispose(); }
    }
    private sealed class ReadStream(byte[] bytes, bool shortReads, Action afterRead) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, shortReads ? Math.Min(count, 1) : count);
            afterRead(); return read;
        }
    }
}
