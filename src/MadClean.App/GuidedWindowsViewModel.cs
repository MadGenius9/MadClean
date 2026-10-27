using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly GuidedWindowsActionLauncher guidedLauncher = new();
    private GuidedWindowsAction? selectedGuidedAction;
    private bool guidedOpening;
    private string guidedMessage = "No workflow opened. Select a documented destination and review its exact effect first.";
    public IReadOnlyList<GuidedWindowsAction> GuidedActions => GuidedWindowsActions.All;
    public ICommand OpenGuidedActionCommand { get; private set; } = null!;
    public ICommand OpenGuidedToolsPageCommand { get; private set; } = null!;
    public bool GuidedOpening { get => guidedOpening; private set { Set(ref guidedOpening, value); CommandManager.InvalidateRequerySuggested(); } }
    public string GuidedMessage { get => guidedMessage; private set => Set(ref guidedMessage, value); }
    public GuidedWindowsAction? SelectedGuidedAction
    {
        get => selectedGuidedAction;
        set
        {
            if (GuidedOpening || value != null && !GuidedActions.Contains(value)) return;
            Set(ref selectedGuidedAction, value);
            PropertyChanged?.Invoke(this, new(nameof(GuidedPreview)));
            GuidedMessage = "Selection reviewed below. No new handoff has been requested for this selection.";
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string GuidedPreview => selectedGuidedAction is { } action
        ? $"{action.Title}\nExact destination: {action.TargetUri.OriginalString}\n\nWhat MadClean will do: {action.ExactEffect}\n\nPermissions: {action.Permissions}\n\nCoverage and limits: {action.Limitations}\n\nOfficial reference: {action.Source}"
        : "Select a workflow to review its exact destination, navigation effect, permissions and limitations. No action is preselected. These tools open Windows pages or official instructions; they do not execute a queued MadClean toggle, uninstall, scan, install or repair.";
    private void InitializeGuidedWindowsTools()
    {
        OpenGuidedToolsPageCommand = new ActionCommand(() => SelectedPage = 15, () => true);
        OpenGuidedActionCommand = new AsyncCommand(OpenGuidedAction, () => selectedGuidedAction != null && !GuidedOpening && !IsBusy);
    }
    private async Task OpenGuidedAction()
    {
        if (selectedGuidedAction is not { } action || GuidedOpening || IsBusy) return;
        GuidedOpening = true;
        GuidedMessage = "Requesting the reviewed Windows destination only. Shell dispatch cannot be rolled back by MadClean.";
        Event(ActivityArea.GuidedWindowsHandoff, ActivityOutcome.Started);
        try
        {
            // This is a separate bounded handoff, not a cancellable servicing/query process.
            var result = await guidedLauncher.OpenAsync(action.Task, CancellationToken.None);
            GuidedMessage = Stamp(result.State, result.Message);
            Event(ActivityArea.GuidedWindowsHandoff, Outcome(result.State));
        }
        catch (Exception)
        {
            GuidedMessage = "Unknown · Windows navigation could not be monitored. No underlying scan/uninstall/install/repair success is claimed; a dispatched page may still open.";
            Event(ActivityArea.GuidedWindowsHandoff, ActivityOutcome.Failed);
        }
        finally { GuidedOpening = false; }
    }
}
