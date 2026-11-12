using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IDiskIdentityAccounting diskIdentityAccounting = new WindowsDiskIdentityAccounting();
    private DiskUsageReport? diskIdentitySnapshot;
    private long diskIdentityGeneration;
    private bool diskIdentityCallActive;
    private DateTimeOffset? diskIdentityAttemptUtc, diskIdentityReceivedUtc, diskIdentityInterruptedUtc;
    private CheckState diskIdentityQueryState = CheckState.Unknown;
    private string diskIdentityMessage = "Not checked. After mapping a folder, explicitly analyze the same snapshot's observed file identities. No file contents are read.";
    private string diskIdentityTotals = "No file-identity accounting result.";
    public ObservableCollection<ResultRow> DiskIdentityRows { get; } = [];
    public ObservableCollection<string> DiskIdentityCoverage { get; } = [];
    public ICommand AnalyzeDiskIdentitiesCommand { get; private set; } = null!;
    public string DiskIdentityMessage { get => diskIdentityMessage; private set => Set(ref diskIdentityMessage, value); }
    public string DiskIdentityTotals { get => diskIdentityTotals; private set => Set(ref diskIdentityTotals, value); }
    public bool HasPendingDiskIdentityRead => diskIdentityCallActive || diskIdentityAccounting.HasPendingNativeRead;
    public string DiskIdentityQueryDetails => diskIdentityAttemptUtc is not { } attempt
        ? "Not checked for the current map. File observations and identity-query times are separate from the original map's collection."
        : $"App attempt UTC: {attempt:u}\n" +
            (diskIdentityReceivedUtc is { } received ? $"Adapter response received UTC: {received:u}; recorded state: {diskIdentityQueryState}. Receipt does not establish a completed native read or a simultaneous filesystem snapshot."
                : diskIdentityInterruptedUtc is { } interrupted ? $"Interruption observed UTC: {interrupted:u}; recorded state: {diskIdentityQueryState}. No completed adapter response was received."
                    : "No completed adapter response yet.") +
            (HasPendingDiskIdentityRead ? "\nIdentity metadata work remains owned. Retry and normal window close wait for its actual return; Cancel does not force-close native handles."
                : "\nNo identity metadata owner is pending. Settlement does not promote an earlier timeout or cancellation to success.");
    private void InitializeDiskIdentities() => AnalyzeDiskIdentitiesCommand = new AsyncCommand(AnalyzeDiskIdentities,
        () => !IsBusy && !HasPendingDiskIdentityRead && diskIdentitySnapshot != null);
    private void InvalidateDiskIdentities()
    {
        diskIdentityGeneration++; diskIdentitySnapshot = null;
        diskIdentityAttemptUtc = diskIdentityReceivedUtc = diskIdentityInterruptedUtc = null;
        diskIdentityQueryState = CheckState.Unknown;
        DiskIdentityRows.Clear(); DiskIdentityCoverage.Clear();
        DiskIdentityTotals = "No current file-identity result.";
        DiskIdentityMessage = "Map snapshot changed. Explicitly analyze the new snapshot to inspect observed aliases; the treemap continues to show path-logical bytes.";
        NotifyDiskIdentityOwnership();
    }
    private Task AnalyzeDiskIdentities()
    {
        var snapshot = diskIdentitySnapshot;
        long generation = diskIdentityGeneration;
        bool Current() => generation == diskIdentityGeneration && ReferenceEquals(snapshot, diskIdentitySnapshot);
        return RunDiscoveryAsync(ActivityArea.DiskIdentityAccounting, "Reading snapshot file identities", async token =>
        {
            if (HasPendingDiskIdentityRead) { DiskIdentityMessage = "A prior identity metadata read remains owned. Wait for its actual completion before retrying."; return CheckState.Unknown; }
            DiskIdentityRows.Clear(); DiskIdentityCoverage.Clear();
            DiskIdentityTotals = "In progress — no completed identity totals.";
            if (snapshot == null) { DiskIdentityMessage = "Map a fixed local folder first. No metadata was requested."; return CheckState.Unknown; }
            diskIdentityAttemptUtc = DateTimeOffset.UtcNow; diskIdentityReceivedUtc = diskIdentityInterruptedUtc = null;
            diskIdentityQueryState = CheckState.Unknown;
            diskIdentityCallActive = true;
            try
            {
                NotifyDiskIdentityOwnership();
                DiskIdentityMessage = "Reading bounded file identity/length metadata for the original selected snapshot, including when viewing a child folder…";
                var result = await diskIdentityAccounting.AnalyzeAsync(snapshot, token);
                var receivedUtc = DateTimeOffset.UtcNow;
                if (!Current()) return CheckState.Cancelled;
                diskIdentityReceivedUtc = receivedUtc;
                token.ThrowIfCancellationRequested();
                bool positive = result.State is CheckState.Available or CheckState.Partial;
                if (!Enum.IsDefined(result.State) || result.Items == null || (positive ? result.Items.Count != 1 : result.Items.Count != 0) ||
                    receivedUtc < diskIdentityAttemptUtc.Value || positive && diskIdentityAccounting.HasPendingNativeRead)
                    throw new InvalidOperationException("Identity metadata response or ownership is inconsistent.");
                diskIdentityQueryState = result.State;
                foreach (var report in result.Items)
                {
                    DiskIdentityTotals = $"Snapshot scope: {snapshot.Root.Path}\nSnapshot path-logical: {Display.Bytes(report.PathLogicalBytes)}; revalidated observed paths: {Display.Bytes(report.ObservedPathLogicalBytes)}; observed unique-file logical: {Display.Bytes(report.ObservedUniqueFileLogicalBytes)}. These are logical observations, not physical allocation or reclaimable bytes.\n" +
                        $"Reported default-stream allocation metadata: {report.AllocationState}; {report.AllocationKnownIdentities} observed identities known, {report.AllocationUnknownIdentities} unknown. Counts cover accepted observed identities only; omitted paths remain unknown. No allocation total is computed: distinct files may share storage, and alias rows must not be summed.";
                    foreach (var observation in report.Observations)
                        DiskIdentityRows.Add(new(observation.Path, $"{Display.Bytes(observation.SnapshotBytes)} logical · {observation.ObservedAliasCount} observed path(s) for this identity · reported link count {observation.LinkCount} · reported default-stream allocation: " +
                            (observation.ReportedAllocationBytes is { } allocation ? $"{allocation:N0} bytes (not physical or reclaimable space)" : "Unknown"), observation.Identity));
                    foreach (var issue in report.Issues) DiskIdentityRows.Add(new(issue.Path, issue.Reason, "Unknown / omitted"));
                    DiskIdentityCoverage.Add(report.Coverage);
                }
                if (result.Items.Count == 0) DiskIdentityTotals = "No completed file-identity totals. Snapshot logical map remains a separate observation.";
                DiskIdentityMessage = Stamp(result.State, result.Message);
                return result.State;
            }
            finally
            {
                // Caller timeout/cancellation does not release the actual metadata owner.
                // Clear the caller marker before draining to avoid waiting on ourselves.
                diskIdentityCallActive = false;
                try { await DrainDiskIdentityRead(); }
                finally { NotifyDiskIdentityOwnership(); }
            }
        }, error =>
        {
            if (!Current()) return;
            DiskIdentityRows.Clear(); DiskIdentityCoverage.Clear();
            DiskIdentityTotals = "No completed result from this attempt.";
            diskIdentityInterruptedUtc = DateTimeOffset.UtcNow;
            diskIdentityQueryState = cancellation?.IsCancellationRequested == true ? CheckState.Cancelled : CheckState.Failed;
            DiskIdentityMessage = error + " Physical/reclaimable usage remains unknown. Cancellation ends this view's wait and does not prove native I/O stopped; a pending worker retains its handles and prevents another read until it finishes.";
            NotifyDiskIdentityOwnership();
        });
    }
    private async Task DrainDiskIdentityRead()
    {
        Exception? displayFailure = null;
        try
        {
            if (diskIdentityAccounting.HasPendingNativeRead)
                Status = "Waiting for the owned disk-identity metadata read to return. No native handle will be force-closed.";
            NotifyDiskIdentityOwnership();
        }
        catch (Exception error) { displayFailure = error; }
        // A display/binding notification failure cannot skip actual owner settlement.
        while (diskIdentityAccounting.HasPendingNativeRead) await Task.Delay(100);
        if (displayFailure != null) throw new InvalidOperationException("Identity ownership display failed after native settlement.", displayFailure);
    }
    public void ExplainDiskIdentityCloseBlock()
    {
        Status = "Disk-identity metadata work is still owned. Cancel stops the caller wait; closing waits for the actual worker to release its handles.";
        NotifyDiskIdentityOwnership();
    }
    private void NotifyDiskIdentityOwnership()
    {
        PropertyChanged?.Invoke(this, new(nameof(HasPendingDiskIdentityRead)));
        PropertyChanged?.Invoke(this, new(nameof(DiskIdentityQueryDetails)));
        CommandManager.InvalidateRequerySuggested();
    }
}
