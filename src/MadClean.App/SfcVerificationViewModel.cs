using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly ISfcVerificationProvider sfcVerification = new SfcVerificationProvider();
    private SfcVerificationIntent? sfcVerificationIntent;
    private ISfcVerificationSession? sfcVerificationSession;
    private Task<SfcVerificationResult>? sfcVerificationCompletion;
    private SfcVerificationResult? sfcVerificationLastResult;
    private CancellationTokenSource? sfcVerificationPreparationStop;
    private DispatcherTimer? sfcVerificationUiTimer;
    private bool sfcVerificationAcknowledged, sfcVerificationOwnsBusy, sfcVerificationRunActive;
    private bool sfcVerificationCallerStopRequested, sfcVerificationDispatchNotified;
    private string sfcVerificationMessage = "Not requested. Preparing a preview starts no SFC client.";
    private string sfcVerificationAssessment = "Windows protected-file integrity: Unknown. No SFC client observation is available.";
    private string sfcVerificationLastObservation = "No returned SFC client observation. A numeric exit code does not establish integrity or successful verification.";

    public ICommand ConfirmSfcVerificationCommand { get; private set; } = null!;
    public ICommand RequestSfcObservationStopCommand { get; private set; } = null!;
    public bool SfcVerificationExecutionValidated => sfcVerification.ProductionExecutionValidated;
    public SfcVerificationResult? SfcVerificationLastResult => sfcVerificationLastResult;
    public string SfcVerificationMessage { get => sfcVerificationMessage; private set => Set(ref sfcVerificationMessage, value); }
    public string SfcVerificationAssessment { get => sfcVerificationAssessment; private set => Set(ref sfcVerificationAssessment, value); }
    public string SfcVerificationLastObservation { get => sfcVerificationLastObservation; private set => Set(ref sfcVerificationLastObservation, value); }
    public bool HasPendingSfcVerification => sfcVerificationRunActive || sfcVerificationCompletion is { IsCompleted: false } ||
        sfcVerificationSession?.HasPendingWork == true || sfcVerificationSession?.RequiresRetention == true ||
        sfcVerification.HasPendingWork || sfcVerification.RequiresRetention;
    public bool CanAcknowledgeSfcVerification => !IsBusy && !HasPendingSfcVerification &&
        !HasPendingComponentStoreStatus && !HasPendingMsiUninstallRequest && SfcVerificationPreviewCurrent;
    public bool SfcVerificationAcknowledged
    {
        get => sfcVerificationAcknowledged;
        set
        {
            bool accepted = value && CanAcknowledgeSfcVerification;
            Set(ref sfcVerificationAcknowledged, accepted);
            if (accepted != value) PropertyChanged?.Invoke(this, new(nameof(SfcVerificationAcknowledged)));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SfcVerificationQualification => SfcVerificationExecutionValidated
        ? "This separate non-repair request requires your current preview, acknowledgement and confirmation. Existing administrator access is rechecked; MadClean requests no elevation."
        : "SFC verification is not available in this build while validation is pending. You may review a fresh identity-bound preview; no checkbox or setting starts the client.";
    private bool SfcVerificationPreviewCurrent => sfcVerificationPreview is { } preview &&
        selectedSystemTool?.Operation == SystemToolOperation.SfcVerifyOnly && systemToolPreview is { } identity &&
        identity.Definition.Operation == SystemToolOperation.SfcVerifyOnly && preview.IdentityPreviewId == identity.PreviewId &&
        preview.ToolPath == identity.ToolPath && preview.ToolVersion == identity.ToolVersion && preview.Sha256 == identity.Sha256 &&
        preview.ExpiresUtc == identity.ExpiresUtc && DateTimeOffset.UtcNow >= preview.PreparedUtc &&
        DateTimeOffset.UtcNow >= identity.CheckedUtc && DateTimeOffset.UtcNow < preview.ExpiresUtc;

    private void InitializeSfcVerification()
    {
        ConfirmSfcVerificationCommand = new AsyncCommand(ConfirmSfcVerification,
            () => CanAcknowledgeSfcVerification && SfcVerificationAcknowledged && SfcVerificationExecutionValidated);
        RequestSfcObservationStopCommand = new ActionCommand(RequestSfcObservationStop,
            () => sfcVerificationOwnsBusy && !sfcVerificationCallerStopRequested && HasPendingSfcVerification);
    }
    private void InvalidateSfcVerificationSelection()
    {
        if (sfcVerificationOwnsBusy) return;
        sfcVerificationIntent?.Invalidate(); sfcVerificationIntent = null;
        SfcVerificationAcknowledged = false;
        SfcVerificationAssessment = "Windows protected-file integrity: Unknown. No current selected request; earlier observations below are historical.";
        NotifySfcVerification();
    }
    private void SfcVerificationPreviewPrepared()
    {
        SfcVerificationMessage = "Review the fixed verify-only scope, permissions, output handling and stop limitations before acknowledgement. Preparing this preview requests no client.";
        StartSfcVerificationUiTimer(); NotifySfcVerification();
    }
    private async Task ConfirmSfcVerification()
    {
        if (!CanAcknowledgeSfcVerification || !SfcVerificationAcknowledged || !SfcVerificationExecutionValidated ||
            sfcVerificationPreview is not { } preview) return;
        // The command, rather than its visual enabled state, rechecks the exact
        // selected identity/source/freshness before one-shot confirmation.
        var confirmed = SfcVerificationPolicy.Confirm(preview, true, true, DateTimeOffset.UtcNow);
        if (confirmed.Items.Count != 1)
        {
            ClearSfcVerificationPreview(); SfcVerificationMessage = confirmed.Message;
            NotifySfcVerification(); return;
        }
        var intent = confirmed.Items[0];
        sfcVerificationIntent = intent; sfcVerificationPreview = null; SfcVerificationAcknowledged = false;
        sfcVerificationSession = null; sfcVerificationCompletion = null;
        sfcVerificationCallerStopRequested = false; sfcVerificationDispatchNotified = false;
        sfcVerificationPreparationStop = new CancellationTokenSource(); cancellation = sfcVerificationPreparationStop;
        sfcVerificationOwnsBusy = true; sfcVerificationRunActive = true; IsBusy = true;
        SfcVerificationPreviewDetails = "This one-shot preview was confirmed. A new attempt requires a fresh identity, preview and acknowledgement.";
        SfcVerificationPreviewMessage = "Request confirmed; no completed client or integrity result is claimed.";
        SfcVerificationAssessment = "Windows protected-file integrity: Unknown. Waiting for the selected client observation; any earlier result below is historical.";
        SfcVerificationMessage = "Preparing the confirmed fixed verify-only request. Administrator and renewed held executable identity checks are still required; client start is not yet reported.";
        Status = "Preparing the selected SFC verify-only request…";
        Event(ActivityArea.SfcVerification, ActivityOutcome.Started);
        StartSfcVerificationUiTimer(); NotifySfcVerification();
        var sessionEvidenceAttempt = BeginIntegrityEvidence(DiagnosticEvidenceArea.SystemFiles);
        try
        {
            sfcVerificationSession = await sfcVerification.StartAsync(intent, sfcVerificationPreparationStop.Token);
            if (sfcVerificationSession == null || sfcVerificationSession.RequestId != intent.RequestId)
                throw new InvalidOperationException();
            sfcVerificationCompletion = sfcVerificationSession.Completion;
            if (sfcVerificationCallerStopRequested || sfcVerificationPreparationStop.IsCancellationRequested)
                sfcVerificationSession.RequestStopObservation();
            NotifySfcVerification();
            var actual = await sfcVerificationCompletion;
            // Do not throw for caller cancellation after the await: real exit and
            // drain/cleanup facts survive an observation-stop request.
            var now = DateTimeOffset.UtcNow;
            if (actual.RequestId != intent.RequestId || !Enum.IsDefined(actual.State) || actual.Integrity != SfcIntegrityAssessment.Unknown ||
                actual.Transport == null || !SfcVerificationPolicy.IsTransportConsistent(actual.Transport) ||
                actual.AttemptedUtc == default || actual.AttemptedUtc.Offset != TimeSpan.Zero ||
                actual.AttemptedUtc < intent.PreparedUtc || actual.AttemptedUtc > now ||
                actual.CompletedUtc is { } end && (end.Offset != TimeSpan.Zero || end < actual.AttemptedUtc || end > now))
                throw new InvalidOperationException();
            // A UI-accepted stop can race the owner's final publication latch.
            // Preserve every actual client fact, but keep that UI observation
            // historical even when the backend had already sealed its result.
            var transport = actual.Transport with
            {
                StopObservationRequested = actual.Transport.StopObservationRequested ||
                    sfcVerificationCallerStopRequested || sfcVerificationSession.StopObservationRequested
            };
            var assessed = SfcVerificationPolicy.Assess(intent, transport, actual.AttemptedUtc, actual.CompletedUtc);
            if (actual.State == CheckState.Available && assessed.State != CheckState.Available && assessed.State != CheckState.Cancelled)
                throw new InvalidOperationException();
            Set(ref sfcVerificationLastResult, assessed, nameof(SfcVerificationLastResult));
            ReplaceInspectionEvidence(DiagnosticIntegrityEvidence.Sfc(intent, assessed, DateTimeOffset.UtcNow));
            SfcVerificationLastObservation = DescribeSfcVerificationObservation(assessed);
            SfcVerificationAssessment = "Windows protected-file integrity: Unknown. " +
                (assessed.Transport.ClientExitObserved == true
                    ? $"The selected client exited with code {assessed.Transport.ClientExitCode?.ToString(CultureInfo.InvariantCulture) ?? "Unobserved"}; this alone does not establish successful verification or absence of corruption."
                    : "No completed client exit was accepted for this request.") +
                (assessed.State == CheckState.Cancelled ? " Observation stop was requested; actual facts remain historical. No native cancellation or rollback is established." : " No repair or restart was requested.");
            SfcVerificationMessage = assessed.Message;
            Status = "Selected SFC request returned; review actual client observations. Windows integrity remains Unknown.";
            Event(ActivityArea.SfcVerification, assessed.State == CheckState.Failed ? ActivityOutcome.Failed :
                assessed.State == CheckState.Cancelled ? ActivityOutcome.Cancelled : ActivityOutcome.Partial);
        }
        catch (OperationCanceledException)
        {
            InterruptIntegrityEvidence(DiagnosticEvidenceArea.SystemFiles, CheckState.Cancelled, sessionEvidenceAttempt);
            SfcVerificationAssessment = "Windows protected-file integrity: Unknown. Preparation or observation was interrupted; no SFC cancellation, rollback or completed verification is established.";
            SfcVerificationMessage = "An observation stop or preparation interruption occurred. Actual owners remain held until completion; no process or service was killed.";
            Status = SfcVerificationMessage; Event(ActivityArea.SfcVerification, ActivityOutcome.Partial);
        }
        catch (Exception)
        {
            InterruptIntegrityEvidence(DiagnosticEvidenceArea.SystemFiles, CheckState.Failed, sessionEvidenceAttempt);
            SfcVerificationAssessment = "Windows protected-file integrity: Unknown. No qualified current client observation was accepted; earlier results below remain historical.";
            SfcVerificationMessage = "The selected request could not return a consistent observation. Pending client, output-drain, identity and cleanup ownership remains held; no success or health verdict is inferred.";
            Status = SfcVerificationMessage; Event(ActivityArea.SfcVerification, ActivityOutcome.Failed);
        }
        finally
        {
            sfcVerificationRunActive = false;
            ReleaseSfcVerificationUiOwnershipIfSettled(); NotifySfcVerification();
        }
    }

    private static string DescribeSfcVerificationObservation(SfcVerificationResult result)
    {
        static string Flag(bool? value) => value?.ToString() ?? "Unobserved";
        static string Time(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "Unobserved";
        var facts = result.Transport;
        return $"Latest returned observation — historical while another request is active or after observation stop\n" +
            $"Coverage: {result.State}\nIntegrity: Unknown\nAttempt UTC: {Time(result.AttemptedUtc)}\nOwner result completion UTC: {Time(result.CompletedUtc)}\n" +
            $"Effective administrator observed: {Flag(facts.AdministratorObserved)}\nClient start attempted/observed: {Flag(facts.ClientStartAttempted)} / {Flag(facts.ClientStartObserved)}\n" +
            $"Actual client start UTC: {Time(facts.ClientStartedUtc)}\nActual client exit observed/code: {Flag(facts.ClientExitObserved)} / {facts.ClientExitCode?.ToString(CultureInfo.InvariantCulture) ?? "Unobserved"}\n" +
            $"Actual client exit UTC: {Time(facts.ClientExitedUtc)}\nStdout/stderr drained: {Flag(facts.StdoutDrained)} / {Flag(facts.StderrDrained)}\n" +
            $"Ownership released: {Flag(facts.OwnershipReleased)}\nObservation stop requested: {facts.StopObservationRequested}\n\n" +
            $"Client output was discarded, not interpreted or retained. Protected Windows log reference: {result.WindowsLogReference}. Its contents were not read. Numeric exit and completed drains are not Windows integrity or repair findings. No repair, elevation request or restart was performed.";
    }
    private void RequestSfcObservationStop()
    {
        if (!sfcVerificationOwnsBusy || sfcVerificationCallerStopRequested) return;
        if (sfcVerificationCompletion is { IsCompleted: true } && sfcVerificationSession?.HasPendingWork != true &&
            sfcVerificationSession?.RequiresRetention != true && !sfcVerification.HasPendingWork && !sfcVerification.RequiresRetention)
        { SfcVerificationMessage = "The owner already returned; awaiting the recorded UI result. No new stop request was sent."; return; }
        sfcVerificationCallerStopRequested = true;
        try { sfcVerificationPreparationStop?.Cancel(); sfcVerificationSession?.RequestStopObservation(); }
        catch (Exception) { /* The actual owner/completion stays retained even if stop delivery is uncertain. */ }
        SfcVerificationMessage = "Observation stop requested. This does not stop SFC or a Windows service; waiting for actual client exit, output drains and ownership settlement. No process is killed or rollback claimed.";
        Status = SfcVerificationMessage; NotifySfcVerification();
    }
    private bool TryRequestSfcObservationStopFromUi()
    {
        if (!sfcVerificationOwnsBusy) return false;
        if (!sfcVerificationCallerStopRequested) RequestSfcObservationStop();
        return true;
    }
    public void ExplainSfcVerificationCloseBlock()
    {
        SfcVerificationMessage = sfcVerificationOwnsBusy
            ? "The selected SFC request or its identity, client, output drains or cleanup is still pending/unconfirmed. Request observation stop if needed and wait for actual settlement; no client/service kill or native cancellation is inferred."
            : "A cooperating Windows diagnostic owner is pending or retained. Review the active diagnostic and wait for its actual settlement; this does not establish that an SFC client started.";
        Status = SfcVerificationMessage;
    }
    private void ReleaseSfcVerificationUiOwnershipIfSettled()
    {
        if (!sfcVerificationOwnsBusy || sfcVerificationRunActive || HasPendingSfcVerification) return;
        if (ReferenceEquals(cancellation, sfcVerificationPreparationStop)) cancellation = null;
        sfcVerificationPreparationStop?.Dispose(); sfcVerificationPreparationStop = null;
        sfcVerificationOwnsBusy = false; IsBusy = false;
    }
    public void ShutdownSfcVerification()
    {
        sfcVerificationPreview?.Invalidate();
        if (HasPendingSfcVerification) return;
        sfcVerificationIntent?.Invalidate(); sfcVerificationUiTimer?.Stop();
        ReleaseSfcVerificationUiOwnershipIfSettled();
    }
    private void StartSfcVerificationUiTimer()
    {
        if (sfcVerificationUiTimer == null)
        {
            sfcVerificationUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            sfcVerificationUiTimer.Tick += (_, _) =>
            {
                if (sfcVerificationPreview is { } preview && DateTimeOffset.UtcNow >= preview.ExpiresUtc)
                {
                    ClearSfcVerificationPreview();
                    SfcVerificationPreviewMessage = "The original identity/operation preview expired. Verify identity and prepare a new preview before acknowledgement or confirmation.";
                }
                if (sfcVerificationRunActive && sfcVerificationIntent is { IsDispatchReserved: false } intent && DateTimeOffset.UtcNow >= intent.ExpiresUtc)
                {
                    intent.Invalidate();
                    sfcVerificationPreparationStop?.Cancel();
                }
                if (sfcVerificationRunActive && sfcVerificationIntent?.IsDispatchReserved == true &&
                    !sfcVerificationDispatchNotified && !sfcVerificationCallerStopRequested)
                {
                    sfcVerificationDispatchNotified = true;
                    SfcVerificationMessage = "The one-shot dispatch boundary was reserved. Actual client start and completion are not yet reported; Windows may take many minutes. No progress percentage, ETA or integrity result is inferred.";
                }
                ReleaseSfcVerificationUiOwnershipIfSettled(); NotifySfcVerification();
                if (!sfcVerificationOwnsBusy && !HasPendingSfcVerification && sfcVerificationPreview == null)
                    sfcVerificationUiTimer.Stop();
            };
        }
        sfcVerificationUiTimer.Start();
    }
    private void NotifySfcVerification()
    {
        foreach (string property in new[] { nameof(HasPendingSfcVerification), nameof(CanAcknowledgeSfcVerification),
            nameof(SfcVerificationExecutionValidated), nameof(SfcVerificationQualification), nameof(SfcVerificationLastPreview) })
            PropertyChanged?.Invoke(this, new(property));
        CommandManager.InvalidateRequerySuggested();
    }
}
