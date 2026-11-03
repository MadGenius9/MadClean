using System.Windows.Input;
using System.Windows.Threading;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly WindowsMsiSelectedProductPreparation msiUninstallPreparationReader = new();
    private readonly MsiUninstallRequestLauncher msiUninstallLauncher = new();
    private MsiUninstallRequestPreview? msiUninstallPreview;
    private InstalledApplication? msiUninstallPreviewSelection;
    private CheckResult<InstalledApplication>? msiUninstallPreviewSnapshot;
    private MsiUninstallRequestIntent? msiUninstallActiveIntent;
    private MsiSelectedProductReadScope? msiUninstallReadScope;
    private Task<CheckResult<MsiSelectedProductPreparation>>? msiUninstallPreparation;
    private Task<MsiUninstallRequestResult>? msiUninstallSubmission;
    private MsiUninstallRequestResult? msiUninstallLastResult;
    private DispatcherTimer? msiUninstallUiTimer;
    private long msiUninstallGeneration;
    private bool msiUninstallAcknowledged, msiUninstallSubmissionActive, msiUninstallPreparationNeedsDrain, msiUninstallUiReservedObserved;
    private string msiUninstallMessage = "Not prepared. This separate selected MSI request never runs during inventory.";
    private string msiUninstallPreviewDetails = "Select one actual Windows Installer registration, then prepare a fresh exact-product preview. No registered uninstall command is read.";
    private string msiUninstallResultDetails = "No MSI uninstall request in this session. Registration absence and leftover ownership have not been checked.";

    public ICommand PrepareMsiUninstallRequestCommand { get; private set; } = null!;
    public ICommand ConfirmMsiUninstallRequestCommand { get; private set; } = null!;
    public string MsiUninstallMessage { get => msiUninstallMessage; private set => Set(ref msiUninstallMessage, value); }
    public string MsiUninstallPreviewDetails { get => msiUninstallPreviewDetails; private set => Set(ref msiUninstallPreviewDetails, value); }
    public string MsiUninstallResultDetails { get => msiUninstallResultDetails; private set => Set(ref msiUninstallResultDetails, value); }
    public MsiUninstallRequestResult? MsiUninstallLastResult { get => msiUninstallLastResult; private set => Set(ref msiUninstallLastResult, value); }
    public bool MsiUninstallAcknowledged
    {
        get => msiUninstallAcknowledged;
        set
        {
            bool accepted = value && MsiUninstallPreviewCurrent;
            Set(ref msiUninstallAcknowledged, accepted);
            if (accepted != value) PropertyChanged?.Invoke(this, new(nameof(MsiUninstallAcknowledged)));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool MsiUninstallSubmissionActive => msiUninstallSubmissionActive;
    public bool MsiUninstallDispatchReserved => msiUninstallSubmissionActive && msiUninstallActiveIntent?.IsDispatchReserved == true;
    public bool HasPendingMsiUninstallRequest => msiUninstallSubmissionActive ||
        msiUninstallPreparation is { IsCompleted: false } || msiUninstallSubmission is { IsCompleted: false } ||
        msiUninstallPreparationNeedsDrain && msiUninstallPreparationReader.HasPendingNativeRead ||
        MsiUninstallRequestLauncher.HasPendingRequest || MsiUninstallRequestLauncher.RequiresRetention;
    public bool CanChangeInstalledAppSelection => !msiUninstallSubmissionActive && !HasPendingMsiUninstallRequest;
    public bool CanAcknowledgeMsiUninstallRequest => CanChangeInstalledAppSelection && MsiUninstallPreviewCurrent;
    public string MsiUninstallQualification => !MsiUninstallRequestLauncher.ProductionDispatchValidated
        ? "Request confirmation is unavailable until this separate Windows Installer route passes its disposable Windows qualification. Preparing a preview is read only; no setting bypasses the qualification gate."
        : msiUninstallPreview is { } preview && !MsiUninstallRequestLauncher.IsProductionContextQualified(preview.Context)
            ? $"The selected {preview.Context} context is not qualified for this request route. Only a separately validated current-user unmanaged product is supported; this preview does not enable machine or managed-user removal."
            : "This separately confirmed route supports a validated current-user unmanaged ProductCode only and requests visible Windows Installer UI. Windows controls permissions and prompts; machine/managed-user contexts are unavailable.";
    private bool MsiUninstallPreviewCurrent => msiUninstallPreview is { } preview &&
        !preview.Intent.IsInvalidated && !preview.Intent.IsDispatchReserved &&
        ReferenceEquals(msiUninstallPreviewSelection, selectedApplication) &&
        ReferenceEquals(msiUninstallPreviewSnapshot, latestInstalledSnapshot) &&
        preview.Intent.SelectedProductScope is { } selectedScope && ReferenceEquals(selectedScope, msiUninstallReadScope) &&
        MsiSelectedProductPreparationPolicy.IsFresh(selectedScope, msiUninstallGeneration, DateTimeOffset.UtcNow) &&
        preview.Intent.SourceGeneration == msiUninstallGeneration &&
        DateTimeOffset.UtcNow >= preview.PreparedUtc && DateTimeOffset.UtcNow < preview.ExpiresUtc;

    private void InitializeMsiUninstallRequest()
    {
        PrepareMsiUninstallRequestCommand = new AsyncCommand(PrepareMsiUninstallRequest,
            () => !IsBusy && !HasPendingMsiUninstallRequest && latestInstalledSnapshot != null &&
                selectedApplication?.RegistrationEvidence?.WindowsInstaller == true);
        ConfirmMsiUninstallRequestCommand = new AsyncCommand(ConfirmMsiUninstallRequest,
            () => !IsBusy && !HasPendingMsiUninstallRequest && MsiUninstallAcknowledged &&
                MsiUninstallPreviewCurrent && msiUninstallPreview is { } preview &&
                MsiUninstallRequestLauncher.IsProductionContextQualified(preview.Context));
    }

    private void InvalidateMsiUninstallRequest()
    {
        msiUninstallGeneration++;
        msiUninstallPreview?.Intent.Invalidate();
        if (!msiUninstallSubmissionActive || msiUninstallActiveIntent?.IsDispatchReserved != true)
            msiUninstallReadScope?.Invalidate();
        msiUninstallReadScope = null;
        // The atomic dispatch boundary cannot be revoked by changing a displayed selection.
        if (msiUninstallSubmissionActive && msiUninstallActiveIntent?.IsDispatchReserved != true)
            cancellation?.Cancel();
        msiUninstallPreview = null; msiUninstallPreviewSelection = null; msiUninstallPreviewSnapshot = null;
        MsiUninstallAcknowledged = false;
        MsiUninstallPreviewDetails = "No current request preview. Selection, search or registration-source changes require a new exact-product review. Previous request observations below remain historical.";
        if (!msiUninstallSubmissionActive)
            MsiUninstallMessage = "Not prepared for this selection. No installer was requested by this invalidation.";
        NotifyMsiUninstallRequest();
    }

    private Task PrepareMsiUninstallRequest()
    {
        InstalledApplication? selected = null;
        CheckResult<InstalledApplication>? snapshot = null;
        long generation = -1;
        bool Current() => generation == msiUninstallGeneration && ReferenceEquals(selected, selectedApplication) && ReferenceEquals(snapshot, latestInstalledSnapshot);
        return RunDiscoveryAsync(ActivityArea.MsiUninstallRequest, "Preparing selected MSI request", async token =>
        {
            selected = selectedApplication; snapshot = latestInstalledSnapshot;
            InvalidateMsiUninstallRequest(); generation = msiUninstallGeneration;
            if (selected == null || snapshot == null || HasPendingMsiUninstallRequest)
            { MsiUninstallMessage = "Select a current MSI registration and wait for existing work to settle; no preparation started."; return CheckState.Unknown; }
            var scoped = MsiSelectedProductPreparationPolicy.CreateScope(snapshot, selected, generation, DateTimeOffset.UtcNow, token);
            if (scoped.Items.Count != 1)
            { MsiUninstallMessage = Stamp(scoped.State, scoped.Message); return scoped.State; }
            var selectedScope = scoped.Items[0]; msiUninstallReadScope = selectedScope;
            using var invalidationOnCancel = token.Register(selectedScope.Invalidate);
            MsiUninstallMessage = "Reading this exact ProductCode in the three supported ARP locations and native current-user/machine MSI contexts. Global inventory is not refreshed, and no installer starts during preparation.";
            msiUninstallPreparation = msiUninstallPreparationReader.ReadAsync(selectedScope, generation, token);
            StartMsiUninstallUiTimer(); NotifyMsiUninstallRequest();
            try
            {
                var preparation = await msiUninstallPreparation;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                var prepared = MsiUninstallRequestPolicy.CreatePreview(selectedScope, preparation, generation, DateTimeOffset.UtcNow, token);
                msiUninstallPreview = prepared.Items.SingleOrDefault();
                if (msiUninstallPreview is { } actualPreview && !ReferenceEquals(actualPreview.Intent.SelectedProductScope, selectedScope))
                { actualPreview.Intent.Invalidate(); msiUninstallPreview = null; throw new InvalidOperationException("Selected-product preview scope is inconsistent."); }
                msiUninstallPreviewSelection = msiUninstallPreview == null ? null : selected;
                msiUninstallPreviewSnapshot = msiUninstallPreview == null ? null : snapshot;
                MsiUninstallMessage = Stamp(prepared.State, prepared.Message);
                MsiUninstallPreviewDetails = DescribeMsiSelectedProductPreparation(selectedScope, preparation);
                if (msiUninstallPreview is { } preview)
                    MsiUninstallPreviewDetails += $"\n\nSelected registration: {preview.Name} · {preview.Version}\nReported publisher: {preview.Publisher}\nOriginal registration source: {preview.Source}\nRegistration identity: {preview.RegistrationIdentity}\nProductCode: {preview.ProductCode}\nUnique actually observed native context: {preview.Context}\nPrepared UTC: {preview.PreparedUtc:u}\nExpires UTC: {preview.ExpiresUtc:u}\n\nExact Windows Installer request: msiexec.exe {string.Join(' ', preview.Arguments)}\n\n{preview.ExactEffect}\n\n{preview.Limitations}\n\nWindows Installer resolves the registered ProductCode. ARP hive provenance does not determine the native context, and this is not an atomic context or immutable package-payload binding. Review Windows Installer's own prompts before continuing.";
                else MsiUninstallPreviewDetails += "\n\nNo request preview was accepted. " + prepared.Message;
                NotifyMsiUninstallRequest();
                return prepared.State;
            }
            finally
            {
                bool retainedByPreview = Current() && ReferenceEquals(msiUninstallPreview?.Intent.SelectedProductScope, selectedScope);
                if (!retainedByPreview)
                {
                    selectedScope.Invalidate();
                    if (ReferenceEquals(msiUninstallReadScope, selectedScope)) msiUninstallReadScope = null;
                }
                msiUninstallPreparationNeedsDrain = msiUninstallPreparationReader.HasPendingNativeRead;
                NotifyMsiUninstallRequest();
            }
        }, error =>
        {
            if (!Current()) return;
            msiUninstallReadScope?.Invalidate(); msiUninstallReadScope = null;
            msiUninstallPreview?.Intent.Invalidate(); msiUninstallPreview = null; msiUninstallPreviewSelection = null; msiUninstallPreviewSnapshot = null; MsiUninstallAcknowledged = false;
            MsiUninstallMessage = error + " No request preview or installer launch is claimed. Any pending native read retains ownership.";
            MsiUninstallPreviewDetails = "No current exact-product request preview; prepare again after work settles.";
            NotifyMsiUninstallRequest();
        });
    }

    private static string DescribeMsiSelectedProductPreparation(MsiSelectedProductReadScope scope,
        CheckResult<MsiSelectedProductPreparation> preparation)
    {
        string summary = $"Historical GLOBAL inventory coverage: {scope.HistoricalGlobalSnapshotState}\nCurrent GLOBAL inventory: Not checked by this exact-product read. Historical coverage is not upgraded.\nOriginal ARP provenance: {MsiSelectedProductPreparationPolicy.SourceLabel(scope.OriginalSource)}\nRead selector validated UTC: {scope.ValidatedUtc:u}\nARP provenance and actual native MSI context are independent observations; the selected hive supplies no native-context assertion.";
        if (preparation.Items.Count != 1 || !ReferenceEquals(preparation.Items[0].Scope, scope))
            return summary + "\n\nNo accepted same-selector target preparation report was returned. Target lookup coverage and native context remain Unknown; no global absence or action permission is inferred.";
        var report = preparation.Items[0];
        string lookups = string.Join("\n", report.Lookups.Take(3).Select(row =>
            $"{(Enum.IsDefined(row.Source) ? MsiSelectedProductPreparationPolicy.SourceLabel(row.Source) : "Unknown source")} · {row.State} · observed UTC: {row.ObservedUtc:u} · native code: {(row.NativeError is { } error ? $"0x{unchecked((uint)error):X8}" : "Unobserved")}"));
        string instances = report.Instances.Count == 0 ? "No completed native context observation; this is not global absence proof."
            : string.Join("\n", report.Instances.Take(3).Select(row =>
                $"Native context: {row.Context} · reported product state: {row.ProductState} · observed UTC: {row.ObservedUtc:u}"));
        return summary + $"\n\nTarget lookup coverage: {report.TargetCoverageState}; preparation state/reason: {report.State} / {report.Reason}\nTarget facts assessed UTC: {report.CheckedUtc:u}\nCurrent global inventory was read: {report.CurrentGlobalSnapshotWasRead}; current global coverage: {report.CurrentGlobalCoverage}\n\nExact-GUID ARP lookup observations:\n{lookups}\n\nNative current-user/machine enumeration complete: {report.EnumerationComplete}; issue count: {report.NativeIssueCount}\n{instances}\n\nThese are sequential read-only target facts, not a frozen registry/context/payload, global inventory completion or execution permission.";
    }

    private async Task ConfirmMsiUninstallRequest()
    {
        if (IsBusy || HasPendingMsiUninstallRequest || !MsiUninstallAcknowledged || !MsiUninstallPreviewCurrent ||
            msiUninstallPreview is not { } preview || !MsiUninstallRequestLauncher.IsProductionContextQualified(preview.Context)) return;
        var intent = preview.Intent;
        long generation = msiUninstallGeneration;
        using var stop = new CancellationTokenSource();
        cancellation = stop; IsBusy = true; msiUninstallSubmissionActive = true; msiUninstallActiveIntent = intent; msiUninstallUiReservedObserved = false;
        MsiUninstallAcknowledged = false;
        MsiUninstallMessage = "Confirmed request: refreshing exact registration/tool evidence before dispatch. Cancel can stop preparation before the dispatch boundary; after it, use Windows Installer's Cancel control if offered, otherwise wait for supported completion. MadClean will not kill the installer.";
        MsiUninstallResultDetails = "No launch or completed uninstall is claimed yet. MadClean will retain actual process observations; registration absence remains unchecked.";
        Status = "Preparing the confirmed Windows Installer request…";
        Event(ActivityArea.MsiUninstallRequest, ActivityOutcome.Started);
        StartMsiUninstallUiTimer(); NotifyMsiUninstallRequest();
        try
        {
            msiUninstallSubmission = msiUninstallLauncher.RequestAsync(intent, true, generation, stop.Token);
            var result = await msiUninstallSubmission;
            // A cancelled UI token after dispatch cannot erase an actual client exit observation.
            if (!ConsistentMsiUninstallResult(intent, result, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("Installer request returned inconsistent observations.");
            MsiUninstallLastResult = result;
            MsiUninstallMessage = result.Message;
            string startObservation = result.ClientStartObserved switch { true => "Started (start call returned true)", false => "Start call returned false", null => "Unobserved" };
            MsiUninstallResultDetails = $"Request observation: {result.State}\nRequested UTC: {result.RequestedUtc:u}\nActual client-start observation: {startObservation}\nObserved OS client start UTC: {result.StartedUtc?.ToString("u") ?? "Unobserved"}\nActual client exit UTC: {result.ExitedUtc?.ToString("u") ?? "Unobserved"}\nActual client exit code: {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unobserved"}\nWindows Installer reported outcome: {result.ExitDisposition?.ToString() ?? "Unobserved"}\n\n{MsiUninstallToolIdentityDetails(result.ToolIdentity)}\n\n{result.Message}\n\nRegistration postcheck: Not checked. Refresh registrations separately and review its coverage; client exit is not proof of global absence or every uninstall effect. The retained baseline is unchanged. Leftover candidates remain candidates, never proven junk. No automatic file removal, rollback or crash recovery is provided.";
            Status = "Windows Installer request observation: " + result.State + ". Review its actual result and refresh registrations separately.";
            var outcome = result.ExitDisposition is MsiUninstallExitDisposition.FailureReported or
                MsiUninstallExitDisposition.RestartInitiatedUnexpectedly or MsiUninstallExitDisposition.InstallerBusy
                ? ActivityOutcome.Failed
                : result.State != MsiUninstallRequestState.ClientExited ? ActivityOutcome.Partial : result.ExitDisposition switch
            {
                MsiUninstallExitDisposition.SuccessReported => ActivityOutcome.Completed,
                MsiUninstallExitDisposition.CancelledByUser => ActivityOutcome.Cancelled,
                _ => ActivityOutcome.Partial
            };
            Event(ActivityArea.MsiUninstallRequest, outcome);
        }
        catch (OperationCanceledException)
        {
            MsiUninstallMessage = intent.IsDispatchReserved
                ? "Request observation was interrupted after the dispatch boundary. Cancellation does not prove that Windows Installer stopped; no rollback or uninstall result is inferred."
                : "Request preparation cancelled before dispatch; no client launch is claimed.";
            MsiUninstallResultDetails = MsiUninstallMessage + " Registration absence remains unchecked. Any pending owner must settle before another request or app close.";
            Status = MsiUninstallMessage;
            Event(ActivityArea.MsiUninstallRequest, intent.IsDispatchReserved ? ActivityOutcome.Partial : ActivityOutcome.Cancelled);
        }
        catch (Exception)
        {
            MsiUninstallMessage = "The request could not return a qualified observation. No completed uninstall, rollback or registration absence is inferred; any pending native/process work remains under its owner's control.";
            MsiUninstallResultDetails = MsiUninstallMessage;
            Status = MsiUninstallMessage;
            Event(ActivityArea.MsiUninstallRequest, ActivityOutcome.Failed);
        }
        finally
        {
            intent.SelectedProductScope?.Invalidate();
            if (ReferenceEquals(msiUninstallReadScope, intent.SelectedProductScope)) msiUninstallReadScope = null;
            intent.Invalidate(); msiUninstallPreview = null; msiUninstallPreviewSelection = null; msiUninstallPreviewSnapshot = null; MsiUninstallAcknowledged = false;
            MsiUninstallPreviewDetails = "This one-shot request preview is no longer available. Refresh and select a current registration before preparing another request.";
            msiUninstallSubmissionActive = false; cancellation = null; IsBusy = false;
            NotifyMsiUninstallRequest();
        }
    }

    private static bool ConsistentMsiUninstallResult(MsiUninstallRequestIntent intent, MsiUninstallRequestResult result, DateTimeOffset now)
    {
        if (result.RequestId != intent.RequestId || !Enum.IsDefined(result.State) || result.RequestedUtc == default ||
            result.RequestedUtc.Offset != TimeSpan.Zero || result.RequestedUtc < intent.PreparedUtc || result.RequestedUtc > now ||
            result.ExitDisposition is { } disposition && !Enum.IsDefined(disposition) ||
            result.ClientProcessId is { } processId && processId <= 0) return false;
        bool hasClientFacts = result.StartedUtc != null || result.ExitedUtc != null || result.ClientProcessId != null ||
            result.ExitCode != null || result.ExitDisposition != null;
        if ((hasClientFacts || result.ClientStartObserved == true) && result.State is MsiUninstallRequestState.NotStarted or MsiUninstallRequestState.Blocked or MsiUninstallRequestState.Preparing)
            return false;
        if (result.ClientStartObserved == false && (hasClientFacts || result.State is MsiUninstallRequestState.ClientStarted or MsiUninstallRequestState.ClientExited))
            return false;
        if (result.StartedUtc is { } start)
        {
            if (start.Offset != TimeSpan.Zero || start < result.RequestedUtc || start > now) return false;
        }
        if (result.ExitedUtc is { } exit)
        {
            if (exit.Offset != TimeSpan.Zero || exit < result.RequestedUtc || exit > now ||
                result.StartedUtc is { } actualStart && exit < actualStart ||
                result.State is not (MsiUninstallRequestState.ClientExited or MsiUninstallRequestState.MonitoringUnknown)) return false;
        }
        else if (result.State == MsiUninstallRequestState.ClientExited || result.ExitCode != null || result.ExitDisposition != null)
            return false;
        return result.ExitCode is { } code
            ? result.ExitDisposition == MsiUninstallRequestPolicy.ClassifyExit(code)
            : result.ExitDisposition == null;
    }

    private static string MsiUninstallToolIdentityDetails(SystemToolIdentityDiagnostic? diagnostic)
    {
        if (diagnostic == null) return "Windows tool identity observation: Unobserved. No trust or launch permission is inferred.";
        if (!Enum.IsDefined(diagnostic.Stage) || !Enum.IsDefined(diagnostic.Reason) || !Enum.IsDefined(diagnostic.TrustRoute) ||
            diagnostic.EmbeddedVersionState is { } embedded && !Enum.IsDefined(embedded))
            return "Windows tool identity observation: Unknown; inconsistent diagnostic metadata was withheld. Actual client observations remain separate, and no trust or launch permission is inferred.";
        string native = diagnostic.NativeTrustResult is { } code ? $"0x{unchecked((uint)code):X8}" : "Unobserved";
        return $"Windows tool identity observation: {diagnostic.Stage} / {diagnostic.Reason}; route: {diagnostic.TrustRoute}; native trust result: {native}; embedded version observation: {diagnostic.EmbeddedVersionState?.ToString() ?? "Unobserved"}.\nThis diagnostic concerns the Windows executable only. It does not authenticate the selected MSI package/publisher or grant launch permission.";
    }

    public void ExplainMsiUninstallCloseBlock()
    {
        MsiUninstallMessage = MsiUninstallDispatchReserved || MsiUninstallRequestLauncher.RequiresRetention
            ? "Windows Installer work or native ownership is still pending/unconfirmed. Use Windows Installer's Cancel control if offered, otherwise wait for supported completion and the actual result. MadClean will not kill it or infer rollback."
            : "Read-only MSI/registration preparation is still pending. Cancel preparation if available, then wait for its owning native read to return before closing; no installer start is inferred.";
        Status = MsiUninstallMessage;
    }
    private bool ExplainCommittedMsiUninstallCancel()
    {
        if (!MsiUninstallDispatchReserved) return false;
        MsiUninstallMessage = "The dispatch boundary has been crossed. Use Windows Installer's Cancel control if offered, otherwise wait for supported completion; MadClean keeps observing and does not kill it or claim cancellation/rollback.";
        Status = MsiUninstallMessage;
        return true;
    }
    private void ShutdownMsiUninstallRequestUi()
    {
        msiUninstallPreview?.Intent.Invalidate();
        if (!MsiUninstallDispatchReserved) msiUninstallReadScope?.Invalidate();
        msiUninstallUiTimer?.Stop();
    }
    private void StartMsiUninstallUiTimer()
    {
        if (msiUninstallUiTimer == null)
        {
            msiUninstallUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            msiUninstallUiTimer.Tick += (_, _) =>
            {
                if (msiUninstallPreparationNeedsDrain && !msiUninstallPreparationReader.HasPendingNativeRead)
                    msiUninstallPreparationNeedsDrain = false;
                if (MsiUninstallDispatchReserved && !msiUninstallUiReservedObserved)
                {
                    msiUninstallUiReservedObserved = true;
                    MsiUninstallMessage = "The one-shot dispatch boundary was crossed. No completed result is observed yet; use Windows Installer's Cancel control if offered, otherwise wait for supported completion. MadClean will not kill it or claim rollback.";
                    Status = "Waiting for the actual Windows Installer client result…";
                }
                if (msiUninstallPreview is { } preview && DateTimeOffset.UtcNow >= preview.ExpiresUtc && !msiUninstallSubmissionActive)
                    InvalidateMsiUninstallRequest();
                NotifyMsiUninstallRequest();
                if (!msiUninstallSubmissionActive && !HasPendingMsiUninstallRequest && msiUninstallPreview == null)
                    msiUninstallUiTimer.Stop();
            };
        }
        msiUninstallUiTimer.Start();
    }
    private void NotifyMsiUninstallRequest()
    {
        foreach (string property in new[] { nameof(HasPendingMsiUninstallRequest), nameof(CanChangeInstalledAppSelection),
            nameof(CanAcknowledgeMsiUninstallRequest), nameof(MsiUninstallSubmissionActive), nameof(MsiUninstallDispatchReserved), nameof(MsiUninstallQualification) })
            PropertyChanged?.Invoke(this, new(property));
        CommandManager.InvalidateRequerySuggested();
    }
}
