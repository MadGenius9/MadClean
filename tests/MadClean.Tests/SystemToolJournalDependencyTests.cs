using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

// These are managed ownership-graph tests. Their simulated statuses establish no
// native RootDirectory relationship, Windows handle identity or action permission.
public sealed class SystemToolJournalDependencyTests
{
    private static RelativeJournalFileRequest Request() => new("lease.bin", RelativeJournalFileOpenMode.CreateNew, false);
    private static TaskCompletionSource Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public void ParentCleanupWaitsForActualChildCleanupAndRefusalDoesNotSpendItsRelease()
    {
        var order = new ConcurrentQueue<string>();
        var parentFrame = new Frame { Releasing = () => { order.Enqueue("parent"); return true; } };
        var parentDomain = new RelativeJournalFileDomain(); var parent = parentDomain.Open(Request(), parentFrame);
        var childFrame = new Frame { Releasing = () => { order.Enqueue("child"); return true; } };
        var child = new RelativeJournalFileDomain().OpenDependent(Request(), childFrame, parent);
        Assert.Equal(0, parent.DependencyDepth); Assert.Equal(1, child.DependencyDepth);
        Assert.False(parent.Release()); Assert.Equal(0, parentFrame.ReleaseCalls);
        Assert.Equal(RelativeJournalFileState.Held, parent.State);
        Assert.True(child.Release()); Assert.True(parent.Release()); Assert.True(parent.Release());
        Assert.Equal(new[] { "child", "parent" }, order.ToArray());
        Assert.Equal(1, parentFrame.ReleaseCalls); Assert.False(parentDomain.HasHeldOwnership);
    }

    [Theory]
    [InlineData("prepare")] [InlineData("invoke")] [InlineData("capture")] [InlineData("release")]
    public void ParentIsAlreadyPinnedDuringEveryChildCallbackWithoutHoldingCallbackLocks(string stage)
    {
        var parentFrame = new Frame(); var parentDomain = new RelativeJournalFileDomain();
        var parent = parentDomain.Open(Request(), parentFrame); var childDomain = new RelativeJournalFileDomain();
        void Probe()
        {
            Assert.True(Task.Run(() =>
            {
                Assert.True(parentDomain.HasPendingWork); Assert.True(parent.HasPendingWork);
                Assert.Equal(RelativeJournalFileState.Held, parent.State);
                Assert.False(parent.Release());
                var competing = new Frame();
                var refused = new RelativeJournalFileDomain().OpenDependent(Request(), competing, parent);
                Assert.False(refused.Release()); Assert.Empty(competing.Calls);
            }).Wait(TimeSpan.FromSeconds(5)));
        }
        var frame = new Frame
        {
            Preparing = () => { if (stage == "prepare") Probe(); return true; },
            Invoking = () => { if (stage == "invoke") Probe(); return 0; },
            Capturing = () => { if (stage == "capture") Probe(); return true; },
            Releasing = () => { if (stage == "release") Probe(); return true; }
        };
        var child = childDomain.OpenDependent(Request(), frame, parent);
        Assert.True(child.Captured); Assert.True(child.Release());
        Assert.Equal(0, parentFrame.ReleaseCalls); Assert.True(parent.Release());
    }

    [Theory]
    [InlineData("precancel")] [InlineData("false")] [InlineData("throw")] [InlineData("cancel")]
    public void KnownPreentryCleanupDetachesChildAndAllowsParentRelease(string scenario)
    {
        using var stop = new CancellationTokenSource();
        var parentFrame = new Frame(); var parent = new RelativeJournalFileDomain().Open(Request(), parentFrame);
        var frame = new Frame { Preparing = () =>
        {
            Assert.False(parent.Release());
            if (scenario == "throw") throw new IOException("owned preparation prefix");
            if (scenario == "cancel") stop.Cancel();
            return scenario != "false";
        } };
        if (scenario == "precancel") stop.Cancel();
        var domain = new RelativeJournalFileDomain(); var child = domain.OpenDependent(Request(), frame, parent, stop.Token);
        Assert.False(child.EntryAttempted); Assert.True(child.OwnershipReleased);
        Assert.Equal(scenario == "precancel" ? 0 : 1, frame.PrepareCalls);
        Assert.Equal(1, frame.ReleaseCalls); Assert.False(domain.RequiresRetention);
        Assert.True(child.Release()); Assert.True(parent.Release()); Assert.Equal(1, parentFrame.ReleaseCalls);
    }

