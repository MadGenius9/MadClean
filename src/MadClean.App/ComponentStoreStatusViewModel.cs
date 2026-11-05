using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IComponentStoreStatusProvider componentStoreStatus = new ComponentStoreStatusProvider();
    private ComponentStoreStatusPreview? componentStorePreview;
    private ComponentStoreCheckKind componentStoreSelectedKind = ComponentStoreCheckKind.RecordedFlags;
    private ComponentStoreStatusIntent? componentStoreIntent;
    private ComponentStoreStatusResult? componentStoreLastResult;
    private IComponentStoreStatusSession? componentStoreSession;
    private Task<ComponentStoreStatusResult>? componentStoreCompletion;
    private CancellationTokenSource? componentStorePreparationStop;
    private DispatcherTimer? componentStoreUiTimer;
    private bool componentStoreAcknowledged, componentStoreRunActive, componentStoreOwnsBusy, componentStoreCallerStopRequested;
    private bool componentStoreDispatchObservedByUi;
    private string componentStoreMessage = "Not checked. Preparing this separate operation shows its scope only; nothing starts with inventory.";
    private string componentStorePreviewDetails = "Prepare the fixed recorded-flags-only operation to review its source, permissions, log writes and stop limitations.";
    private string componentStoreCurrentAssessment = "Not checked. No recorded component-store assessment is available in this session.";
    private string componentStoreLastObservation = "No returned observation. The previous scan/flag date, broader Windows integrity and crash causes are unknown.";

    public ICommand PrepareComponentStoreStatusCommand { get; private set; } = null!;
    public ICommand PrepareComponentStoreScanCommand { get; private set; } = null!;
    public ICommand ConfirmComponentStoreStatusCommand { get; private set; } = null!;
    public ICommand RequestComponentStoreStopCommand { get; private set; } = null!;
    public string ComponentStoreMessage { get => componentStoreMessage; private set => Set(ref componentStoreMessage, value); }
    public string ComponentStorePreviewDetails { get => componentStorePreviewDetails; private set => Set(ref componentStorePreviewDetails, value); }
    public string ComponentStoreCurrentAssessment { get => componentStoreCurrentAssessment; private set => Set(ref componentStoreCurrentAssessment, value); }
    public string ComponentStoreLastObservation { get => componentStoreLastObservation; private set => Set(ref componentStoreLastObservation, value); }
    public ComponentStoreStatusResult? ComponentStoreLastResult => componentStoreLastResult;
    public bool HasPendingComponentStoreStatus => componentStoreRunActive || componentStoreCompletion is { IsCompleted: false } ||
        componentStoreSession?.HasPendingWork == true || componentStoreSession?.RequiresRetention == true ||
        componentStoreStatus.HasPendingWork || componentStoreStatus.RequiresRetention;
    public bool CanAcknowledgeComponentStoreStatus => ComponentStorePreviewCurrent && !IsBusy &&
        !HasPendingComponentStoreStatus && !HasPendingMsiUninstallRequest;
    public bool ComponentStoreAcknowledged
    {
        get => componentStoreAcknowledged;
        set
        {
            bool accepted = value && ComponentStorePreviewCurrent;
            Set(ref componentStoreAcknowledged, accepted);
            if (accepted != value) PropertyChanged?.Invoke(this, new(nameof(ComponentStoreAcknowledged)));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public ComponentStoreCheckKind ComponentStoreSelectedKind => componentStoreSelectedKind;
    public bool ComponentStoreSelectedKindValidated => componentStoreStatus.IsCheckKindValidated(componentStoreSelectedKind);
    public string ComponentStoreQualificationAutomationName => componentStoreSelectedKind == ComponentStoreCheckKind.ScanHealth ? "Fresh component-store scan qualification" : "Recorded component-store check qualification";
    public string ComponentStoreConfirmLabel => componentStoreSelectedKind == ComponentStoreCheckKind.ScanHealth ? "Scan component store…" : "Check recorded status…";
    public string ComponentStoreConfirmAutomationName => componentStoreSelectedKind == ComponentStoreCheckKind.ScanHealth ? "Confirm explicit fresh component-store scan" : "Confirm explicit recorded component-store status check";
    public string ComponentStoreAcknowledgementAutomationName => componentStoreSelectedKind == ComponentStoreCheckKind.ScanHealth ? "Acknowledge fresh component-store scan scope and Windows metadata writes" : "Acknowledge recorded component-store check scope and Windows log writes";
    public string ComponentStoreStatusAutomationName => componentStoreSelectedKind == ComponentStoreCheckKind.ScanHealth ? "Fresh component-store scan status" : "Recorded component-store check status";
    public string ComponentStoreAssessmentAutomationName => componentStoreSelectedKind == ComponentStoreCheckKind.ScanHealth ? "Current accepted fresh component-store scan assessment" : "Current accepted recorded component-store assessment";
    public string ComponentStoreAcknowledgementText => componentStoreSelectedKind == ComponentStoreCheckKind.ScanHealth
        ? "I reviewed this fresh component-store scan, its administrator requirement, several-minute duration, Windows log/health-metadata writes and stop limitations. No repair is selected."
        : "I reviewed this recorded-flags-only check, its administrator requirement, Windows log/scratch writes and stop limitations.";
    public string ComponentStoreQualification => ComponentStoreSelectedKindValidated
        ? componentStoreSelectedKind == ComponentStoreCheckKind.ScanHealth
            ? "This separately confirmed path scans the online Windows component store for corruption. It requests no repair. Current effective administrator access is rechecked before starting the fixed Windows host."
            : "This separately confirmed path checks recorded flags only. The provider rechecks current effective administrator access before starting the fixed Windows host; unavailable access starts no check."
        : "Confirmation is unavailable until this selected check passes its disposable Windows qualification. A preview starts no host or check, and no setting bypasses the qualification gate.";
    private bool ComponentStorePreviewCurrent => componentStorePreview is { } preview &&
        preview.Kind == componentStoreSelectedKind && DateTimeOffset.UtcNow >= preview.PreparedUtc && DateTimeOffset.UtcNow < preview.ExpiresUtc;

    private void InitializeComponentStoreStatus()
    {
        PrepareComponentStoreStatusCommand = new ActionCommand(() => PrepareComponentStoreCheck(ComponentStoreCheckKind.RecordedFlags),
            () => !IsBusy && !HasPendingComponentStoreStatus && !HasPendingMsiUninstallRequest);
        PrepareComponentStoreScanCommand = new ActionCommand(() => PrepareComponentStoreCheck(ComponentStoreCheckKind.ScanHealth),
            () => !IsBusy && !HasPendingComponentStoreStatus && !HasPendingMsiUninstallRequest);
        ConfirmComponentStoreStatusCommand = new AsyncCommand(ConfirmComponentStoreStatus,
            () => !IsBusy && !HasPendingComponentStoreStatus && !HasPendingMsiUninstallRequest &&
                ComponentStorePreviewCurrent && ComponentStoreAcknowledged && componentStorePreview is { } preview && componentStoreStatus.IsCheckKindValidated(preview.Kind));
        RequestComponentStoreStopCommand = new ActionCommand(RequestComponentStoreStop,
            () => componentStoreOwnsBusy && !componentStoreCallerStopRequested && HasPendingComponentStoreStatus);
    }

    private void PrepareComponentStoreCheck(ComponentStoreCheckKind kind)
    {
        if (IsBusy || HasPendingComponentStoreStatus || HasPendingMsiUninstallRequest) return;
        if (!Enum.IsDefined(kind)) return;
        componentStorePreview?.Invalidate(); componentStorePreview = null; ComponentStoreAcknowledged = false;
        if (componentStoreSelectedKind != kind)
            ComponentStoreCurrentAssessment = $"Not checked — selected kind: {kind}. No new check has started. Any last returned observation below is historical for this selection.";
        componentStoreSelectedKind = kind;
        var result = ComponentStoreStatusPolicy.CreatePreview(kind, DateTimeOffset.UtcNow);
        componentStorePreview = result.Items.SingleOrDefault();
        ComponentStoreMessage = result.Message;
        if (componentStorePreview is { } preview)
            ComponentStorePreviewDetails = $"Selected check: {preview.Kind}\nSource: the fixed inbox Windows PowerShell / DISM module on this PC.\nFixed command: {preview.CommandPreview}\nPrepared UTC: {preview.PreparedUtc:u}\nExpires UTC: {preview.ExpiresUtc:u}\n\nEffect: {preview.ExactEffect}\n\nPermissions: {preview.Permissions}\n\nStop behavior: {preview.Cancellation}\n\nLimits: {preview.Limitations}\n\nA confirmed host-identity check can use Windows certificate/revocation metadata under existing Windows policy. This preview itself performs no host, module or servicing query. {(preview.Kind == ComponentStoreCheckKind.RecordedFlags ? "No ScanHealth, RestoreHealth, SFC, repair, cleanup or restart option is selected." : "Only ScanHealth is selected. No RestoreHealth, SFC, repair, cleanup or restart option is selected.")}";
        else ComponentStorePreviewDetails = "No current fixed-operation preview. Prepare again before selecting any check.";
        StartComponentStoreUiTimer(); NotifyComponentStoreStatus();
    }

    private async Task ConfirmComponentStoreStatus()
    {
        if (IsBusy || HasPendingComponentStoreStatus || HasPendingMsiUninstallRequest || !ComponentStoreAcknowledged ||
            !ComponentStorePreviewCurrent || componentStorePreview is not { } preview || !componentStoreStatus.IsCheckKindValidated(preview.Kind)) return;
        var confirmed = ComponentStoreStatusPolicy.Confirm(preview, true, DateTimeOffset.UtcNow);
        if (confirmed.Items.Count != 1)
        { ComponentStoreMessage = confirmed.Message; componentStorePreview = null; ComponentStoreAcknowledged = false; NotifyComponentStoreStatus(); return; }
        var intent = confirmed.Items[0];
        componentStorePreview = null; ComponentStoreAcknowledged = false;
        componentStoreIntent = intent; componentStoreSession = null; componentStoreCompletion = null;
        componentStoreCallerStopRequested = false; componentStoreDispatchObservedByUi = false;
        componentStorePreparationStop = new CancellationTokenSource(); cancellation = componentStorePreparationStop;
        componentStoreOwnsBusy = true; componentStoreRunActive = true; IsBusy = true;
        ComponentStorePreviewDetails = "This one-shot preview was confirmed. A new attempt requires a fresh preview and acknowledgement.";
        ComponentStoreCurrentAssessment = "Unknown — this requested check has not returned a qualified current assessment. Any earlier observation below is historical.";
        ComponentStoreMessage = $"Preparing the explicitly confirmed {intent.Kind} check. Current administrator and fixed-host qualification are still required; no completed check is claimed.";
        Status = intent.Kind == ComponentStoreCheckKind.ScanHealth ? "Preparing the selected fresh component-store scan…" : "Preparing the selected recorded component-store status check…";
        Event(ActivityArea.ComponentStoreStatus, ActivityOutcome.Started);
        StartComponentStoreUiTimer(); NotifyComponentStoreStatus();
        var sessionEvidenceArea = ComponentStoreEvidenceArea(intent.Kind);
        var sessionEvidenceAttempt = BeginIntegrityEvidence(sessionEvidenceArea);
        try
        {
            componentStoreSession = await componentStoreStatus.StartAsync(intent, componentStorePreparationStop.Token);
            if (componentStoreSession == null || componentStoreSession.RequestId != intent.RequestId)
                throw new InvalidOperationException("Component-store session identity is inconsistent.");
            if (componentStoreCallerStopRequested || componentStorePreparationStop.IsCancellationRequested)
            {
                componentStoreCallerStopRequested = true;
                // Stop-delivery failure must not abandon the actual owner's completion task.
                RequestComponentStoreStop();
            }
            componentStoreCompletion = componentStoreSession.Completion;
            NotifyComponentStoreStatus();
            var actual = await componentStoreCompletion;
            // No post-await token throw: actual pipeline/client/cleanup facts survive a stop request.
            var now = DateTimeOffset.UtcNow;
            if (actual.RequestId != intent.RequestId || actual.Kind != intent.Kind || !Enum.IsDefined(actual.Kind) ||
                !Enum.IsDefined(actual.State) || !Enum.IsDefined(actual.Flag) || !Enum.IsDefined(actual.ScanFinding) ||
                actual.AttemptedUtc == default || actual.AttemptedUtc.Offset != TimeSpan.Zero ||
                actual.AttemptedUtc < intent.PreparedUtc || actual.AttemptedUtc > now || actual.Transport == null ||
                actual.CompletedUtc is { } ended && (ended.Offset != TimeSpan.Zero || ended < actual.AttemptedUtc || ended > now))
                throw new InvalidOperationException("Component-store observations are inconsistent.");
            var assessed = ComponentStoreStatusPolicy.Assess(intent, actual.Evidence, actual.Transport,
                actual.AttemptedUtc, actual.CompletedUtc, componentStoreCallerStopRequested || componentStoreSession.StopRequested);
            Set(ref componentStoreLastResult, assessed, nameof(ComponentStoreLastResult));
            ReplaceInspectionEvidence(DiagnosticIntegrityEvidence.ComponentStore(intent, assessed, DateTimeOffset.UtcNow));
            ComponentStoreLastObservation = DescribeComponentStoreObservation(assessed);
            ComponentStoreMessage = assessed.Message;
            ComponentStoreCurrentAssessment = DescribeComponentStoreAssessment(assessed);
            Status = "Selected component-store check returned; review its kind, source, coverage and stop/ownership observations.";
            bool actualFailureObserved = assessed.Evidence?.Outcome == ComponentStoreStatusOutcome.InvocationFailed ||
                assessed.Transport.HostExitObserved == true && assessed.Transport.HostExitCode is { } hostCode && hostCode != 0;
            Event(ActivityArea.ComponentStoreStatus, actualFailureObserved || assessed.State == CheckState.Failed ? ActivityOutcome.Failed :
                assessed.State == CheckState.Available && (assessed.Kind == ComponentStoreCheckKind.RecordedFlags && assessed.Flag != ComponentStoreRecordedFlag.Unknown ||
                    assessed.Kind == ComponentStoreCheckKind.ScanHealth && assessed.ScanFinding != ComponentStoreScanFinding.Unknown) ? ActivityOutcome.Completed : ActivityOutcome.Partial);
        }
        catch (OperationCanceledException)
        {
            InterruptIntegrityEvidence(sessionEvidenceArea, CheckState.Cancelled, sessionEvidenceAttempt);
            ComponentStoreCurrentAssessment = "Unknown — preparation or observation was interrupted. No Windows servicing stop, rollback or health result is established.";
            ComponentStoreMessage = "A stop was requested or preparation was interrupted. Any pending work remains with its owner; wait for actual completion. No process or service was killed.";
            Status = ComponentStoreMessage;
            Event(ActivityArea.ComponentStoreStatus, ActivityOutcome.Partial);
        }
        catch (Exception)
        {
            InterruptIntegrityEvidence(sessionEvidenceArea, CheckState.Failed, sessionEvidenceAttempt);
            ComponentStoreCurrentAssessment = "Unknown — no qualified current result was accepted. Earlier returned observations below remain historical.";
            ComponentStoreMessage = "The selected check could not return a qualified observation. Any pending host, pipeline or cleanup remains owned; no servicing completion, rollback, repair or health verdict is inferred.";
            Status = ComponentStoreMessage;
            Event(ActivityArea.ComponentStoreStatus, ActivityOutcome.Failed);
        }
        finally
        {
            componentStoreRunActive = false;
            ReleaseComponentStoreUiOwnershipIfSettled();
            NotifyComponentStoreStatus();
        }
    }

    private static string DescribeComponentStoreAssessment(ComponentStoreStatusResult result)
    {
        string assessment = result.Kind == ComponentStoreCheckKind.ScanHealth ? result.ScanFinding switch
        {
            ComponentStoreScanFinding.NoCorruptionReported => "No component-store corruption was reported by this ScanHealth invocation. This does not establish overall Windows/PC health or system-file integrity; no repair was requested.",
            ComponentStoreScanFinding.RepairableCorruptionReported => "This fresh component-store scan reported repairable corruption. Its relationship to symptoms and whether repair would succeed remain unknown; no repair was requested.",
            ComponentStoreScanFinding.NonRepairableCorruptionReported => "This fresh component-store scan reported non-repairable corruption. This is not overall PC health or an automatic reinstall/repair recommendation.",
            _ => "Unknown — no accepted current fresh-scan assessment. Any returned source member below is historical after stop or incomplete evidence/ownership."
        } : result.Flag switch
        {
            ComponentStoreRecordedFlag.NoRecordedCorruptionFlag => "No recorded corruption flag reported by this check. No fresh scan was performed; previous scan/flag date and overall Windows health remain unknown.",
            ComponentStoreRecordedFlag.RecordedRepairable => "A recorded repairable component-store flag was reported. Its relationship to crashes or other symptoms is unknown; no repair was requested.",
            ComponentStoreRecordedFlag.RecordedNonRepairable => "A recorded non-repairable component-store flag was reported. This is a component-store assessment, not overall PC health or an automatic repair recommendation.",
            _ => "Unknown — no accepted current recorded-flag assessment. Any returned source flags below are historical if a stop was requested or evidence/ownership is incomplete."
        };
        return assessment + $"\nCheck kind: {result.Kind}; attempt recorded UTC: {result.AttemptedUtc:u}; owner result completion UTC: {result.CompletedUtc?.ToString("u") ?? "Unobserved"}. " +
            (result.Kind == ComponentStoreCheckKind.RecordedFlags ? "These are not the prior corruption-flag time." : "These bound this requested scan, not a date of symptom onset or every file observation.");
    }

    private static string DescribeComponentStoreObservation(ComponentStoreStatusResult result)
    {
        static string Flag(bool? value) => value?.ToString() ?? "Unobserved";
        static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "Unobserved";
        static string Code(int? value) => value is { } actual ? $"0x{unchecked((uint)actual):X8}" : "Unobserved";
        var transport = result.Transport;
        string source = result.Evidence is { } evidence
            ? $"Source check kind: {evidence.Kind}; fixed source stage: {evidence.Stage}; outcome: {evidence.Outcome}\nHost/module/cmdlet/output contract qualified: {Flag(evidence.HostQualified)} / {Flag(evidence.ModuleBound)} / {Flag(evidence.CmdletQualified)} / {Flag(evidence.OutputContractQualified)}\nInvocation started: {Flag(evidence.InvocationStarted)}; pipeline state: {evidence.PipelineState?.ToString() ?? "Unobserved"}; EndInvoke returned: {Flag(evidence.EndInvokeReturned)}\nReported image-state member: {evidence.HealthState ?? "Unobserved"} — retained as source evidence, not overall PC health.\nReturned output/error/warning counts: {Number(evidence.OutputCount)} / {Number(evidence.ErrorCount)} / {Number(evidence.WarningCount)}\nPrivate stop event observed: {Flag(evidence.CancelEventObserved)}; BeginStop called: {Flag(evidence.BeginStopCalled)}; EndStop returned: {Flag(evidence.EndStopReturned)}\nPowerShell/runspace/cancel handle released: {Flag(evidence.PowerShellDisposed)} / {Flag(evidence.RunspaceClosed)} / {Flag(evidence.CancelHandleDisposed)}\nManaged exception HRESULT: {Code(evidence.ManagedExceptionHResult)} (managed wrapper metadata, not a DISM native HRESULT)."
            : "No accepted fixed source evidence. Unobserved or malformed fields were not substituted; transport observations remain separate.";
        return $"Latest returned observation (historical while another attempt is active or after a stop)\nAttempt recorded UTC: {result.AttemptedUtc:u}\nOwner result completion UTC: {result.CompletedUtc?.ToString("u") ?? "Unobserved"}\nObservation coverage: {result.State}\nRequested check kind: {result.Kind}\n{(result.Kind == ComponentStoreCheckKind.ScanHealth ? $"Accepted fresh-scan finding: {result.ScanFinding}" : $"Accepted recorded flag: {result.Flag}")}\n\n{source}\n\nTransport fields consistent: {ComponentStoreStatusPolicy.IsTransportConsistent(transport)}; contradictory observations grant no current assessment.\nEffective administrator observed: {Flag(transport.AdministratorObserved)}\nHost start attempted/returned: {Flag(transport.HostStartAttempted)} / {Flag(transport.HostStartObserved)}\nHost exit observed/code: {Flag(transport.HostExitObserved)} / {Number(transport.HostExitCode)}\nStdout/stderr drained: {Flag(transport.StdoutDrained)} / {Flag(transport.StderrDrained)}; output bound exceeded: {Flag(transport.OutputBoundExceeded)}; stderr character count: {Number(transport.StderrCharacterCount)}\nStop signal result/native code: {Flag(transport.StopSignalSucceeded)} / {Code(transport.StopSignalNativeError)}\nHost/transport ownership released: {Flag(transport.OwnershipReleased)}\n\n{DescribeComponentStoreHostIdentity(transport.HostIdentity)}\n\nWindows default DISM logs/archives may have been written; their contents were not read. {(result.Kind == ComponentStoreCheckKind.RecordedFlags ? "No fresh scan was requested; the prior scan/flag date remains unknown." : "ScanHealth was requested; Windows may record component-store health metadata. This is not a system-file/SFC check or overall PC health.")} No repair or restart was requested. Windows servicing stop/rollback remains unknown.";
    }

    private static string DescribeComponentStoreHostIdentity(SystemToolIdentityDiagnostic? diagnostic)
    {
        if (diagnostic == null) return "Fixed Windows host identity observation: Unobserved.";
        if (!Enum.IsDefined(diagnostic.Stage) || !Enum.IsDefined(diagnostic.Reason) || !Enum.IsDefined(diagnostic.TrustRoute) ||
            diagnostic.EmbeddedVersionState is { } embedded && !Enum.IsDefined(embedded))
            return "Fixed Windows host identity observation: Unknown; inconsistent sidecar diagnostics were withheld without discarding independent pipeline or transport facts.";
        string code = diagnostic.NativeTrustResult is { } actual ? $"0x{unchecked((uint)actual):X8}" : "Unobserved";
        return $"Fixed Windows host identity diagnostic: {diagnostic.Stage} / {diagnostic.Reason}; route: {diagnostic.TrustRoute}; native trust code: {code}. This metadata does not grant execution permission or establish Windows integrity.";
    }

    private void RequestComponentStoreStop()
    {
        if (!componentStoreOwnsBusy) return;
        componentStoreCallerStopRequested = true;
        ComponentStoreCurrentAssessment = "Unknown — stop requested. Any returned image-state member remains historical; Windows servicing stop or rollback is not established.";
        try
        {
            if (componentStoreSession != null) componentStoreSession.RequestStop();
            else componentStorePreparationStop?.Cancel();
            ComponentStoreMessage = "Stop requested for this owned pipeline/preparation. It may wait for Windows; keep MadClean open for actual completion. No process or service is killed, and no servicing cancellation or rollback is claimed.";
        }
        catch (Exception)
        {
            ComponentStoreMessage = "Stop delivery could not be confirmed. Work and ownership remain pending until actually observed; no process/service kill, servicing cancellation or rollback is inferred.";
        }
        Status = ComponentStoreMessage; NotifyComponentStoreStatus();
    }
    private bool TryRequestComponentStoreStopFromUi()
    {
        if (!componentStoreOwnsBusy) return false;
        if (!componentStoreCallerStopRequested) RequestComponentStoreStop();
        else
        {
            ComponentStoreMessage = "A stop is already requested. Wait for the actual owned pipeline, host and cleanup observations; this does not prove Windows servicing stopped or rolled back.";
            Status = ComponentStoreMessage;
        }
        return true;
    }
    public void ExplainComponentStoreCloseBlock()
    {
        ComponentStoreMessage = componentStoreIntent?.IsDispatchReserved == true
            ? "The selected component-store host, pipeline or cleanup is still pending/unconfirmed. Request pipeline stop if needed and wait for actual completion; MadClean will not kill processes or infer servicing cancellation/rollback."
            : "Selected component-store preparation or native identity work is still pending. Request stop and wait for its owner to return; no DISM invocation is inferred.";
        Status = ComponentStoreMessage;
    }
    private void ReleaseComponentStoreUiOwnershipIfSettled()
    {
        if (!componentStoreOwnsBusy || componentStoreRunActive || HasPendingComponentStoreStatus) return;
        if (ReferenceEquals(cancellation, componentStorePreparationStop)) cancellation = null;
        componentStorePreparationStop?.Dispose(); componentStorePreparationStop = null;
        componentStoreOwnsBusy = false; IsBusy = false;
    }
    private void ShutdownComponentStoreStatusUi()
    {
        componentStorePreview?.Invalidate(); componentStoreIntent?.Invalidate();
        componentStoreUiTimer?.Stop();
        if (!HasPendingComponentStoreStatus) ReleaseComponentStoreUiOwnershipIfSettled();
    }
    private void StartComponentStoreUiTimer()
    {
        if (componentStoreUiTimer == null)
        {
            componentStoreUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            componentStoreUiTimer.Tick += (_, _) =>
            {
                if (componentStoreIntent?.IsDispatchReserved == true && componentStoreRunActive && !componentStoreDispatchObservedByUi && !componentStoreCallerStopRequested)
                {
                    componentStoreDispatchObservedByUi = true;
                    ComponentStoreMessage = "The fixed-host dispatch boundary was crossed; no completed assessment of the selected check is observed yet. A stop request targets only the owned pipeline and may wait for Windows.";
                }
                if (componentStorePreview is { } preview && DateTimeOffset.UtcNow >= preview.ExpiresUtc)
                {
                    preview.Invalidate(); componentStorePreview = null; ComponentStoreAcknowledged = false;
                    ComponentStorePreviewDetails = "The operation preview expired. Prepare and acknowledge a new preview before selecting a check.";
                }
                ReleaseComponentStoreUiOwnershipIfSettled(); NotifyComponentStoreStatus();
                if (!componentStoreOwnsBusy && !HasPendingComponentStoreStatus && componentStorePreview == null)
                    componentStoreUiTimer.Stop();
            };
        }
        componentStoreUiTimer.Start();
    }
    private void NotifyComponentStoreStatus()
    {
        foreach (string property in new[] { nameof(HasPendingComponentStoreStatus), nameof(CanAcknowledgeComponentStoreStatus), nameof(ComponentStoreQualification),
            nameof(ComponentStoreSelectedKind), nameof(ComponentStoreSelectedKindValidated), nameof(ComponentStoreQualificationAutomationName), nameof(ComponentStoreConfirmLabel),
            nameof(ComponentStoreConfirmAutomationName), nameof(ComponentStoreAcknowledgementAutomationName),
            nameof(ComponentStoreStatusAutomationName), nameof(ComponentStoreAssessmentAutomationName), nameof(ComponentStoreAcknowledgementText) })
            PropertyChanged?.Invoke(this, new(property));
        CommandManager.InvalidateRequerySuggested();
    }
}
