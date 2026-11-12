using System.Runtime.CompilerServices;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WuaInstallationMonitorTests
{
    [ThreadStatic] private static int callbackDepth;
    private static WuaInstallationTarget[] Targets(int count = 3) => Enumerable.Range(0, count).Select(index => new WuaInstallationTarget(Guid.NewGuid(), index)).ToArray();
    [Fact]
    public async Task EveryNativeBoundaryRunsOutsideCallbacksAndEverySelectedIndexKeepsItsOwnFacts()
    {
        var session = new Job(WuaInstallationMonitorTests.Targets()) { CallbackCompletions = true };
        session.Codes = [2, 4, 999]; session.HResults = [null, unchecked((int)0x80240022), 0]; session.Reboots = [false, true, null];
        var domain = new WuaInstallationMonitorDomain(); var run = new WuaInstallationMonitor(domain).ObserveAsync(session);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); session.SignalCompletion();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Adopted); Assert.True(result.CompletionObserved); Assert.True(result.OwnershipReleased);
        Assert.Equal(0, session.CallbackViolations); Assert.Equal(new[] { 0, 1, 2 }, session.Reads);
        Assert.Equal(WuaInstallationResultCode.Succeeded, result.Aggregate!.ResultCode);
        Assert.Equal(new[] { WuaInstallationResultCode.Succeeded, WuaInstallationResultCode.Failed, WuaInstallationResultCode.Unknown }, result.Items.Select(item => item.ResultCode));
        Assert.Null(result.Items[0].Raw!.HResult); Assert.True(result.Items[1].Raw!.RebootRequired); Assert.Null(result.Items[2].Raw!.RebootRequired);
        Assert.Equal(999, result.Items[2].Raw!.RawResultCode); Assert.All(result.Items, item => Assert.Equal(CheckState.Unknown, item.InstalledState));
        Assert.Equal(CheckState.Unknown, result.InstalledState); Assert.Equal(new[] { "observe", "end", "item:0", "item:1", "item:2", "cleanup", "release" }, session.Calls);
        Assert.False(domain.HasPendingWork); Assert.False(domain.RequiresRetention);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("wrongIndex")]
    public async Task OneUnusableItemCannotBorrowAggregateSuccessOrHideOtherSelectedFailures(string failure)
    {
        var session = new Job(WuaInstallationMonitorTests.Targets()) { ItemFailure = failure, FailedIndex = 1, Codes = [4, 2, 5] };
        var run = new WuaInstallationMonitor(new()).ObserveAsync(session); await session.Entered.Task; session.SignalCompletion();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.OwnershipReleased); Assert.Null(result.Items[1].Raw); Assert.Equal(WuaInstallationResultCode.Unknown, result.Items[1].ResultCode);
        Assert.Equal(WuaInstallationResultCode.Failed, result.Items[0].ResultCode); Assert.Equal(WuaInstallationResultCode.Aborted, result.Items[2].ResultCode);
        Assert.Equal(new[] { 0, 1, 2 }, session.Reads); Assert.Equal(1, session.CleanupCalls); Assert.Equal(1, session.ReleaseCalls);
    }

    [Theory]
    [InlineData("completionFault", false)]
    [InlineData("completionForeign", false)]
    [InlineData("endThrow", true)]
    [InlineData("endForeign", true)]
    public async Task UnknownCompletionOrEndRetainsActualJobWithoutCleanupOrRetry(string failure, bool observed)
    {
        var session = new Job(WuaInstallationMonitorTests.Targets()) { Failure = failure }; var domain = new WuaInstallationMonitorDomain();
        var monitor = new WuaInstallationMonitor(domain); var run = monitor.ObserveAsync(session); await session.Entered.Task; session.SignalCompletion();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Adopted); Assert.Equal(observed, result.CompletionObserved); Assert.False(result.OwnershipReleased);
        Assert.Null(result.Aggregate); Assert.Equal(0, session.CleanupCalls); Assert.Equal(0, session.ReleaseCalls); Assert.Empty(session.Reads);
        Assert.True(domain.RequiresRetention); Assert.False(domain.HasPendingWork);
        var rejected = await monitor.ObserveAsync(session); Assert.False(rejected.Adopted); Assert.False(rejected.OwnershipReleased);
        var other = new Job(WuaInstallationMonitorTests.Targets()); rejected = await monitor.ObserveAsync(other); Assert.False(rejected.Adopted); Assert.Empty(other.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanupFailureNeverCallsFinalReleaseAndReleaseRefusalRemainsRetained(bool cleanupFails)
    {
        var session = new Job(WuaInstallationMonitorTests.Targets()) { Failure = cleanupFails ? "cleanupThrow" : "releaseFalse" };
        var domain = new WuaInstallationMonitorDomain(); var run = new WuaInstallationMonitor(domain).ObserveAsync(session);
        await session.Entered.Task; session.SignalCompletion(); var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.CompletionObserved); Assert.NotNull(result.Aggregate); Assert.False(result.OwnershipReleased);
        Assert.Equal(1, session.CleanupCalls); Assert.Equal(cleanupFails ? 0 : 1, session.ReleaseCalls); Assert.True(domain.RequiresRetention);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerStopIncludingPreCancelledCallerStillMonitorsTheAlreadyOwnedJobAndPendingCleanup(bool preCancelled)
    {
        var session = new Job(WuaInstallationMonitorTests.Targets()) { HoldCleanup = true }; using var stop = new CancellationTokenSource(); if (preCancelled) stop.Cancel();
        var domain = new WuaInstallationMonitorDomain(); var monitor = new WuaInstallationMonitor(domain); var run = monitor.ObserveAsync(session, stop.Token);
        await session.Entered.Task; Assert.False(run.IsCompleted); Assert.True(domain.HasPendingWork);
        var competing = new Job(WuaInstallationMonitorTests.Targets()); var refused = await monitor.ObserveAsync(competing);
        Assert.False(refused.Adopted); Assert.False(refused.OwnershipReleased); Assert.Empty(competing.Calls);
        session.SignalCompletion(); await session.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted); Assert.True(domain.HasPendingWork); Assert.Equal(0, session.ReleaseCalls);
        stop.Cancel(); Assert.False(run.IsCompleted);
        session.CleanupContinue.TrySetResult(); var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.StopRequested); Assert.True(result.OwnershipReleased); Assert.Equal(CheckState.Unknown, result.InstalledState);
        Assert.Equal(WuaInstallationResultCode.Succeeded, result.Aggregate!.ResultCode); // Stop is not an Aborted outcome.
        Assert.False((await monitor.ObserveAsync(session)).Adopted);
        var otherDomain = await new WuaInstallationMonitor(new()).ObserveAsync(session);
        Assert.False(otherDomain.Adopted); Assert.False(otherDomain.OwnershipReleased); Assert.Equal(1, session.EndCalls);
    }

    [Fact]
    public async Task ProcessRootedDomainKeepsDepartedActualSessionAndTasksAliveUntilCompletionAndCleanup()
    {
        var domain = new WuaInstallationMonitorDomain(); var references = await Depart(domain);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(references.Session.TryGetTarget(out var session)); Assert.True(references.Run.TryGetTarget(out var run));
        Assert.True(domain.HasPendingWork); Assert.Equal(0, session!.CleanupCalls);
        session.SignalCompletion(); Assert.True((await run!.WaitAsync(TimeSpan.FromSeconds(5))).OwnershipReleased);
        Assert.False(domain.HasPendingWork); Assert.False(domain.RequiresRetention);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<Job> Session, WeakReference<Task<WuaInstallationMonitorResult>> Run)> Depart(WuaInstallationMonitorDomain domain)
    {
        var session = new Job(WuaInstallationMonitorTests.Targets()); var run = new WuaInstallationMonitor(domain).ObserveAsync(session); await session.Entered.Task;
        return (new(session), new(run));
    }

    [Fact]
    public void TargetCaptureRejectsAmbiguousIdentityAndRetainsAnImmutableOrderedCopy()
    {
        var targets = Targets(); var first = targets[0]; var session = new Job(targets); targets[0] = targets[1];
        Assert.Equal(first, session.Targets[0]); Assert.NotSame(first, session.Targets[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<WuaInstallationTarget>)session.Targets).Clear());
        Assert.Throws<ArgumentException>(() => new Job([]));
        Assert.Throws<ArgumentException>(() => new Job(Targets(129)));
        Assert.Throws<ArgumentException>(() => new Job([first, first with { Revision = first.Revision + 1 }]));
        Assert.Throws<ArgumentException>(() => new Job([new(Guid.Empty, 0)]));
        Assert.Throws<ArgumentException>(() => new Job([new(Guid.NewGuid(), -1)]));
    }

    private sealed class Job(IReadOnlyList<WuaInstallationTarget> targets) : WuaInstallationJobSession(targets)
    {
        public string? Failure, ItemFailure; public int FailedIndex = 1;
        public int?[] Codes = [2, 2, 2], HResults = [0, 0, 0]; public bool?[] Reboots = [false, false, false];
        public bool CallbackCompletions, HoldCleanup; public int CallbackViolations, EndCalls, CleanupCalls, ReleaseCalls;
        public readonly List<int> Reads = []; public readonly List<string> Calls = [];
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), CleanupContinue = new();
        private readonly TaskCompletionSource<WuaInstallationCompletion> completion = new();
        public void SignalCompletion()
        {
            callbackDepth++;
            try
            {
                if (Failure == "completionFault") completion.TrySetException(new InvalidOperationException("Controlled callback observation failure."));
                else completion.TrySetResult(Failure == "completionForeign" ? new Job(WuaInstallationMonitorTests.Targets()).CreateCompletion() : CreateCompletion());
            }
            finally { callbackDepth--; }
        }
        private void Enter(string name) { if (callbackDepth != 0) Interlocked.Increment(ref CallbackViolations); Calls.Add(name); }
        protected override Task<WuaInstallationCompletion> ObserveCompletionAsync() { Enter("observe"); Entered.TrySetResult(); return completion.Task; }
        protected override Task<WuaInstallationAggregate> EndInstallAsync()
        {
            Enter("end"); EndCalls++;
            if (Failure == "endThrow") throw new InvalidOperationException("Controlled EndInstall uncertainty.");
            return Return(Failure == "endForeign" ? new Job(WuaInstallationMonitorTests.Targets()).CreateAggregate(2, 0, false) : CreateAggregate(2, 0, false));
        }
        protected override Task<WuaInstallationItemResult> ReadUpdateResultAsync(int index)
        {
            Enter("item:" + index); Reads.Add(index);
            if (index == FailedIndex)
            {
                if (ItemFailure == "throw") throw new InvalidOperationException("Controlled per-index failure.");
                if (ItemFailure == "missing") return Return<WuaInstallationItemResult>(null!);
                if (ItemFailure == "foreign") return Return(new Job(WuaInstallationMonitorTests.Targets()).CreateItemResult(index, 2, 0, false));
                if (ItemFailure == "wrongIndex") return Return(CreateItemResult(index + 1, 2, 0, false));
            }
            return Return(CreateItemResult(index, Codes[index], HResults[index], Reboots[index]));
        }
        protected override async Task CleanUpAsync()
        {
            Enter("cleanup"); CleanupCalls++; CleanupEntered.TrySetResult();
            if (Failure == "cleanupThrow") throw new InvalidOperationException("Controlled cleanup uncertainty.");
            if (HoldCleanup) await CleanupContinue.Task;
            await Return(true);
        }
        protected override Task<bool> ReleaseOwnershipAsync()
        { Enter("release"); ReleaseCalls++; return Return(Failure != "releaseFalse"); }
        private Task<T> Return<T>(T value)
        {
            if (!CallbackCompletions) return Task.FromResult(value);
            var source = new TaskCompletionSource<T>();
            ThreadPool.QueueUserWorkItem(_ => { callbackDepth++; try { source.TrySetResult(value); } finally { callbackDepth--; } });
            return source.Task;
        }
    }
}