    [Theory]
    [InlineData("invokeThrow")] [InlineData("pending")] [InlineData("nonzero")]
    [InlineData("prepareCleanupFalse")] [InlineData("prepareCleanupThrow")]
    [InlineData("captureCleanupFalse")] [InlineData("releaseFalse")] [InlineData("releaseThrow")]
    public void UnknownChildOwnershipPermanentlyRetainsParentAndBlocksReplacement(string scenario)
    {
        var parentDomain = new RelativeJournalFileDomain(); var parentFrame = new Frame();
        var parent = parentDomain.Open(Request(), parentFrame);
        var childDomain = new RelativeJournalFileDomain();
        var frame = new Frame
        {
            Preparing = () => !scenario.StartsWith("prepare", StringComparison.Ordinal),
            Invoking = () => scenario == "invokeThrow" ? throw new IOException("unknown entry")
                : scenario == "pending" ? 0x103 : scenario == "nonzero" ? 1 : 0,
            Capturing = () => scenario != "captureCleanupFalse",
            Releasing = () => scenario.EndsWith("Throw", StringComparison.Ordinal)
                ? throw new IOException("unknown cleanup") : false
        };
        var child = childDomain.OpenDependent(Request(), frame, parent);
        if (child.Captured) Assert.False(child.Release());
        Assert.Equal(RelativeJournalFileState.Retained, child.State);
        Assert.Equal(RelativeJournalFileState.Held, parent.State);
        Assert.True(childDomain.RequiresRetention); Assert.True(parentDomain.RequiresRetention);
        Assert.True(parent.RequiresRetention); Assert.False(parentDomain.HasPendingWork);
        Assert.False(parent.Release()); Assert.Equal(0, parentFrame.ReleaseCalls);
        int cleanupCalls = frame.ReleaseCalls; frame.Releasing = () => true;
        Assert.False(child.Release()); Assert.Equal(cleanupCalls, frame.ReleaseCalls);
        var replacement = new Frame();
        new RelativeJournalFileDomain().OpenDependent(Request(), replacement, parent);
        Assert.Empty(replacement.Calls);
        var rootReplacement = new Frame(); parentDomain.Open(Request(), rootReplacement);
        Assert.Empty(rootReplacement.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedCaptureWithKnownSuccessfulCleanupDetachesWithoutQualifyingTheChild(bool throws)
    {
        var parent = new RelativeJournalFileDomain().Open(Request(), new Frame());
        var frame = new Frame { Capturing = () => throws ? throw new IOException("capture") : false };
        var child = new RelativeJournalFileDomain().OpenDependent(Request(), frame, parent);
        Assert.False(child.Captured); Assert.Equal(RelativeJournalFileState.Released, child.State);
        Assert.Equal(1, frame.ReleaseCalls); Assert.False(parent.RequiresRetention);
        Assert.True(parent.Release());
    }

    [Fact]
    public void CancellationAfterChildEntryKeepsKnownHeldOutputAndParentUntilExplicitRelease()
    {
        using var stop = new CancellationTokenSource();
        var parent = new RelativeJournalFileDomain().Open(Request(), new Frame());
        var frame = new Frame { Invoking = () => { stop.Cancel(); return 0; } };
        var child = new RelativeJournalFileDomain().OpenDependent(Request(), frame, parent, stop.Token);
        Assert.True(child.Captured); Assert.False(parent.Release()); Assert.Equal(0, frame.ReleaseCalls);
        Assert.True(child.Release()); Assert.True(parent.Release());
    }

    [Fact]
    public async Task BlockedChildOpenIsVisibleThroughAllAncestorsAndExcludesConcurrentSibling()
    {
        var entered = Source(); var resume = Source();
        var rootDomain = new RelativeJournalFileDomain(); var root = rootDomain.Open(Request(), new Frame());
        var middle = new RelativeJournalFileDomain().OpenDependent(Request(), new Frame(), root);
        var frame = new Frame { Preparing = () => { entered.SetResult(); return resume.Task.Wait(TimeSpan.FromSeconds(10)); } };
        var work = Task.Run(() => new RelativeJournalFileDomain().OpenDependent(Request(), frame, middle));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(rootDomain.HasPendingWork); Assert.True(root.HasPendingWork); Assert.True(middle.HasPendingWork);
            Assert.False(root.Release()); Assert.False(middle.Release());
            var sibling = new Frame();
            await Task.Run(() => new RelativeJournalFileDomain().OpenDependent(Request(), sibling, middle)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(sibling.Calls);
        }
        finally { resume.TrySetResult(); }
        var child = await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rootDomain.HasPendingWork); Assert.True(child.Release()); Assert.True(middle.Release()); Assert.True(root.Release());
    }

    [Fact]
    public async Task ChildCleanupKeepsParentPinnedUntilActualConcurrentReleaseReturns()
    {
        var entered = Source(); var resume = Source();
        var parentDomain = new RelativeJournalFileDomain(); var parentFrame = new Frame();
        var parent = parentDomain.Open(Request(), parentFrame);
        var frame = new Frame { Releasing = () => { entered.SetResult(); return resume.Task.Wait(TimeSpan.FromSeconds(10)); } };
        var child = new RelativeJournalFileDomain().OpenDependent(Request(), frame, parent);
        var cleanup = Task.Run(child.Release); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(parentDomain.HasPendingWork); Assert.False(parent.Release()); Assert.False(child.Release());
            Assert.Equal(0, parentFrame.ReleaseCalls); Assert.Equal(1, frame.ReleaseCalls);
        }
        finally { resume.TrySetResult(); }
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(child.Release()); Assert.True(parent.Release()); Assert.Equal(1, frame.ReleaseCalls);
    }

