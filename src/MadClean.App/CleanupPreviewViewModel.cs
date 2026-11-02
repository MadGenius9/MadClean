using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly CleanupExclusionStore? cleanupExclusionStore = CreateExclusionStore();
    private IReadOnlyList<CleanupCandidate> cleanupPreview = Array.Empty<CleanupCandidate>();
    private ResultRow? selectedCleanupRow;
    private CleanupExclusion? selectedExclusion;
    private string exclusionsMessage = "Exclusions have not been loaded.";
    public ObservableCollection<CleanupExclusion> CleanupExclusions { get; } = [];
    public string ExclusionsMessage { get => exclusionsMessage; private set => Set(ref exclusionsMessage, value); }
    public ResultRow? SelectedCleanupRow { get => selectedCleanupRow; set { if (!ReferenceEquals(selectedCleanupRow, value)) InvalidateCleanupFileEvidence(); Set(ref selectedCleanupRow, value); PropertyChanged?.Invoke(this, new(nameof(SelectedCleanupDetails))); CommandManager.InvalidateRequerySuggested(); } }
    public string SelectedCleanupDetails => selectedCleanupRow == null ? "Select a preview row to inspect and copy its complete path, size/observation and category before choosing an exclusion." :
        selectedCleanupRow.Name + "\n" + selectedCleanupRow.Detail + "\n" + selectedCleanupRow.State +
        (cleanupPreview.Any(candidate => candidate.Path.Equals(selectedCleanupRow.Name, StringComparison.OrdinalIgnoreCase))
            ? "\nActive use is not established. Files can remain memory-mapped after ordinary handles close; a successful sharing check is not proof they are inactive. This is a read-only candidate, not deletion permission."
            : "\nThis is an excluded/error observation, not a cleanup candidate or deletion permission.");
    public CleanupExclusion? SelectedCleanupExclusion { get => selectedExclusion; set { Set(ref selectedExclusion, value); CommandManager.InvalidateRequerySuggested(); } }
    public ICommand ExcludeCleanupFileCommand { get; private set; } = null!;
    public ICommand ExcludeCleanupFolderCommand { get; private set; } = null!;
    public ICommand RemoveCleanupExclusionCommand { get; private set; } = null!;

    private static CleanupExclusionStore? CreateExclusionStore()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.IsPathFullyQualified(root) ? new(Path.Combine(root, "MadClean", "cleanup-exclusions.json")) : null;
    }
    private void InitializeCleanupPreview()
    {
        ExcludeCleanupFileCommand = new ActionCommand(ExcludeSelectedCleanupFile, () => !IsBusy && SelectedCleanupRow != null &&
            cleanupPreview.Any(c => c.Path.Equals(SelectedCleanupRow.Name, StringComparison.OrdinalIgnoreCase)));
        ExcludeCleanupFolderCommand = new ActionCommand(ExcludeCleanupFolder, () => !IsBusy);
        RemoveCleanupExclusionCommand = new ActionCommand(RemoveCleanupExclusion, () => !IsBusy && SelectedCleanupExclusion != null);
        InitializeCleanupFileEvidence();
        LoadCleanupPreferences();
    }
    private CleanupExclusionSettings LoadCleanupPreferences()
    {
        var settings = cleanupExclusionStore?.Load() ?? new CleanupExclusionSettings([], "The local settings folder is unavailable. Cleanup is blocked until exclusions can be read safely.");
        CleanupExclusions.Clear(); foreach (var item in settings.Items) CleanupExclusions.Add(item);
        ExclusionsMessage = settings.Error ?? $"{settings.Items.Count} saved exclusions. They apply to cleanup previews only; file contents and other discovery scopes are unchanged.";
        return settings;
    }
    private void ExcludeSelectedCleanupFile()
    {
        if (SelectedCleanupRow == null || !cleanupPreview.Any(c => c.Path.Equals(SelectedCleanupRow.Name, StringComparison.OrdinalIgnoreCase))) return;
        SaveCleanupPreferences(items => items.Append(new(SelectedCleanupRow.Name, false)).ToArray());
    }
    private void ExcludeCleanupFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to exclude from cleanup previews", Multiselect = false };
        if (dialog.ShowDialog() == true) SaveCleanupPreferences(items => items.Append(new(dialog.FolderName, true)).ToArray());
    }
    private void RemoveCleanupExclusion()
    {
        var selected = SelectedCleanupExclusion;
        if (selected != null) SaveCleanupPreferences(items => items.Where(i => i != selected).ToArray());
    }
    private void SaveCleanupPreferences(Func<IReadOnlyList<CleanupExclusion>, IReadOnlyList<CleanupExclusion>> change)
    {
        var current = LoadCleanupPreferences();
        var error = current.Error ?? cleanupExclusionStore?.Save(change(current.Items));
        if (cleanupExclusionStore == null) error ??= "The local settings folder is unavailable.";
        if (error != null) { ExclusionsMessage = error; InvalidateCleanupPreview("Exclusion settings could not be saved. Fix settings access, then scan again; no cleanup operation occurred."); return; }
        LoadCleanupPreferences();
        SelectedCleanupExclusion = null;
        InvalidateCleanupPreview("Saved exclusion preferences. Choose Scan local to rebuild the exact preview using these exclusions. No inventory file was changed.");
        Event(ActivityArea.CleanupPreferences, ActivityOutcome.Completed);
    }
    private void InvalidateCleanupPreview(string message)
    {
        InvalidateCleanupEvidenceSnapshot();
        cleanupPreview = Array.Empty<CleanupCandidate>(); SelectedCleanupRow = null;
        CleanupRows.Clear(); CleanupCategories.Clear(); CleanupSummary = "Rescan required"; CleanupMessage = message;
    }
}
