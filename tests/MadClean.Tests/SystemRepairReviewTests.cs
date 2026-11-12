using System.ComponentModel;
using System.Collections.Concurrent;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemRepairReviewTests
{
    private static void Acknowledge(SystemRepairReview review)
    { review.OperationSelected = review.EffectsAcknowledged = review.ElevationAcknowledged = review.ObservationLimitsAcknowledged = true; }
    private static SystemRepairReview Local(Service service, Clock? clock = null)
    {
        var review = new SystemRepairReview(service, clock); review.SelectOperation(SystemToolOperation.DismRestoreHealth);
        Assert.True(review.Prepare()); return review;
    }
    [Fact]
    public async Task ClosedGatesStillExposeLocalDraftButAcknowledgmentsCannotInvokeServiceIo()
    {
        var service = new Service { Enabled = false }; var review = Local(service);
        Assert.NotNull(review.Draft); Assert.Contains("Local effects review", review.Status); Acknowledge(review);
        Assert.False(review.CanConfirm); Assert.False(review.CanReviewHistory); Assert.True(review.CanPrepare);
        int reads = service.PropertyReads; _ = review.CanConfirm; _ = review.CanPrepare; _ = review.CanReviewHistory;
        Assert.Equal(reads, service.PropertyReads); // Readiness getters cannot query services or mutate confirmation.
        await review.ConfirmAsync(); await review.ReviewHistoryAsync();
        Assert.Equal(0, service.ExecuteCalls); Assert.Equal(0, service.ReviewCalls);
        Assert.Equal(CheckState.Unknown, review.RepairSucceeded); Assert.Equal(CheckState.Unknown, review.Integrity);
    }
    [Fact]
    public async Task ExactFreshDraftAndEveryAcknowledgmentAreRequiredAfterOwnedHistoryReview()
    {
        var service = new Service(); var review = Local(service); await review.ReviewHistoryAsync();
        Assert.True(review.HistoryQualified); Assert.Null(review.Draft); Assert.True(review.Prepare());
        var draft = review.Draft;
        review.OperationSelected = review.EffectsAcknowledged = review.ElevationAcknowledged = true;
        Assert.False(review.CanConfirm); await review.ConfirmAsync(); Assert.Equal(0, service.ExecuteCalls);
        review.ObservationLimitsAcknowledged = true; Assert.True(review.CanConfirm);
        service.OnExecute = (request, generation) => { Assert.True(review.IsBusy); Assert.Same(draft, request); Assert.Equal(review.Generation, generation); };
        await review.ConfirmAsync();
        Assert.Equal(1, service.ExecuteCalls); Assert.Equal(SystemRepairWorkflowOutcome.NoClientCreated, review.HistoricalResult!.Outcome);
        Assert.False(review.CanConfirm); Assert.False(review.HistoryQualified); Assert.False(review.HasPendingWork);
        Assert.Equal(CheckState.Unknown, review.Integrity); Assert.Contains("No client", review.Status);
        var historical = review.HistoricalResult; review.SelectOperation(SystemToolOperation.SfcScanNow); Assert.True(review.Prepare()); Acknowledge(review);
        Assert.Same(historical, review.HistoricalResult); await review.ConfirmAsync(); Assert.Equal(1, service.ExecuteCalls);
        await review.ReviewHistoryAsync(); Assert.Equal(1, service.ReviewCalls); // No controller retry/reset.
    }
    [Fact]
    public async Task SelectionReplacementExpiryAndFinalGuardRecheckInvalidateConfirmationWithoutExecution()
    {
        var clock = new Clock(); var service = new Service(); var review = Local(service, clock); await review.ReviewHistoryAsync(); Assert.True(review.Prepare());
        var first = review.Draft; Acknowledge(review); review.SelectOperation(SystemToolOperation.SfcScanNow);
        Assert.Null(review.Draft); Assert.False(review.OperationSelected); Assert.False(review.EffectsAcknowledged);
        Assert.True(review.Prepare()); Assert.NotSame(first, review.Draft); Acknowledge(review);
        clock.Now = review.Draft!.ExpiresUtc; review.Refresh(); Assert.Null(review.Draft); Assert.False(review.CanConfirm);
        Assert.True(review.Prepare()); Acknowledge(review); Assert.True(review.CanConfirm);
        service.Enabled = false; await review.ConfirmAsync(); Assert.Equal(0, service.ExecuteCalls);
        review.SelectOperation(SystemToolOperation.DismCheckHealth); Assert.Null(review.SelectedOperation); Assert.False(review.CanPrepare);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("unresolved")]
    [InlineData("stopped")]
    [InlineData("cleanup")]
    [InlineData("forged")]
    public async Task UnqualifiedOrContradictoryHistoryCannotEnableConfirmation(string kind)
    {
        var service = new Service { Recovery = History(kind) }; var review = Local(service);
        await review.ReviewHistoryAsync(); Assert.False(review.HistoryQualified); Assert.True(review.HistoryReviewAttempted);
        if (!review.RequiresRetention) { Assert.True(review.Prepare()); Acknowledge(review); }
        await review.ConfirmAsync(); Assert.Equal(0, service.ExecuteCalls); Assert.False(review.CanReviewHistory);
        Assert.Equal(kind == "cleanup", review.RequiresRetention);
    }
    [Fact]
    public async Task ObserverFaultsAndReentrancyCannotAbandonActualHistoryTaskOrReenableReadiness()
    {
        var pending = new TaskCompletionSource<SystemRepairRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Service { PendingHistory = pending.Task }; var review = Local(service); int healthy = 0;
        review.PropertyChanged += (_, _) => { if (review.IsBusy) { review.SelectOperation(SystemToolOperation.SfcScanNow); throw new InvalidOperationException("Controlled observer failure."); } };
        review.PropertyChanged += (_, _) => healthy++;
        var run = review.ReviewHistoryAsync(); Assert.True(review.IsBusy); Assert.True(review.HasPendingWork); Assert.True(review.PresentationFault);
        Assert.False(run.IsCompleted); Assert.Equal(SystemToolOperation.DismRestoreHealth, review.SelectedOperation);
        pending.SetResult(History()); await run;
        Assert.NotNull(review.HistoricalRecovery); Assert.False(review.HistoryQualified); Assert.False(review.CanConfirm); Assert.False(review.CanPrepare);
        Assert.False(review.HasPendingWork); Assert.True(healthy >= 2); Assert.Equal(1, service.ReviewCalls);
    }
    [Fact]
    public async Task StoppedExecuteAndNotifierFaultStillDrainActualResultAndRejectForeignRequestEvidence()
    {
        var service = new Service(); var review = Local(service); await review.ReviewHistoryAsync(); Assert.True(review.Prepare()); Acknowledge(review);
        var pending = new TaskCompletionSource<SystemRepairWorkflowResult>(TaskCreationOptions.RunContinuationsAsynchronously); service.PendingExecution = pending.Task;
        using var stop = new CancellationTokenSource(); var run = review.ConfirmAsync(stop.Token);
        Assert.True(review.IsBusy); Assert.True(review.HasPendingWork); stop.Cancel(); Assert.False(run.IsCompleted);
        review.PropertyChanged += ThrowingObserver;
        pending.SetResult(Result(service.Request!) with { RequestId = Guid.NewGuid() }); await run;
        Assert.Null(review.HistoricalResult); Assert.True(review.RequiresRetention); Assert.True(review.PresentationFault);
        Assert.False(review.CanConfirm); Assert.Equal(1, service.ExecuteCalls);
        static void ThrowingObserver(object? sender, PropertyChangedEventArgs args) => throw new InvalidOperationException("Controlled observer failure.");
    }
    [Fact]
    public async Task AsyncPublicationUsesCapturedSynchronizationContext()
    {
        var old = SynchronizationContext.Current; var context = new Pump();
        var pending = new TaskCompletionSource<SystemRepairRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Service { PendingHistory = pending.Task }; SystemRepairReview review; Task run; int published = 0;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            review = Local(service);
            review.PropertyChanged += (_, _) => { Assert.Same(context, SynchronizationContext.Current); published++; };
            run = review.ReviewHistoryAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(old); }
        pending.SetResult(History()); await context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SynchronizationContext.SetSynchronizationContext(context);
        try { context.Drain(); }
        finally { SynchronizationContext.SetSynchronizationContext(old); }
        await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(review.HistoryQualified); Assert.True(published >= 2);
    }
    [Theory]
    [InlineData("exitWithoutObservation")]
    [InlineData("noClientWithoutRaw")]
    [InlineData("observationWithoutDispatch")]
    [InlineData("releasedButRetained")]
    [InlineData("releasedWithoutCompletion")]
    public async Task ContradictoryOwnedResultCannotBecomeAcceptedHistoricalFacts(string contradiction)
    {
        var service = new Service(); var review = Local(service); await review.ReviewHistoryAsync(); Assert.True(review.Prepare()); Acknowledge(review);
        service.TransformResult = result => contradiction switch
        {
            "exitWithoutObservation" => result with { Outcome = SystemRepairWorkflowOutcome.ClientExited },
            "noClientWithoutRaw" => result with { DirectCreation = null, DirectCompletion = null },
            "observationWithoutDispatch" => result with { DispatchAttempted = false, ClientObservation = new(ClientProcessObservationState.Exited,
                ClientProcessObservationReason.ClientExited, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, 42, null, 0, false) },
            "releasedWithoutCompletion" => result with { DirectCompletion = null, DirectEvidenceConsistent = false, Outcome = SystemRepairWorkflowOutcome.DispatchUncertain },
            _ => result with { DirectCompletion = result.DirectCompletion! with { RequiresRetention = true } }
        };
        await review.ConfirmAsync(); Assert.Null(review.HistoricalResult); Assert.True(review.RequiresRetention); Assert.False(review.CanConfirm);
    }
    [Fact]
    public async Task BoundUncertainRawEvidenceRemainsHistoricalWithItsExplicitConsistencyMarker()
    {
        var service = new Service(); var review = Local(service); await review.ReviewHistoryAsync(); Assert.True(review.Prepare()); Acknowledge(review);
        service.TransformResult = result => result with
        {
            Outcome = SystemRepairWorkflowOutcome.DispatchUncertain, OwnershipReleased = false, DirectEvidenceConsistent = false,
            DirectCreation = result.DirectCreation! with { State = SystemRepairDirectCreationState.Uncertain },
            DirectCompletion = result.DirectCompletion! with { CreationState = SystemRepairDirectCreationState.Uncertain,
                CleanupConfirmed = false, RequiresRetention = true, Stage = SystemRepairDirectStage.Create }
        };
        await review.ConfirmAsync(); Assert.NotNull(review.HistoricalResult); Assert.False(review.HistoricalResult.DirectEvidenceConsistent);
        Assert.NotNull(review.HistoricalResult.DirectCompletion); Assert.True(review.RequiresRetention); Assert.False(review.CanConfirm);
        Assert.Equal(CheckState.Unknown, review.RepairSucceeded);
    }

    [Fact]
    public async Task OwnershipGetterFailureAfterAnOwnedReviewRetainsServiceInsteadOfAllowingClose()
    {
        var service = new Service(); var review = Local(service); await review.ReviewHistoryAsync(); Assert.True(review.HistoryQualified);
        service.ThrowOwnershipGetters = true; review.Refresh();
        Assert.True(review.RequiresRetention); Assert.False(review.CanConfirm); Assert.False(review.ExecutionQualified); Assert.False(review.RecoveryReviewQualified);
    }

    private sealed class Pump : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        public readonly TaskCompletionSource Posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state) { queue.Enqueue((callback, state)); Posted.TrySetResult(); }
        public void Drain() { while (queue.TryDequeue(out var item)) item.Callback(item.State); }
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private static SystemRepairRecoveryResult History(string kind = "empty")
    {
        var now = DateTimeOffset.UtcNow; SystemToolJournalHistory history;
        if (kind is "unresolved" or "forged")
        {
            var row = new SystemToolJournalEntry(Guid.NewGuid(), SystemToolOperation.DismRestoreHealth, 1, now,
                SystemToolJournalPhase.DispatchIntent, null, null, null, null);
            history = SystemToolJournalReconciliation.Review([SystemToolJournal.Review([row], now)], true, now);
            if (kind == "forged") history = history with { State = SystemToolJournalHistoryState.RecordedTerminalOnly };
        }
        else history = SystemToolJournalReconciliation.Review(kind == "unknown" ? null : [], kind != "unknown", now);
        return new(Guid.NewGuid(), SystemRepairRecoveryOutcome.Reviewed, history, kind == "stopped", kind != "cleanup", false, "Controlled history facts.");
    }
    private static SystemRepairWorkflowResult Result(SystemRepairRequest request, SystemRepairExecutionContext? context = null)
    {
        var result = new SystemRepairWorkflowResult(request.RequestId, context?.ExecutionId ?? Guid.NewGuid(),
            SystemRepairWorkflowOutcome.NoClientCreated, true, false, true, SystemToolJournalState.Unresolved, null, "Controlled no-client result.");
        if (context == null) return result;
        return result with
        {
            DirectEvidenceConsistent = true,
            DirectCreation = new(context, SystemRepairDirectCreationState.KnownNotCreated, true, null, DateTimeOffset.UtcNow, 2),
            DirectCompletion = new(context, SystemRepairDirectCreationState.KnownNotCreated, SystemRepairDirectStage.Completed,
                true, null, null, null, null, null, null, true, false, 2, "Controlled no-native-client evidence.")
        };
    }
    private sealed class CaptureBackend : ISystemRepairWorkflowBackend
    {
        public SystemRepairExecutionContext? Context;
        public bool ProductionExecutionValidated => true; // Context capture only; no dispatch or native IO.
        public Task<SystemRepairJournalSession> OpenJournalAsync(SystemRepairExecutionContext context, CancellationToken token)
        { Context = context; return Task.FromResult<SystemRepairJournalSession>(new Refused(context)); }
        public Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit) => throw new InvalidOperationException("Context capture must not dispatch.");
        private sealed class Refused(SystemRepairExecutionContext context) : SystemRepairJournalSession(context)
        {
            protected override Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry) => Task.FromResult(SystemRepairJournalWriteOutcome.RefusedBeforeWrite);
            protected override Task<bool> ReleaseOwnershipAsync() => Task.FromResult(true);
        }
    }
    private sealed class Service : ISystemRepairReviewService
    {
        public bool Enabled = true, ThrowOwnershipGetters; public int PropertyReads, ExecuteCalls, ReviewCalls;
        public Func<SystemRepairWorkflowResult, SystemRepairWorkflowResult>? TransformResult;
        public SystemRepairRecoveryResult Recovery = History();
        public Task<SystemRepairRecoveryResult>? PendingHistory; public Task<SystemRepairWorkflowResult>? PendingExecution;
        public Action<SystemRepairRequest, long>? OnExecute; public SystemRepairRequest? Request;
        public bool ProductionExecutionValidated { get { PropertyReads++; return Enabled; } }
        public bool ProductionRecoveryReviewValidated { get { PropertyReads++; return Enabled; } }
        public bool HasPendingWork { get { PropertyReads++; if (ThrowOwnershipGetters) throw new InvalidOperationException("Controlled ownership getter failure."); return false; } }
        public bool RequiresRetention { get { PropertyReads++; return false; } }
        public bool IsHistoryBlocked { get { PropertyReads++; return false; } }
        public Task<SystemRepairRecoveryResult> ReviewHistoryAsync(CancellationToken token) { ReviewCalls++; return PendingHistory ?? Task.FromResult(Recovery); }
        public async Task<SystemRepairWorkflowResult> ExecuteAsync(SystemRepairRequest request, long generation, CancellationToken token)
        {
            ExecuteCalls++; Request = request; OnExecute?.Invoke(request, generation);
            if (PendingExecution != null) return await PendingExecution;
            var capture = new CaptureBackend();
            await new SystemRepairWorkflow(capture, new()).ExecuteAsync(request, generation, token);
            var result = Result(request, capture.Context);
            return TransformResult?.Invoke(result) ?? result;
        }
    }
}
