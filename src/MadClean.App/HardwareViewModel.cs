using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IHardwareInventory hardwareInventory = new MadClean.Windows.HardwareInventory();
    private HardwareFact? selectedHardwareFact;
    private string hardwareMessage = "Not checked. Read hardware inventory queries local Windows providers only; no compatibility check or recommendation has run.";
    private string hardwareAttemptTime = "Not checked";
    public ObservableCollection<HardwareFact> HardwareFacts { get; } = [];
    public ICommand ReadHardwareCommand { get; private set; } = null!;
    public string HardwareMessage { get => hardwareMessage; private set => Set(ref hardwareMessage, value); }
    public string HardwareAttemptTime { get => hardwareAttemptTime; private set => Set(ref hardwareAttemptTime, value); }
    public HardwareFact? SelectedHardwareFact
    {
        get => selectedHardwareFact;
        set { Set(ref selectedHardwareFact, value); PropertyChanged?.Invoke(this, new(nameof(HardwareFactDetails))); }
    }
    public string HardwareFactDetails => selectedHardwareFact is not { } fact
        ? "Select a detected fact to inspect/copy its value and exact provider source. No manufacturer specification has been verified."
        : $"{fact.Category} · {fact.Component}\n{fact.Property}: {fact.Value}\nSource: {fact.Source}\nDetected / firmware-reported. Query attempt: {HardwareAttemptTime}. These values may be incomplete or inaccurate; they do not establish manufacturer-supported upgrades.";
    private void InitializeHardwareDiscovery() => ReadHardwareCommand = new AsyncCommand(ReadHardware, () => !IsBusy);
    private Task ReadHardware() => RunDiscoveryAsync(ActivityArea.HardwareInventory, "Reading hardware inventory", async token =>
    {
        HardwareFacts.Clear(); SelectedHardwareFact = null;
        HardwareAttemptTime = DateTimeOffset.Now.ToString("g");
        HardwareMessage = "Reading supported local Windows CIM hardware properties…";
        var result = await hardwareInventory.ReadAsync(token); token.ThrowIfCancellationRequested();
        foreach (var fact in result.Items) HardwareFacts.Add(fact);
        HardwareMessage = Stamp(result.State, result.Message);
        SelectedHardwareFact = HardwareFacts.FirstOrDefault();
        return result.State;
    }, error => HardwareMessage = error);
}