    [Fact]
    public void ExactlyEightDependenciesCanBeUnwoundInReverseOrderAndNinthIsRefusedBeforeClaim()
    {
        Assert.Equal(8, RelativeJournalFileDomain.MaximumDependencyDepth);
        var chain = new List<RelativeJournalFileAttempt> { new RelativeJournalFileDomain().Open(Request(), new Frame()) };
        for (int depth = 1; depth <= 8; depth++)
        {
            var child = new RelativeJournalFileDomain().OpenDependent(Request(), new Frame(), chain[^1]);
            Assert.True(child.Captured); Assert.Equal(depth, child.DependencyDepth); chain.Add(child);
        }
        var ninthFrame = new Frame(); var ninthDomain = new RelativeJournalFileDomain();
        var refused = ninthDomain.OpenDependent(Request(), ninthFrame, chain[^1]);
        Assert.False(refused.EntryAttempted); Assert.False(refused.Release()); Assert.Empty(ninthFrame.Calls);
        var unrelated = ninthDomain.Open(Request(), ninthFrame); Assert.True(unrelated.Captured); Assert.True(unrelated.Release());
        for (int index = chain.Count - 1; index >= 0; index--) Assert.True(chain[index].Release());
    }

    [Theory]
    [InlineData("refused")] [InlineData("released")] [InlineData("retained")] [InlineData("sameDomain")]
    public void InvalidParentsAndCyclesDoNotConsumeFreshChildFrames(string scenario)
    {
        var parentDomain = new RelativeJournalFileDomain();
        var parent = parentDomain.Open(scenario == "refused" ? null : Request(),
            new Frame { Invoking = () => scenario == "retained" ? 0x103 : 0 });
        if (scenario == "released") Assert.True(parent.Release());
        var frame = new Frame(); var domain = scenario == "sameDomain" ? parentDomain : new RelativeJournalFileDomain();
        var refused = domain.OpenDependent(Request(), frame, parent);
        Assert.False(refused.EntryAttempted); Assert.False(refused.Release()); Assert.Empty(frame.Calls);
        var fresh = new RelativeJournalFileDomain().Open(Request(), frame); Assert.True(fresh.Release());
        if (scenario == "sameDomain") Assert.True(parent.Release());
    }

    [Fact]
    public void ReplayedFramesAndBusyChildDomainsLeaveParentAvailableForARealDependent()
    {
        var parent = new RelativeJournalFileDomain().Open(Request(), new Frame());
        var used = new Frame(); var previous = new RelativeJournalFileDomain().Open(Request(), used); Assert.True(previous.Release());
        var domain = new RelativeJournalFileDomain();
        var refused = domain.OpenDependent(Request(), used, parent); Assert.False(refused.Release());
        Assert.Equal(1, used.PrepareCalls); Assert.False(parent.HasPendingWork);
        var held = domain.OpenDependent(Request(), new Frame(), parent); Assert.True(held.Captured);
        var competingParent = new RelativeJournalFileDomain().Open(Request(), new Frame());
        var untouched = new Frame(); domain.OpenDependent(Request(), untouched, competingParent);
        Assert.Empty(untouched.Calls); Assert.True(competingParent.Release());
        Assert.True(held.Release()); Assert.True(parent.Release());
    }

