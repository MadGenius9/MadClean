using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IMsiActionBindingInventory msiActionBindings = new WindowsMsiActionBindingInventory();
    private long msiActionBindingGeneration;
    private string msiActionBindingMessage = "Not checked. Explicitly recheck the selected MSI registration across current-user and machine contexts.";
    private string msiActionBindingDetails = "No selection/context binding observed. Execution remains disabled; a matching product code is not permission to uninstall.";
    public ICommand RecheckMsiActionBindingCommand { get; private set; } = null!;
    public string MsiActionBindingMessage { get => msiActionBindingMessage; private set => Set(ref msiActionBindingMessage, value); }
    public string MsiActionBindingDetails { get => msiActionBindingDetails; private set => Set(ref msiActionBindingDetails, value); }
    private void InitializeMsiActionBinding() => RecheckMsiActionBindingCommand = new AsyncCommand(RecheckMsiActionBinding,
        () => !IsBusy && latestInstalledSnapshot != null && selectedApplication?.RegistrationEvidence?.WindowsInstaller == true);
    private void InvalidateMsiActionBinding()
    {
        msiActionBindingGeneration++;
        MsiActionBindingMessage = "Not checked for this selection. No prior context-binding result is current.";
        MsiActionBindingDetails = "Select an MSI registration and explicitly recheck its current source/context binding. No installer action is available.";
    }
    private Task RecheckMsiActionBinding()
    {
        long generation = -1;
        InstalledApplication? selected = null;
        CheckResult<InstalledApplication>? snapshot = null;
        bool Current() => generation == msiActionBindingGeneration && ReferenceEquals(selected, selectedApplication) && ReferenceEquals(snapshot, latestInstalledSnapshot);
        return RunDiscoveryAsync(ActivityArea.MsiRegistrationVerification, "Rechecking selected MSI context binding", async token =>
        {
            selected = selectedApplication; snapshot = latestInstalledSnapshot;
            InvalidateMsiActionBinding(); generation = msiActionBindingGeneration;
            if (selected == null || snapshot == null)
            { MsiActionBindingMessage = "Select a current MSI registration first; no query started."; return CheckState.Unknown; }
            var prepared = MsiActionBindingPolicy.CreateScope(snapshot, selected, DateTimeOffset.UtcNow, token);
            if (prepared.Items.Count != 1)
            { MsiActionBindingMessage = Stamp(prepared.State, prepared.Message); return prepared.State; }
            var scope = prepared.Items[0];
            MsiActionBindingMessage = "Refreshing actual ARP evidence and checking only this product across current-user/machine contexts…";
            MsiActionBindingDetails = "In progress. No current unique binding, global absence or action permission is established.";
            var result = await msiActionBindings.VerifyAsync(scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            MsiActionBindingMessage = Stamp(result.State, result.Message);
            if (result.Items.Count != 1)
            { MsiActionBindingDetails = "No completed binding report. Pending, inaccessible or failed evidence remains unknown; no installer action is authorized."; return result.State; }
            var data = result.Items[0];
            if (data.CanExecute || data.RegistrationIdentity != scope.Application.Identity || data.ProductCode != scope.ProductCode ||
                data.SelectionSnapshotState != scope.SnapshotState || data.CheckedUtc.Offset != TimeSpan.Zero || data.CheckedUtc < scope.ValidatedUtc || data.CheckedUtc > DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Selected MSI binding result is inconsistent.");
            string instances = data.Instances.Count == 0 ? "No accepted context observations; this is not absence proof."
                : string.Join("\n", data.Instances.Select(row => $"{row.Context} · {row.ProductState} · {row.Name} · {row.Version} · {row.Publisher}"));
            MsiActionBindingDetails = $"Selection/context review: {data.State}\nObserved UTC: {data.CheckedUtc:u}\nHistorical ARP coverage: {data.SelectionSnapshotState}; fresh ARP coverage: {data.CurrentSnapshotState}\nFresh NoRemove: {(data.NoRemove.HasValue ? data.NoRemove.Value ? "Restricted" : "No restriction reported" : "Unknown")}\nCurrent-user/machine enumeration complete: {data.EnumerationComplete}\n\n{instances}\n\n{string.Join("\n", data.Coverage)}";
            return result.State;
        }, error =>
        {
            if (!Current()) return;
            MsiActionBindingMessage = error;
            MsiActionBindingDetails = "No completed current context binding is claimed. Native queries may still return under worker ownership. No uninstall, installation or elevation occurred.";
        });
    }
}
