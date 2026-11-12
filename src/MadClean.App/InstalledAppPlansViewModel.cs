using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private InstalledAppActionPlan? installedAppPlan;
    private string installedAppPlanDetails = "Select a registration, then prepare its supported-review preview. No uninstall command is read or run.";
    public string InstalledAppPlanDetails { get => installedAppPlanDetails; private set => Set(ref installedAppPlanDetails, value); }
    public ICommand PreviewInstalledAppPlanCommand { get; private set; } = null!;
    public ICommand ReviewInstalledAppHandoffCommand { get; private set; } = null!;

    private void InitializeInstalledAppPlans()
    {
        InitializeMsiRegistration();
        InitializeMsiUninstallRequest();
        PreviewInstalledAppPlanCommand = new ActionCommand(PreviewInstalledAppPlan,
            () => !IsBusy && latestInstalledSnapshot != null && CurrentInstalledApplication != null);
        ReviewInstalledAppHandoffCommand = new ActionCommand(ReviewInstalledAppHandoff,
            () => !IsBusy && !GuidedOpening && CurrentInstalledApplication != null && installedAppPlan != null);
    }
    private void InvalidateInstalledAppPlan()
    {
        InvalidateMsiWatchCapture();
        InvalidateMsiUninstallRequest();
        InvalidateMsiRegistration();
        installedAppPlan = null;
        InstalledAppPlanDetails = "No current preview. Select a registration and prepare a fresh review plan; incomplete, restricted or ambiguous metadata cannot authorize a plan.";
        CommandManager.InvalidateRequerySuggested();
    }
    private void PreviewInstalledAppPlan()
    {
        installedAppPlan = null;
        var selected = CurrentInstalledApplication;
        var snapshot = latestInstalledSnapshot;
        if (IsBusy || snapshot == null || selected == null) return;
        var result = InstalledAppActionPlans.Preview(snapshot, selected.Identity, DateTimeOffset.UtcNow);
        installedAppPlan = result.Items.SingleOrDefault();
        InstalledAppPlanDetails = Stamp(result.State, result.Message);
        if (installedAppPlan is { } plan)
            InstalledAppPlanDetails += $"\nSelected registration: {plan.Application.Name} · {plan.Application.Version}\nSource: {plan.Application.Source}\nIdentity: {plan.Application.Identity}\nMechanism observation: {plan.Mechanism}\nObserved UTC: {plan.Evidence.ObservedUtc:u}\n\n{plan.ExactEffect}\n\nPermissions: {plan.Permissions}\n\nLimits: {plan.Limitations}";
        CommandManager.InvalidateRequerySuggested();
    }
    private void ReviewInstalledAppHandoff()
    {
        // Revalidate freshness and selection before routing; this button does not dispatch Windows.
        if (GuidedOpening) return;
        PreviewInstalledAppPlan();
        if (installedAppPlan is not { } plan) return;
        SelectedGuidedAction = GuidedWindowsActions.Find(plan.Handoff);
        SelectedPage = 15;
        GuidedMessage = "Review the fixed Windows destination below, then explicitly open it if wanted. Find the selected application yourself inside Windows; MadClean passes no app identifier and performs no uninstall.";
    }
}
