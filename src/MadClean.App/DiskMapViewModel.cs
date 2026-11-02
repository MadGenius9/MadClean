using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly Stack<DiskUsageNode> diskMapParents = new();
    private DiskUsageNode? diskMapRoot, diskMapCurrent, selectedDiskNode;
    private string? diskMapRequestedPath;
    private long diskMapGeneration;
    private DateTimeOffset? diskMapAttemptUtc, diskMapReceivedUtc, diskMapInterruptedUtc;
    private CheckState diskMapSourceState = CheckState.Unknown;
    private bool diskMapReading;
    private string diskMapMessage = "Not checked. Choose one local folder to inspect its known logical file sizes. No whole-PC scan runs automatically.";
    public IReadOnlyList<DiskUsageNode> DiskMapChildren { get; private set; } = Array.Empty<DiskUsageNode>();
    public string DiskMapMessage { get => diskMapMessage; private set => Set(ref diskMapMessage, value); }
    public string DiskMapPath => diskMapCurrent?.Path ?? "No folder selected";
    public string DiskMapKnownBytes => diskMapCurrent == null ? "Not checked" : Display.Bytes(diskMapCurrent.Bytes) + " known logical bytes";
    public string DiskMapQueryDetails => diskMapRequestedPath == null
        ? "No original folder selected. Refresh runs only when explicitly requested."
        : $"Original requested scope: {diskMapRequestedPath}\nAttempt started UTC: {diskMapAttemptUtc:u}\n" +
            (diskMapReading ? "Reading metadata; no completed result from this attempt."
                : diskMapReceivedUtc is { } received
                    ? $"Report received UTC: {received:u}; coverage: {diskMapSourceState}. This is not a per-file observation time."
                    : diskMapInterruptedUtc is { } interrupted
                        ? $"Attempt interruption observed UTC: {interrupted:u}; state: {diskMapSourceState}. No completed report is displayed."
                        : $"Scope validation state: {diskMapSourceState}. No metadata report was received.") +
            "\nRefresh rechecks this original path, including when viewing a child. It does not establish that the path still identifies the same folder or that all observations were simultaneous.";
    public DiskUsageNode? DiskMapCurrent { get => diskMapCurrent; private set => Set(ref diskMapCurrent, value); }
    public DiskUsageNode? SelectedDiskNode
    {
        get => selectedDiskNode;
        set { Set(ref selectedDiskNode, value); PropertyChanged?.Invoke(this, new(nameof(DiskMapSelection))); CommandManager.InvalidateRequerySuggested(); }
    }
    public string DiskMapSelection => selectedDiskNode == null ? "Select a tile or list item for its exact path and known size. Small items remain accessible in the list." :
        $"{selectedDiskNode.Path}\n{Display.Bytes(selectedDiskNode.Bytes)} known logical bytes · {(selectedDiskNode.IsDirectory ? "folder" : "file")}. Size is not a junk or deletion-safety classification.";
    public ICommand ChooseDiskMapFolderCommand { get; private set; } = null!;
    public ICommand RefreshDiskMapCommand { get; private set; } = null!;
    public ICommand DrillDiskMapCommand { get; private set; } = null!;
    public ICommand UpDiskMapCommand { get; private set; } = null!;
    public ICommand ResetDiskMapCommand { get; private set; } = null!;
    private void InitializeDiskMap()
    {
        InitializeDiskIdentities();
        ChooseDiskMapFolderCommand = new AsyncCommand(ChooseDiskMapFolder, () => !IsBusy);
        RefreshDiskMapCommand = new AsyncCommand(() => diskMapRequestedPath is { } path ? MapDiskFolder(path) : Task.CompletedTask,
            () => !IsBusy && diskMapRequestedPath != null);
        DrillDiskMapCommand = new ActionCommand(DrillDiskMap, () => !IsBusy && SelectedDiskNode?.IsDirectory == true && SelectedDiskNode.Children.Count > 0);
        UpDiskMapCommand = new ActionCommand(() => ShowDiskFolder(diskMapParents.Pop()), () => !IsBusy && diskMapParents.Count > 0);
        ResetDiskMapCommand = new ActionCommand(() => { diskMapParents.Clear(); if (diskMapRoot != null) ShowDiskFolder(diskMapRoot); }, () => !IsBusy && diskMapRoot != null);
    }
    private async Task ChooseDiskMapFolder()
    {
        if (IsBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose a local folder for a read-only disk-space map", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        await MapDiskFolder(dialog.FolderName);
    }
    private Task MapDiskFolder(string path)
    {
        if (IsBusy) return Task.CompletedTask;
        long generation = -1;
        bool Current() => generation == diskMapGeneration;
        return RunDiscoveryAsync(ActivityArea.DiskMap, "Mapping the selected local folder", async token =>
        {
            generation = ++diskMapGeneration;
            diskMapRequestedPath = path;
            diskMapAttemptUtc = DateTimeOffset.UtcNow; diskMapReceivedUtc = null; diskMapInterruptedUtc = null;
            diskMapReading = true; diskMapSourceState = CheckState.Unknown;
            ClearDiskMapResult(); NotifyDiskMapQuery();
            DiskMapMessage = "Revalidating the original selected local folder; no previous map or identity totals remain current…";
            var validation = await Task.Run(() => ValidateDiskMapDrive(path, token), token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            if (validation != CheckState.Available)
            {
                diskMapReading = false; diskMapSourceState = validation;
                DiskMapMessage = validation == CheckState.Unsupported
                    ? "Unsupported: choose a folder on a fixed local drive; network/device paths are outside this phase. No current map is displayed."
                    : "The selected drive could not be validated. Check the location and permissions, then retry Refresh or choose another folder. No current map is displayed.";
                NotifyDiskMapQuery(); return validation;
            }
            DiskMapMessage = "Reading file metadata under the selected folder; use Cancel to stop…";
            var report = await new DiskUsageScanner().ScanAsync(path, token); token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            if (!string.Equals(Path.GetFullPath(path), report.Root.Path, StringComparison.OrdinalIgnoreCase) || report.Root.Bytes < 0)
                throw new InvalidDataException("Disk map report scope is inconsistent.");
            bool partial = report.Truncated || report.Issues.Count > 0 || report.SkippedEntries > 0;
            diskMapReading = false; diskMapReceivedUtc = DateTimeOffset.UtcNow;
            diskMapSourceState = partial ? CheckState.Partial : CheckState.Available;
            diskIdentitySnapshot = report;
            diskMapRoot = report.Root; ShowDiskFolder(report.Root);
            DiskMapMessage = Stamp(diskMapSourceState,
                $"{Display.Bytes(report.Root.Bytes)} known logical bytes; {report.SkippedEntries} observed skips; {report.Issues.Count} issues. " +
                (partial ? "Incomplete: skipped/unreadable/unvisited bytes are unknown. " : "Selected-folder metadata inventory completed. ") +
                "This is not allocated/physical disk usage; hardlinks may be counted more than once in this map. Use the optional identity analysis to inspect observed aliases separately. No file contents were read or changed.");
            DiskMapIssues.Clear(); foreach (var issue in report.Issues) DiskMapIssues.Add(new(issue.Path, issue.Reason, "Unknown / skipped"));
            NotifyDiskMapQuery(); return diskMapSourceState;
        }, error =>
        {
            if (!Current()) return;
            ClearDiskMapResult();
            diskMapReading = false; diskMapReceivedUtc = null; diskMapInterruptedUtc = DateTimeOffset.UtcNow;
            diskMapSourceState = cancellation?.IsCancellationRequested == true ? CheckState.Cancelled : CheckState.Failed;
            DiskMapMessage = error + " No current map or identity totals remain. Refresh retries the original requested folder; it does not reuse a drilled child or previous observations.";
            NotifyDiskMapQuery();
        });
    }
    private static CheckState ValidateDiskMapDrive(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
                new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
                return CheckState.Unsupported;
            token.ThrowIfCancellationRequested(); return CheckState.Available;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { token.ThrowIfCancellationRequested(); return CheckState.Unavailable; }
    }
    private void ClearDiskMapResult()
    {
        InvalidateDiskIdentities();
        diskMapRoot = null; diskMapParents.Clear(); DiskMapCurrent = null; DiskMapChildren = Array.Empty<DiskUsageNode>(); SelectedDiskNode = null; DiskMapIssues.Clear();
        PropertyChanged?.Invoke(this, new(nameof(DiskMapChildren))); PropertyChanged?.Invoke(this, new(nameof(DiskMapPath))); PropertyChanged?.Invoke(this, new(nameof(DiskMapKnownBytes)));
    }
    private void NotifyDiskMapQuery()
    {
        PropertyChanged?.Invoke(this, new(nameof(DiskMapQueryDetails)));
        CommandManager.InvalidateRequerySuggested();
    }
    public ObservableCollection<ResultRow> DiskMapIssues { get; } = [];
    private void DrillDiskMap()
    {
        if (SelectedDiskNode?.IsDirectory != true || SelectedDiskNode.Children.Count == 0 || diskMapCurrent == null) return;
        diskMapParents.Push(diskMapCurrent); ShowDiskFolder(SelectedDiskNode);
    }
    private void ShowDiskFolder(DiskUsageNode folder)
    {
        DiskMapCurrent = folder; DiskMapChildren = folder.Children; PropertyChanged?.Invoke(this, new(nameof(DiskMapChildren)));
        SelectedDiskNode = folder.Children.FirstOrDefault();
        PropertyChanged?.Invoke(this, new(nameof(DiskMapPath))); PropertyChanged?.Invoke(this, new(nameof(DiskMapKnownBytes)));
        CommandManager.InvalidateRequerySuggested();
    }
}
