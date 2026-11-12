using MadClean.Core;

namespace MadClean.Windows;

/// <summary>One explicitly reviewed workflow; construction and closed capabilities perform no Windows work.</summary>
public sealed class WindowsSystemRepairReviewService : ISystemRepairReviewService
{
    private static readonly object RootsSync = new();
    private static readonly HashSet<WindowsSystemRepairReviewService> Roots = new(ReferenceEqualityComparer.Instance);
    private readonly object sync = new();
    private readonly WindowsSystemRepairBackend backend = new();
    private readonly WindowsSystemRepairRecoveryReader reader = new();
    private readonly SystemRepairAdmission admission = new();
    private readonly SystemRepairRecovery recovery;
    private readonly SystemRepairWorkflow workflow;
    private readonly Task<SystemRepairRecoveryResult> closedReview = Task.FromResult(new SystemRepairRecoveryResult(
        null, SystemRepairRecoveryOutcome.Blocked, null, false, true, null,
        "Existing repair-history review is not qualified. No history, admission or Windows work was requested."));
    private Task<SystemRepairRecoveryResult>? reviewing, actualReview;
    private Task<SystemRepairWorkflowResult>? executing, actualExecution;
    private SystemRepairRequest? selectedRequest;
    private long selectedGeneration;
    private bool retained, historyBlocked;

    public WindowsSystemRepairReviewService()
    {
        recovery = SystemToolServicingAdmission.CreateRepairRecovery(reader);
        workflow = new SystemRepairWorkflow(backend, admission);
    }
    public bool ProductionExecutionValidated => false;
    public bool ProductionRecoveryReviewValidated => false;
    public bool HasPendingWork
    {
        get
        {
            bool owned;
            lock (sync) owned = reviewing is { IsCompleted: false } || actualReview is { IsCompleted: false } ||
                executing is { IsCompleted: false } || actualExecution is { IsCompleted: false };
            return owned || ProductionExecutionValidated && (backend.HasPendingWork || admission.HasPendingWork) ||
                ProductionRecoveryReviewValidated && (reader.HasPendingWork || recovery.HasPendingWork);
        }
    }
    public bool RequiresRetention
    {
        get
        {
            bool owned; lock (sync) owned = retained;
            return owned || ProductionExecutionValidated && (backend.RequiresRetention || admission.RequiresRetention) ||
                ProductionRecoveryReviewValidated && (reader.RequiresRetention || recovery.RequiresRetention);
        }
    }
    public bool IsHistoryBlocked
    {
        get
        {
            bool owned; lock (sync) owned = historyBlocked;
            return owned || ProductionExecutionValidated && (backend.IsHistoryBlocked || admission.IsBlocked) ||
                (ProductionExecutionValidated || ProductionRecoveryReviewValidated) && SystemToolServicingAdmission.IsHistoryBlocked;
        }
    }

