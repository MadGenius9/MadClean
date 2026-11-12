using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using MadClean.Core;
using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemToolJournalRelativeFileTests
{
    private static RelativeJournalFileRequest Request(string leaf = "lease.bin", RelativeJournalFileOpenMode mode = RelativeJournalFileOpenMode.CreateNew, bool readOnly = false) => new(leaf, mode, readOnly);

    [Theory]
    [InlineData("")] [InlineData("LEASE.BIN")] [InlineData("../lease.bin")] [InlineData("x/lease.bin")]
    [InlineData("lease.bin:stream")] [InlineData("lease.bin ")] [InlineData("00000000000000000000000000000000.jsonl")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.jsonl")] [InlineData("0123456789abcdef0123456789abcdef.JSONL")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef.jsonl")]
    public void UnsupportedNamesNeverClaimOrCallTheSuppliedFrame(string leaf)
    {
        var frame = new Frame(); var domain = new RelativeJournalFileDomain();
        var refused = domain.Open(Request(leaf), frame);
        Assert.Equal(RelativeJournalFileState.RefusedBeforeEntry, refused.State); Assert.False(refused.EntryAttempted);
        Assert.False(refused.OwnershipReleased); Assert.False(refused.Release()); Assert.Empty(frame.Calls);
        Assert.False(domain.HasPendingWork); Assert.False(domain.RequiresRetention);
        var valid = domain.Open(Request(), frame); // Invalid input did not consume caller-owned preparation.
        Assert.Equal(RelativeJournalFileState.Held, valid.State); Assert.True(valid.Release());
    }

    [Fact]
    public void ExactLowercaseGuidAndOpenModesAreCheckedBeforeAnyAdapterCallback()
    {
        string leaf = Guid.NewGuid().ToString("N") + ".jsonl";
        Assert.True(RelativeJournalFileDomain.IsSupported(Request(leaf)));
        Assert.True(RelativeJournalFileDomain.IsSupported(Request(leaf, RelativeJournalFileOpenMode.OpenExisting, true)));
        foreach (var invalid in new RelativeJournalFileRequest?[] { null, Request(null!),
                     Request(mode: (RelativeJournalFileOpenMode)999), Request(readOnly: true) })
        {
            var frame = new Frame(); var result = new RelativeJournalFileDomain().Open(invalid, frame);
            Assert.False(result.EntryAttempted); Assert.False(result.Release()); Assert.Empty(frame.Calls);
        }
    }

    [Theory]
    [InlineData("false")] [InlineData("throw")] [InlineData("precancel")] [InlineData("cancelDuringPrepare")]
    public void KnownPreentryRefusalChecksResourcePrefixReleaseExactlyOnce(string scenario)
    {
        using var stop = new CancellationTokenSource();
        var frame = new Frame();
        frame.Preparing = () =>
        {
            if (scenario == "throw") throw new IOException("controlled preparation prefix");
            if (scenario == "cancelDuringPrepare") stop.Cancel();
            return scenario != "false";
        };
        if (scenario == "precancel") stop.Cancel();
        var domain = new RelativeJournalFileDomain(); var attempt = domain.Open(Request(), frame, stop.Token);
        Assert.Equal(RelativeJournalFileState.RefusedBeforeEntry, attempt.State); Assert.False(attempt.EntryAttempted);
        Assert.Null(attempt.NativeStatus); Assert.False(attempt.Captured); Assert.True(attempt.OwnershipReleased);
        Assert.Equal(scenario == "precancel" ? 0 : 1, frame.PrepareCalls);
        Assert.Equal(0, frame.InvokeCalls); Assert.Equal(0, frame.CaptureCalls); Assert.Equal(1, frame.ReleaseCalls);
        Assert.True(attempt.Release()); Assert.Equal(1, frame.ReleaseCalls);
        Assert.False(domain.HasPendingWork); Assert.False(domain.RequiresRetention);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownPrefixCleanupRetainsItsActualFrameAndCannotBeRetried(bool throws)
    {
        var frame = new Frame { Preparing = () => false, Releasing = () => throws ? throw new IOException("controlled release") : false };
        var domain = new RelativeJournalFileDomain(); var attempt = domain.Open(Request(), frame);
        Assert.Equal(RelativeJournalFileState.Retained, attempt.State); Assert.True(domain.RequiresRetention);
        Assert.False(domain.HasPendingWork); Assert.False(attempt.OwnershipReleased);
        frame.Releasing = () => true;
        Assert.False(attempt.Release()); Assert.Equal(1, frame.ReleaseCalls);
        var next = new Frame(); domain.Open(Request(), next); Assert.Empty(next.Calls);
    }

    [Theory]
    [InlineData(0x103)] [InlineData(1)] [InlineData(unchecked((int)0xC0000001))]
    public void EveryNonzeroRawStatusRetainsInputsWithoutCapturingOrCleaningUnknownOutputs(int status)
    {
        var frame = new Frame { Invoking = () => status }; var domain = new RelativeJournalFileDomain();
        var attempt = domain.Open(Request(), frame);
        Assert.Equal(status, attempt.NativeStatus); Assert.True(attempt.EntryAttempted);
        Assert.Equal(RelativeJournalFileState.Retained, attempt.State); Assert.False(attempt.Captured);
        Assert.Equal(0, frame.CaptureCalls); Assert.Equal(0, frame.ReleaseCalls);
        Assert.False(attempt.Release()); Assert.Equal(0, frame.ReleaseCalls); Assert.True(domain.RequiresRetention);
    }

    [Fact]
    public void ThrowingInvokeDoesNotInventNativeEntryStatusOrOutputOwnership()
    {
        var frame = new Frame { Invoking = () => throw new IOException("controlled invocation uncertainty") };
        var attempt = new RelativeJournalFileDomain().Open(Request(), frame);
        Assert.True(attempt.EntryAttempted); Assert.Null(attempt.NativeStatus);
        Assert.Equal(RelativeJournalFileState.Retained, attempt.State); Assert.False(attempt.Release());
        Assert.Equal(new[] { "prepare", "invoke" }, frame.Calls.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ZeroStatusCaptureFailureMayReleaseKnownOwnershipButNeverClaimsAQualifiedFile(bool throws)
    {
        var frame = new Frame { Capturing = () => throws ? throw new IOException("controlled capture") : false };
        var domain = new RelativeJournalFileDomain(); var attempt = domain.Open(Request(), frame);
        Assert.Equal(0, attempt.NativeStatus); Assert.True(attempt.EntryAttempted); Assert.False(attempt.Captured);
        Assert.Equal(RelativeJournalFileState.Released, attempt.State); Assert.True(attempt.OwnershipReleased);
        Assert.Equal(new[] { "prepare", "invoke", "capture", "release" }, frame.Calls.ToArray());
        Assert.True(attempt.Release()); Assert.Equal(1, frame.ReleaseCalls); Assert.False(domain.RequiresRetention);
    }

    [Fact]
    public void PositiveCaptureKeepsTheActualFrameHeldUntilAnExplicitMemoizedRelease()
    {
        var frame = new Frame(); var domain = new RelativeJournalFileDomain(); var attempt = domain.Open(Request(), frame);
        Assert.Equal(RelativeJournalFileState.Held, attempt.State); Assert.True(attempt.Captured);
        Assert.True(domain.HasHeldOwnership); Assert.False(domain.HasPendingWork); Assert.False(attempt.OwnershipReleased);
        Assert.Equal(0, frame.ReleaseCalls); Assert.True(attempt.Release()); Assert.True(attempt.Release());
        Assert.Equal(1, frame.ReleaseCalls); Assert.True(attempt.OwnershipReleased);
        Assert.Equal(RelativeJournalFileState.Released, attempt.State); Assert.False(domain.HasHeldOwnership);
        var next = domain.Open(Request(), new Frame()); Assert.Equal(RelativeJournalFileState.Held, next.State);
        Assert.True(attempt.Release()); Assert.True(domain.HasHeldOwnership); Assert.True(next.Release());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void FailedKnownSuccessCleanupIsStickyWhetherCaptureQualifiedOrNot(bool captured, bool throws)
    {
        var frame = new Frame { Capturing = () => captured,
            Releasing = () => throws ? throw new IOException("controlled successful-output cleanup") : false };
        var domain = new RelativeJournalFileDomain(); var attempt = domain.Open(Request(), frame);
        if (captured) Assert.False(attempt.Release());
        Assert.Equal(0, attempt.NativeStatus); Assert.Equal(captured, attempt.Captured);
        Assert.Equal(RelativeJournalFileState.Retained, attempt.State); Assert.True(domain.RequiresRetention);
        Assert.False(domain.HasPendingWork); Assert.False(attempt.OwnershipReleased);
        frame.Releasing = () => true;
        Assert.False(attempt.Release()); Assert.Equal(1, frame.ReleaseCalls);
        var next = new Frame(); domain.Open(Request(), next); Assert.Empty(next.Calls);
    }

    [Fact]
    public void AnotherDomainCannotReopenOrReleaseAnAlreadyClaimedFrame()
    {
        var frame = new Frame(); var first = new RelativeJournalFileDomain().Open(Request(), frame);
        var other = new RelativeJournalFileDomain(); var refused = other.Open(Request(), frame);
        Assert.False(refused.EntryAttempted); Assert.False(refused.Release()); Assert.Equal(0, frame.ReleaseCalls);
        Assert.True(first.Release());
        var again = other.Open(Request(), frame); Assert.False(again.Release());
        Assert.Equal(1, frame.PrepareCalls); Assert.Equal(1, frame.InvokeCalls); Assert.Equal(1, frame.CaptureCalls); Assert.Equal(1, frame.ReleaseCalls);
    }

    [Fact]
    public async Task ReleaseDuringActualOpenCallbackIsRefusedWithoutSpendingTheLaterHeldCleanup()
    {
        var entered = Source(); var resume = Source(); var frame = new Frame();
        frame.Invoking = () => { entered.SetResult(); Assert.True(resume.Task.Wait(TimeSpan.FromSeconds(5))); return 0; };
        var domain = new RelativeJournalFileDomain(); var work = Task.Run(() => domain.Open(Request(), frame));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var actual = Assert.IsType<RelativeJournalFileAttempt>(typeof(RelativeJournalFileDomain)
            .GetField("owner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(domain));
        try
        {
            Assert.True(domain.HasPendingWork); Assert.False(actual.Release()); Assert.Equal(0, frame.ReleaseCalls);
            var other = new Frame(); domain.Open(Request(), other); Assert.Empty(other.Calls);
        }
        finally { resume.TrySetResult(); }
        Assert.Same(actual, await work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(RelativeJournalFileState.Held, actual.State); Assert.True(actual.Release()); Assert.Equal(1, frame.ReleaseCalls);
    }

    [Fact]
    public async Task ConcurrentReleaseNeverCallsCleanupTwiceAndMemoizesOnlyTheActualTerminalResult()
    {
        var entered = Source(); var resume = Source(); var frame = new Frame();
        frame.Releasing = () => { entered.SetResult(); return resume.Task.Wait(TimeSpan.FromSeconds(5)); };
        var domain = new RelativeJournalFileDomain(); var attempt = domain.Open(Request(), frame);
        var releasing = Task.Run(attempt.Release); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.True(domain.HasPendingWork); Assert.False(attempt.Release()); Assert.Equal(1, frame.ReleaseCalls); }
        finally { resume.TrySetResult(); }
        Assert.True(await releasing.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(attempt.Release()); Assert.Equal(1, frame.ReleaseCalls);
    }

    [Fact]
    public void CancellationAfterInvokeDoesNotDiscardKnownSuccessOrPretendToRollbackAFile()
    {
        using var stop = new CancellationTokenSource(); var frame = new Frame { Invoking = () => { stop.Cancel(); return 0; } };
        var attempt = new RelativeJournalFileDomain().Open(Request(), frame, stop.Token);
        Assert.True(attempt.Captured); Assert.Equal(RelativeJournalFileState.Held, attempt.State); Assert.Equal(0, frame.ReleaseCalls);
        Assert.True(attempt.Release());
    }

    [Fact]
    public void ExternalCallbacksNeverRunUnderProtocolOrDomainLocks()
    {
        var domain = new RelativeJournalFileDomain();
        void Probe() => Assert.True(Task.Run(() => domain.HasPendingWork).Wait(TimeSpan.FromSeconds(3)));
        var frame = new Frame { Preparing = () => { Probe(); return true; }, Invoking = () => { Probe(); return 0; },
            Capturing = () => { Probe(); return true; }, Releasing = () => { Probe(); return true; } };
        var attempt = domain.Open(Request(), frame); Assert.True(attempt.Captured); Assert.True(attempt.Release());
    }

    [Fact]
    public void AProcessRootedRetainedDomainKeepsItsActualFrameAndParentGraphAlive()
    {
        var domain = new RelativeJournalFileDomain(); var weak = Abandon(domain);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(weak.TryGetTarget(out var frame)); Assert.NotNull(frame!.ParentGraph); Assert.Equal(0, frame.ReleaseCalls);
        Assert.True(domain.RequiresRetention); GC.KeepAlive(domain);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Frame> Abandon(RelativeJournalFileDomain domain)
    {
        var frame = new Frame { Invoking = () => 0x103 };
        domain.Open(Request(), frame); return new(frame);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OwnedFileStreamAndCoreJournalCodecExerciseRoundtripOrMalformedTailWithoutRepair(bool malformed)
    {
        string root = OwnedDirectory();
        try
        {
            var id = Guid.NewGuid(); string leaf = id.ToString("N") + ".jsonl";
            var entry = new SystemToolJournalEntry(id, SystemToolOperation.DismRestoreHealth, 1, DateTimeOffset.UtcNow.AddSeconds(-1),
                SystemToolJournalPhase.DispatchIntent, null, null, null, null);
            Assert.True(SystemToolJournal.TryEncode([entry], DateTimeOffset.UtcNow, out var encoded));
            byte[] data = malformed ? encoded!.Concat(new byte[] { (byte)'\n', (byte)'{' }).ToArray()
                : encoded!.Concat(new byte[] { (byte)'\n' }).ToArray();
            var frame = new FileFrame(root, data); var attempt = new RelativeJournalFileDomain().Open(Request(leaf), frame);
            if (malformed)
            {
                Assert.False(attempt.Captured); Assert.Equal(RelativeJournalFileState.Released, attempt.State);
                Assert.Equal(SystemToolJournalState.Unknown, frame.Review!.State);
            }
            else
            {
                Assert.True(attempt.Captured); Assert.Equal(SystemToolJournalState.Unresolved, frame.Review!.State);
                Assert.True(attempt.Release());
            }
            string file = Path.Combine(root, leaf);
            Assert.True(File.Exists(file)); Assert.Equal(data, File.ReadAllBytes(file)); // Release is not deletion or repair.
            if (!malformed)
            {
                var reader = new FileFrame(root, null);
                var opened = new RelativeJournalFileDomain().Open(Request(leaf, RelativeJournalFileOpenMode.OpenExisting, true), reader);
                Assert.True(opened.Captured); Assert.Equal(SystemToolJournalState.Unresolved, reader.Review!.State);
                Assert.True(opened.Release()); Assert.Equal(data, File.ReadAllBytes(file)); Assert.Equal(1, reader.ReleaseCalls);
            }
            Assert.Equal(1, frame.ReleaseCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OwnedCreateCollisionPreservesExistingBytesAndDoesNotFallBackToOpeningTheFile()
    {
        string root = OwnedDirectory();
        try
        {
            string leaf = Guid.NewGuid().ToString("N") + ".jsonl";
            string path = Path.Combine(root, leaf); byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(path, original);
            var frame = new FileFrame(root, [9]); var domain = new RelativeJournalFileDomain();
            var attempt = domain.Open(Request(leaf), frame);
            Assert.Equal(RelativeJournalFileState.Retained, attempt.State);
            Assert.True(attempt.EntryAttempted); Assert.Null(attempt.NativeStatus);
            Assert.False(attempt.Captured); Assert.Null(frame.Review);
            Assert.Equal(1, frame.InvokeCalls); Assert.Equal(0, frame.ReleaseCalls);
            Assert.False(attempt.Release()); Assert.True(domain.RequiresRetention);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OwnedBlockedParentRefusesPreparationWithoutEnteringOrChangingTheSentinel()
    {
        string root = OwnedDirectory();
        try
        {
            string blocked = Path.Combine(root, "owned-parent-file"); File.WriteAllText(blocked, "owned sentinel");
            var frame = new FileFrame(blocked, []); var attempt = new RelativeJournalFileDomain().Open(Request(), frame);
            Assert.False(attempt.EntryAttempted); Assert.Equal(RelativeJournalFileState.RefusedBeforeEntry, attempt.State);
            Assert.True(attempt.OwnershipReleased); Assert.Equal(0, frame.InvokeCalls);
            Assert.Equal("owned sentinel", File.ReadAllText(blocked)); Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string OwnedDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "MadCleanRelativeFile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static TaskCompletionSource Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Frame : RelativeJournalFileFrame
    {
        internal readonly ConcurrentQueue<string> Calls = new();
        internal readonly object ParentGraph = new();
        internal int PrepareCalls, InvokeCalls, CaptureCalls, ReleaseCalls;
        internal Func<bool>? Preparing, Capturing, Releasing;
        internal Func<int>? Invoking;
        internal override bool Prepare(RelativeJournalFileRequest request, CancellationToken token)
        { Interlocked.Increment(ref PrepareCalls); Calls.Enqueue("prepare"); return Preparing?.Invoke() ?? true; }
        internal override int Invoke() { Interlocked.Increment(ref InvokeCalls); Calls.Enqueue("invoke"); return Invoking?.Invoke() ?? 0; }
        internal override bool CaptureSuccess() { Interlocked.Increment(ref CaptureCalls); Calls.Enqueue("capture"); return Capturing?.Invoke() ?? true; }
        internal override bool ReleaseKnownOwnership() { Interlocked.Increment(ref ReleaseCalls); Calls.Enqueue("release"); return Releasing?.Invoke() ?? true; }
    }
    // Controlled portable file fixture: return values here are simulated protocol statuses,
    // not observations of NtCreateFile, ACLs, Windows handles or power-loss durability.
    private sealed class FileFrame(string parent, byte[]? initial) : RelativeJournalFileFrame
    {
        private RelativeJournalFileRequest? request;
        private FileStream? stream;
        internal int InvokeCalls, ReleaseCalls;
        internal SystemToolJournalReview? Review;
        internal override bool Prepare(RelativeJournalFileRequest actual, CancellationToken token)
        { token.ThrowIfCancellationRequested(); request = actual; return Directory.Exists(parent); }
        internal override int Invoke()
        {
            InvokeCalls++;
            stream = new(Path.Combine(parent, request!.Leaf), request.Mode == RelativeJournalFileOpenMode.CreateNew ? FileMode.CreateNew : FileMode.Open,
                request.ReadOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None);
            return 0;
        }
        internal override bool CaptureSuccess()
        {
            if (request!.Mode == RelativeJournalFileOpenMode.CreateNew && initial != null)
            { stream!.Write(initial); stream.Flush(true); }
            if (stream!.Length > SystemToolJournal.MaximumUtf8Bytes) return false;
            stream.Position = 0; byte[] actual = new byte[checked((int)stream.Length)]; stream.ReadExactly(actual);
            Review = SystemToolJournal.DecodeSnapshots(actual, DateTimeOffset.UtcNow);
            return Review.State != SystemToolJournalState.Unknown;
        }
        internal override bool ReleaseKnownOwnership() { ReleaseCalls++; stream?.Dispose(); return true; }
    }
}
