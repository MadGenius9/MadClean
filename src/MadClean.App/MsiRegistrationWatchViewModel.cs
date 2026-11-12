using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IMsiRegistrationWatchInventory msiRegistrationWatch = new WindowsMsiRegistrationWatch();
    private MsiRegistrationWatchTarget? msiWatchTarget;
    private MsiRegistrationWatchReadScope? msiWatchScope;
    private long msiWatchGeneration;
    private bool msiWatchCallActive, msiWatchNeedsDrain, msiWatchCapturing;
    private DateTimeOffset? msiWatchAttemptUtc, msiWatchReceivedUtc;
    private string msiWatchMessage = "No product remembered. This separate check reads registration metadata only; it never starts an uninstaller.";
    public ObservableCollection<ResultRow> MsiWatchArpRows { get; } = [];
    public ObservableCollection<MsiRegistrationWatchContext> MsiWatchContextRows { get; } = [];
    public ICommand RememberMsiRegistrationCommand { get; private set; } = null!;
    public ICommand RecheckRememberedMsiCommand { get; private set; } = null!;
    public ICommand ClearRememberedMsiCommand { get; private set; } = null!;
    public string MsiWatchMessage { get => msiWatchMessage; private set => Set(ref msiWatchMessage, value); }
    public bool HasPendingMsiRegistrationWatchWork => msiWatchCallActive || msiWatchNeedsDrain && msiRegistrationWatch.HasPendingNativeRead;
    public string MsiWatchTargetDetails => msiWatchTarget is not { } target
        ? "No remembered MSI product. Select a current Windows Installer registration, then explicitly remember its verified metadata. The target stays in memory only and is lost on exit."
        : $"Remembered name: {target.Name}\nHistorical version: {target.Version}; publisher metadata: {target.Publisher}\nExact ProductCode: {target.ProductCode}\nOriginal ARP source: {target.OriginalSource}; verified MSI context: {target.OriginalContext}\nHistorical NoRemove flag: {Flag(target.HistoricalNoRemove)} (metadata only, never overridden by this watch)\nRemembered UTC: {target.CapturedUtc:u}; historical source coverage: {target.HistoricalCoverage}\n" +
            "This is a historical registration target, usable for at most 24 hours in this session. Selecting another app or refreshing the desktop inventory does not silently retarget it. No live installed version, package trust, removal permission or leftover ownership is inferred.";
    public string MsiWatchQueryDetails => (msiWatchAttemptUtc is { } attempt
        ? $"App metadata attempt UTC: {attempt:u}\n" + (msiWatchReceivedUtc is { } received
            ? $"Adapter response received UTC: {received:u}. Receipt alone does not prove complete source coverage."
            : "No adapter response from this attempt has been accepted.")
        : "No registration-watch metadata request for the current remembered target.") +
        (HasPendingMsiRegistrationWatchWork
            ? "\nMSI/registry work remains owned. Cancel does not force-close native handles; normal close waits for actual settlement."
            : "\nNo registration-watch worker remains pending.");
    private string msiWatchReportDetails = "No current recheck. Current global inventory, complete uninstall and leftover ownership remain Unknown.";
    public string MsiWatchReportDetails { get => msiWatchReportDetails; private set => Set(ref msiWatchReportDetails, value); }

    private void InitializeMsiRegistrationWatch()
    {
        RememberMsiRegistrationCommand = new AsyncCommand(RememberMsiRegistration,
            () => !IsBusy && !HasPendingMsiRegistrationWatchWork && !msiRegistrationWatch.HasPendingNativeRead &&
                CurrentInstalledApplication?.RegistrationEvidence?.WindowsInstaller == true);
        RecheckRememberedMsiCommand = new AsyncCommand(RecheckRememberedMsi,
            () => !IsBusy && !HasPendingMsiRegistrationWatchWork && !msiRegistrationWatch.HasPendingNativeRead && msiWatchTarget != null);
        ClearRememberedMsiCommand = new ActionCommand(ClearRememberedMsi,
            () => msiWatchTarget != null || msiWatchCapturing || msiWatchScope != null);
    }
    private void NotifyMsiWatch()
    {
        foreach (string name in new[] { nameof(MsiWatchTargetDetails), nameof(MsiWatchQueryDetails), nameof(HasPendingMsiRegistrationWatchWork) })
            PropertyChanged?.Invoke(this, new(name));
        CommandManager.InvalidateRequerySuggested();
    }
    private void ClearRememberedMsi()
    {
        msiWatchGeneration++; msiWatchScope?.Invalidate(); msiWatchScope = null; msiWatchTarget = null;
        msiWatchAttemptUtc = msiWatchReceivedUtc = null;
        MsiWatchArpRows.Clear(); MsiWatchContextRows.Clear();
        MsiWatchReportDetails = "No current recheck. Global inventory, complete uninstall and leftover ownership remain Unknown.";
        MsiWatchMessage = "Remembered target cleared in memory only. Any already-pending read retains its owner; no registration was changed.";
        NotifyMsiWatch();
    }
    private void InvalidateMsiWatchCapture()
    {
        // Source/selection changes invalidate a capture in progress, including away/back.
        // A previously remembered historical target is intentionally independent of selection.
        if (!msiWatchCapturing) return;
        msiWatchGeneration++;
        MsiWatchMessage = "Selection or registration source changed while remembering. No target from that read will be accepted; select the current row and try again after it settles.";
        NotifyMsiWatch();
    }
    private Task RememberMsiRegistration()
    {
        long generation = -1;
        InstalledApplication? selected = null;
        CheckResult<InstalledApplication>? snapshot = null;
        bool Current() => generation == msiWatchGeneration && ReferenceEquals(selected, CurrentInstalledApplication) && ReferenceEquals(snapshot, latestInstalledSnapshot);
        return RunDiscoveryAsync(ActivityArea.MsiRegistrationWatch, "Remembering one verified MSI registration", async token =>
        {
            if (HasPendingMsiRegistrationWatchWork || msiRegistrationWatch.HasPendingNativeRead) return CheckState.Unknown;
            ClearRememberedMsi(); selected = CurrentInstalledApplication; snapshot = latestInstalledSnapshot; generation = msiWatchGeneration;
            if (selected == null || snapshot == null) return CheckState.Unknown;
            var prepared = MsiRegistrationReview.CreateScope(snapshot, selected.Identity, DateTimeOffset.UtcNow, token);
            if (prepared.Items.Count != 1)
            { MsiWatchMessage = Stamp(prepared.State, prepared.Message); return prepared.State; }
            msiWatchAttemptUtc = DateTimeOffset.UtcNow; msiWatchReceivedUtc = null;
            msiWatchCallActive = msiWatchNeedsDrain = msiWatchCapturing = true;
            try
            {
                NotifyMsiWatch();
                MsiWatchMessage = "Reading this selected product's source-context metadata before remembering it. No uninstaller or maintenance request is made.";
                var verification = await msiRegistrations.VerifyAsync(prepared.Items[0], token);
                var received = DateTimeOffset.UtcNow;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                msiWatchReceivedUtc = received;
                if (TryMsiWatchFailure(verification, out var failureState, out var failureReason))
                { MsiWatchMessage = Stamp(failureState, failureReason); NotifyMsiWatch(); return failureState; }
                if (verification.Items.Count == 1 && verification.Items[0].CheckedUtc < msiWatchAttemptUtc.Value)
                    throw new InvalidOperationException("MSI capture response predates its request.");
                var captured = MsiRegistrationWatchPolicy.Capture(snapshot, selected, verification, received, token);
                if (captured.Items.Count == 1) msiWatchTarget = captured.Items[0];
                MsiWatchMessage = Stamp(captured.State, captured.Message);
                NotifyMsiWatch(); return captured.State;
            }
            finally
            {
                msiWatchCallActive = msiWatchCapturing = false;
                await DrainMsiWatch();
            }
        }, error =>
        {
            if (!Current()) return;
            msiWatchTarget = null; MsiWatchMessage = error + " No verified target was remembered. Refresh registrations and select a current MSI row before retrying.";
            NotifyMsiWatch();
        });
    }
    private Task RecheckRememberedMsi()
    {
        var target = msiWatchTarget;
        long generation = msiWatchGeneration;
        MsiRegistrationWatchReadScope? scope = null;
        bool Current() => generation == msiWatchGeneration && ReferenceEquals(target, msiWatchTarget) &&
            ReferenceEquals(scope, msiWatchScope) && scope?.IsInvalidated == false;
        return RunDiscoveryAsync(ActivityArea.MsiRegistrationWatch, "Rechecking the remembered MSI product", async token =>
        {
            if (HasPendingMsiRegistrationWatchWork || msiRegistrationWatch.HasPendingNativeRead) return CheckState.Unknown;
            MsiWatchArpRows.Clear(); MsiWatchContextRows.Clear();
            msiWatchAttemptUtc = msiWatchReceivedUtc = null;
            MsiWatchReportDetails = "Checking sequential registration observations; no complete uninstall or leftover ownership is established.";
            msiWatchScope?.Invalidate(); msiWatchScope = null;
            var prepared = MsiRegistrationWatchPolicy.Prepare(target, DateTimeOffset.UtcNow, token);
            if (prepared.Items.Count != 1)
            {
                MsiWatchMessage = Stamp(prepared.State, prepared.Message);
                MsiWatchReportDetails = "No fresh selector was prepared; no Windows recheck started. Remember a supported current target again or clear it. Uninstall completion remains Unknown.";
                NotifyMsiWatch(); return prepared.State;
            }
            scope = prepared.Items[0]; msiWatchScope = scope;
            msiWatchAttemptUtc = DateTimeOffset.UtcNow; msiWatchReceivedUtc = null;
            msiWatchCallActive = msiWatchNeedsDrain = true;
            try
            {
                NotifyMsiWatch();
                MsiWatchMessage = "Reading only the exact remembered ProductCode in three ARP sources and current-user/machine MSI contexts. No global app inventory or filesystem scan starts.";
                var result = await msiRegistrationWatch.ReadAsync(scope, token);
                var received = DateTimeOffset.UtcNow;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                msiWatchReceivedUtc = received;
                if (TryMsiWatchFailure(result, out var failureState, out var failureReason))
                {
                    MsiWatchMessage = Stamp(failureState, failureReason);
                    MsiWatchReportDetails = "No completed current recheck. Source absence, complete uninstall and leftover ownership remain Unknown.";
                    NotifyMsiWatch(); return failureState;
                }
                if (!MsiRegistrationWatchPolicy.IsConsistent(scope, result, received))
                {
                    MsiWatchMessage = "No consistent current registration-watch report was accepted. Check Windows source access, read limits and target age, then retry. No absence or uninstall completion is inferred.";
                    return CheckState.Unknown;
                }
                var report = result.Items[0];
                foreach (var arp in report.ArpLookups)
                    MsiWatchArpRows.Add(new(arp.Source.ToString(), $"Observed UTC: {arp.ObservedUtc:u}; native code: {arp.NativeError?.ToString() ?? "not reported"}; reported NoRemove: {Flag(arp.Registration?.RegistrationEvidence?.NoRemove)}", arp.State.ToString()));
                foreach (var context in report.Contexts) MsiWatchContextRows.Add(context);
                MsiWatchReportDetails = $"Exact ProductCode: {report.ProductCode}\nQuery interval UTC: {report.QueryStartedUtc:u} — {report.CheckedUtc:u}\nHistorical source coverage: {report.HistoricalCoverage}\nExact-product MSI enumeration complete: {report.EnumerationComplete}; native issue count: {report.NativeIssueCount}\n" +
                    "ARP and MSI context rows are independent sequential observations. Absent is scoped to that covered source/context; access errors and incomplete enumeration stay Unknown. Another context can remain present.\n\nCurrent global inventory: Not checked by this operation. Complete uninstall: Unknown. Leftover-file ownership: Unknown. Registration absence does not prove file removal, stopped processes, successful uninstall or cleanup eligibility. No uninstaller, install, repair, deletion or restart was requested.";
                foreach (var instance in report.NativeInstances)
                    MsiWatchReportDetails += $"\n\nCurrent {instance.Context} metadata: {instance.Name}\nReported version: {instance.Version}; publisher metadata: {instance.Publisher}\nObserved UTC: {instance.ObservedUtc:u}. These fields do not authenticate a publisher or establish continuity with the historical target.";
                MsiWatchMessage = Stamp(result.State, "Remembered-product registration observations returned. Review each source and context; Available means observations were established, not that uninstall completed.");
                NotifyMsiWatch(); return result.State;
            }
            finally { msiWatchCallActive = false; await DrainMsiWatch(); }
        }, error =>
        {
            if (!Current()) return;
            MsiWatchArpRows.Clear(); MsiWatchContextRows.Clear();
            MsiWatchReportDetails = "No completed current recheck. Registration absence, complete uninstall and leftover ownership remain Unknown.";
            MsiWatchMessage = error + " No successful registration recheck is claimed.";
            NotifyMsiWatch();
        });
    }
    private static bool TryMsiWatchFailure<T>(CheckResult<T> result, out CheckState state, out string reason)
    {
        state = CheckState.Unknown; reason = "No consistent source response was accepted. Check Windows metadata access and retry; no absence is inferred.";
        if (!Enum.IsDefined(result.State) || result.Items is not { Count: 0 } ||
            result.State is not (CheckState.Unknown or CheckState.Unavailable or CheckState.Unsupported or CheckState.Failed or CheckState.Cancelled)) return false;
        state = result.State;
        // These adapters supply fixed English reasons/numeric codes, never provider payloads.
        if (result.Message is { Length: > 0 and <= 2048 } message && message.All(character => character is >= ' ' and <= '~')) reason = message;
        return true;
    }
    private async Task DrainMsiWatch()
    {
        Exception? displayFailure = null;
        try
        {
            if (msiRegistrationWatch.HasPendingNativeRead)
                Status = "Waiting for owned MSI/registry metadata work to return. No handle, client or service will be terminated.";
            NotifyMsiWatch();
        }
        catch (Exception error) { displayFailure = error; }
        while (msiRegistrationWatch.HasPendingNativeRead) await Task.Delay(100);
        msiWatchNeedsDrain = false;
        NotifyMsiWatch();
        if (displayFailure != null) throw new InvalidOperationException("Registration-watch display failed after native settlement.", displayFailure);
    }
    public void ExplainMsiWatchCloseBlock()
    {
        Status = "MSI registration metadata is still owned. Cancel ends the caller wait; closing waits for the actual worker to settle.";
        NotifyMsiWatch();
    }
}
