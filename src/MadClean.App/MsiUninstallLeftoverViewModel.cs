using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private Task<CheckResult<InstalledApplication>>? msiUninstallLeftoverRead;
    private Task<CheckResult<LeftoverCandidate>>? msiUninstallLeftoverComparison;
    private readonly TaskCompletionSource<bool> msiUninstallLeftoverRetention = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool msiUninstallLeftoverActive, msiUninstallLeftoverNeedsDrain, msiUninstallLeftoverOwnershipUnknown;
    private long msiUninstallLeftoverGeneration;
    private string msiUninstallLeftoverMessage = "Not checked. Requires a complete baseline captured before request preparation and an accepted same-request registration postcheck. No query starts automatically.";
    private string msiUninstallLeftoverDetails = "No historical-request leftover review has been requested. Complete uninstall and leftover ownership remain Unknown.";

    public ICommand ReviewMsiUninstallLeftoversCommand { get; private set; } = null!;
    public string MsiUninstallLeftoverMessage { get => msiUninstallLeftoverMessage; private set => Set(ref msiUninstallLeftoverMessage, value); }
    public string MsiUninstallLeftoverDetails { get => msiUninstallLeftoverDetails; private set => Set(ref msiUninstallLeftoverDetails, value); }
    public bool HasPendingMsiUninstallLeftoverReview => msiUninstallLeftoverActive || msiUninstallLeftoverOwnershipUnknown ||
        msiUninstallLeftoverRead is { IsCompleted: false } || msiUninstallLeftoverComparison is { IsCompleted: false } ||
        msiUninstallLeftoverNeedsDrain && MsiUninstallLeftoverNativePending();

    private void InitializeMsiUninstallLeftoverReview() => ReviewMsiUninstallLeftoversCommand = new AsyncCommand(ReviewMsiUninstallLeftovers,
        () => !IsBusy && !HasPendingMsiUninstallRequest && !HasPendingMsiUninstallPostcheck && !HasPendingMsiUninstallLeftoverReview &&
            !MsiUninstallLeftoverNativePending() && CanReviewMsiUninstallLeftovers());

    private bool CanReviewMsiUninstallLeftovers() => applicationBaseline is { } baseline && applicationBaselineReceivedUtc is { } baselineReceived &&
        msiUninstallPostcheckSession is { } session && msiUninstallAcceptedPostcheck is { } postcheck &&
        msiUninstallAcceptedPostcheckReceivedUtc is { } received &&
        MsiUninstallLeftoverReview.CanReview(session, postcheck, received,
            CapturedApplicationBaseline(baseline), baselineReceived, DateTimeOffset.UtcNow);

    private static CheckResult<InstalledApplication> CapturedApplicationBaseline(IReadOnlyList<InstalledApplication> baseline) =>
        new(CheckState.Available, "Complete manually captured in-session ARP baseline; no exact-product read is substituted for global coverage.", baseline);

    private void InvalidateMsiUninstallLeftoverReview()
    {
        // Invalidate the historical workflow before notifications. The separately
        // captured manual baseline and current inventory are never replaced here.
        Interlocked.Increment(ref msiUninstallLeftoverGeneration);
        msiUninstallLeftoverMessage = "Not checked for the current historical request/postcheck. A valid earlier complete baseline and a new explicit read are required.";
        msiUninstallLeftoverDetails = "No current same-request leftover review. Complete uninstall and leftover ownership remain Unknown.";
    }

    private Task ReviewMsiUninstallLeftovers()
    {
        if (IsBusy || HasPendingMsiUninstallLeftoverReview || HasPendingMsiUninstallRequest || HasPendingMsiUninstallPostcheck) return Task.CompletedTask;
        var session = msiUninstallPostcheckSession;
        var intent = msiUninstallPostcheckIntent;
        var historicalResult = msiUninstallPostcheckResult;
        var postcheck = msiUninstallAcceptedPostcheck;
        var postReceived = msiUninstallAcceptedPostcheckReceivedUtc;
        var baseline = applicationBaseline;
        var baselineReceived = applicationBaselineReceivedUtc;
        var generation = Interlocked.Increment(ref msiUninstallLeftoverGeneration);
        var postcheckGeneration = msiUninstallPostcheckGeneration;
        bool refreshStarted = false, refreshPublished = false;
        var refreshOwner = new InstalledApplicationsRefreshOwner();
        bool Current() => generation == Interlocked.Read(ref msiUninstallLeftoverGeneration) && postcheckGeneration == msiUninstallPostcheckGeneration &&
            ReferenceEquals(session, msiUninstallPostcheckSession) && ReferenceEquals(intent, msiUninstallPostcheckIntent) &&
            ReferenceEquals(historicalResult, msiUninstallPostcheckResult) && ReferenceEquals(postcheck, msiUninstallAcceptedPostcheck) &&
            postReceived == msiUninstallAcceptedPostcheckReceivedUtc && ReferenceEquals(baseline, applicationBaseline) && baselineReceived == applicationBaselineReceivedUtc;
        return RunDiscoveryAsync(ActivityArea.LeftoverReview, "Reviewing historical MSI request leftover candidates", async token =>
        {
            InvalidateLeftoverContents();
            LeftoverCandidates.Clear(); SelectedLeftoverCandidate = null;
            MsiUninstallLeftoverDetails = "No accepted completed comparison from this attempt. Complete uninstall and leftover ownership remain Unknown.";
            if (!Current() || baseline == null || baselineReceived == null || session == null || postcheck == null || postReceived == null ||
                !MsiUninstallLeftoverReview.CanReview(session, postcheck, postReceived.Value,
                    CapturedApplicationBaseline(baseline), baselineReceived.Value, DateTimeOffset.UtcNow) || MsiUninstallLeftoverNativePending())
            {
                MsiUninstallLeftoverMessage = "Unavailable for review: require an accepted same-request postcheck and a complete, matching baseline captured before the request was prepared. No inventory or path probe ran.";
                LeftoverMessage = MsiUninstallLeftoverMessage;
                return CheckState.Unknown;
            }
            var before = CapturedApplicationBaseline(baseline);
            msiUninstallLeftoverActive = msiUninstallLeftoverNeedsDrain = true;
            msiUninstallLeftoverRead = null; msiUninstallLeftoverComparison = null;
            try
            {
                MsiUninstallLeftoverMessage = "Explicitly refreshing the complete covered ARP inventory, then comparing only this historical request's recorded install path. No file contents or uninstall actions are requested.";
                LeftoverMessage = MsiUninstallLeftoverMessage;
                refreshStarted = true;
                BeginInstalledApplicationsRefresh(owner: refreshOwner);
                if (!Current()) return CheckState.Cancelled;
                string sourceDetails = $"Historical request: {historicalResult!.RequestId}\nExact ProductCode: {intent!.ProductCode}\nHistorical registration: {intent.RegistrationIdentity}\nActual recorded client exit UTC: {historicalResult.ExitedUtc:O}\nComplete baseline response received UTC: {baselineReceived.Value:O}\nAccepted exact-product postcheck received UTC: {postReceived.Value:O}\n";
                var started = DateTimeOffset.UtcNow;
                MsiUninstallLeftoverDetails = sourceDetails + $"Fresh global ARP read attempt UTC: {started:O}. No completed global response is accepted yet. Complete uninstall and leftover ownership remain Unknown.";
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                var read = Task.Run(() => installedApplications.ScanAsync(token), CancellationToken.None);
                msiUninstallLeftoverRead = read;
                NotifyMsiUninstallLeftoverReview();
                var after = await read;
                var received = DateTimeOffset.UtcNow;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                MsiUninstallLeftoverDetails = sourceDetails + $"Fresh global ARP read attempt UTC: {started:O}\nFresh global response received UTC: {received:O}; coverage: {after.State}. No completed path comparison is accepted yet. Complete uninstall and leftover ownership remain Unknown.";
                if (!Current()) return CheckState.Cancelled;
                await PublishInstalledApplications(after, token, refreshOwner);
                refreshPublished = true;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                var comparison = Task.Run(() => MsiUninstallLeftoverReview.Compare(session, postcheck, postReceived.Value,
                    before, baselineReceived.Value, after, started, received, path =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (generation != Interlocked.Read(ref msiUninstallLeftoverGeneration))
                            throw new OperationCanceledException("Historical review source changed.");
                        return ProbeRecordedInstallDirectory(path);
                    }), CancellationToken.None);
                msiUninstallLeftoverComparison = comparison;
                NotifyMsiUninstallLeftoverReview();
                var review = await comparison;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                foreach (var candidate in review.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Current()) return CheckState.Cancelled;
                    LeftoverCandidates.Add(candidate);
                }
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                SelectedLeftoverCandidate = null; // A separate explicit selection precedes metadata reads.
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                msiUninstallLeftoverDetails = $"Historical request: {historicalResult!.RequestId}\nExact ProductCode: {intent!.ProductCode}\nHistorical registration: {intent.RegistrationIdentity}\nActual recorded client exit UTC: {historicalResult.ExitedUtc:O}\nComplete baseline response received UTC: {baselineReceived.Value:O}\nAccepted exact-product postcheck received UTC: {postReceived.Value:O}\nFresh global ARP read attempt UTC: {started:O}\nFresh global response received UTC: {received:O}; coverage: {after.State}\nTarget candidate rows: {review.Items.Count}\n\n" +
                    "Only the historical target's prior recorded install path can be probed, using full before/after registrations for overlap cautions. These sequential reads are non-atomic. No other missing app is probed by this request. The complete manual baseline and original MSI result are unchanged.\n\nComplete uninstall: Unknown. Leftover ownership/removal eligibility: Unknown. Registration absence and a surviving directory are candidates, never proof of junk or authority to delete shared dependencies, user data, saves or settings. Select a candidate in Leftover review for a separate explicit guarded metadata preview.";
                // Publish this coherent display before the first callback can
                // invalidate the historical source; no old-source writes follow it.
                msiUninstallLeftoverMessage = Stamp(review.State, review.Message);
                leftoverMessage = msiUninstallLeftoverMessage;
                NotifyMsiUninstallLeftoverReview();
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                return review.State;
            }
            finally
            {
                try { await DrainMsiUninstallLeftoverReview(); }
                finally
                {
                    // Shared display ownership is distinct from historical-source
                    // currency: retire this unfinished refresh even after a source
                    // change, but never overwrite a newer refresh's display.
                    try
                    {
                        if (refreshStarted && !refreshPublished)
                            FailInstalledApplicationsRefresh("Cancelled or failed · the historical-request inventory refresh did not publish completely.", refreshOwner.Generation);
                    }
                    finally { msiUninstallLeftoverActive = false; NotifyMsiUninstallLeftoverReview(); }
                }
            }
        }, error =>
        {
            if (!Current()) return;
            InvalidateLeftoverContents();
            if (!Current()) return;
            LeftoverCandidates.Clear();
            if (!Current()) return;
            SelectedLeftoverCandidate = null;
            if (!Current()) return;
            MsiUninstallLeftoverMessage = error + " No completed historical-request candidate review is accepted.";
            if (!Current()) return;
            MsiUninstallLeftoverDetails += "\n\nThis attempt did not complete; stage times above are observations only. The original MSI result, accepted postcheck and manual baseline remain historical. Complete uninstall and leftover ownership remain Unknown.";
            if (!Current()) return;
            LeftoverMessage = MsiUninstallLeftoverMessage + RegistryCancellationCaveat;
            if (!Current()) return;
            NotifyMsiUninstallLeftoverReview();
        });
    }

    private bool MsiUninstallLeftoverNativePending()
    {
        if (msiUninstallLeftoverOwnershipUnknown) return true;
        try
        {
            if (installedApplications is InstalledApplicationInventory reader) return reader.HasPendingNativeRead;
            msiUninstallLeftoverOwnershipUnknown = true;
        }
        catch { msiUninstallLeftoverOwnershipUnknown = true; }
        return true;
    }
    private async Task DrainMsiUninstallLeftoverReview()
    {
        Exception? displayFailure = null;
        try { NotifyMsiUninstallLeftoverReview(); } catch (Exception error) { displayFailure = error; }
        if (msiUninstallLeftoverRead is { } read) try { await read; } catch { }
        if (msiUninstallLeftoverComparison is { } comparison) try { await comparison; } catch { }
        while (MsiUninstallLeftoverNativePending())
        {
            if (msiUninstallLeftoverOwnershipUnknown)
            {
                msiUninstallLeftoverMessage = "Registry ownership could not be observed. The actual workflow remains retained; no completed review or safe normal close is established.";
                try { NotifyMsiUninstallLeftoverReview(); } catch (Exception error) { displayFailure ??= error; }
                await msiUninstallLeftoverRetention.Task; // No reset, retry, force-close or invented release.
            }
            await Task.Delay(100);
        }
        msiUninstallLeftoverNeedsDrain = false;
        if (displayFailure != null) throw new InvalidOperationException("Historical leftover review presentation failed after owned work settled.", displayFailure);
    }
    private void NotifyMsiUninstallLeftoverReview()
    {
        foreach (var name in new[] { nameof(MsiUninstallLeftoverMessage), nameof(MsiUninstallLeftoverDetails), nameof(LeftoverMessage), nameof(HasPendingMsiUninstallLeftoverReview) })
            PropertyChanged?.Invoke(this, new(name));
        CommandManager.InvalidateRequerySuggested();
    }
    public void ExplainMsiUninstallLeftoverCloseBlock()
    {
        Status = "The historical-request registration read or recorded-path comparison is still settling. Wait or cancel observation; MadClean does not terminate registry workers or remove files.";
        NotifyMsiUninstallLeftoverReview();
    }
}
