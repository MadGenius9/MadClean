using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IFirmwareIdentityInventory firmwareInventory = new FirmwareIdentityInventory();
    private FirmwareIdentity? firmwareIdentity;
    private FirmwareSupportGuide? firmwareGuide;
    private ResultRow? selectedFirmwareFact;
    private SystemUpdate? selectedFirmwareOffer;
    private string firmwareIdentityMessage = "Not checked. Choose Read BIOS / PC identity for local firmware-reported metadata; no update or flash runs.";
    private string firmwareIdentityTime = "Not checked";
    private string firmwareOffersMessage = "Unknown: Windows Update firmware-related offers have not been checked.";
    private string driverOffersMessage = "Unknown: Windows Update driver offers have not been checked.";
    private string windowsSoftwareMessage = "Windows software offers have not been checked.";
    private string firmwareOfferTime = "Not checked";
    private string firmwareGuideAction = "";
    public ObservableCollection<ResultRow> FirmwareIdentityRows { get; } = [];
    public ObservableCollection<ResultRow> DriverOfferRows { get; } = [];
    public ObservableCollection<ResultRow> WindowsSoftwareRows { get; } = [];
    public ObservableCollection<SystemUpdate> FirmwareOffers { get; } = [];
    public ICommand ReadFirmwareIdentityCommand { get; private set; } = null!;
    public ICommand OpenFirmwareSupportCommand { get; private set; } = null!;
    public ICommand OpenFirmwarePrecautionsCommand { get; private set; } = null!;
    public string FirmwareIdentityMessage { get => firmwareIdentityMessage; private set => Set(ref firmwareIdentityMessage, value); }
    public string FirmwareIdentityTime { get => firmwareIdentityTime; private set => Set(ref firmwareIdentityTime, value); }
    public string FirmwareOffersMessage { get => firmwareOffersMessage; private set => Set(ref firmwareOffersMessage, value); }
    public string DriverOffersMessage { get => driverOffersMessage; private set => Set(ref driverOffersMessage, value); }
    public string WindowsSoftwareMessage { get => windowsSoftwareMessage; private set => Set(ref windowsSoftwareMessage, value); }
    public string FirmwareOfferTime { get => firmwareOfferTime; private set => Set(ref firmwareOfferTime, value); }
    public string FirmwareInstalledVersion => firmwareIdentity?.BiosVersion ?? "Unknown";
    public string FirmwareAvailableVersion => "Unknown — no exact-model/revision manufacturer BIOS version has been verified.";
    public string FirmwareComparison => "Unknown — vendor version ordering and exact OEM/model/SKU/board-revision compatibility are not verified. A Windows Update package is not a BIOS-version comparison.";
    public string FirmwareManufacturerCheckTime => firmwareHistoryTime;
    public string FirmwareGuideStatus => firmwareIdentity == null
        ? "Unknown — PC identity has not been obtained."
        : firmwareIdentity.PcManufacturer == "Unknown" || firmwareIdentity.PcModel == "Unknown"
            ? "Unknown — PC OEM/model is unidentified; no firmware source can be matched."
            : firmwareGuide == null
                ? "Unsupported official-guide coverage for this reported PC identity. Exact-model/revision manufacturer matching remains Unknown; never use a similarly named retail board's firmware."
                : firmwareGuide.Vendor + " · " + firmwareGuide.Scope + " · " + firmwareGuide.MatchStatus;
    public string FirmwareOfficialSource => firmwareGuide?.SupportUri.AbsoluteUri ?? "Unknown — no supported official guide matched the reported PC identity.";
    public string FirmwarePrerequisites => firmwareGuide?.Precautions ?? "Unknown vendor-specific power/battery/recovery requirements. Verify exact OEM model/SKU/revision and current official instructions. If encryption is enabled, ensure recovery-key access before considering firmware changes; never provide the key to MadClean.";
    public string FirmwareGuideAction { get => firmwareGuideAction; private set => Set(ref firmwareGuideAction, value); }
    public ResultRow? SelectedFirmwareFact
    {
        get => selectedFirmwareFact;
        set { Set(ref selectedFirmwareFact, value); PropertyChanged?.Invoke(this, new(nameof(FirmwareFactDetails))); }
    }
    public string FirmwareFactDetails => selectedFirmwareFact == null ? "Select a reported fact to inspect/copy its value and source. These are firmware-reported strings, not manufacturer-verified specifications." :
        selectedFirmwareFact.Name + ": " + selectedFirmwareFact.Detail + "\nSource: " + selectedFirmwareFact.State + ". Firmware-reported; not an exact-model compatibility verification.";
    public SystemUpdate? SelectedFirmwareOffer
    {
        get => selectedFirmwareOffer;
        set { Set(ref selectedFirmwareOffer, value); PropertyChanged?.Invoke(this, new(nameof(FirmwareOfferDetails))); }
    }
    public string FirmwareOfferDetails => selectedFirmwareOffer == null
        ? "No firmware-class offer selected. No returned offer does not mean the BIOS is up to date. Other driver offers may have missing class metadata."
        : $"{selectedFirmwareOffer.Title}\nSource: configured Windows Update service · update ID: {selectedFirmwareOffer.Identity}\nDriver class: {selectedFirmwareOffer.DriverClass}; package date: {selectedFirmwareOffer.PackageDate} (not installed BIOS date).\nAvailable BIOS/package version: Unknown. Exact model/revision and BIOS-versus-device role: Unknown.\nProvider description (not verified model-specific BIOS release notes): {selectedFirmwareOffer.Description}\nOfficial BIOS release notes/prerequisites: Unknown until checked on the exact OEM support source. No install/flash or restart is authorized.";
    private void InitializeFirmwareDiscovery()
    {
        InitializeFirmwareHistory();
        ReadFirmwareIdentityCommand = new AsyncCommand(ReadFirmwareIdentity, () => !IsBusy);
        OpenFirmwareSupportCommand = new ActionCommand(() => OpenFirmwareGuide(false), () => firmwareGuide != null);
        OpenFirmwarePrecautionsCommand = new ActionCommand(() => OpenFirmwareGuide(true), () => firmwareGuide != null);
    }
    private Task ReadFirmwareIdentity() => RunDiscoveryAsync(ActivityArea.FirmwareIdentity, "Reading BIOS / PC identity", async token =>
    {
        InvalidateFirmwareHistory();
        firmwareIdentity = null; firmwareGuide = null; FirmwareIdentityRows.Clear(); SelectedFirmwareFact = null;
        FirmwareGuideAction = ""; NotifyFirmwareIdentity();
        FirmwareIdentityMessage = "Reading supported local CIM identity classes…";
        var result = await firmwareInventory.ReadAsync(token); token.ThrowIfCancellationRequested();
        firmwareIdentity = result.Items.SingleOrDefault();
        if (firmwareIdentity != null)
        {
            var i = firmwareIdentity;
            FirmwareIdentityRows.Add(new("PC manufacturer", i.PcManufacturer, "Win32_ComputerSystem.Manufacturer"));
            FirmwareIdentityRows.Add(new("Exact reported PC model", i.PcModel, "Win32_ComputerSystem.Model"));
            FirmwareIdentityRows.Add(new("Reported PC SKU", i.PcSku, "Win32_ComputerSystem.SystemSKUNumber"));
            FirmwareIdentityRows.Add(new("Motherboard manufacturer", i.BoardManufacturer, "Win32_BaseBoard.Manufacturer"));
            FirmwareIdentityRows.Add(new("Motherboard product", i.BoardProduct, "Win32_BaseBoard.Product"));
            FirmwareIdentityRows.Add(new("Reported board version (revision unverified)", i.BoardRevision, "Win32_BaseBoard.Version"));
            FirmwareIdentityRows.Add(new("BIOS manufacturer", i.BiosManufacturer, "Win32_BIOS.Manufacturer"));
            FirmwareIdentityRows.Add(new("Installed reported BIOS version", i.BiosVersion, "Win32_BIOS.SMBIOSBIOSVersion"));
            FirmwareIdentityRows.Add(new("Reported BIOS release date", i.BiosReleaseDate, "Win32_BIOS.ReleaseDate · reported calendar date"));
            firmwareGuide = FirmwareSupportPolicy.FindGuide(i);
            SelectedFirmwareFact = FirmwareIdentityRows.FirstOrDefault();
        }
        FirmwareIdentityTime = DateTimeOffset.Now.ToString("g");
        FirmwareIdentityMessage = Stamp(result.State, result.Message + " Values are firmware-reported, not manufacturer-verified limits or firmware compatibility.");
        NotifyFirmwareIdentity();
        return result.State;
    }, error => { FirmwareIdentityTime = DateTimeOffset.Now.ToString("g"); FirmwareIdentityMessage = error; NotifyFirmwareIdentity(); });
    private void NotifyFirmwareIdentity()
    {
        foreach (var property in new[] { nameof(FirmwareInstalledVersion), nameof(FirmwareGuideStatus), nameof(FirmwareOfficialSource), nameof(FirmwarePrerequisites), nameof(FirmwareManufacturerCheckTime) })
            PropertyChanged?.Invoke(this, new(property));
        CommandManager.InvalidateRequerySuggested();
    }
    private void ResetUpdateCategoryResults()
    {
        BeginDriverOffersQuery(DateTimeOffset.UtcNow);
        WindowsSoftwareRows.Clear(); DriverOfferRows.Clear(); FirmwareOffers.Clear(); SelectedFirmwareOffer = null;
        DriverOffersMessage = FirmwareOffersMessage = WindowsSoftwareMessage = "Checking the configured Windows Update service…";
    }
    private void PublishUpdateCategories(CheckResult<SystemUpdate> result)
    {
        PublishDriverOffersQuery(result, DateTimeOffset.UtcNow);
        WindowsSoftwareRows.Clear(); DriverOfferRows.Clear(); FirmwareOffers.Clear();
        foreach (var offer in result.Items)
        {
            if (offer.Kind == "Firmware") FirmwareOffers.Add(offer);
            else if (offer.Kind == "Driver") DriverOfferRows.Add(new(offer.Title, $"{offer.Identity} · class {offer.DriverClass} · package date {offer.PackageDate}", "Windows Update offer; installed/available version Unknown"));
            else WindowsSoftwareRows.Add(new(offer.Title, offer.Identity, offer.Kind + " · configured Windows Update service"));
        }
        FirmwareOfferTime = DateTimeOffset.Now.ToString("g");
        int unclassified = result.Items.Count(o => o.Kind == "Unknown" || o.Kind == "Driver" && o.DriverClass == "Unknown");
        WindowsSoftwareMessage = Stamp(result.State, $"{WindowsSoftwareRows.Count} software/unclassified offers returned. {result.Message}");
        DriverOffersMessage = Stamp(result.State, $"{DriverOfferRows.Count} non-firmware or class-unknown driver offers returned. Installed/available driver versions are not verified here. Manufacturer driver catalogs: Unsupported.");
        FirmwareOffersMessage = Stamp(result.State, $"{FirmwareOffers.Count} firmware-class offers returned; {unclassified} offers lack usable classification. BIOS availability/model match remains Unknown, even with zero returned offers. Firmware may target system or device components; no manufacturer-wide check or flashing performed.");
        SelectedFirmwareOffer = FirmwareOffers.FirstOrDefault();
    }
    private void MarkUpdateCategoryFailure(string error)
    {
        InterruptDriverOffersQuery(DateTimeOffset.UtcNow);
        FirmwareOfferTime = DateTimeOffset.Now.ToString("g");
        DriverOffersMessage = WindowsSoftwareMessage = error;
        FirmwareOffersMessage = error + " BIOS update coverage and available version remain Unknown.";
    }
    private void OpenFirmwareGuide(bool precautions)
    {
        if (firmwareGuide == null) return;
        var uri = precautions ? firmwareGuide.PrecautionsUri : firmwareGuide.SupportUri;
        if (!FirmwareSupportPolicy.IsTrustedGuideUri(uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            FirmwareGuideAction = $"Opened official guidance {DateTime.Now:g}. This is a generic manual guide, not a completed model/version check, download or flashing action.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        { FirmwareGuideAction = "Windows could not open the official guide. No firmware check, download, install or restart occurred."; }
    }
}
