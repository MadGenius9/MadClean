using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IDiskIdentityAccounting diskIdentityAccounting = new WindowsDiskIdentityAccounting();
    private DiskUsageReport? diskIdentitySnapshot;
    private string diskIdentityMessage = "Not checked. After mapping a folder, explicitly analyze the same snapshot's observed file identities. No file contents are read.";
    private string diskIdentityTotals = "No file-identity accounting result.";
    public ObservableCollection<ResultRow> DiskIdentityRows { get; } = [];
    public ObservableCollection<string> DiskIdentityCoverage { get; } = [];
    public ICommand AnalyzeDiskIdentitiesCommand { get; private set; } = null!;
    public string DiskIdentityMessage { get => diskIdentityMessage; private set => Set(ref diskIdentityMessage, value); }
    public string DiskIdentityTotals { get => diskIdentityTotals; private set => Set(ref diskIdentityTotals, value); }
    private void InitializeDiskIdentities() => AnalyzeDiskIdentitiesCommand = new AsyncCommand(AnalyzeDiskIdentities,
        () => !IsBusy && diskIdentitySnapshot != null);
    private void InvalidateDiskIdentities()
    {
        diskIdentitySnapshot = null;
        DiskIdentityRows.Clear(); DiskIdentityCoverage.Clear();
        DiskIdentityTotals = "No current file-identity result.";
        DiskIdentityMessage = "Map snapshot changed. Explicitly analyze the new snapshot to inspect observed aliases; the treemap continues to show path-logical bytes.";
        CommandManager.InvalidateRequerySuggested();
    }
    private Task AnalyzeDiskIdentities() => RunDiscoveryAsync(ActivityArea.DiskIdentityAccounting, "Reading snapshot file identities", async token =>
    {
        var snapshot = diskIdentitySnapshot;
        DiskIdentityRows.Clear(); DiskIdentityCoverage.Clear();
        DiskIdentityTotals = "In progress — no completed identity totals.";
        if (snapshot == null) { DiskIdentityMessage = "Map a fixed local folder first. No metadata was requested."; return CheckState.Unknown; }
        DiskIdentityMessage = "Reading bounded file identity/length metadata for the original selected snapshot, including when viewing a child folder…";
        var result = await diskIdentityAccounting.AnalyzeAsync(snapshot, token);
        token.ThrowIfCancellationRequested();
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
    }, error =>
    {
        DiskIdentityRows.Clear(); DiskIdentityCoverage.Clear();
        DiskIdentityTotals = "No completed result from this attempt.";
        DiskIdentityMessage = error + " Physical/reclaimable usage remains unknown. Cancellation ends this view's wait and does not prove native I/O stopped; a pending worker retains its handles and prevents another read until it finishes.";
    });
}
