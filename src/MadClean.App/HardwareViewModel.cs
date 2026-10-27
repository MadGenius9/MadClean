using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IHardwareInventory hardwareInventory = new MadClean.Windows.HardwareInventory();
    private HardwareFact? selectedHardwareFact;
    private HardwareCompatibilityIdentityReference? hardwareIdentityReference;
    private string hardwareMessage = "Not checked. Read hardware inventory queries local Windows providers only; no compatibility check or recommendation has run.";
    private string hardwareAttemptTime = "Not checked";
    public ObservableCollection<HardwareFact> HardwareFacts { get; } = [];
    public string HardwareIdentityReferenceSummary => hardwareIdentityReference is { } reference
        ? "Bundled exact model/SKU association: " + reference.ProductLabel + ". Identity only; no upgrade compatibility is verified."
        : "Unknown / no matching bundled exact model/SKU association. RAM/CPU/BIOS/expansion specifications remain unverified.";
    public string HardwareIdentityReferenceDetails => hardwareIdentityReference is { } reference
        ? $"Source: {reference.SourceUri}\nDevelopment reference review: {reference.DevelopmentReviewDate:yyyy-MM-dd}.\n{reference.Scope}"
        : "The bundled catalogue has two developer-reviewed Surface model/SKU associations. The separate selected online service-guidance check supports only those exact identities. Missing/ambiguous/different identity remains Unknown; no retail-board substitution or inferred limits.";
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
        : $"{fact.Category} · {fact.Component}\n{fact.Property}: {fact.Value}\nSource: {fact.Source}\nSource enumeration reported complete: {fact.SourceEnumerationComplete} (not proof of value accuracy or compatibility).\nDetected / firmware-reported. Query attempt: {HardwareAttemptTime}. These values may be incomplete or inaccurate; they do not establish manufacturer-supported upgrades.";
    private void InitializeHardwareDiscovery() => ReadHardwareCommand = new AsyncCommand(ReadHardware, () => !IsBusy);
    private Task ReadHardware() => RunDiscoveryAsync(ActivityArea.HardwareInventory, "Reading hardware inventory", async token =>
    {
        InvalidateManufacturerServiceFacts();
        HardwareFacts.Clear(); SelectedHardwareFact = null; UpdateHardwareIdentityReference();
        HardwareAttemptTime = DateTimeOffset.Now.ToString("g");
        HardwareMessage = "Reading supported local Windows CIM hardware properties…";
        var result = await hardwareInventory.ReadAsync(token); token.ThrowIfCancellationRequested();
        foreach (var fact in result.Items) HardwareFacts.Add(fact);
        UpdateHardwareIdentityReference();
        HardwareMessage = Stamp(result.State, result.Message);
        SelectedHardwareFact = HardwareFacts.FirstOrDefault();
        return result.State;
    }, error => HardwareMessage = error);
    private void UpdateHardwareIdentityReference()
    {
        const string source = "root/cimv2:Win32_ComputerSystem.";
        var computer = HardwareFacts.Where(fact => fact.Source.StartsWith(source, StringComparison.Ordinal)).GroupBy(fact => fact.Component).ToArray();
        hardwareIdentityReference = null;
        if (computer.Length == 1)
        {
            string? Value(string field)
            {
                var values = computer[0].Where(fact => fact.Source == source + field).ToArray();
                return values.Length == 1 ? values[0].Value : null;
            }
            hardwareIdentityReference = HardwareCompatibilityIdentityCatalog.Find(Value("Manufacturer"), Value("Model"), Value("SystemSKUNumber"));
        }
        PropertyChanged?.Invoke(this, new(nameof(HardwareIdentityReferenceSummary)));
        PropertyChanged?.Invoke(this, new(nameof(HardwareIdentityReferenceDetails)));
        manufacturerServiceIdentity = HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(HardwareFacts);
        hardwareIdentityComplete = manufacturerServiceIdentity != null;
        NotifyManufacturerEligibility();
    }
}
