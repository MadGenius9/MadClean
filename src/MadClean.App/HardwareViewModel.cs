using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IHardwareInventory hardwareInventory = new MadClean.Windows.HardwareInventory();
    private Task<CheckResult<HardwareFact>>? hardwareQueryTask;
    private HardwareFact? selectedHardwareFact;
    private CheckResult<HardwareFact>? hardwareLastObservation;
    private DateTimeOffset? hardwareAttemptedUtc, hardwareCompletedUtc;
    private HardwareCompatibilityIdentityReference? hardwareIdentityReference;
    private string hardwareMessage = "Not checked. Read hardware inventory queries local Windows providers only; no compatibility check or recommendation has run.";
    private string hardwareAttemptTime = "Not checked";
    public ObservableCollection<HardwareFact> HardwareFacts { get; } = [];
    // Actual adapter task only. A UI continuation may still be pending after it
    // settles; this flag never claims CIM provider/service completion.
    public bool HasPendingHardwareQuery => hardwareQueryTask is { IsCompleted: false };
    public CheckResult<HardwareFact>? HardwareLastObservation
    { get => hardwareLastObservation; private set => Set(ref hardwareLastObservation, value); }
    public DateTimeOffset? HardwareAttemptedUtc
    { get => hardwareAttemptedUtc; private set => Set(ref hardwareAttemptedUtc, value); }
    // Response receipt only: the CIM classes do not expose a shared completion timestamp.
    public DateTimeOffset? HardwareCompletedUtc
    { get => hardwareCompletedUtc; private set => Set(ref hardwareCompletedUtc, value); }
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
        set
        {
            if (value != null && !HardwareFacts.Any(fact => ReferenceEquals(fact, value)))
            {
                PropertyChanged?.Invoke(this, new(nameof(SelectedHardwareFact)));
                return;
            }
            Set(ref selectedHardwareFact, value);
            PropertyChanged?.Invoke(this, new(nameof(HardwareFactDetails)));
        }
    }
    public string HardwareFactDetails => selectedHardwareFact is not { } fact
        ? "Select a detected fact to inspect/copy its value and exact provider source. No manufacturer specification has been verified."
        : $"{fact.Category} · {fact.Component}\n{fact.Property}: {fact.Value}\nSource: {fact.Source}\nSource enumeration reported complete: {fact.SourceEnumerationComplete} (not proof of value accuracy or compatibility).\nDetected / firmware-reported. Shared query attempt UTC: {HardwareAttemptTime}. Response received UTC: {HardwareCompletedUtc?.ToString("O") ?? "Not received"}; this is not a per-class completion time. These values may be incomplete or inaccurate; they do not establish manufacturer-supported upgrades.";
    private void InitializeHardwareDiscovery()
    {
        ReadHardwareCommand = new AsyncCommand(ReadHardware, () => !IsBusy && !HasPendingHardwareQuery);
        InitializeHardwareUpgradeReview();
    }
    private Task ReadHardware()
    {
        if (HasPendingHardwareQuery)
        {
            ExplainHardwareQueryCloseBlock();
            return Task.CompletedTask;
        }
        return RunDiscoveryAsync(ActivityArea.HardwareInventory, "Reading hardware inventory", async token =>
        {
            ResetCurrentHardwareObservation();
            var attemptedUtc = DateTimeOffset.UtcNow;
            HardwareAttemptedUtc = attemptedUtc;
            HardwareAttemptTime = attemptedUtc.ToString("O");
            PropertyChanged?.Invoke(this, new(nameof(HardwareFactDetails)));
            HardwareMessage = "Reading supported local Windows CIM hardware properties…";
            // Hold the actual query task before awaiting or notifying UI observers.
            // Its adapter owns its child and streams even if the dispatcher departs.
            var query = hardwareInventory.ReadAsync(token);
            hardwareQueryTask = query;
            // A departed dispatcher may never resume this await. Observe a
            // terminal fault without exposing provider text or changing results.
            _ = query.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                NotifyHardwareQueryOwnership();
                var result = await query; token.ThrowIfCancellationRequested();
                var receivedUtc = DateTimeOffset.UtcNow;
                if (!Enum.IsDefined(result.State) || receivedUtc < attemptedUtc)
                    throw new InvalidOperationException("Hardware observation state or timestamps are inconsistent.");
                // Freeze the actual response container, preserving its exact fact objects.
                // The public observation is not a test/provider injection point.
                var accepted = result with { Items = Array.AsReadOnly(result.Items.ToArray()) };
                HardwareLastObservation = accepted;
                HardwareCompletedUtc = receivedUtc;
                foreach (var fact in accepted.Items) HardwareFacts.Add(fact);
                UpdateHardwareIdentityReference();
                HardwareMessage = Stamp(accepted.State, accepted.Message);
                SelectedHardwareFact = HardwareFacts.FirstOrDefault();
                PropertyChanged?.Invoke(this, new(nameof(HardwareFactDetails)));
                return accepted.State;
            }
            finally
            {
                // An observer/dispatcher exception must not clear a still-running
                // actual task. Main's lifetime fence can continue observing it.
                if (query.IsCompleted && ReferenceEquals(hardwareQueryTask, query))
                {
                    hardwareQueryTask = null;
                    NotifyHardwareQueryOwnership();
                }
            }
        }, error =>
        {
            ResetCurrentHardwareObservation();
            HardwareMessage = error;
            PropertyChanged?.Invoke(this, new(nameof(HardwareFactDetails)));
        });
    }
    public void ExplainHardwareQueryCloseBlock()
    {
        HardwareMessage = "The local hardware query is pending. Use Cancel while available, then wait for the owned read-only query process and output streams to settle. CIM service completion is not inferred.";
        Status = HardwareMessage;
        NotifyHardwareQueryOwnership();
    }
    private void NotifyHardwareQueryOwnership()
    {
        PropertyChanged?.Invoke(this, new(nameof(HasPendingHardwareQuery)));
        CommandManager.InvalidateRequerySuggested();
    }
    private void ResetCurrentHardwareObservation()
    {
        InvalidateHardwareUpgradeContext();
        InvalidateManufacturerServiceFacts();
        HardwareLastObservation = null;
        HardwareCompletedUtc = null;
        HardwareFacts.Clear(); SelectedHardwareFact = null;
        UpdateHardwareIdentityReference();
    }
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
