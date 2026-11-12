using MadClean.Core;
using System.Runtime.CompilerServices;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemRepairWorkflowTests
{
    private const long Generation = 7;
    private static SystemRepairRequest Request(SystemToolOperation operation = SystemToolOperation.DismRestoreHealth,
        DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        var request = Assert.IsType<SystemRepairRequest>(SystemRepairWorkflow.Prepare(operation, Generation, time));
        Assert.Equal(SystemRepairConfirmation.Confirmed, Confirm(request, time));
        return request;
    }
    private static SystemRepairConfirmation Confirm(SystemRepairRequest request, DateTimeOffset? now = null) =>
        request.Confirm(request.RequestId, request.Definition.Operation, Generation, true, true, true, true, now ?? DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(SystemToolOperation.DismCheckHealth)] [InlineData(SystemToolOperation.DismScanHealth)]
    [InlineData(SystemToolOperation.SfcVerifyOnly)] [InlineData((SystemToolOperation)999)]
    public void OnlyTwoFixedRepairOperationsCanEnterThisProtocol(SystemToolOperation operation) =>
        Assert.Null(SystemRepairWorkflow.Prepare(operation, Generation, DateTimeOffset.UtcNow));

    [Fact]
    public async Task ConfirmationIsExactAcknowledgedAndAtomicRatherThanReusableConsent()
    {
        var now = DateTimeOffset.UtcNow;
        var request = Assert.IsType<SystemRepairRequest>(SystemRepairWorkflow.Prepare(SystemToolOperation.SfcScanNow, Generation, now));
        Assert.Equal(SystemRepairConfirmation.MissingAcknowledgment,
            request.Confirm(request.RequestId, request.Definition.Operation, Generation, true, true, false, true, now));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Confirm(request))));
        Assert.Single(results, outcome => outcome == SystemRepairConfirmation.Confirmed);
        Assert.Equal(11, results.Count(outcome => outcome == SystemRepairConfirmation.AlreadyConfirmed));
        var wrong = Assert.IsType<SystemRepairRequest>(SystemRepairWorkflow.Prepare(SystemToolOperation.SfcScanNow, Generation, now));
        Assert.Equal(SystemRepairConfirmation.Invalidated, wrong.Confirm(Guid.NewGuid(), wrong.Definition.Operation, Generation, true, true, true, true, now));
        Assert.Equal(SystemRepairConfirmation.Invalidated, Confirm(wrong));
    }

    [Fact]
    public async Task ClosedBackendGateHasNoJournalDispatchOrAdmissionSideEffects()
    {
        var backend = new Backend { Enabled = false };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.Blocked, result.Outcome);
        Assert.Equal(0, backend.OpenCalls); Assert.Equal(0, backend.DispatchCalls);
        Assert.False(admission.HasPendingWork); Assert.False(admission.IsBlocked);
        Assert.True(result.OwnershipReleased); Assert.False(result.DispatchAttempted);
    }

    [Fact]
    public async Task PreCancelledOrExpiredOrChangedGenerationRequestsCannotBeReplayed()
    {
        foreach (int scenario in Enumerable.Range(0, 3))
        {
            var backend = new Backend(); var admission = new SystemRepairAdmission();
            var workflow = new SystemRepairWorkflow(backend, admission);
            var request = Request(now: scenario == 1 ? DateTimeOffset.UtcNow.AddMinutes(-3) : null);
            using var cancellation = new CancellationTokenSource();
            if (scenario == 0) cancellation.Cancel();
            var result = await workflow.ExecuteAsync(request, scenario == 2 ? Generation + 1 : Generation, cancellation.Token);
            Assert.False(result.DispatchAttempted); Assert.Equal(0, backend.OpenCalls);
            var replay = await workflow.ExecuteAsync(request, Generation);
            Assert.Equal(SystemRepairWorkflowOutcome.Blocked, replay.Outcome); Assert.Equal(0, backend.OpenCalls);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelOrInvalidateDuringDurableAppendWaitsForItAndSuppressesDispatch(bool invalidate)
    {
        var entered = Signal(); var acknowledge = Source<SystemRepairJournalWriteOutcome>();
        var backend = new Backend { Persist = entry => entry.Sequence == 1 ? PendingIntent() : Task.FromResult(SystemRepairJournalWriteOutcome.DurableAcknowledged) };
        async Task<SystemRepairJournalWriteOutcome> PendingIntent() { entered.TrySetResult(); return await acknowledge.Task; }
        var admission = new SystemRepairAdmission(); var request = Request();
        using var cancellation = new CancellationTokenSource();
        var run = new SystemRepairWorkflow(backend, admission).ExecuteAsync(request, Generation, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (invalidate) Assert.True(request.Invalidate()); else cancellation.Cancel();
        Assert.False(run.IsCompleted); Assert.True(admission.HasPendingWork); Assert.Equal(0, backend.DispatchCalls);
        acknowledge.SetResult(SystemRepairJournalWriteOutcome.DurableAcknowledged);
        var result = await run;
        Assert.False(result.DispatchAttempted); Assert.Equal(0, backend.DispatchCalls);
        Assert.Equal(SystemToolJournalState.Unresolved, result.JournalState);
        Assert.True(result.OwnershipReleased); Assert.True(admission.IsBlocked); Assert.False(admission.RequiresRetention);
        Assert.Equal(new[] { SystemToolJournalPhase.DispatchIntent, SystemToolJournalPhase.ObservationLost }, backend.Journal!.Persisted.Select(entry => entry.Phase));
        Assert.All(backend.Journal.Persisted, entry => Assert.Null(entry.NativeError));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ClockExpiryOrBackwardJumpAfterIntentCannotAuthorizeDispatch(bool expiry)
    {
        var initial = DateTimeOffset.UtcNow.AddMinutes(-10); var clock = new Clock(initial);
        var backend = new Backend
        {
            Persist = entry =>
            {
                if (entry.Sequence == 1) clock.Now = expiry ? initial.AddMinutes(3) : initial.AddSeconds(-1);
                return Task.FromResult(SystemRepairJournalWriteOutcome.DurableAcknowledged);
            }
        };
        var result = await new SystemRepairWorkflow(backend, new(), clock).ExecuteAsync(Request(now: expiry ? initial : initial.AddSeconds(-5)), Generation);
        Assert.False(result.DispatchAttempted); Assert.Equal(0, backend.DispatchCalls);
        Assert.NotEqual(SystemToolJournalState.RecordedClientExit, result.JournalState);
    }

    [Theory]
    [InlineData(SystemRepairJournalWriteOutcome.RefusedBeforeWrite, false)]
    [InlineData(SystemRepairJournalWriteOutcome.Uncertain, true)]
    [InlineData((SystemRepairJournalWriteOutcome)999, true)]
    public async Task UnacknowledgedIntentNeverDispatchesAndUncertainBytesLatchHistory(SystemRepairJournalWriteOutcome write, bool blocked)
    {
        var backend = new Backend { Persist = _ => Task.FromResult(write) }; var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.Equal(0, backend.DispatchCalls); Assert.Equal(1, backend.Journal!.WriteCalls);
        Assert.False(result.DispatchAttempted); Assert.Equal(blocked, admission.IsBlocked);
        Assert.True(result.OwnershipReleased); Assert.False(admission.RequiresRetention);
    }

    [Fact]
    public async Task OwnedDispatchUsesDurableIntentAndOneClaimAndCannotExecuteTwice()
    {
        var backend = new Backend(); var admission = new SystemRepairAdmission(); var workflow = new SystemRepairWorkflow(backend, admission);
        var request = Request(SystemToolOperation.SfcScanNow);
        var result = await workflow.ExecuteAsync(request, Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome);
        Assert.True(backend.IntentWasDurableAtDispatch); Assert.True(backend.FirstClaim); Assert.False(backend.SecondClaim);
        Assert.True(result.OwnershipReleased); Assert.False(admission.IsBlocked); Assert.False(admission.HasPendingWork);
        Assert.Equal(SystemToolJournalState.RecordedClientExit, result.JournalState);
        Assert.Equal(CheckState.Unknown, result.RepairSucceeded); Assert.Equal(CheckState.Unknown, result.Integrity);
        Assert.Equal(0, result.ClientObservation!.ExitCode);
        Assert.Equal(new[] { SystemToolJournalPhase.DispatchIntent, SystemToolJournalPhase.ClientObserved, SystemToolJournalPhase.ClientExited }, backend.Journal!.Persisted.Select(entry => entry.Phase));
        Assert.Equal(SystemRepairWorkflowOutcome.Blocked, (await workflow.ExecuteAsync(request, Generation)).Outcome);
        Assert.Equal(1, backend.OpenCalls); Assert.Equal(1, backend.DispatchCalls);
    }

    [Fact]
    public async Task SameConfirmedRequestRacingAcrossIndependentOwnersReservesOnlyOnce()
    {
        var begin = Signal(); var entered = Signal(); var acknowledge = Source<SystemRepairJournalWriteOutcome>();
        async Task<SystemRepairJournalWriteOutcome> Persist(SystemToolJournalEntry entry)
        {
            if (entry.Sequence != 1) return SystemRepairJournalWriteOutcome.DurableAcknowledged;
            entered.TrySetResult(); return await acknowledge.Task;
        }
        var first = new Backend { Persist = Persist }; var second = new Backend { Persist = Persist };
        var request = Request();
        var left = Task.Run(async () => { await begin.Task; return await new SystemRepairWorkflow(first, new()).ExecuteAsync(request, Generation); });
        var right = Task.Run(async () => { await begin.Task; return await new SystemRepairWorkflow(second, new()).ExecuteAsync(request, Generation); });
        begin.SetResult(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loser = await Task.WhenAny(left, right).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SystemRepairWorkflowOutcome.Blocked, (await loser).Outcome);
        Assert.Equal(1, first.OpenCalls + second.OpenCalls); Assert.Equal(0, first.DispatchCalls + second.DispatchCalls);
        acknowledge.SetResult(SystemRepairJournalWriteOutcome.DurableAcknowledged);
        var results = await Task.WhenAll(left, right);
        Assert.Single(results, result => result.Outcome == SystemRepairWorkflowOutcome.ClientExited);
        Assert.Equal(1, first.DispatchCalls + second.DispatchCalls);
    }

    [Fact]
    public async Task CancellationDuringOpenWaitsForTheOwnedTaskBeforeClosingWithoutWritingIntent()
    {
        var entered = Signal(); var opened = Source<SystemRepairJournalSession>();
        MemoryJournal? journal = null;
        var backend = new Backend();
        backend.Open = context => { journal = new(backend, context); entered.TrySetResult(); return opened.Task; };
        var admission = new SystemRepairAdmission(); using var cancellation = new CancellationTokenSource();
        var run = new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.False(run.IsCompleted); Assert.True(admission.HasPendingWork); Assert.Equal(0, journal!.ReleaseCalls);
        opened.SetResult(journal);
        var result = await run;
        Assert.Equal(SystemRepairWorkflowOutcome.CancelledBeforeDispatch, result.Outcome);
        Assert.Equal(0, journal.WriteCalls); Assert.Equal(1, journal.ReleaseCalls);
        Assert.True(result.OwnershipReleased); Assert.False(admission.IsBlocked);
    }

    [Fact]
    public async Task PostCommitCancellationCannotAbandonCompletionOrPretendRollback()
    {
        var observation = Source<ClientProcessObservation>(); var backend = new Backend { Observe = _ => observation.Task };
        var admission = new SystemRepairAdmission(); var request = Request(); using var cancellation = new CancellationTokenSource();
        var run = new SystemRepairWorkflow(backend, admission).ExecuteAsync(request, Generation, cancellation.Token);
        await backend.ObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel(); Assert.False(request.Invalidate());
        Assert.False(run.IsCompleted); Assert.True(admission.HasPendingWork); Assert.Equal(0, backend.Client!.ReleaseCalls);
        Assert.Equal(SystemRepairWorkflowOutcome.Busy, (await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation)).Outcome);
        observation.SetResult(Exit(backend.Client, 0));
        var result = await run;
        Assert.True(result.StopRequested); Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome);
        Assert.Equal(0, result.ClientObservation!.ExitCode); Assert.Equal(CheckState.Unknown, result.RepairSucceeded);
        Assert.True(result.OwnershipReleased); Assert.False(admission.RequiresRetention);
    }

    [Fact]
    public async Task TerminalPersistenceFailurePreservesActualExitButBlocksFurtherWork()
    {
        var backend = new Backend { Persist = entry => Task.FromResult(entry.Phase == SystemToolJournalPhase.ClientExited ? SystemRepairJournalWriteOutcome.Uncertain : SystemRepairJournalWriteOutcome.DurableAcknowledged) };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome); Assert.Equal(0, result.ClientObservation!.ExitCode);
        Assert.Equal(SystemToolJournalState.Unknown, result.JournalState); Assert.True(admission.IsBlocked);
        Assert.True(result.OwnershipReleased); Assert.False(admission.RequiresRetention);
        Assert.Equal(3, backend.Journal!.WriteCalls); Assert.Equal(CheckState.Unknown, result.RepairSucceeded);
    }

    [Fact]
    public async Task FailedObservedRecordStillAwaitsActualExitWithoutRetryingOrLosingCode()
    {
        var backend = new Backend { Persist = entry => Task.FromResult(entry.Phase == SystemToolJournalPhase.ClientObserved ? SystemRepairJournalWriteOutcome.Uncertain : SystemRepairJournalWriteOutcome.DurableAcknowledged) };
        var result = await new SystemRepairWorkflow(backend, new()).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome); Assert.Equal(0, result.ClientObservation!.ExitCode);
        Assert.Equal(2, backend.Journal!.WriteCalls); Assert.True(result.OwnershipReleased);
    }

    [Fact]
    public async Task DispatchExceptionIsAmbiguousAndNeverRetriedEvenWithNoReturnedClient()
    {
        var backend = new Backend { DispatchFailure = true }; var admission = new SystemRepairAdmission(); var workflow = new SystemRepairWorkflow(backend, admission);
        var result = await workflow.ExecuteAsync(Request(), Generation);
        Assert.True(result.DispatchAttempted); Assert.False(result.OwnershipReleased); Assert.True(admission.RequiresRetention);
        Assert.Equal(SystemRepairWorkflowOutcome.DispatchUncertain, result.Outcome);
        Assert.Equal(SystemRepairWorkflowOutcome.Busy, (await workflow.ExecuteAsync(Request(), Generation)).Outcome);
        Assert.Equal(1, backend.DispatchCalls);
    }

    [Fact]
    public async Task ForeignJournalAndClientAreNotInvokedOrReleasedAsOwnedSessions()
    {
        var original = new Backend(); await new SystemRepairWorkflow(original, new()).ExecuteAsync(Request(), Generation);
        var foreignJournal = new Backend { Open = _ => Task.FromResult<SystemRepairJournalSession>(original.Journal!) };
        var result = await new SystemRepairWorkflow(foreignJournal, new()).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.PreparationUncertain, result.Outcome); Assert.Equal(1, original.Journal!.ReleaseCalls);
        Assert.Equal(3, original.Journal.WriteCalls); Assert.Equal(0, foreignJournal.DispatchCalls);
        var foreignClient = new Backend { Dispatch = _ => Task.FromResult<SystemRepairDispatchSession>(original.Client!) };
        result = await new SystemRepairWorkflow(foreignClient, new()).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.DispatchUncertain, result.Outcome); Assert.Equal(1, original.Client!.ObserveCalls);
        Assert.Equal(1, original.Client.ReleaseCalls); Assert.False(result.OwnershipReleased);
    }

    [Fact]
    public async Task ObservationLossCannotReleaseTheClientEvenIfCleanupWouldReturnTrue()
    {
        var backend = new Backend { Observe = client => Task.FromResult(new ClientProcessObservation(ClientProcessObservationState.ObservationStopped,
            ClientProcessObservationReason.StopRequested, client.ObservationStarted, DateTimeOffset.UtcNow, client.ProcessId, client.ClientStartUtc, null, true)) };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.ObservationLost, result.Outcome); Assert.Equal(0, backend.Client!.ReleaseCalls);
        Assert.False(result.OwnershipReleased); Assert.True(admission.RequiresRetention); Assert.True(admission.IsBlocked);
    }

    [Fact]
    public async Task ConflictingKnownClientStartEvidenceDoesNotBecomeSettledOwnedExit()
    {
        var backend = new Backend { Observe = client => Task.FromResult(Exit(client, 0) with { ClientStartUtc = client.ClientStartUtc!.Value.AddTicks(-1) }) };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.Null(result.ClientObservation); Assert.False(result.OwnershipReleased); Assert.True(admission.RequiresRetention);
        Assert.Equal(0, backend.Client!.ReleaseCalls);
    }

    [Fact]
    public async Task CleanupMustActuallySettleBeforeResultAndAdmissionRelease()
    {
        var release = Source<bool>(); var backend = new Backend { ReleaseClient = () => release.Task }; var admission = new SystemRepairAdmission();
        var run = new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        await backend.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted); Assert.True(admission.HasPendingWork); Assert.Equal(0, backend.Journal!.ReleaseCalls);
        release.SetResult(false);
        var result = await run;
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome); Assert.False(result.OwnershipReleased);
        Assert.True(admission.RequiresRetention); Assert.True(admission.IsBlocked);
    }

    [Fact]
    public async Task GateRootsOwnedClientEvenWhenCallerDropsItsTaskAndWorkflow()
    {
        var admission = new SystemRepairAdmission();
        var abandoned = StartAbandoned(admission);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(abandoned.Client.TryGetTarget(out var client)); Assert.True(admission.HasPendingWork);
        Assert.True(abandoned.Run.TryGetTarget(out var run));
        client!.OwnedObservation.SetResult(Exit(client, 0));
        await run!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(admission.HasPendingWork); Assert.False(admission.IsBlocked);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<MemoryClient> Client, WeakReference<Task<SystemRepairWorkflowResult>> Run) StartAbandoned(SystemRepairAdmission admission)
    {
        // The pending TCS is owned only inside the client/backend cycle. Neither a
        // caller-held TCS nor a continuation captures that cycle from outside admission.
        var backend = new Backend { ClientOwnsPendingObservation = true };
        var run = new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.NotNull(backend.Client);
        return (new(backend.Client), new(run));
    }

    private static ClientProcessObservation Exit(MemoryClient client, int exit) => new(ClientProcessObservationState.Exited,
        ClientProcessObservationReason.ClientExited, client.ObservationStarted, DateTimeOffset.UtcNow, client.ProcessId, client.ClientStartUtc, exit, false);
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Backend : ISystemRepairWorkflowBackend
    {
        public bool Enabled = true, DispatchFailure, ClientOwnsPendingObservation;
        public int OpenCalls, DispatchCalls;
        public bool IntentWasDurableAtDispatch, FirstClaim, SecondClaim;
        public Func<SystemRepairExecutionContext, Task<SystemRepairJournalSession>>? Open;
        public Func<SystemRepairDispatchPermit, Task<SystemRepairDispatchSession>>? Dispatch;
        public Func<SystemToolJournalEntry, Task<SystemRepairJournalWriteOutcome>>? Persist;
        public Func<MemoryClient, Task<ClientProcessObservation>>? Observe;
        public Func<Task<bool>>? ReleaseClient;
        public MemoryJournal? Journal; public MemoryClient? Client;
        public TaskCompletionSource ObservationEntered = Signal(), ReleaseEntered = Signal();
        public bool ProductionExecutionValidated => Enabled;
        public Task<SystemRepairJournalSession> OpenJournalAsync(SystemRepairExecutionContext context, CancellationToken token)
        {
            OpenCalls++;
            if (Open != null) return Open(context);
            Journal = new(this, context); return Task.FromResult<SystemRepairJournalSession>(Journal);
        }
        public Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit)
        {
            DispatchCalls++;
            IntentWasDurableAtDispatch = Journal?.Persisted.Count == 1 && Journal.Persisted[0].Phase == SystemToolJournalPhase.DispatchIntent;
            FirstClaim = permit.TryClaim(permit.Context); SecondClaim = permit.TryClaim(permit.Context);
            if (DispatchFailure) throw new InvalidOperationException("Fixture ambiguous dispatch");
            if (Dispatch != null) return Dispatch(permit);
            Client = new(this, permit.Context); return Task.FromResult<SystemRepairDispatchSession>(Client);
        }
    }

    private sealed class MemoryJournal(Backend backend, SystemRepairExecutionContext context) : SystemRepairJournalSession(context)
    {
        public readonly List<SystemToolJournalEntry> Persisted = [];
        public int WriteCalls, ReleaseCalls;
        protected override async Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry)
        {
            WriteCalls++;
            var outcome = backend.Persist == null ? SystemRepairJournalWriteOutcome.DurableAcknowledged : await backend.Persist(entry);
            if (outcome == SystemRepairJournalWriteOutcome.DurableAcknowledged) Persisted.Add(entry);
            return outcome;
        }
        protected override Task<bool> ReleaseOwnershipAsync() { ReleaseCalls++; return Task.FromResult(true); }
    }

    private sealed class MemoryClient(Backend backend, SystemRepairExecutionContext context)
        : SystemRepairClientSession(context, 731, DateTimeOffset.UtcNow)
    {
        public int ObserveCalls, ReleaseCalls;
        public DateTimeOffset ObservationStarted;
        public readonly TaskCompletionSource<ClientProcessObservation> OwnedObservation = Source<ClientProcessObservation>();
        protected override Task<ClientProcessObservation> ObserveAsync()
        {
            ObserveCalls++; ObservationStarted = DateTimeOffset.UtcNow; backend.ObservationEntered.TrySetResult();
            return backend.ClientOwnsPendingObservation ? OwnedObservation.Task : backend.Observe?.Invoke(this) ?? Task.FromResult(Exit(this, 0));
        }
        protected override async Task<bool> ReleaseOwnershipAsync()
        {
            ReleaseCalls++; backend.ReleaseEntered.TrySetResult();
            bool released = backend.ReleaseClient == null || await backend.ReleaseClient();
            return released;
        }
    }
}
