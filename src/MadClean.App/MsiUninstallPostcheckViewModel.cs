using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IMsiRegistrationWatchInventory msiUninstallPostcheckReader = new WindowsMsiRegistrationWatch();
    private MsiUninstallRequestIntent? msiUninstallPostcheckIntent;
    private MsiUninstallRequestResult? msiUninstallPostcheckResult;
    private MsiUninstallPostcheckSession? msiUninstallPostcheckSession;
    private CheckResult<MsiRegistrationWatchReport>? msiUninstallAcceptedPostcheck;
    private DateTimeOffset? msiUninstallAcceptedPostcheckReceivedUtc;
    private Task<CheckResult<MsiRegistrationWatchReport>>? msiUninstallPostcheckRead;
    private readonly TaskCompletionSource<bool> msiUninstallPostcheckRetention = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long msiUninstallPostcheckGeneration;
    private bool msiUninstallPostcheckActive, msiUninstallPostcheckNeedsDrain, msiUninstallPostcheckOwnershipUnknown;
    private string msiUninstallPostcheckMessage = "Not checked. This separate read requires an actual accepted Windows Installer client-exit observation.";
    private string msiUninstallPostcheckDetails = "No registration postcheck has been requested. Client exit, registration observations, complete uninstall and leftover ownership are separate facts.";

    public ICommand RecheckMsiUninstallRegistrationsCommand { get; private set; } = null!;
    public string MsiUninstallPostcheckMessage { get => msiUninstallPostcheckMessage; private set => Set(ref msiUninstallPostcheckMessage, value); }
    public string MsiUninstallPostcheckDetails { get => msiUninstallPostcheckDetails; private set => Set(ref msiUninstallPostcheckDetails, value); }
    public bool HasPendingMsiUninstallPostcheck => msiUninstallPostcheckActive || msiUninstallPostcheckOwnershipUnknown ||
        msiUninstallPostcheckRead is { IsCompleted: false } || msiUninstallPostcheckNeedsDrain && MsiPostcheckNativePending();

    private void InitializeMsiUninstallPostcheck()
    {
        InitializeMsiUninstallLeftoverReview();
        RecheckMsiUninstallRegistrationsCommand = new AsyncCommand(RecheckMsiUninstallRegistrations,
            () => !IsBusy && !HasPendingMsiUninstallRequest &&
                msiUninstallPostcheckIntent?.PostcheckTarget != null &&
                msiUninstallPostcheckResult?.State == MsiUninstallRequestState.ClientExited && !MsiPostcheckNativePending());
    }

    private void CaptureMsiUninstallPostcheckSource(MsiUninstallRequestIntent intent, MsiUninstallRequestResult result)
    {
        // No notification may run before the old source/session/display is invalidated.
        // Historical ownership is independent of current selection and consumed preview scope.
        msiUninstallPostcheckGeneration++;
        msiUninstallAcceptedPostcheck = null;
        msiUninstallAcceptedPostcheckReceivedUtc = null;
        InvalidateMsiUninstallLeftoverReview();
        msiUninstallPostcheckSession?.Scope.Invalidate();
        msiUninstallPostcheckSession = null;
        msiUninstallPostcheckMessage = "Not checked for the newly accepted historical request.";
        msiUninstallPostcheckDetails = "No accepted registration postcheck for this historical request. Complete uninstall and leftover ownership remain Unknown.";
        msiUninstallPostcheckIntent = intent;
        msiUninstallPostcheckResult = result;
        msiUninstallPostcheckMessage = result.State == MsiUninstallRequestState.ClientExited && intent.PostcheckTarget != null
            ? "Not checked for this historical request. Explicitly recheck its exact ProductCode; no query starts automatically."
            : "Postcheck unavailable for this observation. An accepted actual client exit and captured exact-product target are required.";
        msiUninstallPostcheckDetails = $"Historical request: {result.RequestId}\nActual client observation: {result.State}\nActual exit UTC: {result.ExitedUtc?.ToString("u") ?? "Unobserved"}\nReported installer disposition: {result.ExitDisposition?.ToString() ?? "Unobserved"}\n" +
            (intent.PostcheckTarget is { } target ? $"Exact historical ProductCode: {target.ProductCode}\nHistorical name: {target.Name}\nOriginal native context: {target.OriginalContext}\n" : "No captured exact-product postcheck target.\n") +
            "\nRegistration postcheck: Not checked. Current global inventory, complete uninstall and leftover ownership remain Unknown. Selecting another app does not retarget this historical request.";
    }

    private Task RecheckMsiUninstallRegistrations()
    {
        var intent = msiUninstallPostcheckIntent;
        var historicalResult = msiUninstallPostcheckResult;
        long generation = msiUninstallPostcheckGeneration;
        MsiUninstallPostcheckSession? session = null;
        bool SourceCurrent() => generation == msiUninstallPostcheckGeneration && ReferenceEquals(intent, msiUninstallPostcheckIntent) &&
            ReferenceEquals(historicalResult, msiUninstallPostcheckResult);
        bool Current() => SourceCurrent() && ReferenceEquals(session, msiUninstallPostcheckSession) &&
            session?.Scope.IsInvalidated == false;
        return RunDiscoveryAsync(ActivityArea.MsiRegistrationWatch, "Rechecking the historical MSI request's registrations", async token =>
        {
            if (HasPendingMsiUninstallRequest || MsiPostcheckNativePending()) return CheckState.Unknown;
            msiUninstallAcceptedPostcheck = null;
            msiUninstallAcceptedPostcheckReceivedUtc = null;
            InvalidateMsiUninstallLeftoverReview();
            msiUninstallPostcheckSession?.Scope.Invalidate();
            msiUninstallPostcheckSession = null;
            MsiUninstallPostcheckDetails = "No completed observation from this attempt. Current global inventory, complete uninstall and leftover ownership remain Unknown.";
            var prepared = MsiUninstallPostcheckPolicy.Prepare(intent, historicalResult, DateTimeOffset.UtcNow, token);
            if (prepared.Items.Count != 1)
            {
                MsiUninstallPostcheckMessage = Stamp(prepared.State, prepared.Message);
                return prepared.State;
            }
            session = prepared.Items[0]; msiUninstallPostcheckSession = session;
            using var invalidateOnStop = token.Register(session.Scope.Invalidate);
            msiUninstallPostcheckActive = msiUninstallPostcheckNeedsDrain = true;
            try
            {
                NotifyMsiUninstallPostcheck();
                MsiUninstallPostcheckMessage = "Reading the historical request's exact ProductCode in three ARP sources and supported current-user/machine MSI contexts. No global inventory or file scan starts.";
                msiUninstallPostcheckRead = msiUninstallPostcheckReader.ReadAsync(session.Scope, token);
                var observed = await msiUninstallPostcheckRead;
                var received = DateTimeOffset.UtcNow;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                if (TryMsiWatchFailure(observed, out var failureState, out var failureReason))
                {
                    MsiUninstallPostcheckMessage = Stamp(failureState, failureReason);
                    return failureState;
                }
                if (!MsiUninstallPostcheckPolicy.IsConsistent(session, observed, received))
                {
                    MsiUninstallPostcheckMessage = "No consistent same-request registration report was accepted. Source presence/absence, complete uninstall and leftover ownership remain Unknown.";
                    return CheckState.Unknown;
                }
                msiUninstallAcceptedPostcheck = observed;
                msiUninstallAcceptedPostcheckReceivedUtc = received;
                var report = observed.Items[0];
                string arp = string.Join("\n", report.ArpLookups.Select(row =>
                    $"{row.Source}: {row.State}; observed UTC: {row.ObservedUtc:u}; native code: {row.NativeError?.ToString() ?? "Unobserved"}"));
                string contexts = string.Join("\n", report.Contexts.Select(row =>
                    $"{row.Context}: {row.Presence}; observed UTC: {row.ObservedUtc?.ToString("u") ?? "Unobserved"}"));
                MsiUninstallPostcheckDetails = $"Historical request: {historicalResult!.RequestId}\nActual client exit UTC: {historicalResult.ExitedUtc:u}\nInstaller disposition: {historicalResult.ExitDisposition}\nExact historical ProductCode: {report.ProductCode}\nQuery interval UTC: {report.QueryStartedUtc:u} — {report.CheckedUtc:u}\nResponse received UTC: {received:u}\nHistorical global coverage: {report.HistoricalCoverage}\nExact-product enumeration complete: {report.EnumerationComplete}; native issue count: {report.NativeIssueCount}\n\nARP source observations:\n{arp}\n\nMSI context observations:\n{contexts}\n\n" +
                    "These are sequential, non-atomic registration observations. Absent applies only to a covered source/context; inaccessible or incomplete sources remain Unknown. Another context or a reinstalled registration may be present.\n\nCurrent global inventory: Not checked. Complete uninstall: Unknown. Leftover ownership: Unknown. No file scan, deletion, installer, repair or restart was requested; registration absence grants no cleanup permission.";
                MsiUninstallPostcheckMessage = Stamp(observed.State,
                    "Historical request registration observations returned. Review each covered source/context; this does not prove complete uninstall.");
                return observed.State;
            }
            finally
            {
                try { await DrainMsiUninstallPostcheck(); }
                finally { msiUninstallPostcheckActive = false; NotifyMsiUninstallPostcheck(); }
            }
        }, error =>
        {
            if (!SourceCurrent()) return;
            msiUninstallAcceptedPostcheck = null;
            msiUninstallAcceptedPostcheckReceivedUtc = null;
            if (msiUninstallPostcheckOwnershipUnknown) { NotifyMsiUninstallPostcheck(); return; }
            MsiUninstallPostcheckMessage = error + " No completed registration postcheck is claimed. Actual client-exit facts remain historical.";
            MsiUninstallPostcheckDetails = "No accepted report from this attempt. Current global inventory, complete uninstall and leftover ownership remain Unknown.";
            NotifyMsiUninstallPostcheck();
        });
    }

    private bool MsiPostcheckNativePending()
    {
        if (msiUninstallPostcheckOwnershipUnknown) return true;
        try { return msiUninstallPostcheckReader.HasPendingNativeRead; }
        catch { msiUninstallPostcheckOwnershipUnknown = true; msiUninstallPostcheckNeedsDrain = true; return true; }
    }
    private async Task DrainMsiUninstallPostcheck()
    {
        Exception? presentationFailure = null;
        try { NotifyMsiUninstallPostcheck(); }
        catch (Exception error) { presentationFailure = error; }
        // A display failure or cancelled caller cannot skip the actual returned task.
        if (msiUninstallPostcheckRead is { } actual)
            try { await actual; } catch { /* The native pending proof is still required below. */ }
        while (true)
        {
            bool pending = MsiPostcheckNativePending();
            if (msiUninstallPostcheckOwnershipUnknown)
            {
                // Do not reset an unobservable owner or manufacture safe app-close permission.
                msiUninstallPostcheckMessage = "Native registration ownership could not be observed. It remains retained; no completed postcheck or safe normal close is established.";
                msiUninstallPostcheckDetails = "The postcheck cannot establish settled native ownership. Current global inventory, complete uninstall and leftover ownership remain Unknown. No worker, client or service is terminated.";
                try { NotifyMsiUninstallPostcheck(); }
                catch (Exception error) { presentationFailure ??= error; }
                // Permanent unknown ownership is neither polled nor retried. Keep
                // the actual App task, global busy state and close protection alive.
                await msiUninstallPostcheckRetention.Task;
            }
            if (!pending) { msiUninstallPostcheckNeedsDrain = false; break; }
            await Task.Delay(100);
        }
        if (presentationFailure != null) throw new InvalidOperationException("Postcheck presentation failed after owned work was observed.", presentationFailure);
    }
    private void NotifyMsiUninstallPostcheck()
    {
        foreach (string name in new[] { nameof(MsiUninstallPostcheckMessage), nameof(MsiUninstallPostcheckDetails),
            nameof(HasPendingMsiUninstallPostcheck), nameof(HasPendingMsiUninstallRequest),
            nameof(MsiUninstallLeftoverMessage), nameof(MsiUninstallLeftoverDetails), nameof(HasPendingMsiUninstallLeftoverReview) })
            PropertyChanged?.Invoke(this, new(name));
        CommandManager.InvalidateRequerySuggested();
    }
    private void ShutdownMsiUninstallPostcheck() => msiUninstallPostcheckSession?.Scope.Invalidate();
}
