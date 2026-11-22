using System.Windows.Input;
using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private DefenderCustomScanDraft? defenderCustomScanDraft;
    private string defenderCustomScanStatus = "Choose a folder to review the scope of a future custom scan.";

    public ICommand ReviewDefenderCustomScanCommand { get; private set; } = null!;
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
        ClearDefenderCustomScanCommand = new ActionCommand(() => SetDefenderCustomScanDraft(null,
            "Selection cleared."), () => !IsBusy && defenderCustomScanDraft != null);
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

    private void SetDefenderCustomScanDraft(DefenderCustomScanDraft? draft, string status)
    {
        defenderCustomScanDraft = draft;
        DefenderCustomScanStatus = status;
        PropertyChanged?.Invoke(this, new(nameof(DefenderCustomScanFolder)));
        CommandManager.InvalidateRequerySuggested();
    }
}