    public Task<SystemRepairRecoveryResult> ReviewHistoryAsync(CancellationToken token)
    {
        // No token, shared slot, native status or profile access precedes this literal boundary.
        if (!ProductionRecoveryReviewValidated) return closedReview;
        Task<Task<SystemRepairRecoveryResult>> scheduled;
        Task<SystemRepairRecoveryResult> owned;
        lock (sync)
        {
            if (reviewing != null) return reviewing;
            if (!TryRoot()) return reviewing = Task.FromResult(new SystemRepairRecoveryResult(null, SystemRepairRecoveryOutcome.Busy,
                null, false, true, null, "Review ownership capacity is unavailable; no work was requested."));
            try
            {
                scheduled = new Task<Task<SystemRepairRecoveryResult>>(() => ReviewWorker(token),
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                reviewing = owned = scheduled.Unwrap(); // Actual cold worker is rooted before Start.
                Observe(owned);
            }
            catch { retained = true; throw; }
        }
        Start(scheduled);
        return owned;
    }

    private async Task<SystemRepairRecoveryResult> ReviewWorker(CancellationToken token)
    {
        if (!ProductionRecoveryReviewValidated || !reader.ProductionReviewValidated)
            return await closedReview.ConfigureAwait(false);
        var actual = recovery.RunAsync(token);
        lock (sync) actualReview = actual;
        var result = await actual.ConfigureAwait(false);
        lock (sync)
        {
            historyBlocked |= result.HistoryBlocked == true;
            retained |= !result.OwnershipReleased;
        }
        return result;
    }

    public Task<SystemRepairWorkflowResult> ExecuteAsync(SystemRepairRequest request, long generation, CancellationToken token)
    {
        // Even a null/foreign request is not inspected at a closed execution boundary.
        if (!ProductionExecutionValidated || !ProductionRecoveryReviewValidated)
            return RefusedExecution();
        Task<Task<SystemRepairWorkflowResult>> scheduled;
        Task<SystemRepairWorkflowResult> owned;
        lock (sync)
        {
            if (executing != null)
                return ReferenceEquals(request, selectedRequest) && generation == selectedGeneration ? executing : RefusedExecution();
            selectedRequest = request;
            selectedGeneration = generation;
            if (!TryRoot()) return executing = Task.FromResult(Blocked(request, false, SystemRepairWorkflowOutcome.Busy));
            try
            {
                scheduled = new Task<Task<SystemRepairWorkflowResult>>(() => ExecuteWorker(request, generation, token),
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                executing = owned = scheduled.Unwrap();
                Observe(owned);
            }
            catch { retained = true; throw; }
        }
        Start(scheduled);
        return owned;
    }

    private async Task<SystemRepairWorkflowResult> ExecuteWorker(SystemRepairRequest request, long generation, CancellationToken token)
    {
        if (!ProductionExecutionValidated || !ProductionRecoveryReviewValidated ||
            !backend.ProductionExecutionValidated || !reader.ProductionReviewValidated)
            return Blocked(request, false);
        SystemRepairRecoveryResult? reviewed;
        lock (sync)
        {
            if (retained || historyBlocked || !ReferenceEquals(request, selectedRequest) || generation != selectedGeneration ||
                reviewing is not { IsCompletedSuccessfully: true }) return Blocked(request, false);
            reviewed = reviewing.Result;
        }
        // This is this facade's actual memoized result, not a caller-supplied receipt.
        if (!QualifiedHistory(reviewed) || request == null || generation < 0 || generation != request.SourceGeneration ||
            token.IsCancellationRequested || SystemToolServicingAdmission.HasPendingWork ||
            SystemToolServicingAdmission.RequiresRetention || SystemToolServicingAdmission.IsHistoryBlocked ||
            backend.HasPendingWork || backend.RequiresRetention || backend.IsHistoryBlocked ||
            reader.HasPendingWork || reader.RequiresRetention || recovery.HasPendingWork || recovery.RequiresRetention ||
            admission.HasPendingWork || admission.RequiresRetention || admission.IsBlocked)
            return Blocked(request, token.IsCancellationRequested);
        // Workflow/backend independently reserve the exact confirmed request, obtain
        // the same shared admission and reread history before a new durable intent.
        // Its token semantics stop only before commit; no post-entry task abandonment.
        var actual = workflow.ExecuteAsync(request, generation, token);
        lock (sync) actualExecution = actual;
        var result = await actual.ConfigureAwait(false);
        lock (sync)
        {
            retained |= !result.OwnershipReleased;
            historyBlocked |= result.JournalState is SystemToolJournalState.Unknown or SystemToolJournalState.Unresolved;
        }
        return result;
    }

    private static bool QualifiedHistory(SystemRepairRecoveryResult? result)
    {
        try
        {
            if (result is not { Outcome: SystemRepairRecoveryOutcome.Reviewed, StopRequested: false,
                OwnershipReleased: true, HistoryBlocked: false, ReviewId: { } id, History: { } history } || id == Guid.Empty ||
                !history.EnumerationComplete || history.State is not (SystemToolJournalHistoryState.NoRecordsObserved or SystemToolJournalHistoryState.RecordedTerminalOnly))
                return false;
            var canonical = SystemToolJournalReconciliation.Review(history.Journals, true, DateTimeOffset.UtcNow);
            return canonical.EnumerationComplete && canonical.State == history.State &&
                canonical.State is SystemToolJournalHistoryState.NoRecordsObserved or SystemToolJournalHistoryState.RecordedTerminalOnly;
        }
        catch { return false; } // No collaborator/IO has been entered by this pure preflight.
    }
    private static SystemRepairWorkflowResult Blocked(SystemRepairRequest? request, bool stopped,
        SystemRepairWorkflowOutcome outcome = SystemRepairWorkflowOutcome.Blocked) => new(request?.RequestId ?? Guid.Empty,
        null, stopped ? SystemRepairWorkflowOutcome.CancelledBeforeDispatch : outcome, false, stopped, true,
        SystemToolJournalState.Unknown, null, "The repair prerequisites did not qualify this attempt; no Windows work or journal access was requested.");
    private bool TryRoot()
    {
        lock (RootsSync)
        {
            if (Roots.Contains(this)) return true;
            if (Roots.Count >= 128) return false;
            return Roots.Add(this);
        }
    }
    private void Observe(Task task)
    {
        _ = task.ContinueWith(done =>
        {
            lock (sync)
            {
                if (done.IsFaulted) { _ = done.Exception; retained = true; }
                if (done.IsCanceled) retained = true;
                bool releaseRoot = !retained && (reviewing == null || reviewing.IsCompletedSuccessfully) &&
                    (executing == null || executing.IsCompletedSuccessfully) &&
                    (actualReview == null || actualReview.IsCompletedSuccessfully) &&
                    (actualExecution == null || actualExecution.IsCompletedSuccessfully);
                // Same lock/order as admission: a finishing review cannot remove
                // the static root after a concurrent execution has reused it.
                if (releaseRoot) lock (RootsSync) Roots.Remove(this);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void Start(Task task)
    {
        try { task.Start(TaskScheduler.Default); }
        catch { lock (sync) retained = true; throw; }
    }
    private static InvalidOperationException ExecutionRefusal() => new(
        "The exact reviewed repair workflow is unavailable or unqualified. No new execution permission was granted.");
    private static Task<SystemRepairWorkflowResult> RefusedExecution() => Task.FromException<SystemRepairWorkflowResult>(ExecutionRefusal());
}
