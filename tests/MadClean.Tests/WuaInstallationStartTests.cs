using System.Runtime.CompilerServices;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WuaInstallationStartTests
{
    [ThreadStatic] private static int callbackDepth;
    private static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static WuaInstallationTarget Target() => new(Guid.NewGuid(), 17);
    private static WuaInstallationStartRequest Request(WuaInstallationTarget? target = null, long generation = 4, bool confirmed = true)
    {
        var request = WuaInstallationStart.CreateRequest(target ?? Target(), generation, Now)!;
        if (confirmed) Assert.True(request.Confirm(request.RequestId, request.Target, generation, true, Now));
        return request;
    }
    private static WuaInstallationStart Controller(WuaInstallationStartDomain domain, TimeProvider? clock = null) => new(domain, clock ?? new Clock());

    [Fact]
    public async Task MetadataPreparationAndDeclinedConfirmationGrantNoBeginOrResourceRelease()
    {
        var target = Target(); var request = Request(target, confirmed: false);
        Assert.NotSame(target, request.Target); Assert.Equal(target, request.Target);
        Assert.False(request.Confirm(request.RequestId, request.Target, 4, false, Now));
        var job = new Job([target]); var prepared = new Prepared(request, job);
        var result = await Controller(new()).StartAsync(request, prepared, 4).WaitAsync(Deadline);
        AssertRefused(result, prepared, job);
        Assert.Null(WuaInstallationStart.CreateRequest(null, 0, Now));
        Assert.Null(WuaInstallationStart.CreateRequest(new(Guid.Empty, 0), 0, Now));
        Assert.Null(WuaInstallationStart.CreateRequest(new(Guid.NewGuid(), -1), 0, Now));
        Assert.Null(WuaInstallationStart.CreateRequest(target, -1, Now));
        Assert.Null(WuaInstallationStart.CreateRequest(target, 0, Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.Null(WuaInstallationStart.CreateRequest(target, 0, DateTimeOffset.MaxValue));
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("preCancelled")]
    [InlineData("expired")]
    [InlineData("generation")]
    [InlineData("invalidated")]
    [InlineData("cancelAtEntry")]
    [InlineData("expireAtEntry")]
    public async Task KnownPreEntryRefusalsNeverBeginAndLeaveUnenteredPreparationWithCaller(string cause)
    {
        var request = Request(confirmed: cause != "declined"); var job = new Job([request.Target]);
        var prepared = new Prepared(request, job); var domain = new WuaInstallationStartDomain();
        using var stop = new CancellationTokenSource();
        var clock = new Clock();
        if (cause == "preCancelled") stop.Cancel();
        if (cause == "expired") clock.Value = request.ExpiresUtc;
        if (cause == "invalidated") Assert.True(request.Invalidate());
        if (cause == "cancelAtEntry") clock.Hook = call => { if (call == 2) stop.Cancel(); };
        if (cause == "expireAtEntry") clock.Hook = call => { if (call == 2) clock.Value = request.ExpiresUtc; };
        var result = await Controller(domain, clock).StartAsync(request, prepared, cause == "generation" ? 5 : 4, stop.Token).WaitAsync(Deadline);
        AssertRefused(result, prepared, job);
        Assert.False(request.BeginCommitted); Assert.False(domain.HasPendingWork); Assert.False(domain.RequiresRetention);
        Assert.Equal(cause is "preCancelled" or "cancelAtEntry", result.StopRequested);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("multiple")]
    public async Task PreparedGraphMustMatchTheExactRequestAndSingleIdentityRevision(string mismatch)
    {
        var request = Request();
        var targets = mismatch switch
        {
            "id" => new[] { request.Target with { UpdateId = Guid.NewGuid() } },
            "revision" => new[] { request.Target with { Revision = request.Target.Revision + 1 } },
            "multiple" => new[] { request.Target, Target() },
            _ => new[] { request.Target }
        };
        var job = new Job(targets); var prepared = new Prepared(mismatch == "request" ? Request(request.Target) : request, job);
        var domain = new WuaInstallationStartDomain();
        AssertRefused(await Controller(domain).StartAsync(request, prepared, 4).WaitAsync(Deadline), prepared, job);
        Assert.False(domain.HasPendingWork); Assert.False(domain.RequiresRetention);
    }

    [Fact]
    public async Task ConfirmationMismatchInvalidatesRatherThanAllowingASecondCorrectedConfirmation()
    {
        foreach (string mismatch in new[] { "request", "revision", "generation" })
        {
            var request = Request(confirmed: false);
            Assert.False(request.Confirm(mismatch == "request" ? Guid.NewGuid() : request.RequestId,
                mismatch == "revision" ? request.Target with { Revision = 18 } : request.Target,
                mismatch == "generation" ? 5 : 4, true, Now));
            Assert.True(request.IsInvalidated);
            Assert.False(request.Confirm(request.RequestId, request.Target, 4, true, Now));
            var job = new Job([request.Target]); var prepared = new Prepared(request, job);
            AssertRefused(await Controller(new()).StartAsync(request, prepared, 4).WaitAsync(Deadline), prepared, job);
        }
    }

    [Fact]
    public async Task SameJobCannotBeginThroughAnotherWrapperDomainOrReplayedRequest()
    {
        var request = Request(); var job = new Job([request.Target]); var first = new Prepared(request, job) { HoldBegin = true };
        var domain = new WuaInstallationStartDomain(); var controller = Controller(domain);
        var run = controller.StartAsync(request, first, 4); await first.Entered.Task.WaitAsync(Deadline);
        var otherRequest = Request(request.Target); var other = new Prepared(otherRequest, job);
        var otherDomain = new WuaInstallationStartDomain();
        var refused = await Controller(otherDomain).StartAsync(otherRequest, other, 4).WaitAsync(Deadline);
        Assert.Equal(WuaInstallationStartOutcome.RefusedBeforeEntry, refused.Outcome);
        Assert.Equal(0, other.BeginCalls); Assert.False(refused.OwnershipReleased);
        var competingRequest = Request(); var competingJob = new Job([competingRequest.Target]);
        var competing = new Prepared(competingRequest, competingJob);
        AssertRefused(await controller.StartAsync(competingRequest, competing, 4).WaitAsync(Deadline), competing, competingJob);
        Assert.Equal(WuaInstallationStartOutcome.RefusedBeforeEntry, (await controller.StartAsync(request, first, 4).WaitAsync(Deadline)).Outcome);
        Assert.Equal(1, first.BeginCalls); Assert.True(domain.HasPendingWork); Assert.Equal(0, job.ObserveCalls);
        first.FinishBegin(); await job.ObserveEntered.Task.WaitAsync(Deadline); job.SignalCompletion();
        Assert.True((await run.WaitAsync(Deadline)).OwnershipReleased);
        Assert.Equal(WuaInstallationStartOutcome.RefusedBeforeEntry, (await controller.StartAsync(request, first, 4).WaitAsync(Deadline)).Outcome);
        var thirdRequest = Request(request.Target); var third = new Prepared(thirdRequest, job);
        Assert.Equal(WuaInstallationStartOutcome.RefusedBeforeEntry,
            (await Controller(new()).StartAsync(thirdRequest, third, 4).WaitAsync(Deadline)).Outcome);
        Assert.Equal(0, third.BeginCalls); Assert.Equal(1, job.EndCalls);
    }

    [Fact]
    public async Task PublicMonitorCannotStealAReservedJobWhileBeginIsPending()
    {
        var request = Request(); var job = new Job([request.Target]); var prepared = new Prepared(request, job) { HoldBegin = true };
        var domain = new WuaInstallationStartDomain(); var run = Controller(domain).StartAsync(request, prepared, 4);
        await prepared.Entered.Task.WaitAsync(Deadline);
        var refused = await new WuaInstallationMonitor(new()).ObserveAsync(job).WaitAsync(Deadline);
        Assert.False(refused.Adopted); Assert.False(refused.OwnershipReleased); Assert.Equal(0, job.ObserveCalls);
        Assert.True(domain.HasPendingWork);
        prepared.FinishBegin(); await job.ObserveEntered.Task.WaitAsync(Deadline); job.SignalCompletion();
        Assert.True((await run.WaitAsync(Deadline)).OwnershipReleased);
    }

    [Fact]
    public async Task AJobAlreadyOwnedByPublicMonitorCannotBeBegunLater()
    {
        var request = Request(); var job = new Job([request.Target]);
        var monitoring = new WuaInstallationMonitor(new()).ObserveAsync(job); await job.ObserveEntered.Task.WaitAsync(Deadline);
        var prepared = new Prepared(request, job); var refused = await Controller(new()).StartAsync(request, prepared, 4).WaitAsync(Deadline);
        Assert.Equal(WuaInstallationStartOutcome.RefusedBeforeEntry, refused.Outcome);
        Assert.Equal(0, prepared.BeginCalls); Assert.False(refused.OwnershipReleased);
        job.SignalCompletion(); Assert.True((await monitoring.WaitAsync(Deadline)).OwnershipReleased);
        Assert.Equal(1, job.ObserveCalls); Assert.Equal(1, job.CleanupCalls);
    }

    [Fact]
    public async Task InlineCompletionBeforeBeginReturnsCannotRunMonitorOnCallbackStackOrBorrowAggregateSuccess()
    {
        var request = Request(); var job = new Job([request.Target]) { ItemCode = 4, ItemHResult = null, ItemReboot = true };
        var prepared = new Prepared(request, job) { HoldBegin = true, CompleteInline = true };
        var domain = new WuaInstallationStartDomain(); Task<WuaInstallationStartResult> run;
        callbackDepth++;
        try { run = Controller(domain).StartAsync(request, prepared, 4); }
        finally { callbackDepth--; }
        await prepared.Entered.Task.WaitAsync(Deadline);
        Assert.False(run.IsCompleted); Assert.Equal(0, job.ObserveCalls); Assert.Equal(0, job.EndCalls); Assert.Equal(0, job.CleanupCalls);
        prepared.FinishBegin(); var result = await run.WaitAsync(Deadline);
        Assert.Equal(WuaInstallationStartOutcome.Monitored, result.Outcome); Assert.True(result.OwnershipReleased);
        Assert.Equal(0, prepared.CallbackViolations); Assert.Equal(0, job.CallbackViolations);
        Assert.Equal(WuaInstallationResultCode.Succeeded, result.Monitor!.Aggregate!.ResultCode);
        Assert.Equal(WuaInstallationResultCode.Failed, result.Monitor.Items.Single().ResultCode);
        Assert.Null(result.Monitor.Items.Single().Raw!.HResult); Assert.True(result.Monitor.Items.Single().Raw!.RebootRequired);
        Assert.Equal(CheckState.Unknown, result.InstalledState); Assert.Equal(CheckState.Unknown, result.Monitor.Items.Single().InstalledState);
        Assert.Equal(new[] { "observe", "end", "item", "cleanup", "release" }, job.Calls);
    }

    [Fact]
    public async Task StopAfterEntryDoesNotAbandonDelayedBeginOrActualCleanup()
    {
        var request = Request(); var job = new Job([request.Target]) { HoldCleanup = true, HoldRelease = true };
        var prepared = new Prepared(request, job) { HoldBegin = true }; using var stop = new CancellationTokenSource();
        var domain = new WuaInstallationStartDomain(); var run = Controller(domain).StartAsync(request, prepared, 4, stop.Token);
        await prepared.Entered.Task.WaitAsync(Deadline); Assert.True(request.BeginCommitted); stop.Cancel();
        Assert.False(request.Invalidate()); Assert.False(run.IsCompleted); Assert.True(domain.HasPendingWork);
        prepared.FinishBegin(); await job.ObserveEntered.Task.WaitAsync(Deadline); job.SignalCompletion();
        await job.CleanupEntered.Task.WaitAsync(Deadline); Assert.False(run.IsCompleted); Assert.Equal(0, job.ReleaseCalls);
        job.CleanupContinue.TrySetResult(); await job.ReleaseEntered.Task.WaitAsync(Deadline);
        Assert.False(run.IsCompleted); Assert.True(domain.HasPendingWork); Assert.Equal(1, job.ReleaseCalls);
        job.ReleaseContinue.TrySetResult(); var result = await run.WaitAsync(Deadline);
        Assert.True(result.StopRequested); Assert.True(result.OwnershipReleased); Assert.Equal(1, prepared.BeginCalls);
        Assert.Equal(WuaInstallationResultCode.Succeeded, result.Monitor!.Aggregate!.ResultCode);
        Assert.Equal(CheckState.Unknown, result.InstalledState); Assert.False(domain.HasPendingWork);
    }

    [Fact]
    public async Task ProcessRootedDomainKeepsDepartedPreparedGraphAndBeginTaskAlive()
    {
        var domain = new WuaInstallationStartDomain(); var references = await Depart(domain);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(references.Prepared.TryGetTarget(out var prepared)); Assert.True(references.Job.TryGetTarget(out var job));
        Assert.True(references.Run.TryGetTarget(out var run)); Assert.True(domain.HasPendingWork);
        job!.SignalCompletion(); prepared!.FinishBegin();
        Assert.True((await run!.WaitAsync(Deadline)).OwnershipReleased); Assert.False(domain.RequiresRetention);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<Prepared> Prepared, WeakReference<Job> Job, WeakReference<Task<WuaInstallationStartResult>> Run)> Depart(WuaInstallationStartDomain domain)
    {
        var request = Request(); var job = new Job([request.Target]); var prepared = new Prepared(request, job) { HoldBegin = true };
        var run = Controller(domain).StartAsync(request, prepared, 4); await prepared.Entered.Task.WaitAsync(Deadline);
        return (new(prepared), new(job), new(run));
    }

    [Theory]
    [InlineData("failed", -2147467259)]
    [InlineData("positive", 1)]
    [InlineData("missingHResult", null)]
    [InlineData("missingPacket", null)]
    [InlineData("missingJob", null)]
    [InlineData("foreignJob", null)]
    [InlineData("foreignOwner", null)]
    [InlineData("throw", null)]
    public async Task UncertainBeginRetainsExactOwnerWithoutObservationEndCleanupOrRetry(string failure, int? expectedHResult)
    {
        var request = Request(); var job = new Job([request.Target]); var prepared = new Prepared(request, job) { Failure = failure };
        var domain = new WuaInstallationStartDomain(); var controller = Controller(domain);
        var result = await controller.StartAsync(request, prepared, 4).WaitAsync(Deadline);
        Assert.Equal(WuaInstallationStartOutcome.BeginUncertain, result.Outcome); Assert.True(result.BeginAttempted);
        Assert.Equal(expectedHResult, result.BeginHResult); Assert.False(result.OwnershipReleased); Assert.Null(result.Monitor);
        Assert.Equal(CheckState.Unknown, result.InstalledState); Assert.True(domain.RequiresRetention); Assert.Empty(job.Calls);
        Assert.Equal(WuaInstallationStartOutcome.RefusedBeforeEntry, (await controller.StartAsync(request, prepared, 4).WaitAsync(Deadline)).Outcome);
        var nextRequest = Request(request.Target); var next = new Prepared(nextRequest, job);
        Assert.Equal(WuaInstallationStartOutcome.RefusedBeforeEntry, (await Controller(new()).StartAsync(nextRequest, next, 4).WaitAsync(Deadline)).Outcome);
        Assert.Equal(1, prepared.BeginCalls); Assert.Equal(0, next.BeginCalls); Assert.Empty(job.Calls);
        var competingRequest = Request(); var competingJob = new Job([competingRequest.Target]);
        var competing = new Prepared(competingRequest, competingJob);
        AssertRefused(await controller.StartAsync(competingRequest, competing, 4).WaitAsync(Deadline), competing, competingJob);
    }

    [Theory]
    [InlineData("completion", 0, 0, 0)]
    [InlineData("cleanup", 1, 1, 0)]
    [InlineData("release", 1, 1, 1)]
    public async Task ValidBeginDoesNotEraseUnknownCompletionCleanupOrFinalRelease(string failure, int ends, int cleanups, int releases)
    {
        var request = Request(); var job = new Job([request.Target]) { Failure = failure };
        var prepared = new Prepared(request, job) { CompleteInline = true }; var domain = new WuaInstallationStartDomain();
        var result = await Controller(domain).StartAsync(request, prepared, 4).WaitAsync(Deadline);
        Assert.Equal(WuaInstallationStartOutcome.MonitoringUncertain, result.Outcome); Assert.Equal(0, result.BeginHResult);
        Assert.True(result.BeginAttempted); Assert.NotNull(result.Monitor); Assert.False(result.OwnershipReleased);
        Assert.Equal(ends, job.EndCalls); Assert.Equal(cleanups, job.CleanupCalls); Assert.Equal(releases, job.ReleaseCalls);
        Assert.True(domain.RequiresRetention); Assert.Equal(CheckState.Unknown, result.InstalledState);
    }

    private static void AssertRefused(WuaInstallationStartResult result, Prepared prepared, Job job)
    {
        Assert.Equal(WuaInstallationStartOutcome.RefusedBeforeEntry, result.Outcome); Assert.False(result.BeginAttempted);
        Assert.False(result.OwnershipReleased); Assert.Null(result.BeginHResult); Assert.Null(result.Monitor);
        Assert.Equal(CheckState.Unknown, result.InstalledState); Assert.Equal(0, prepared.BeginCalls); Assert.Empty(job.Calls);
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Value = Now;
        internal Action<int>? Hook;
        private int calls;
        public override DateTimeOffset GetUtcNow() { Hook?.Invoke(Interlocked.Increment(ref calls)); return Value; }
    }
    private sealed class Prepared : WuaInstallationStartSession
    {
        private readonly Job job;
        internal Prepared(WuaInstallationStartRequest request, Job job) : base(request, job) => this.job = job;
        internal bool HoldBegin, CompleteInline;
        internal string? Failure;
        internal int BeginCalls, CallbackViolations;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WuaInstallationBeginResult?> returned = new();
        protected override Task<WuaInstallationBeginResult?> BeginInstallAsync()
        {
            if (callbackDepth != 0) Interlocked.Increment(ref CallbackViolations);
            Interlocked.Increment(ref BeginCalls);
            if (CompleteInline) job.SignalCompletion();
            Entered.TrySetResult();
            if (Failure == "throw") throw new InvalidOperationException("Controlled Begin uncertainty.");
            return HoldBegin ? returned.Task : Task.FromResult(Packet());
        }
        internal void FinishBegin()
        {
            callbackDepth++;
            try { returned.TrySetResult(Packet()); }
            finally { callbackDepth--; }
        }
        private WuaInstallationBeginResult? Packet() => Failure switch
        {
            "failed" => CreateBeginResult(-2147467259, job),
            "positive" => CreateBeginResult(1, job),
            "missingHResult" => CreateBeginResult(null, job),
            "missingPacket" => null,
            "missingJob" => CreateBeginResult(0, null),
            "foreignJob" => CreateBeginResult(0, new Job(job.Targets)),
            "foreignOwner" => new Prepared(Request, job).CreateBeginResult(73, job),
            _ => CreateBeginResult(0, job)
        };
    }
    private sealed class Job(IReadOnlyList<WuaInstallationTarget> targets) : WuaInstallationJobSession(targets)
    {
        internal string? Failure;
        internal int? ItemCode = 2, ItemHResult = 0;
        internal bool? ItemReboot = false;
        internal bool HoldCleanup, HoldRelease;
        internal int ObserveCalls, EndCalls, CleanupCalls, ReleaseCalls, CallbackViolations;
        internal readonly List<string> Calls = [];
        internal readonly TaskCompletionSource ObserveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), CleanupContinue = new(),
            ReleaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseContinue = new();
        private readonly TaskCompletionSource<WuaInstallationCompletion> completed = new();
        internal void SignalCompletion()
        {
            callbackDepth++;
            try
            {
                if (Failure == "completion") completed.TrySetException(new InvalidOperationException("Controlled completion uncertainty."));
                else completed.TrySetResult(CreateCompletion());
            }
            finally { callbackDepth--; }
        }
        private void Enter(string name)
        { if (callbackDepth != 0) Interlocked.Increment(ref CallbackViolations); Calls.Add(name); }
        protected override Task<WuaInstallationCompletion> ObserveCompletionAsync()
        { Enter("observe"); Interlocked.Increment(ref ObserveCalls); ObserveEntered.TrySetResult(); return completed.Task; }
        protected override Task<WuaInstallationAggregate> EndInstallAsync()
        { Enter("end"); Interlocked.Increment(ref EndCalls); return Task.FromResult(CreateAggregate(2, 0, false)); }
        protected override Task<WuaInstallationItemResult> ReadUpdateResultAsync(int index)
        { Enter("item"); return Task.FromResult(CreateItemResult(index, ItemCode, ItemHResult, ItemReboot)); }
        protected override async Task CleanUpAsync()
        {
            Enter("cleanup"); Interlocked.Increment(ref CleanupCalls); CleanupEntered.TrySetResult();
            if (Failure == "cleanup") throw new InvalidOperationException("Controlled cleanup uncertainty.");
            if (HoldCleanup) await CleanupContinue.Task;
        }
        protected override async Task<bool> ReleaseOwnershipAsync()
        {
            Enter("release"); Interlocked.Increment(ref ReleaseCalls); ReleaseEntered.TrySetResult();
            if (HoldRelease) await ReleaseContinue.Task;
            return Failure != "release";
        }
    }
}