    [Fact]
    public void InvalidRequestDoesNotPinParentOrClaimFrameAndCleanChildSlotCanBeReused()
    {
        var parent = new RelativeJournalFileDomain().Open(Request(), new Frame());
        var domain = new RelativeJournalFileDomain(); var frame = new Frame();
        var refused = domain.OpenDependent(null, frame, parent);
        Assert.False(refused.Release()); Assert.Empty(frame.Calls); Assert.False(parent.HasPendingWork);
        var first = domain.OpenDependent(Request(), frame, parent); Assert.True(first.Release());
        var second = domain.OpenDependent(Request(), new Frame(), parent); Assert.True(second.Captured);
        Assert.False(parent.Release()); Assert.True(first.Release()); Assert.False(parent.Release());
        Assert.True(second.Release()); Assert.True(parent.Release());
    }

    [Fact]
    public async Task ParentAlreadyCleaningCannotAcquireAChildEvenBeforeItsStateChanges()
    {
        var entered = Source(); var resume = Source();
        var parent = new RelativeJournalFileDomain().Open(Request(), new Frame
        { Releasing = () => { entered.SetResult(); return resume.Task.Wait(TimeSpan.FromSeconds(10)); } });
        var cleanup = Task.Run(parent.Release); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(RelativeJournalFileState.Held, parent.State);
            var frame = new Frame(); var domain = new RelativeJournalFileDomain();
            var refused = domain.OpenDependent(Request(), frame, parent);
            Assert.False(refused.EntryAttempted); Assert.False(refused.Release()); Assert.Empty(frame.Calls);
            var unrelated = domain.Open(Request(), frame); Assert.True(unrelated.Captured); Assert.True(unrelated.Release());
        }
        finally { resume.TrySetResult(); }
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ParentRootsActualAbandonedChildDomainFrameAndAttempt(bool retained)
    {
        var rootDomain = new RelativeJournalFileDomain(); var parent = rootDomain.Open(Request(), new Frame());
        var weak = AbandonChild(parent, retained);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(weak.Attempt.TryGetTarget(out var child)); Assert.True(weak.Frame.TryGetTarget(out var frame));
        Assert.True(weak.Domain.TryGetTarget(out _)); Assert.Equal(0, frame!.ReleaseCalls);
        Assert.False(parent.Release());
        if (retained) Assert.True(rootDomain.RequiresRetention);
        else { Assert.True(child!.Release()); Assert.True(parent.Release()); }
        GC.KeepAlive(rootDomain);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<RelativeJournalFileAttempt> Attempt, WeakReference<Frame> Frame,
        WeakReference<RelativeJournalFileDomain> Domain) AbandonChild(RelativeJournalFileAttempt parent, bool retained)
    {
        var domain = new RelativeJournalFileDomain(); var frame = new Frame { Invoking = () => retained ? 0x103 : 0 };
        var child = domain.OpenDependent(Request(), frame, parent);
        return (new(child), new(frame), new(domain));
    }

    [Fact]
    public void ActualOwnedStreamRemainsUsableThroughChildReadbackAndClosesOnlyAfterChildDetach()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MadCleanDependency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "owned.bin"); byte[] bytes = [12, 34, 56, 78];
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            stream.Write(bytes); stream.Flush();
            var parentFrame = new Frame { Releasing = () => { stream.Dispose(); return true; } };
            var parent = new RelativeJournalFileDomain().Open(Request(), parentFrame);
            var child = new RelativeJournalFileDomain().OpenDependent(Request(), new Frame { Capturing = () =>
            {
                Assert.False(parent.Release()); stream.Position = 0;
                byte[] observed = new byte[bytes.Length]; stream.ReadExactly(observed); Assert.Equal(bytes, observed); return true;
            } }, parent);
            Assert.True(stream.CanRead); Assert.False(parent.Release()); Assert.True(child.Release());
            Assert.True(stream.CanRead); Assert.True(parent.Release()); Assert.False(stream.CanRead);
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Frame : RelativeJournalFileFrame
    {
        internal readonly ConcurrentQueue<string> Calls = new();
        internal int PrepareCalls, ReleaseCalls;
        internal Func<bool>? Preparing, Capturing, Releasing;
        internal Func<int>? Invoking;
        internal override bool Prepare(RelativeJournalFileRequest request, CancellationToken token)
        { Interlocked.Increment(ref PrepareCalls); Calls.Enqueue("prepare"); return Preparing?.Invoke() ?? true; }
        internal override int Invoke() { Calls.Enqueue("invoke"); return Invoking?.Invoke() ?? 0; }
        internal override bool CaptureSuccess() { Calls.Enqueue("capture"); return Capturing?.Invoke() ?? true; }
        internal override bool ReleaseKnownOwnership()
        { Interlocked.Increment(ref ReleaseCalls); Calls.Enqueue("release"); return Releasing?.Invoke() ?? true; }
    }
}
