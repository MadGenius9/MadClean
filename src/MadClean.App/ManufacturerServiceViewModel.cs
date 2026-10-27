using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IManufacturerServiceFactsInventory manufacturerServiceFacts = new SurfaceServiceFactsInventory();
    private bool hardwareIdentityComplete;
    private HardwareCompatibilityIdentityReference? manufacturerServiceIdentity;
    private ManufacturerServiceFact? selectedManufacturerServiceFact;
    private string manufacturerServiceMessage = "Not checked. This optional online check supports only the exact listed Surface Laptop 6 for Business identities.";
    private string manufacturerServiceTime = "No manufacturer source retrieved in this session.";
    public ObservableCollection<ManufacturerServiceFact> ManufacturerServiceRows { get; } = [];
    public ObservableCollection<string> ManufacturerServiceCoverage { get; } = [];
    public ICommand ReadManufacturerServiceFactsCommand { get; private set; } = null!;
    public string ManufacturerServiceMessage { get => manufacturerServiceMessage; private set => Set(ref manufacturerServiceMessage, value); }
    public string ManufacturerServiceTime { get => manufacturerServiceTime; private set => Set(ref manufacturerServiceTime, value); }
    public string ManufacturerServiceEligibility => hardwareIdentityComplete && manufacturerServiceIdentity is { } reference
        ? "Supported source scope: " + reference.ProductLabel + ". Identity remains Windows/firmware-reported; verify the physical model before any service."
        : "Unknown / unsupported for this snapshot. First obtain a complete, unique PC identity source with an exact supported manufacturer/model/SKU. Unrelated hardware fields can remain unknown; missing, truncated, ambiguous or unmatched PC identity sends no source request.";
    public ManufacturerServiceFact? SelectedManufacturerServiceFact
    {
        get => selectedManufacturerServiceFact;
        set { Set(ref selectedManufacturerServiceFact, value); PropertyChanged?.Invoke(this, new(nameof(ManufacturerServiceDetails))); }
    }
    public string ManufacturerServiceDetails => selectedManufacturerServiceFact is { } fact
        ? $"Topic: {fact.Topic}\nManufacturer statement: {fact.Observation}\nApplies to: {fact.Scope}\nOfficial source: {fact.SourceUri}\nSource section: {fact.SourceSection}\nRetrieved UTC: {fact.RetrievedUtc:u}\nUncertainty: {fact.Uncertainty}\n\nThis is limited manufacturer service guidance, not an upgrade recommendation, compatible part selection or permission to open or modify the device."
        : "Select a source-qualified service observation. RAM maximum/type/upgradeability, supported replacement CPUs/BIOS, aftermarket SSD compatibility/capacity, physical slots, power and clearance remain Unknown unless separately evidenced. A parts list is not proof of upgrade compatibility.";
    private void InitializeManufacturerServiceFacts() => ReadManufacturerServiceFactsCommand = new AsyncCommand(ReadManufacturerServiceFacts,
        () => !IsBusy && hardwareIdentityComplete && manufacturerServiceIdentity != null);
    private void NotifyManufacturerEligibility()
    {
        PropertyChanged?.Invoke(this, new(nameof(ManufacturerServiceEligibility)));
        CommandManager.InvalidateRequerySuggested();
    }
    private void InvalidateManufacturerServiceFacts()
    {
        hardwareIdentityComplete = false; manufacturerServiceIdentity = null;
        ManufacturerServiceRows.Clear(); ManufacturerServiceCoverage.Clear(); SelectedManufacturerServiceFact = null;
        ManufacturerServiceMessage = "Local inventory changed. Manufacturer service guidance has not been checked for this snapshot.";
        ManufacturerServiceTime = "No current manufacturer source result.";
        NotifyManufacturerEligibility();
    }
    private Task ReadManufacturerServiceFacts() => RunDiscoveryAsync(ActivityArea.ManufacturerServiceFacts, "Reading supported official service guidance", async token =>
    {
        var reference = manufacturerServiceIdentity;
        if (!hardwareIdentityComplete || reference == null)
        {
            ManufacturerServiceMessage = "Unknown / unsupported identity. No online source request was sent.";
            return CheckState.Unsupported;
        }
        ManufacturerServiceRows.Clear(); ManufacturerServiceCoverage.Clear(); SelectedManufacturerServiceFact = null;
        ManufacturerServiceMessage = "Retrieving the fixed official manufacturer sources for the selected detected identity…";
        ManufacturerServiceTime = "In progress; no completed source result is claimed.";
        var result = await manufacturerServiceFacts.ReadAsync(reference.Manufacturer, reference.Model, reference.Sku, token);
        token.ThrowIfCancellationRequested();
        ManufacturerServiceTime = "No complete verified source set returned; review the status and coverage.";
        foreach (var report in result.Items)
        {
            foreach (var fact in report.Facts) ManufacturerServiceRows.Add(fact);
            foreach (var note in report.Coverage) ManufacturerServiceCoverage.Add(note);
            ManufacturerServiceTime = $"Source check UTC: {report.CheckedUtc:u}; scope: {report.ProductLabel}. This is not a BIOS/update check.";
        }
        ManufacturerServiceMessage = Stamp(result.State, result.Message);
        return result.State;
    }, error =>
    {
        ManufacturerServiceRows.Clear(); ManufacturerServiceCoverage.Clear(); SelectedManufacturerServiceFact = null;
        ManufacturerServiceMessage = error + " No manufacturer compatibility or upgrade verdict is inferred.";
        ManufacturerServiceTime = "No completed source result from this attempt.";
    });
}
