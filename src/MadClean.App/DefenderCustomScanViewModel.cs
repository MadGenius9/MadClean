using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private bool defenderScopeCheckPending;
    private DefenderCustomScanDraft? defenderCustomScanDraft;
    private string defenderCustomScanStatus = "Choose a folder to review the scope of a future custom scan.";

    public ICommand ReviewDefenderCustomScanCommand { get; private set; } = null!;
    public ICommand CheckDefenderCustomScanFolderCommand { get; private set; } = null!;
    public ICommand ClearDefenderCustomScanCommand { get; private set; } = null!;
    public string DefenderCustomScanFolder => defenderCustomScanDraft?.Folder ?? "No folder selected";
    public string DefenderCustomScanEffects => DefenderCustomScanDraft.Effects;
    public string DefenderCustomScanStatus
    {
        get => defenderCustomScanStatus;
        private set => Set(ref defenderCustomScanStatus, value);
    }

    private void InitializeDefenderCustomScanReview()
    {
        ReviewDefenderCustomScanCommand = new ActionCommand(ReviewDefenderCustomScan, () => !IsBusy);
        CheckDefenderCustomScanFolderCommand = new AsyncCommand(CheckDefenderCustomScanFolder,
            () => !IsBusy && !defenderScopeCheckPending && defenderCustomScanDraft != null);
        ClearDefenderCustomScanCommand = new ActionCommand(() => SetDefenderCustomScanDraft(null,
            "Selection cleared."), () => !IsBusy && !defenderScopeCheckPending && defenderCustomScanDraft != null);
    }

    private void ReviewDefenderCustomScan()
    {
        if (IsBusy) return;
        var picker = new OpenFolderDialog { Title = "Choose one folder to preview a Defender custom scan", Multiselect = false };
        if (picker.ShowDialog() != true || IsBusy) return;
        var draft = DefenderCustomScanDraft.Prepare(picker.FolderName);
        SetDefenderCustomScanDraft(draft, draft == null
            ? "Choose a folder inside a local drive using an ordinary path."
            : "Folder selected for review. Drive type, folder access, and redirected paths still require verification.");
    }

    internal bool HasPendingDefenderCustomScanScope => defenderScopeCheckPending;

    internal void ExplainDefenderCustomScanScopeCloseBlock() =>
        Status = "Wait for the selected folder check to finish before closing MadClean.";

    private async Task CheckDefenderCustomScanFolder()
    {
        if (IsBusy || defenderScopeCheckPending || defenderCustomScanDraft is not { } draft) return;
        defenderScopeCheckPending = true;
        try
        {
            await RunDiscoveryAsync(ActivityArea.Defender, "Checking the selected Defender folder path", async token =>
            {
                DefenderCustomScanStatus = "Checking the selected folder path…";
                var result = await WindowsDefenderScanScope.ObserveAsync(draft, token);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(draft, defenderCustomScanDraft)) return CheckState.Cancelled;
                DefenderCustomScanStatus = Stamp(result.State, result.Message);
                return result.State;
            }, error =>
            {
                if (ReferenceEquals(draft, defenderCustomScanDraft))
                    DefenderCustomScanStatus = error + " The selected folder has no completed scope check.";
            });
        }
        finally
        {
            defenderScopeCheckPending = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void SetDefenderCustomScanDraft(DefenderCustomScanDraft? draft, string status)
    {
        defenderCustomScanDraft = draft;
        DefenderCustomScanStatus = status;
        PropertyChanged?.Invoke(this, new(nameof(DefenderCustomScanFolder)));
        CommandManager.InvalidateRequerySuggested();
    }
}
