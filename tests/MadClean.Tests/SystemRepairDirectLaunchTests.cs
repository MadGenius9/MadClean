using System.Runtime.CompilerServices;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemRepairDirectLaunchTests
{
    private const long Generation = 3;
    private static SystemRepairRequest Request()
    {
        var request = Assert.IsType<SystemRepairRequest>(SystemRepairWorkflow.Prepare(SystemToolOperation.DismRestoreHealth, Generation, DateTimeOffset.UtcNow));
        Assert.Equal(SystemRepairConfirmation.Confirmed, request.Confirm(request.RequestId, request.Definition.Operation, Generation,
            true, true, true, true, DateTimeOffset.UtcNow));
        return request;
    }
    private static async Task<Fixture> Start(Api? api = null, SystemRepairDirectLaunchDomain? domain = null)
    {
        var fixture = new Fixture(api ?? new(), domain ?? new());
        fixture.Run = new SystemRepairWorkflow(fixture, new()).ExecuteAsync(Request(), Generation);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return fixture;
    }
    private static async Task<SystemRepairDirectLaunchResult> Finish(Fixture fixture)
    {
        var result = await fixture.Session!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Run!.WaitAsync(TimeSpan.FromSeconds(5));
        return result;
    }

    [Fact]
    public async Task ExactThreeInheritableChildEndsAndReadyParentDrainsPrecedeOneCreate()
    {
        var fixture = await Start(); var result = await Finish(fixture); var api = fixture.Api;
        Assert.Equal(SystemRepairDirectCreationState.Created, result.CreationState);
        Assert.True(result.CleanupConfirmed); Assert.False(result.RequiresRetention);
        Assert.Equal(CheckState.Unknown, result.RepairSucceeded); Assert.Equal(CheckState.Unknown, result.Integrity);
        Assert.Equal(new[] { SystemRepairDirectHandleRole.StdinChildRead, SystemRepairDirectHandleRole.StdoutChildWrite,
            SystemRepairDirectHandleRole.StderrChildWrite }, api.Listed.Select(handle => handle.Role));
        Assert.All(api.Listed, handle => Assert.True(handle.Inheritable));
        Assert.All(api.Handles.Where(handle => handle.Role is SystemRepairDirectHandleRole.StdinParentWrite or
            SystemRepairDirectHandleRole.StdoutParentRead or SystemRepairDirectHandleRole.StderrParentRead), handle => Assert.False(handle.Inheritable));
        Assert.True(api.Calls.IndexOf("drain:out") < api.Calls.IndexOf("create"));
        Assert.True(api.Calls.IndexOf("drain:err") < api.Calls.IndexOf("create"));
        Assert.True(api.Calls.IndexOf("close:StdinParentWrite") < api.Calls.IndexOf("create"));
        Assert.True(api.Calls.IndexOf("close:StdoutChildWrite") < api.Calls.IndexOf("drain-done:out"));
        Assert.True(api.Calls.IndexOf("close:StderrChildWrite") < api.Calls.IndexOf("drain-done:err"));
        Assert.True(api.Calls.IndexOf("delete-list") < api.Calls.IndexOf("free:HandleListValues"));
        Assert.True(api.Calls.IndexOf("delete-list") < api.Calls.IndexOf("free:AttributeList"));
        Assert.Equal(1, api.CreateCalls); Assert.Same(api.Process, api.Waited);
        Assert.All(api.Handles, handle => Assert.Equal(1, handle.CloseCalls));
        Assert.All(api.Allocations, allocation => Assert.Equal(1, allocation.FreeCalls));
    }

    [Theory]
    [InlineData(SystemRepairDirectPipe.StandardInput)]
    [InlineData(SystemRepairDirectPipe.StandardOutput)]
    [InlineData(SystemRepairDirectPipe.StandardError)]
    public async Task FailedPipeOutputsAreIndeterminateAndNeverAdoptedOrClosed(SystemRepairDirectPipe failed)
    {
        var fixture = await Start(new() { FailedPipe = failed }); var result = await Finish(fixture);
        Assert.Equal(SystemRepairDirectCreationState.NotAttempted, result.CreationState);
        Assert.False(result.CreateAttempted); Assert.True(result.CleanupConfirmed); Assert.Equal(0, fixture.Api.CreateCalls);
        Assert.All(fixture.Api.Poison, handle => Assert.Equal(0, handle.CloseCalls));
        Assert.All(fixture.Api.Handles, handle => Assert.Equal(1, handle.CloseCalls));
        var workflow = await fixture.Run!;
        Assert.Equal(SystemRepairWorkflowOutcome.NoClientCreated, workflow.Outcome); Assert.True(workflow.OwnershipReleased);
        Assert.Same(result, workflow.DirectCompletion); Assert.Null(workflow.ClientObservation);
    }

    [Fact]
    public async Task KnownCreateFailureIgnoresProcessInformationAndFinishesOnlyOwnedSetup()
    {
        var fixture = await Start(new() { CreateFalse = true }); var result = await Finish(fixture);
        Assert.Equal(SystemRepairDirectCreationState.KnownNotCreated, result.CreationState);
        Assert.True(result.CreateAttempted); Assert.Equal(2, result.NativeError); Assert.True(result.CleanupConfirmed);
        Assert.Null(fixture.Api.Waited); Assert.All(fixture.Api.Poison, handle => Assert.Equal(0, handle.CloseCalls));
        Assert.True(result.Stdout!.EndOfStreamObserved); Assert.True(result.Stderr!.EndOfStreamObserved);
        Assert.Equal(1, fixture.Api.DeleteCalls); Assert.All(fixture.Api.Allocations, item => Assert.Equal(1, item.FreeCalls));
    }

    [Fact]
    public async Task CreationExceptionRetainsUnknownProcessAndAllLivePipeAndAttributeOwners()
    {
        var fixture = await Start(new() { CreateThrows = true }); var result = await Finish(fixture);
        Assert.Equal(SystemRepairDirectCreationState.Uncertain, result.CreationState);
        Assert.True(result.RequiresRetention); Assert.False(result.CleanupConfirmed); Assert.True(fixture.Domain.RequiresRetention);
        var workflow = await fixture.Run!; Assert.False(workflow.DirectEvidenceConsistent);
        Assert.Equal(SystemRepairWorkflowOutcome.DispatchUncertain, workflow.Outcome); Assert.Same(result, workflow.DirectCompletion);
        Assert.Null(fixture.Api.Waited); Assert.Equal(0, fixture.Api.DeleteCalls);
        Assert.All(fixture.Api.Allocations, item => Assert.Equal(0, item.FreeCalls));
        Assert.All(fixture.Api.Handles.Where(handle => handle.Role != SystemRepairDirectHandleRole.StdinParentWrite), handle => Assert.Equal(0, handle.CloseCalls));
        Assert.True(fixture.Session!.HasPendingWork); Assert.False(await fixture.Session.ReleaseOwnershipAsync());
        fixture.Api.CompleteUnderlyingWorkers();
        Assert.True(fixture.Domain.RequiresRetention); Assert.False(await fixture.Session.ReleaseOwnershipAsync());
    }

    [Theory]
    [InlineData("size")] [InlineData("initialize")] [InlineData("update")]
    [InlineData("parent-flag")] [InlineData("child-flag")] [InlineData("create-state")]
    public async Task KnownPreparationRefusalCleansOnlyAcquiredResourcesWithoutClaimingThePermit(string refusal)
    {
        var fixture = await Start(new() { Refusal = refusal }); var result = await Finish(fixture);
        Assert.False(result.CreateAttempted); Assert.False(fixture.Permit!.WasClaimed);
        Assert.True(result.CleanupConfirmed); Assert.Equal(0, fixture.Api.CreateCalls);
        Assert.Equal(refusal is "update" or "create-state" ? 1 : 0, fixture.Api.DeleteCalls);
        Assert.All(fixture.Api.Handles, item => Assert.Equal(1, item.CloseCalls));
        int priorCalls = fixture.Api.Calls.Count;
        var repeat = new SystemRepairDirectLaunch(fixture.Api, fixture.Domain).Start(fixture.Permit, new Target(fixture.Permit.Context));
        Assert.Equal(SystemRepairDirectCreationState.NotAttempted, (await repeat.Completion).CreationState);
        Assert.Equal(priorCalls, fixture.Api.Calls.Count); // An admitted setup attempt is not retried.
    }

    [Fact]
    public async Task AttributeDeleteExceptionCannotFreeEitherBackingAllocationOrRetryDeletion()
    {
        var fixture = await Start(new() { DeleteThrows = true }); var result = await Finish(fixture);
        Assert.True(result.ExitObserved); Assert.True(result.RequiresRetention);
        Assert.Equal(1, fixture.Api.DeleteCalls); Assert.All(fixture.Api.Allocations, item => Assert.Equal(0, item.FreeCalls));
        Assert.False(await fixture.Session!.ReleaseOwnershipAsync()); Assert.False(await fixture.Session.ReleaseOwnershipAsync());
        Assert.Equal(1, fixture.Api.DeleteCalls);
    }

    [Theory]
    [InlineData(SystemRepairDirectAllocationRole.AttributeList)] [InlineData(SystemRepairDirectAllocationRole.HandleListValues)]
    [InlineData(SystemRepairDirectAllocationRole.CreateCallState)]
    public async Task FailedAllocationReleaseIsNeverRetriedOrTurnedIntoCleanup(SystemRepairDirectAllocationRole failed)
    {
        var fixture = await Start(new() { FailedFree = failed }); var result = await Finish(fixture);
        Assert.True(result.ExitObserved); Assert.False(result.CleanupConfirmed); Assert.True(result.RequiresRetention);
        Assert.All(fixture.Api.Allocations, item => Assert.Equal(1, item.FreeCalls));
        await fixture.Session!.ReleaseOwnershipAsync(); Assert.All(fixture.Api.Allocations, item => Assert.Equal(1, item.FreeCalls));
    }

    [Fact]
    public async Task FailedParentChildWriteCloseDoesNotWaitForUnreachableEofOrForceCloseReaders()
    {
        var fixture = await Start(new() { FailedClose = SystemRepairDirectHandleRole.StdoutChildWrite });
        var result = await Finish(fixture);
        Assert.True(result.RequiresRetention); Assert.False(result.CleanupConfirmed);
        Assert.True(fixture.Session!.HasPendingWork); Assert.Equal(0, fixture.Api.OutRead!.CloseCalls);
        Assert.Equal(1, fixture.Api.OutWrite!.CloseCalls); Assert.Equal(0, fixture.Api.DeleteCalls);
        fixture.Api.CompleteUnderlyingWorkers();
        Assert.False(await fixture.Session.ReleaseOwnershipAsync()); Assert.Equal(1, fixture.Api.OutWrite.CloseCalls);
    }

    [Fact]
    public async Task WorkerReleaseWithoutEofDoesNotQualifyOutputCleanup()
    {
        var fixture = await Start(new() { EndOfStream = false }); var result = await Finish(fixture);
        Assert.True(result.ExitObserved); Assert.True(result.Stdout!.WorkerReleased); Assert.False(result.Stdout.EndOfStreamObserved);
        Assert.False(result.CleanupConfirmed); Assert.True(result.RequiresRetention); Assert.False(await fixture.Session!.ReleaseOwnershipAsync());
        Assert.Equal(0, fixture.Api.OutRead!.CloseCalls); Assert.Equal(0, fixture.Api.Process!.CloseCalls);
        Assert.All(fixture.Api.Allocations, item => Assert.Equal(0, item.FreeCalls));
    }

    [Fact]
    public async Task EofWithoutWorkerReleaseCannotCloseTheReadersOrFreeBuffers()
    {
        var fixture = await Start(new() { WorkerReleased = false }); var result = await Finish(fixture);
        Assert.True(result.Stdout!.EndOfStreamObserved); Assert.False(result.Stdout.WorkerReleased);
        Assert.True(result.RequiresRetention); Assert.False(result.CleanupConfirmed);
        Assert.Equal(0, fixture.Api.OutRead!.CloseCalls); Assert.Equal(0, fixture.Api.Process!.CloseCalls);
        Assert.All(fixture.Api.Allocations, item => Assert.Equal(0, item.FreeCalls));
    }

    [Fact]
    public async Task UnreadyDrainPreventsCreationAndKeepsReadHandlesUntilActualWorkerSettlement()
    {
        var api = new Api { DrainReady = false, HoldDrains = true }; var fixture = await Start(api);
        await fixture.Session!.Creation.WaitAsync(TimeSpan.FromSeconds(5));
        await api.ChildCopiesClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Session.Completion.IsCompleted); Assert.Equal(0, api.CreateCalls);
        Assert.Equal(0, api.OutRead!.CloseCalls); Assert.True(api.OutWrite!.Closed);
        api.CompleteDrains(); var result = await Finish(fixture);
        Assert.False(result.CreateAttempted); Assert.True(result.CleanupConfirmed); Assert.Equal(1, api.OutRead.CloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReadyReaderThatAlreadyTerminatedCannotClaimThePermitOrCreateAClient(bool eof)
    {
        var fixture = await Start(new() { PrematureOutputCompletion = eof }); var result = await Finish(fixture);
        Assert.False(result.CreateAttempted); Assert.False(fixture.Permit!.WasClaimed); Assert.Equal(0, fixture.Api.CreateCalls);
        Assert.True(result.RequiresRetention); Assert.False(result.CleanupConfirmed);
        Assert.Equal(eof, result.Stdout!.EndOfStreamObserved); Assert.True(result.Stdout.WorkerReleased);
        Assert.Equal(0, fixture.Api.OutRead!.CloseCalls); Assert.Equal(0, fixture.Api.DeleteCalls);
        Assert.All(fixture.Api.Allocations, allocation => Assert.Equal(0, allocation.FreeCalls));
        fixture.Api.CompleteUnderlyingWorkers(); Assert.False(await fixture.Session!.ReleaseOwnershipAsync());
    }

    [Fact]
    public async Task CompetingAttemptCannotDisplaceAPendingOriginalOwner()
    {
        var domain = new SystemRepairDirectLaunchDomain(); var first = await Start(new() { PendingWait = true }, domain);
        await first.Api.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await Start(new(), domain); var refused = await Finish(second);
        Assert.Equal(SystemRepairDirectCreationState.NotAttempted, refused.CreationState);
        Assert.Empty(second.Api.Calls); Assert.True(domain.HasPendingWork);
        first.Api.WaitCompletion.SetResult(new(true, 0)); Assert.True((await Finish(first)).CleanupConfirmed);
    }

    [Theory]
    [InlineData(259)] [InlineData(unchecked((int)0xC0000135))]
    public async Task ActualProcessSignalKeepsLiteralExitIncludingStillActiveNumberAndInitializationFailure(int exit)
    {
        var fixture = await Start(new() { ExitCode = exit }); var result = await Finish(fixture);
        Assert.Equal(SystemRepairDirectCreationState.Created, result.CreationState);
        Assert.True(result.ExitObserved); Assert.Equal(exit, result.ExitCode); Assert.True(result.CleanupConfirmed);
        Assert.Equal(CheckState.Unknown, result.RepairSucceeded); Assert.Equal(CheckState.Unknown, result.Integrity);
    }

    [Fact]
    public async Task ProcessWaitMustUseTheActualReturnedHandleAndFailureRetainsIt()
    {
        var fixture = await Start(new() { WaitFailure = true }); var result = await Finish(fixture);
        Assert.Same(fixture.Api.Process, fixture.Api.Waited); Assert.False(result.ExitObserved);
        Assert.Null(result.ExitCode); Assert.Equal(6, result.NativeError); Assert.True(result.RequiresRetention);
        Assert.Equal(0, fixture.Api.Process!.CloseCalls); Assert.Equal(0, fixture.Api.OutRead!.CloseCalls);
        fixture.Api.CompleteUnderlyingWorkers();
    }

    [Fact]
    public async Task ExitCodeReadFailureKeepsActualWaitAndItsErrorWithoutInventingACode()
    {
        var fixture = await Start(new() { MissingExitCode = true }); var result = await Finish(fixture);
        Assert.True(result.ExitObserved); Assert.Null(result.ExitCode); Assert.Equal(5, result.NativeError);
        Assert.Equal(SystemRepairDirectCreationState.Created, result.CreationState); Assert.True(result.CleanupConfirmed);
        Assert.Equal(CheckState.Unknown, result.RepairSucceeded);
    }

    [Fact]
    public async Task PendingWaitAndDrainsSurviveCallerDepartureAndCloseOnlyAfterTheirRealCompletion()
    {
        var api = new Api { PendingWait = true, HoldDrains = true }; var fixture = await Start(api);
        await api.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Session!.Completion.IsCompleted); Assert.Equal(0, api.Process!.CloseCalls);
        api.WaitCompletion.SetResult(new(true, 0));
        await api.WaitReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Session.Completion.IsCompleted); Assert.Equal(0, api.OutRead!.CloseCalls);
        api.CompleteDrains(); var result = await Finish(fixture);
        Assert.True(result.CleanupConfirmed); Assert.Equal(1, api.Process.CloseCalls); Assert.Equal(1, api.OutRead.CloseCalls);
    }

    [Fact]
    public async Task BadRoleOrRepeatedReferenceCannotBeAdoptedAndClosedTwice()
    {
        var fixture = await Start(new() { RepeatPipeReference = true }); var result = await Finish(fixture);
        Assert.False(result.CreateAttempted); Assert.True(result.RequiresRetention);
        Assert.All(fixture.Api.Handles, item => Assert.Equal(0, item.CloseCalls));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(65_537, false)] [InlineData(128, true)]
    public async Task InvalidOrUncertainSizingNeverAllocatesOrAttemptsCreation(int size, bool uncertain)
    {
        var api = new Api { AttributeSize = (nuint)size, UncertainSize = uncertain }; var fixture = await Start(api);
        var result = await Finish(fixture);
        Assert.False(result.CreateAttempted); Assert.Empty(api.Allocations); Assert.Equal(0, api.CreateCalls);
        Assert.Equal(uncertain, result.RequiresRetention); Assert.Equal(!uncertain, result.CleanupConfirmed);
        Assert.All(api.Handles, handle => Assert.Equal(uncertain ? 0 : 1, handle.CloseCalls));
    }

    [Fact]
    public async Task ForeignAllocationContextIsNeverAdoptedFreedOrUsedForProcessCreation()
    {
        var warm = await Start(new() { Refusal = "size" }); await Finish(warm);
        var api = new Api { ForeignAllocationContext = warm.Permit!.Context }; var fixture = await Start(api);
        var result = await Finish(fixture); var foreign = Assert.Single(api.Allocations);
        Assert.Same(warm.Permit.Context, foreign.Context); Assert.Equal(0, foreign.FreeCalls);
        Assert.True(result.RequiresRetention); Assert.False(result.CreateAttempted); Assert.Equal(0, api.DeleteCalls);
        Assert.All(api.Handles, handle => Assert.Equal(0, handle.CloseCalls));
    }

    [Fact]
    public async Task ProcessRootedDomainRetainsAbandonedOwnerApiTargetAndActualWaitTask()
    {
        var domain = new SystemRepairDirectLaunchDomain(); var weak = await Abandon(domain);
        for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.True(weak.Api.TryGetTarget(out var api)); Assert.True(weak.Session.TryGetTarget(out var session));
        Assert.True(weak.Run.TryGetTarget(out var run)); Assert.True(domain.HasPendingWork);
        api!.WaitCompletion.SetResult(new(true, 0));
        Assert.True((await session!.Completion.WaitAsync(TimeSpan.FromSeconds(5))).CleanupConfirmed);
        await run!.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(domain.HasPendingWork);
        GC.KeepAlive(domain);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<Api> Api, WeakReference<SystemRepairDirectLaunchSession> Session,
        WeakReference<Task<SystemRepairWorkflowResult>> Run)> Abandon(SystemRepairDirectLaunchDomain domain)
    {
        var fixture = await Start(new() { PendingWait = true }, domain);
        await fixture.Api.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (new(fixture.Api), new(fixture.Session!), new(fixture.Run!));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task SharedServicingReleasesActualNoChildOrIncompleteExitEvidenceButKeepsHistoryBlocked(bool createFalse, bool missingCode, bool missingPid)
    {
        var api = new Api { CreateFalse = createFalse, MissingExitCode = missingCode, MissingProcessId = missingPid };
        var platform = new CompositionPlatform(api); var gate = new SystemToolAdmissionGate();
        var backend = new SystemRepairServicingBackend(platform, gate);
        var result = await new SystemRepairWorkflow(backend, new()).ExecuteAsync(Request(), Generation).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(createFalse ? SystemRepairWorkflowOutcome.NoClientCreated : SystemRepairWorkflowOutcome.ObservationLost, result.Outcome);
        Assert.True(result.OwnershipReleased); Assert.True(gate.IsHistoryBlocked);
        Assert.False(backend.RequiresRetention); Assert.False(backend.HasPendingWork); Assert.False(gate.HasPendingWork);
        Assert.Equal(1, platform.Prepared!.ReleaseCalls); Assert.Equal(new[] { "journal", "prepared" }, platform.Releases);
        Assert.Null(result.ClientObservation); Assert.True(result.DirectEvidenceConsistent); Assert.NotNull(result.DirectCreation); Assert.NotNull(result.DirectCompletion);
        Assert.Equal(createFalse ? SystemRepairDirectCreationState.KnownNotCreated : SystemRepairDirectCreationState.Created, result.DirectCreation.State);
        Assert.Equal<bool?>(createFalse ? null : true, result.DirectCompletion.ExitObserved); Assert.Equal(missingPid ? 0 : (int?)null, result.DirectCompletion.ExitCode);
        if (missingPid) Assert.Null(result.DirectCompletion.ProcessId);
        Assert.Null(gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid()));
    }

    [Fact]
    public async Task SharedServicingKeepsPendingDirectDrainsAndBlocksCompetingNonRepairUntilTheySettle()
    {
        var api = new Api { HoldDrains = true }; var platform = new CompositionPlatform(api);
        var gate = new SystemToolAdmissionGate(); var backend = new SystemRepairServicingBackend(platform, gate);
        using var stop = new CancellationTokenSource();
        var run = new SystemRepairWorkflow(backend, new()).ExecuteAsync(Request(), Generation, stop.Token);
        await api.ChildCopiesClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(backend.HasPendingWork); Assert.True(gate.HasPendingWork); Assert.False(run.IsCompleted);
        Assert.Null(gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid())); Assert.Empty(platform.Releases);
        stop.Cancel(); Assert.False(run.IsCompleted); Assert.Empty(platform.Releases);
        api.CompleteDrains(); var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome); Assert.True(result.OwnershipReleased); Assert.True(result.StopRequested);
        Assert.False(gate.IsHistoryBlocked); Assert.False(backend.HasPendingWork); Assert.False(backend.RequiresRetention);
        var next = gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid()); Assert.NotNull(next); next.Complete(true);
    }

    [Fact]
    public async Task SharedServicingRetainsUnsettledDirectCleanupAndNeverReleasesPreparedResources()
    {
        var api = new Api { EndOfStream = false }; var platform = new CompositionPlatform(api);
        var gate = new SystemToolAdmissionGate(); var backend = new SystemRepairServicingBackend(platform, gate);
        var result = await new SystemRepairWorkflow(backend, new()).ExecuteAsync(Request(), Generation).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.OwnershipReleased); Assert.True(backend.RequiresRetention); Assert.True(gate.RequiresRetention);
        Assert.Empty(platform.Releases); Assert.Equal(0, platform.Prepared!.ReleaseCalls);
        Assert.False(result.DirectCompletion!.CleanupConfirmed); Assert.Null(gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid()));
    }

    [Fact]
    public async Task ARefusedReplaySessionCannotBeWrappedAsAnAdmittedNoChildOutcome()
    {
        var fixture = await Start(new() { PendingWait = true });
        await fixture.Api.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replay = new SystemRepairDirectLaunch(fixture.Api, fixture.Domain).Start(fixture.Permit!, new Target(fixture.Permit!.Context));
        Assert.Throws<ArgumentException>(() => SystemRepairDirectDispatchSession.Create(fixture.Permit!, replay));
        fixture.Api.CompleteUnderlyingWorkers(); await Finish(fixture);
    }

    private sealed class CompositionPlatform(Api api) : ISystemRepairExecutionPlatform
    {
        public readonly Api Api = api; public readonly List<string> Releases = []; public PreparedSession? Prepared;
        public bool ProductionExecutionValidated => true; // Controlled mock only.
        public Task<SystemRepairPreparedSession> PrepareAsync(SystemRepairExecutionContext context, CancellationToken token)
        { Prepared = new(this, Api, context); return Task.FromResult<SystemRepairPreparedSession>(Prepared); }
        public sealed class PreparedSession : SystemRepairPreparedSession
        {
            private readonly CompositionPlatform platform; private readonly Api api; private readonly SystemRepairDirectLaunchDomain domain = new();
            public int ReleaseCalls;
            public PreparedSession(CompositionPlatform platform, Api api, SystemRepairExecutionContext context) : base(context)
            { this.platform = platform; this.api = api; Journal = new CompositionJournal(platform, context); }
            public override SystemRepairJournalSession Journal { get; }
            public override Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit) =>
                Task.FromResult<SystemRepairDispatchSession>(SystemRepairDirectDispatchSession.Create(permit,
                    new SystemRepairDirectLaunch(api, domain).Start(permit, new Target(Context))));
            public override Task<bool> ReleaseOwnershipAsync()
            { Assert.All(api.Allocations, allocation => Assert.True(allocation.Freed)); ReleaseCalls++; platform.Releases.Add("prepared"); return Task.FromResult(true); }
        }
        private sealed class CompositionJournal(CompositionPlatform platform, SystemRepairExecutionContext context) : SystemRepairJournalSession(context)
        {
            protected override Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry) => Task.FromResult(SystemRepairJournalWriteOutcome.DurableAcknowledged);
            protected override Task<bool> ReleaseOwnershipAsync()
            { Assert.All(platform.Api.Allocations, allocation => Assert.True(allocation.Freed)); platform.Releases.Add("journal"); return Task.FromResult(true); }
        }
    }

    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Target(SystemRepairExecutionContext context) : SystemRepairDirectLaunchTarget(context);
    private sealed class Handle(SystemRepairExecutionContext context, SystemRepairDirectHandleRole role) : SystemRepairDirectHandle(context, role)
    { public bool Inheritable, Closed; public int CloseCalls; }
    private sealed class Allocation(SystemRepairExecutionContext context, SystemRepairDirectAllocationRole role, nuint bytes)
        : SystemRepairDirectAllocation(context, role, bytes)
    { public bool Initialized, Deleted, Freed; public int FreeCalls; }
    private sealed class Drain(SystemRepairExecutionContext context, Handle read, bool ready, Task<SystemRepairDirectDrainResult> completion)
        : SystemRepairDirectDrain(context, read, ready, completion);

    private sealed class Fixture(Api api, SystemRepairDirectLaunchDomain domain) : ISystemRepairWorkflowBackend
    {
        public readonly Api Api = api; public readonly SystemRepairDirectLaunchDomain Domain = domain;
        public Task<SystemRepairWorkflowResult>? Run; public SystemRepairDirectLaunchSession? Session; public SystemRepairDispatchPermit? Permit;
        public readonly TaskCompletionSource Entered = Signal();
        public bool ProductionExecutionValidated => true;
        public Task<SystemRepairJournalSession> OpenJournalAsync(SystemRepairExecutionContext context, CancellationToken token) =>
            Task.FromResult<SystemRepairJournalSession>(new Journal(context));
        public Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit)
        {
            Permit = permit;
            Session = new SystemRepairDirectLaunch(Api, Domain).Start(permit, new Target(permit.Context));
            Entered.TrySetResult();
            return Task.FromResult<SystemRepairDispatchSession>(SystemRepairDirectDispatchSession.Create(permit, Session));
        }
        private sealed class Journal(SystemRepairExecutionContext context) : SystemRepairJournalSession(context)
        {
            protected override Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry) => Task.FromResult(SystemRepairJournalWriteOutcome.DurableAcknowledged);
            protected override Task<bool> ReleaseOwnershipAsync() => Task.FromResult(true);
        }
    }

    private sealed class Api : ISystemRepairDirectLaunchApi
    {
        private readonly object sync = new();
        public SystemRepairDirectPipe? FailedPipe; public SystemRepairDirectHandleRole? FailedClose;
        public SystemRepairDirectAllocationRole? FailedFree; public string? Refusal;
        public SystemRepairExecutionContext? ForeignAllocationContext;
        public bool? PrematureOutputCompletion;
        public nuint AttributeSize = 128; public bool UncertainSize;
        public bool CreateFalse, CreateThrows, DeleteThrows, WaitFailure, MissingExitCode, MissingProcessId, PendingWait, HoldDrains, RepeatPipeReference,
            EndOfStream = true, WorkerReleased = true, DrainReady = true;
        public int ExitCode, CreateCalls, DeleteCalls; private bool childRunning;
        public readonly List<string> Calls = []; public readonly List<Handle> Handles = [], Poison = [];
        public readonly List<Allocation> Allocations = []; public Handle[] Listed = [];
        public IReadOnlyList<SystemRepairDirectHandle>? SubmittedList;
        public Handle? Process, Waited, OutRead, OutWrite, ErrRead, ErrWrite;
        public Allocation? Attributes, Values, CreateState;
        private readonly TaskCompletionSource<SystemRepairDirectDrainResult> output = Source<SystemRepairDirectDrainResult>(), errors = Source<SystemRepairDirectDrainResult>();
        public readonly TaskCompletionSource<SystemRepairDirectWaitResult> WaitCompletion = Source<SystemRepairDirectWaitResult>();
        public readonly TaskCompletionSource WaitEntered = Signal(), WaitReturned = Signal(), ChildCopiesClosed = Signal();
        public SystemRepairDirectPipeResult CreatePipe(SystemRepairExecutionContext context, SystemRepairDirectPipe pipe)
        {
            Calls.Add("pipe:" + pipe);
            var read = new Handle(context, pipe switch { SystemRepairDirectPipe.StandardInput => SystemRepairDirectHandleRole.StdinChildRead,
                SystemRepairDirectPipe.StandardOutput => SystemRepairDirectHandleRole.StdoutParentRead, _ => SystemRepairDirectHandleRole.StderrParentRead });
            var write = new Handle(context, pipe switch { SystemRepairDirectPipe.StandardInput => SystemRepairDirectHandleRole.StdinParentWrite,
                SystemRepairDirectPipe.StandardOutput => SystemRepairDirectHandleRole.StdoutChildWrite, _ => SystemRepairDirectHandleRole.StderrChildWrite });
            if (FailedPipe == pipe) { Poison.Add(read); Poison.Add(write); return new(false, read, write, 5); }
            if (RepeatPipeReference && pipe == SystemRepairDirectPipe.StandardError) return new(true, Handles[0], write);
            Handles.Add(read); Handles.Add(write);
            if (pipe == SystemRepairDirectPipe.StandardOutput) { OutRead = read; OutWrite = write; }
            if (pipe == SystemRepairDirectPipe.StandardError) { ErrRead = read; ErrWrite = write; }
            return new(true, read, write);
        }
        public SystemRepairDirectOperationResult MakeParentNonInheritable(SystemRepairDirectHandle handle)
        { Calls.Add("parent-flag:" + handle.Role); ((Handle)handle).Inheritable = false; return new(Refusal != "parent-flag", 5); }
        public SystemRepairDirectOperationResult MakeChildInheritable(SystemRepairDirectHandle handle)
        { Calls.Add("child-flag:" + handle.Role); ((Handle)handle).Inheritable = true; return new(Refusal != "child-flag", 5); }
        public SystemRepairDirectSizeResult QueryAttributeSize(SystemRepairExecutionContext context)
        { Calls.Add("size-probe"); return new(UncertainSize ? SystemRepairDirectSizeState.Uncertain : Refusal == "size" ? SystemRepairDirectSizeState.Failed : SystemRepairDirectSizeState.RequiredSize, AttributeSize, 122); }
        public SystemRepairDirectAllocation AllocateAttributes(SystemRepairExecutionContext context, nuint count)
        { Calls.Add("allocate-list"); Attributes = new(ForeignAllocationContext ?? context, SystemRepairDirectAllocationRole.AttributeList, count); Allocations.Add(Attributes); return Attributes; }
        public SystemRepairDirectAllocation AllocateHandleValues(SystemRepairExecutionContext context, IReadOnlyList<SystemRepairDirectHandle> children)
        {
            Calls.Add("allocate-values"); SubmittedList = children; Listed = children.Cast<Handle>().ToArray();
            Assert.Throws<NotSupportedException>(() => ((IList<SystemRepairDirectHandle>)children).Clear());
            Values = new(context, SystemRepairDirectAllocationRole.HandleListValues, 32); Allocations.Add(Values); return Values;
        }
        public SystemRepairDirectOperationResult InitializeAttributes(SystemRepairDirectAllocation attributes)
        { Calls.Add("initialize-list"); ((Allocation)attributes).Initialized = Refusal != "initialize"; return new(Refusal != "initialize", 87); }
        public SystemRepairDirectOperationResult UpdateHandleList(SystemRepairDirectAllocation attributes, SystemRepairDirectAllocation values,
            IReadOnlyList<SystemRepairDirectHandle> children)
        { Calls.Add("update-list"); Assert.Same(SubmittedList, children); Assert.True(((Allocation)attributes).Initialized); Assert.All(children.Cast<Handle>(), item => Assert.True(item.Inheritable)); return new(Refusal != "update", 87); }
        public SystemRepairDirectDrain StartDiscardDrain(SystemRepairDirectHandle parentRead)
        {
            var read = (Handle)parentRead; bool isOutput = read.Role == SystemRepairDirectHandleRole.StdoutParentRead;
            Calls.Add("drain:" + (isOutput ? "out" : "err")); Assert.False(read.Inheritable);
            if (isOutput && PrematureOutputCompletion is { } eof) output.TrySetResult(new(eof, true, eof ? null : 5));
            return new Drain(read.Context, read, DrainReady, isOutput ? output.Task : errors.Task);
        }
        public SystemRepairDirectAllocation? AllocateCreateState(SystemRepairExecutionContext context, SystemRepairDirectLaunchTarget target)
        {
            Calls.Add("allocate-create-state"); Assert.Same(context, target.Context);
            if (Refusal == "create-state") return null;
            CreateState = new(context, SystemRepairDirectAllocationRole.CreateCallState, 256); Allocations.Add(CreateState); return CreateState;
        }
        public SystemRepairDirectCreateResult CreateProcess(SystemRepairDirectLaunchTarget target, SystemRepairDirectAllocation attributes,
            SystemRepairDirectAllocation createState, IReadOnlyList<SystemRepairDirectHandle> children)
        {
            CreateCalls++; Calls.Add("create");
            Assert.Same(SubmittedList, children); Assert.Equal(Listed, children); Assert.True(Handles.Single(item => item.Role == SystemRepairDirectHandleRole.StdinParentWrite).Closed);
            Assert.Contains("drain:out", Calls); Assert.Contains("drain:err", Calls);
            Assert.False(Values!.Freed); Assert.False(Attributes!.Freed); Assert.False(Attributes.Deleted);
            Assert.Same(CreateState, createState); Assert.False(CreateState!.Freed);
            if (CreateThrows) { childRunning = true; throw new InvalidOperationException("Unknown postcreation outcome"); }
            var process = new Handle(target.Context, SystemRepairDirectHandleRole.Process); var thread = new Handle(target.Context, SystemRepairDirectHandleRole.PrimaryThread);
            if (CreateFalse) { Poison.Add(process); Poison.Add(thread); return new(false, process, thread, 13, 2); }
            Handles.Add(process); Handles.Add(thread); Process = process; childRunning = true;
            return new(true, process, thread, MissingProcessId ? null : 313);
        }
        public async Task<SystemRepairDirectWaitResult> WaitForExitAsync(SystemRepairDirectHandle process)
        {
            lock (sync) { Waited = (Handle)process; Assert.Same(Process, Waited); Calls.Add("wait"); }
            WaitEntered.TrySetResult();
            var result = PendingWait ? await WaitCompletion.Task : new SystemRepairDirectWaitResult(!WaitFailure,
                WaitFailure || MissingExitCode ? null : ExitCode, WaitFailure ? 6 : MissingExitCode ? 5 : null);
            if (result.ExitObserved) { lock (sync) childRunning = false; MaybeCompleteDrains(); }
            WaitReturned.TrySetResult(); return result;
        }
        public SystemRepairDirectOperationResult Close(SystemRepairDirectHandle value)
        {
            lock (sync)
            {
                var handle = (Handle)value; handle.CloseCalls++; Calls.Add("close:" + handle.Role);
                Assert.DoesNotContain(handle, Poison); Assert.Equal(1, handle.CloseCalls);
                if (FailedClose == handle.Role) return new(false, 5);
                handle.Closed = true; MaybeCompleteDrains(); return new(true);
            }
        }
        public void DeleteAttributes(SystemRepairDirectAllocation attributes)
        {
            DeleteCalls++; Calls.Add("delete-list"); Assert.False(Values?.Freed ?? false); Assert.False(Attributes!.Freed);
            if (DeleteThrows) throw new InvalidOperationException("Deletion completion unknown");
            ((Allocation)attributes).Deleted = true;
        }
        public SystemRepairDirectOperationResult Free(SystemRepairDirectAllocation value)
        {
            var allocation = (Allocation)value; allocation.FreeCalls++; Calls.Add("free:" + allocation.Role); Assert.Equal(1, allocation.FreeCalls);
            if (Attributes!.Initialized) Assert.True(Attributes.Deleted);
            if (FailedFree == allocation.Role) return new(false, 5);
            allocation.Freed = true; return new(true);
        }
        private void MaybeCompleteDrains()
        {
            lock (sync)
            {
                if (OutWrite?.Closed == true && ErrWrite?.Closed == true) ChildCopiesClosed.TrySetResult();
                if (HoldDrains || childRunning) return;
                if (OutWrite?.Closed == true && !output.Task.IsCompleted)
                { Calls.Add("drain-done:out"); output.TrySetResult(new(EndOfStream, WorkerReleased)); }
                if (ErrWrite?.Closed == true && !errors.Task.IsCompleted)
                { Calls.Add("drain-done:err"); errors.TrySetResult(new(EndOfStream, WorkerReleased)); }
            }
        }
        public void CompleteDrains()
        {
            lock (sync)
            {
                if (!output.Task.IsCompleted) { Calls.Add("drain-done:out"); output.TrySetResult(new(EndOfStream, WorkerReleased)); }
                if (!errors.Task.IsCompleted) { Calls.Add("drain-done:err"); errors.TrySetResult(new(EndOfStream, WorkerReleased)); }
            }
        }
        public void CompleteUnderlyingWorkers()
        { WaitCompletion.TrySetResult(new(true, ExitCode)); CompleteDrains(); }
    }
}
