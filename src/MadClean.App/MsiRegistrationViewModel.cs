using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IMsiRegistrationInventory msiRegistrations = new WindowsMsiRegistrationInventory();
    private long msiRegistrationGeneration;
    private string msiRegistrationMessage = "Not checked. Select an MSI registration and explicitly verify its Windows Installer metadata. No installer package is opened and no uninstall runs.";
    private string msiRegistrationDetails = "No selected Windows Installer context has been checked. Registration hints are not verified package integrity or removal eligibility.";
    private string msiRegistrationAttempt = "Not checked";
    public ObservableCollection<MsiRegistrationObservation> MsiRegistrationRows { get; } = [];
    public ObservableCollection<string> MsiRegistrationCoverage { get; } = [];
    public ICommand VerifyMsiRegistrationCommand { get; private set; } = null!;
    public string MsiRegistrationMessage { get => msiRegistrationMessage; private set => Set(ref msiRegistrationMessage, value); }
    public string MsiRegistrationDetails { get => msiRegistrationDetails; private set => Set(ref msiRegistrationDetails, value); }
    public string MsiRegistrationAttempt { get => msiRegistrationAttempt; private set => Set(ref msiRegistrationAttempt, value); }
    private void InitializeMsiRegistration()
    {
        InitializeMsiActionBinding();
        VerifyMsiRegistrationCommand = new AsyncCommand(VerifyMsiRegistration,
            () => !IsBusy && latestInstalledSnapshot != null && CurrentInstalledApplication?.RegistrationEvidence?.WindowsInstaller == true);
    }
    private void InvalidateMsiRegistration()
    {
        InvalidateMsiActionBinding();
        msiRegistrationGeneration++;
        MsiRegistrationRows.Clear(); MsiRegistrationCoverage.Clear();
        MsiRegistrationMessage = "No current MSI verification. Select an MSI registration and choose Verify selected MSI metadata.";
        MsiRegistrationDetails = "No current context evidence. This separate read-only check cannot authorize uninstall or override removal restrictions.";
        MsiRegistrationAttempt = "Not checked for the current selection";
    }
    private Task VerifyMsiRegistration()
    {
        long generation = -1;
        InstalledApplication? selection = null;
        CheckResult<InstalledApplication>? snapshot = null;
        bool Current() => generation == msiRegistrationGeneration && ReferenceEquals(selection, CurrentInstalledApplication) && ReferenceEquals(snapshot, latestInstalledSnapshot);
        return RunDiscoveryAsync(ActivityArea.MsiRegistrationVerification, "Verifying selected Windows Installer metadata", async token =>
        {
            selection = CurrentInstalledApplication; snapshot = latestInstalledSnapshot;
            InvalidateMsiRegistration(); generation = msiRegistrationGeneration;
            if (selection == null || snapshot == null)
            { MsiRegistrationMessage = "Select a current MSI registration first. No Windows Installer query ran."; return CheckState.Unknown; }
            var qualified = MsiRegistrationReview.CreateScope(snapshot, selection.Identity, DateTimeOffset.UtcNow, token);
            var scope = qualified.Items.SingleOrDefault();
            if (scope == null)
            { MsiRegistrationMessage = Stamp(qualified.State, qualified.Message); return qualified.State; }
            MsiRegistrationAttempt = DateTimeOffset.Now.ToString("g");
            MsiRegistrationMessage = "Reading this exact product's current-user or machine registration metadata only…";
            var result = await msiRegistrations.VerifyAsync(scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            foreach (var report in result.Items)
            {
                foreach (var instance in report.Instances) MsiRegistrationRows.Add(instance);
                foreach (var note in report.Coverage) MsiRegistrationCoverage.Add(note);
                string restriction = report.NoRemove switch { true => "Restricted by the source registration", false => "No restriction declared by the source registration", _ => "Unknown" };
                MsiRegistrationDetails = $"Selected registration: {report.RegistrationIdentity}\nProduct code: {report.ProductCode}\nSource inventory state: {report.SnapshotState}\nARP metadata observation UTC: {scope.Application.RegistrationEvidence!.ObservedUtc:u}\nSelected scope validation UTC: {scope.ValidatedUtc:u}\nRemoval restriction: {restriction}\nMSI metadata check UTC: {report.CheckedUtc:u}\n\nWindows Installer context/state and display metadata are observations, not package authenticity, repair status, removal permission or uninstall completion. Multiple contexts or mismatches remain unresolved. No other user's registrations or SIDs are requested. Existing removal restrictions are retained; uninstall execution remains unavailable.";
            }
            MsiRegistrationMessage = Stamp(result.State, result.Message);
            return result.State;
        }, error =>
        {
            if (!Current()) return;
            MsiRegistrationRows.Clear(); MsiRegistrationCoverage.Clear();
            MsiRegistrationMessage = error + " A synchronous Windows Installer metadata read may still finish; another query is blocked while that worker remains pending. No uninstall or installation was started.";
            MsiRegistrationDetails = "No completed verification from this attempt; Windows Installer context and removal eligibility remain unknown.";
        });
    }
}
