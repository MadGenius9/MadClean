using System.ComponentModel;

namespace MadClean.Core;

public interface ISystemRepairReviewService
{
    bool ProductionExecutionValidated { get; }
    bool ProductionRecoveryReviewValidated { get; }
    bool HasPendingWork { get; }
    bool RequiresRetention { get; }
    bool IsHistoryBlocked { get; }
    Task<SystemRepairRecoveryResult> ReviewHistoryAsync(CancellationToken token);
    Task<SystemRepairWorkflowResult> ExecuteAsync(SystemRepairRequest request, long generation, CancellationToken token);
}

/// <summary>
/// UI-thread controller for one owned history review and at most one execution attempt. Local
/// preparation is available with closed gates; it grants no execution permission. Calls/publications
/// belong to the captured UI synchronization context; cross-thread mutation is unsupported.
/// No controller reset clears admission, retained ownership or unresolved history.
/// </summary>
public sealed class SystemRepairReview : INotifyPropertyChanged
{
    private readonly ISystemRepairReviewService service;
    private readonly TimeProvider clock;
    private Task? ownedTask;
    private bool executionValidated, recoveryValidated, servicePending, serviceRetention, historyBlocked, capabilityUnknown;
    private bool busy, presentationFault, historyAttempted, historyQualified, executionAttempted, ownershipUncertain, publishing;
    private bool operationSelected, effectsAcknowledged, elevationAcknowledged, observationLimitsAcknowledged;
    private DateTimeOffset now;
    private bool timeValid;
    private string status = "Local repair review is available; execution remains unqualified.";
    public SystemRepairReview(ISystemRepairReviewService service, TimeProvider? clock = null)
    { this.service = service ?? throw new ArgumentNullException(nameof(service)); this.clock = clock ?? TimeProvider.System; Refresh(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public SystemToolOperation? SelectedOperation { get; private set; }
    public long Generation { get; private set; }
    public SystemRepairRequest? Draft { get; private set; }
    public SystemRepairRecoveryResult? HistoricalRecovery { get; private set; }
    public SystemRepairWorkflowResult? HistoricalResult { get; private set; }
    public bool ExecutionQualified => executionValidated && !capabilityUnknown;
    public bool RecoveryReviewQualified => recoveryValidated && !capabilityUnknown;
    public bool ExecutionAttempted => executionAttempted;
    public bool IsBusy => busy;
    public bool PresentationFault => presentationFault;
    public bool HistoryReviewAttempted => historyAttempted;
    public bool HistoryQualified => historyQualified;
    public bool HasPendingWork => busy || ownedTask is { IsCompleted: false } || servicePending;
    public bool RequiresRetention => serviceRetention || ownershipUncertain;
    public bool IsHistoryBlocked => historyBlocked;
    public string Status => status;
    public CheckState RepairSucceeded => CheckState.Unknown;
    public CheckState Integrity => CheckState.Unknown;
    public bool OperationSelected { get => operationSelected; set => SetAcknowledgment(ref operationSelected, value); }
    public bool EffectsAcknowledged { get => effectsAcknowledged; set => SetAcknowledgment(ref effectsAcknowledged, value); }
    public bool ElevationAcknowledged { get => elevationAcknowledged; set => SetAcknowledgment(ref elevationAcknowledged, value); }
    public bool ObservationLimitsAcknowledged { get => observationLimitsAcknowledged; set => SetAcknowledgment(ref observationLimitsAcknowledged, value); }
    public bool CanPrepare => !busy && !HasPendingWork && !RequiresRetention && !presentationFault && timeValid && Fixed(SelectedOperation);
    public bool CanReviewHistory => recoveryValidated && !capabilityUnknown && !historyAttempted && !busy &&
        !HasPendingWork && !RequiresRetention && !historyBlocked && !presentationFault;
    public bool CanConfirm => executionValidated && recoveryValidated && !capabilityUnknown && historyQualified && !executionAttempted &&
        !busy && !HasPendingWork && !RequiresRetention && !historyBlocked && !presentationFault && FreshDraft() && Acknowledged;
    private bool Acknowledged => operationSelected && effectsAcknowledged && elevationAcknowledged && observationLimitsAcknowledged;
    private static bool Fixed(SystemToolOperation? operation) => operation is SystemToolOperation.DismRestoreHealth or SystemToolOperation.SfcScanNow;
    private bool FreshDraft() => timeValid && Draft is { IsInvalidated: false } draft && draft.SourceGeneration == Generation &&
        draft.Definition.Operation == SelectedOperation && now >= draft.PreparedUtc && now < draft.ExpiresUtc;

    public void SelectOperation(SystemToolOperation? operation)
    {
        if (busy) return;
        InvalidateDraft(); SelectedOperation = Fixed(operation) ? operation : null;
        status = SelectedOperation == null ? "Select RestoreHealth or scannow for a local effects review." : "Prepare a local effects review; no command is run by preparation.";
        Publish();
    }
    public bool Prepare()
    {
        if (busy) return false;
        RefreshSnapshots();
        if (!CanPrepare) { Publish(); return false; }
        InvalidateDraft();
        Draft = SystemRepairWorkflow.Prepare(SelectedOperation!.Value, Generation, now);
        status = Draft == null ? "The local request could not be prepared." : executionValidated && recoveryValidated
            ? "Review the exact fixed operation and all four acknowledgments. History review and final validation are required."
            : "Local effects review only: repair execution and history review are not qualified for this build. No command will run.";
        Publish(); return Draft != null;
    }
    public void Refresh()
    {
        RefreshSnapshots();
        if (!busy && Draft != null && !FreshDraft()) { InvalidateDraft(); status = "The draft expired or its source changed. Prepare and acknowledge a new review."; }
        else if (!busy && !executionValidated && Draft == null) status = "Local repair review is available; repair execution is not qualified for this build.";
        if (!busy && ownershipUncertain) status = "Service ownership could not be established. The service is retained and further confirmation is disabled.";
        Publish();
    }
    private void RefreshSnapshots()
    {
        try { now = clock.GetUtcNow(); timeValid = SystemRepairWorkflow.Utc(now); } catch { timeValid = false; }
        try
        {
            executionValidated = service.ProductionExecutionValidated; recoveryValidated = service.ProductionRecoveryReviewValidated;
            servicePending = service.HasPendingWork; serviceRetention = service.RequiresRetention; historyBlocked = service.IsHistoryBlocked;
            capabilityUnknown = false;
        }
        catch
        {
            executionValidated = recoveryValidated = false; capabilityUnknown = true; historyQualified = false;
            if (historyAttempted || executionAttempted || ownedTask != null) ownershipUncertain = true;
        }
    }
    private void SetAcknowledgment(ref bool field, bool value)
    { if (busy || field == value) return; field = value; Publish(); }
    private void InvalidateDraft()
    {
        Draft?.Invalidate(); Draft = null; Generation++;
        operationSelected = effectsAcknowledged = elevationAcknowledged = observationLimitsAcknowledged = false;
    }
    public async Task ReviewHistoryAsync(CancellationToken token = default)
    {
        if (busy) return;
        RefreshSnapshots(); if (!CanReviewHistory) { Publish(); return; }
        InvalidateDraft(); historyAttempted = true; historyQualified = false; busy = true;
        long generation = Generation;
        status = "Reviewing existing history. Caller stop cannot abandon the owned reader.";
        try
        {
            var actual = service.ReviewHistoryAsync(token); ownedTask = actual;
            Publish();
            var result = await actual; // Preserve the captured UI synchronization context for state publication.
            HistoricalRecovery = result;
            ownershipUncertain |= result?.OwnershipReleased != true;
            RefreshSnapshots();
            var canonical = SystemToolJournalReconciliation.Review(result?.History?.Journals,
                result?.History?.EnumerationComplete == true, timeValid ? now : default);
            historyQualified = recoveryValidated && !presentationFault && Generation == generation && result is { Outcome: SystemRepairRecoveryOutcome.Reviewed,
                OwnershipReleased: true, StopRequested: false, HistoryBlocked: false, ReviewId: { } id } && id != Guid.Empty &&
                result.History?.State == canonical.State && (canonical.State is SystemToolJournalHistoryState.NoRecordsObserved or SystemToolJournalHistoryState.RecordedTerminalOnly) &&
                !servicePending && !serviceRetention && !historyBlocked && !capabilityUnknown;
            status = historyQualified ? "Existing history was reviewed and released. This supplies one prerequisite only; exact confirmation is still required."
                : "History did not qualify this review. Unknown, stopped, unresolved or unreleased evidence cannot authorize repair.";
        }
        catch { historyQualified = false; ownershipUncertain = true; status = "History review did not establish a complete owned result. The service remains retained; no retry or reset is supplied."; }
        finally { RefreshSnapshots(); busy = false; Publish(); }
    }
    public async Task ConfirmAsync(CancellationToken token = default)
    {
        if (busy) return;
        RefreshSnapshots();
        var request = Draft; long generation = Generation;
        // Recheck facts and exact request here. UI readiness is never execution authority.
        if (request == null || !executionValidated || !recoveryValidated || capabilityUnknown || !historyQualified || executionAttempted ||
            HasPendingWork || RequiresRetention || historyBlocked || presentationFault || !FreshDraft() || !Acknowledged || token.IsCancellationRequested)
        { status = "Confirmation was refused. Current qualification, fresh exact selection and all acknowledgments are required."; Publish(); return; }
        if (request.Confirm(request.RequestId, request.Definition.Operation, generation, operationSelected, effectsAcknowledged,
            elevationAcknowledged, observationLimitsAcknowledged, now) != SystemRepairConfirmation.Confirmed)
        { InvalidateDraft(); status = "The exact request was stale or refused. No execution was requested."; Publish(); return; }
        busy = true; executionAttempted = true; historyQualified = false;
        status = "Observing the exact owned repair request. Stop is not cancellation of Windows or rollback.";
        try
        {
            var actual = service.ExecuteAsync(request, generation, token); ownedTask = actual;
            Publish();
            var result = await actual;
            if (result == null || result.RequestId != request.RequestId || !AdmitResult(result, request))
            { ownershipUncertain = true; status = "The returned result did not belong to this exact request. Foreign evidence is withheld; ownership remains uncertain."; return; }
            HistoricalResult = result;
            ownershipUncertain |= !result.OwnershipReleased;
            status = result.Outcome switch
            {
                SystemRepairWorkflowOutcome.NoClientCreated => "No client was created for the admitted attempt. Unresolved intent and actual resource release remain separate findings.",
                SystemRepairWorkflowOutcome.ClientExited => "The owned client exit was observed. Repair success and Windows integrity remain unknown.",
                _ => "The request did not establish a qualified client exit. Review its history, raw evidence and resource-release status separately."
            };
        }
        catch { ownershipUncertain = true; status = "The owned request did not return a complete result. It is not retried; Windows work may continue independently."; }
        finally { RefreshSnapshots(); busy = false; Publish(); }
    }
    private static bool AdmitResult(SystemRepairWorkflowResult result, SystemRepairRequest request)
    {
        if (!Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.JournalState) || result.ExecutionId == Guid.Empty) return false;
        var observation = result.ClientObservation;
        if (observation != null && (!result.DispatchAttempted || result.ExecutionId == null ||
            ClientProcessObservationProjection.Normalize(observation) != observation)) return false;
        if (result.Outcome == SystemRepairWorkflowOutcome.ClientExited && (result.ExecutionId == null ||
            observation?.State != ClientProcessObservationState.Exited)) return false;
        var creation = result.DirectCreation; var completion = result.DirectCompletion;
        if (creation != null && (!ReferenceEquals(creation.Context.Request, request) || creation.Context.ExecutionId != result.ExecutionId) ||
            completion != null && (!ReferenceEquals(completion.Context.Request, request) || completion.Context.ExecutionId != result.ExecutionId)) return false;
        if (creation != null && completion != null && !ReferenceEquals(creation.Context, completion.Context)) return false;
        if ((creation != null || completion != null) && result.DirectEvidenceConsistent == null) return false;
        if (result.DirectEvidenceConsistent == true && (creation == null || completion == null ||
            creation.State != completion.CreationState || creation.CreateAttempted != completion.CreateAttempted || creation.ProcessId != completion.ProcessId)) return false;
        if (result.OwnershipReleased && (creation != null || completion != null) &&
            (creation == null || completion == null || result.DirectEvidenceConsistent != true || !completion.CleanupConfirmed ||
             completion.RequiresRetention || completion.Stage != SystemRepairDirectStage.Completed ||
             completion.CreationState == SystemRepairDirectCreationState.Created && completion.ExitObserved != true ||
             completion.Stdout is { } stdout && (!stdout.EndOfStreamObserved || !stdout.WorkerReleased) ||
             completion.Stderr is { } stderr && (!stderr.EndOfStreamObserved || !stderr.WorkerReleased))) return false;
        if (result.Outcome == SystemRepairWorkflowOutcome.NoClientCreated)
        {
            if (!result.DispatchAttempted || result.ExecutionId == null || result.DirectEvidenceConsistent != true || creation == null || completion == null ||
                observation != null || creation.ProcessId != null || completion.ExitObserved != null || completion.ExitCode != null || completion.ExitObservedUtc != null ||
                result.JournalState == SystemToolJournalState.RecordedClientExit) return false;
            return creation.State switch
            {
                SystemRepairDirectCreationState.NotAttempted => !creation.CreateAttempted,
                SystemRepairDirectCreationState.KnownNotCreated => creation.CreateAttempted,
                _ => false
            };
        }
        if (completion != null && result.Outcome == SystemRepairWorkflowOutcome.ClientExited &&
            (result.DirectEvidenceConsistent != true || completion.CreationState != SystemRepairDirectCreationState.Created ||
             completion.ExitObserved != true || completion.ProcessId != observation!.ObservedProcessId || completion.ExitCode != observation.ExitCode)) return false;
        return true;
    }
    private void Publish()
    {
        if (publishing) return;
        publishing = true;
        if (presentationFault) { historyQualified = false; status = "Presentation failed. Readiness is disabled while any actual owned work continues to settlement."; }
        try
        {
            bool wasFaulted = presentationFault;
            NotifyObservers();
            if (!wasFaulted && presentationFault) NotifyObservers(); // Healthy observers see final disabled readiness.
        }
        finally { publishing = false; }
    }
    private void NotifyObservers()
    {
        var handlers = PropertyChanged?.GetInvocationList(); if (handlers == null) return;
        foreach (PropertyChangedEventHandler handler in handlers)
        {
            try { handler(this, new PropertyChangedEventArgs(string.Empty)); }
            catch { presentationFault = true; historyQualified = false; status = "Presentation failed. Readiness is disabled while any actual owned work continues to settlement."; }
        }
    }
}
