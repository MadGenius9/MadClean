using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private SystemRepairReview? repairReview;
    private Dispatcher? repairReviewDispatcher;
    private DispatcherTimer? repairReviewTimer;
    private CancellationTokenSource? repairReviewStop;
    private Task? repairReviewTask;
    private bool repairReviewOwnsBusy, repairReviewCallActive, repairReviewStopRequested;
    private bool repairReviewPresentationFault, publishingRepairReview;

    public SystemRepairReview RepairReview => repairReview ?? throw new InvalidOperationException("Repair review has not been initialized.");
    public ICommand PrepareRepairReviewCommand { get; private set; } = null!;
    public ICommand ReviewRepairHistoryCommand { get; private set; } = null!;
    public ICommand ConfirmRepairCommand { get; private set; } = null!;
    public ICommand RequestRepairObservationStopCommand { get; private set; } = null!;
    public bool HasPendingRepairReview => repairReviewOwnsBusy || repairReviewCallActive || RepairReviewUnderlyingUnsettled();
    private bool RepairSelectionMatches => repairReview is { SelectedOperation: { } operation } &&
        selectedSystemTool?.Operation == operation && operation is SystemToolOperation.DismRestoreHealth or SystemToolOperation.SfcScanNow;
    private bool RepairDraftMatches => RepairSelectionMatches && repairReview?.Draft is { } draft &&
        draft.Definition.Operation == selectedSystemTool?.Operation;
    public bool CanAcknowledgeRepairReview => !IsBusy && !HasPendingRepairReview && !repairReviewPresentationFault &&
        repairReview is { PresentationFault: false } && RepairDraftMatches;
    public string RepairReviewHeading => !repairReviewPresentationFault && repairReview is { PresentationFault: false, ExecutionQualified: true }
        ? "Guided repair review" : "Guided repair review · execution unavailable";
    public string RepairReviewQualification
    {
        get
        {
            var review = repairReview;
            if (repairReviewPresentationFault || review?.PresentationFault == true)
                return "Presentation failed. Repair actions are disabled. Any actual owned work is still retained until it settles; a display error is not cancellation.";
            if (review == null) return "Repair review is not initialized. No command is available.";
            if (review.RequiresRetention)
                return "Actual resource ownership remains unconfirmed. Requests stay blocked while the owner is retained; stop is not Windows cancellation or rollback.";
            if (review.HasPendingWork || repairReviewCallActive || repairReviewTask is { IsCompleted: false })
                return "An actual history review or selected request remains owned. Wait for real settlement; no completion or repair-success verdict is implied.";
            if (review.IsHistoryBlocked)
                return "Unresolved history blocks further requests. This is not proof that Windows work is running; no reset, history clearing or automatic retry is supplied.";
            if (review.ExecutionAttempted)
                return "This view has consumed its one execution attempt. Review any historical evidence below; no further attempt or repair-success conclusion is supplied.";
            if (IsBusy)
                return "Another app operation is busy; repair-review commands remain blocked. Execution is " +
                    (review.ExecutionQualified ? "qualified" : "not qualified") + "; existing-history review is " +
                    (review.RecoveryReviewQualified ? "qualified" : "not qualified") + ". These capabilities do not establish repair success.";
            if (!review.ExecutionQualified)
                return review.RecoveryReviewQualified
                    ? review.HistoryReviewAttempted
                        ? "The one existing-history review was attempted. Repair execution is not qualified; history and acknowledgments cannot enable it."
                        : "One explicit existing-history review is available. Repair execution is not qualified; local effects review and acknowledgments cannot bypass that gate."
                    : "Repair execution and existing-history review are not qualified. Local effects review runs no command. All four acknowledgments remain insufficient to enable either gate.";
            if (!review.RecoveryReviewQualified)
                return "The repair execution boundary is qualified, but its required existing-history review is not available. Confirmation remains blocked.";
            if (review.HistoryReviewAttempted && !review.HistoryQualified)
                return "The one existing-history review did not qualify this view. Unknown, stopped or unresolved history cannot authorize repair; no retry/reset is supplied.";
            if (!review.HistoryReviewAttempted)
                return "An explicit qualified existing-history review is still required before any confirmation. Local effects preparation runs no command.";
            return RepairDraftMatches && review.CanConfirm
                ? "Current review prerequisites admit a separate exact confirmation. The owned provider still rechecks permission, identity, history and one-shot dispatch; repair success remains Unknown."
                : "Prepare a fresh exact draft and review all four acknowledgments. History supplies one prerequisite only; current selection and final provider validation remain required.";
        }
    }
    public string RepairDraftDetails => repairReview?.Draft is not { } draft
        ? "No current repair draft. Select DISM RestoreHealth or SFC scannow above, then prepare a local effects review. No history, identity or servicing query starts automatically."
        : $"Exact selected operation: {draft.Definition.Title}\nFixed command: {draft.Definition.FileName} {string.Join(" ", draft.Definition.Arguments)}\n" +
          $"Prepared UTC: {draft.PreparedUtc:O}; expires UTC: {draft.ExpiresUtc:O}\n\nEffects: {draft.Definition.Effect}\n\n" +
          "Permissions: Already-effective administrator rights are required by this repair candidate. MadClean requests no UAC, credentials, silent elevation or administrator relaunch. Identity and permission checks remain separately required before any future qualified execution.\n\n" +
          (draft.Definition.Operation == SystemToolOperation.DismRestoreHealth
              ? "Sources: Windows Update or configured repair sources may be used under Windows policy. No custom source, offline image or LimitAccess override is offered. /NoRestart requests DISM restart suppression.\n\n"
              : "Scope: Protected Windows system files; /scannow attempts repairs. No invented SFC restart switch, offline target or custom source is supplied.\n\n") +
          "Windows may change components/files and write protected servicing logs. Actual result log reference: " + draft.Definition.WindowsLogReference +
          ". Log contents are not read by preparing this review. Restore points do not recover deleted files.\n\n" +
          "Stop requests observation only and do not stop Windows, kill a client/service or promise rollback. Exit zero, completed cleanup and valid journal history do not establish successful repair or healthy Windows. Repair success and integrity remain Unknown. No restart action is supplied.";
    public string RepairRecoveryDetails
    {
        get
        {
            if (repairReview?.HistoricalRecovery is not { } result)
                return "No returned existing-history review. Preparation does not read journals. Unknown or unresolved history cannot authorize repair; missing records do not prove no Windows operation is running.";
            return $"Historical review outcome: {result.Outcome}\nRecorded history state: {result.History?.State.ToString() ?? "Unknown"}\n" +
                $"Enumeration complete: {Flag(result.History?.EnumerationComplete)}; reviewed journals: {result.History?.Journals.Count.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}\n" +
                $"Observation stop requested: {result.StopRequested}; ownership released: {result.OwnershipReleased}; history blocks admission: {Flag(result.HistoryBlocked)}\n" +
                "This is a historical source observation, not current process state, record authorship, repair success, integrity or execution permission. No records are cleared, repaired or resumed by this panel.";
        }
    }
    public string RepairResultDetails
    {
        get
        {
            if (repairReview?.HistoricalResult is not { } result)
                return "No returned repair request result. No client start, completion, successful repair or Windows integrity finding is established.";
            var client = result.ClientObservation;
            var creation = result.DirectCreation;
            var completion = result.DirectCompletion;
            return $"Historical request outcome: {result.Outcome}\nDispatch attempted: {result.DispatchAttempted}; caller stop requested: {result.StopRequested}\n" +
                $"Journal state: {result.JournalState}; actual ownership released: {result.OwnershipReleased}\n" +
                $"Client observation state: {client?.State.ToString() ?? "Unknown"}; literal exit code: {client?.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}\n" +
                $"Observation start UTC: {client?.ObservationStartedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "Unknown"}; completion UTC: {client?.ObservationCompletedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "Unknown"}\n" +
                $"Direct creation state: {creation?.State.ToString() ?? "Unknown"}; native creation error: {NativeCode(creation?.NativeError)}\n" +
                $"Direct completion stage: {completion?.Stage.ToString() ?? "Unknown"}; cleanup confirmed: {Flag(completion?.CleanupConfirmed)}; native completion error: {NativeCode(completion?.NativeError)}\n" +
                $"Stdout end-of-stream / worker release: {Flag(completion?.Stdout?.EndOfStreamObserved)} / {Flag(completion?.Stdout?.WorkerReleased)}\n" +
                $"Stderr end-of-stream / worker release: {Flag(completion?.Stderr?.EndOfStreamObserved)} / {Flag(completion?.Stderr?.WorkerReleased)}\n" +
                $"Direct evidence consistency: {Flag(result.DirectEvidenceConsistent)}\n\n" +
                "These observations remain historical and separate. KnownNotCreated is not native cancellation; uncertain creation is not a safe refusal. Numeric errors/exits, journal records and resource cleanup are not a repair-success or integrity verdict. No raw command output or Windows log contents are displayed. Repair success: Unknown. Windows integrity: Unknown.";
        }
    }

    private void InitializeRepairReview()
    {
        if (repairReview != null) return; // Reinitialization cannot reset this view's one-shot controller.
        repairReviewDispatcher = Dispatcher.CurrentDispatcher;
        repairReview = new(new WindowsSystemRepairReviewService());
        repairReview.PropertyChanged += RepairReviewChanged;
        PropertyChanged += RepairShellStateChanged;
        PrepareRepairReviewCommand = new ActionCommand(PrepareRepairReview, () => !IsBusy && !repairReviewPresentationFault &&
            RepairSelectionMatches && repairReview.CanPrepare);
        ReviewRepairHistoryCommand = new AsyncCommand(() => RunRepairReviewAsync(true), () => !IsBusy && !repairReviewPresentationFault &&
            RepairSelectionMatches && repairReview.CanReviewHistory);
        ConfirmRepairCommand = new AsyncCommand(() => RunRepairReviewAsync(false), () => !IsBusy && !repairReviewPresentationFault &&
            RepairDraftMatches && repairReview.CanConfirm);
        RequestRepairObservationStopCommand = new ActionCommand(RequestRepairObservationStop,
            () => repairReviewOwnsBusy && !repairReviewStopRequested && HasPendingRepairReview);
    }
    private void RepairSelectionChanged(SystemToolOperation? operation)
    {
        repairReview?.SelectOperation(operation);
        NotifyRepairReviewSafely();
    }
    private void PrepareRepairReview()
    {
        if (IsBusy || repairReviewPresentationFault || !RepairSelectionMatches || repairReview?.CanPrepare != true) return;
        repairReview.Prepare();
        StartRepairReviewTimer();
        NotifyRepairReviewSafely();
    }
    private async Task RunRepairReviewAsync(bool history)
    {
        if (IsBusy || repairReviewPresentationFault || !RepairSelectionMatches || repairReview == null ||
            (history ? !repairReview.CanReviewHistory : !RepairDraftMatches || !repairReview.CanConfirm)) return;
        var stop = new CancellationTokenSource();
        repairReviewStop = stop; cancellation = stop;
        repairReviewOwnsBusy = repairReviewCallActive = true; repairReviewStopRequested = false;
        Task? actual = null;
        try
        {
            // Hold global ownership before entering the model or service. Notifications
            // cannot abandon a task that has already entered an external collaborator.
            IsBusy = true;
            Status = history ? "Reviewing existing repair history; no repair is requested." : "Observing the explicitly confirmed repair request; success remains Unknown.";
            StartRepairReviewTimer();
            actual = history ? repairReview.ReviewHistoryAsync(stop.Token) : repairReview.ConfirmAsync(stop.Token);
            repairReviewTask = actual;
            NotifyRepairReviewSafely();
            await actual;
        }
        catch
        {
            repairReviewPresentationFault = true;
            SetRepairStatusSafely("Repair review could not complete normally. Any actual owned task remains observed; no cancellation, rollback or successful repair is inferred.");
        }
        finally
        {
            // A notification exception before the normal await must still drain the
            // same task. Retention cannot be cleared by a timer, caller stop or close.
            if (actual != null) { try { await actual; } catch { repairReviewPresentationFault = true; } }
            repairReviewCallActive = false;
            while (RepairReviewUnderlyingUnsettled())
            {
                RefreshRepairReviewSafely();
                SetRepairStatusSafely("Waiting for actual repair-review ownership settlement. Observation stop does not stop Windows servicing; no client or service is killed.");
                await Task.Delay(100);
            }
            if (ReferenceEquals(cancellation, stop)) cancellation = null;
            stop.Dispose();
            if (ReferenceEquals(repairReviewStop, stop)) repairReviewStop = null;
            repairReviewOwnsBusy = false;
            if (!repairReviewPresentationFault && repairReview != null) SetRepairStatusSafely(repairReview.Status);
            try { IsBusy = false; } catch { repairReviewPresentationFault = true; }
            NotifyRepairReviewSafely();
        }
    }
    private bool RepairReviewUnderlyingUnsettled()
    {
        try { return repairReviewTask is { IsCompleted: false } || repairReview?.HasPendingWork == true || repairReview?.RequiresRetention == true; }
        catch { return true; } // Unknown actual ownership cannot authorize normal close.
    }
    private void RequestRepairObservationStop()
    {
        if (!repairReviewOwnsBusy || repairReviewStopRequested) return;
        repairReviewStopRequested = true;
        try { repairReviewStop?.Cancel(); } catch { repairReviewPresentationFault = true; }
        SetRepairStatusSafely("Repair observation stop requested. This does not cancel a committed Windows operation, kill a process/service or promise rollback. Waiting for actual ownership settlement.");
        NotifyRepairReviewSafely();
    }
    private bool TryRequestRepairObservationStopFromUi()
    {
        if (!repairReviewOwnsBusy) return false;
        RequestRepairObservationStop(); return true;
    }
    public void ExplainRepairReviewCloseBlock()
    {
        SetRepairStatusSafely("Repair review or its actual resources remain pending or unconfirmed. Request observation stop if available and wait; unresolved history alone is not proof of running Windows work.");
        NotifyRepairReviewSafely();
    }
    private void ShutdownRepairReviewUi()
    {
        if (HasPendingRepairReview) { RequestRepairObservationStop(); return; }
        repairReview?.Draft?.Invalidate();
        repairReviewTimer?.Stop();
    }
    private void StartRepairReviewTimer()
    {
        if (repairReviewTimer == null)
        {
            repairReviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            repairReviewTimer.Tick += (_, _) =>
            {
                RefreshRepairReviewSafely();
                if (!HasPendingRepairReview && repairReview?.Draft == null) repairReviewTimer.Stop();
            };
        }
        repairReviewTimer.Start();
    }
    private void RefreshRepairReviewSafely()
    {
        try { repairReview?.Refresh(); } catch { repairReviewPresentationFault = true; }
        NotifyRepairReviewSafely();
    }
    private void RepairReviewChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (repairReviewDispatcher?.CheckAccess() != false) { NotifyRepairReviewSafely(); return; }
        try
        {
            if (repairReviewDispatcher.HasShutdownStarted || repairReviewDispatcher.HasShutdownFinished) { repairReviewPresentationFault = true; return; }
            _ = repairReviewDispatcher.BeginInvoke((Action)NotifyRepairReviewSafely);
        }
        catch { repairReviewPresentationFault = true; }
    }
    private void RepairShellStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(IsBusy)) RepairReviewChanged(sender, args);
    }
    private void SetRepairStatusSafely(string message)
    { try { Status = message; } catch { repairReviewPresentationFault = true; } }
    private void NotifyRepairReviewSafely()
    {
        if (publishingRepairReview) return;
        publishingRepairReview = true;
        try
        {
            bool wasFaulted = repairReviewPresentationFault;
            string[] properties = [nameof(HasPendingRepairReview), nameof(CanAcknowledgeRepairReview), nameof(RepairReviewHeading),
                nameof(RepairReviewQualification), nameof(RepairDraftDetails), nameof(RepairRecoveryDetails), nameof(RepairResultDetails)];
            NotifyRepairObservers(properties);
            try { CommandManager.InvalidateRequerySuggested(); }
            catch { repairReviewPresentationFault = true; }
            if (!wasFaulted && repairReviewPresentationFault)
                NotifyRepairObservers([nameof(CanAcknowledgeRepairReview), nameof(RepairReviewQualification), nameof(RepairReviewHeading)]);
        }
        finally { publishingRepairReview = false; }
    }
    private void NotifyRepairObservers(IEnumerable<string> properties)
    {
        var handlers = PropertyChanged?.GetInvocationList();
        if (handlers == null) return;
        foreach (string property in properties)
        foreach (PropertyChangedEventHandler handler in handlers)
        try
        {
            handler(this, new(property));
        }
        catch
        {
            // One faulty subscriber cannot hide final disabled readiness from others,
            // clear ownership, or throw back through the service's publication.
            repairReviewPresentationFault = true;
        }
    }
    private static string NativeCode(int? value) => value is { } code ? $"0x{unchecked((uint)code):X8}" : "Unknown";
}
