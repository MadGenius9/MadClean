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
    private string diskMapMessage = "Not checked. Choose one local folder to inspect its known logical file sizes. No whole-PC scan runs automatically.";
    public IReadOnlyList<DiskUsageNode> DiskMapChildren { get; private set; } = Array.Empty<DiskUsageNode>();
    public string DiskMapMessage { get => diskMapMessage; private set => Set(ref diskMapMessage, value); }
    public string DiskMapPath => diskMapCurrent?.Path ?? "No folder selected";
    public string DiskMapKnownBytes => diskMapCurrent == null ? "Not checked" : Display.Bytes(diskMapCurrent.Bytes) + " known logical bytes";
    public DiskUsageNode? DiskMapCurrent { get => diskMapCurrent; private set => Set(ref diskMapCurrent, value); }
    public DiskUsageNode? SelectedDiskNode
    {
        get => selectedDiskNode;
        set { Set(ref selectedDiskNode, value); PropertyChanged?.Invoke(this, new(nameof(DiskMapSelection))); CommandManager.InvalidateRequerySuggested(); }
    }
    public string DiskMapSelection => selectedDiskNode == null ? "Select a tile or list item for its exact path and known size. Small items remain accessible in the list." :
        $"{selectedDiskNode.Path}\n{Display.Bytes(selectedDiskNode.Bytes)} known logical bytes · {(selectedDiskNode.IsDirectory ? "folder" : "file")}. Size is not a junk or deletion-safety classification.";
    public ICommand ChooseDiskMapFolderCommand { get; private set; } = null!;
    public ICommand DrillDiskMapCommand { get; private set; } = null!;
    public ICommand UpDiskMapCommand { get; private set; } = null!;
    public ICommand ResetDiskMapCommand { get; private set; } = null!;
    private void InitializeDiskMap()
    {
        InitializeDiskIdentities();
        ChooseDiskMapFolderCommand = new AsyncCommand(ChooseDiskMapFolder, () => !IsBusy);
        DrillDiskMapCommand = new ActionCommand(DrillDiskMap, () => !IsBusy && SelectedDiskNode?.IsDirectory == true && SelectedDiskNode.Children.Count > 0);
        UpDiskMapCommand = new ActionCommand(() => ShowDiskFolder(diskMapParents.Pop()), () => !IsBusy && diskMapParents.Count > 0);
        ResetDiskMapCommand = new ActionCommand(() => { diskMapParents.Clear(); if (diskMapRoot != null) ShowDiskFolder(diskMapRoot); }, () => !IsBusy && diskMapRoot != null);
    }
    private async Task ChooseDiskMapFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a local folder for a read-only disk-space map", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FolderName;
        await RunDiscoveryAsync(ActivityArea.DiskMap, "Mapping the selected local folder", async token =>
        {
            InvalidateDiskIdentities();
            diskMapRoot = null; diskMapParents.Clear(); DiskMapCurrent = null; DiskMapChildren = Array.Empty<DiskUsageNode>(); SelectedDiskNode = null; DiskMapIssues.Clear();
            PropertyChanged?.Invoke(this, new(nameof(DiskMapChildren))); PropertyChanged?.Invoke(this, new(nameof(DiskMapPath))); PropertyChanged?.Invoke(this, new(nameof(DiskMapKnownBytes)));
            try
            {
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
                { DiskMapMessage = "Unsupported: choose a folder on a fixed local drive; network/device paths are outside this phase."; return CheckState.Unsupported; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { DiskMapMessage = "The selected drive could not be validated. Check the location and permissions."; return CheckState.Unavailable; }
            DiskMapMessage = "Reading file metadata under the selected folder; use Cancel to stop…";
            var report = await new DiskUsageScanner().ScanAsync(path, token); token.ThrowIfCancellationRequested();
            diskIdentitySnapshot = report;
            diskMapRoot = report.Root; ShowDiskFolder(report.Root);
            bool partial = report.Truncated || report.Issues.Count > 0 || report.SkippedEntries > 0;
            DiskMapMessage = Stamp(partial ? CheckState.Partial : CheckState.Available,
                $"{Display.Bytes(report.Root.Bytes)} known logical bytes; {report.SkippedEntries} observed skips; {report.Issues.Count} issues. " +
                (partial ? "Incomplete: skipped/unreadable/unvisited bytes are unknown. " : "Selected-folder metadata inventory completed. ") +
                "This is not allocated/physical disk usage; hardlinks may be counted more than once in this map. Use the optional identity analysis to inspect observed aliases separately. No file contents were read or changed.");
            DiskMapIssues.Clear(); foreach (var issue in report.Issues) DiskMapIssues.Add(new(issue.Path, issue.Reason, "Unknown / skipped"));
            return partial ? CheckState.Partial : CheckState.Available;
        }, error => DiskMapMessage = error);
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
