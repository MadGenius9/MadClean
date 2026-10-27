using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IFirmwareReleaseHistory firmwareReleaseHistory = new SurfaceFirmwareHistoryInventory();
    private SurfaceFirmwareReleaseObservation? selectedFirmwareRelease;
    private string firmwareHistoryMessage = "Not checked. Explicit online release-history review is separate from Windows Update and local identity inventory. Exact supported coverage is limited; unknown models receive no compatibility verdict.";
    private string firmwareHistoryTime = "Not checked — no manufacturer release history retrieved.";
    private string firmwareHistoryLinkMessage = "No release page opened.";
    public ObservableCollection<SurfaceFirmwareReleaseObservation> FirmwareHistoryRows { get; } = [];
    public ObservableCollection<string> FirmwareHistoryCoverage { get; } = [];
    public ICommand CheckFirmwareHistoryCommand { get; private set; } = null!;
    public ICommand ReviewFirmwareHistorySourceCommand { get; private set; } = null!;
    public string FirmwareHistoryMessage { get => firmwareHistoryMessage; private set => Set(ref firmwareHistoryMessage, value); }
    public string FirmwareHistoryLinkMessage { get => firmwareHistoryLinkMessage; private set => Set(ref firmwareHistoryLinkMessage, value); }
    public SurfaceFirmwareReleaseObservation? SelectedFirmwareRelease
    {
        get => selectedFirmwareRelease;
        set { Set(ref selectedFirmwareRelease, value); PropertyChanged?.Invoke(this, new(nameof(FirmwareHistoryDetails))); CommandManager.InvalidateRequerySuggested(); }
    }
    public string FirmwareHistoryDetails => selectedFirmwareRelease is { } release
        ? $"Published release: {release.PublishedDate}\n{release.Heading}\n\nPublished observations: {release.Summary}\nPublished UEFI version: {release.PublishedUefiVersion ?? "Unknown / no explicit UEFI version in this release"}\nSource section: {release.SourceSection}\nOfficial source: {release.SourceUri}\nRetrieved UTC: {release.RetrievedUtc:u}\n\nThese are official published release observations, not a verified update applicable to this device, latest BIOS determination or installed-version comparison. Verify exact device configuration and official prerequisites. No firmware file is fetched or installed."
        : "Select a dated official release observation. Driver releases do not establish a new BIOS; historical UEFI versions do not prove applicability or whether this PC needs an update.";

    private void InitializeFirmwareHistory()
    {
        CheckFirmwareHistoryCommand = new AsyncCommand(CheckFirmwareHistory, () => !IsBusy && firmwareIdentity != null);
        ReviewFirmwareHistorySourceCommand = new ActionCommand(ReviewFirmwareHistorySource,
            () => !IsBusy && selectedFirmwareRelease != null && SurfaceFirmwareHistorySources.IsTrustedHistoryUri(selectedFirmwareRelease.SourceUri));
    }
    private void InvalidateFirmwareHistory()
    {
        FirmwareHistoryRows.Clear(); FirmwareHistoryCoverage.Clear(); SelectedFirmwareRelease = null;
        FirmwareHistoryMessage = "Local identity changed. No manufacturer release history checked for this snapshot.";
        firmwareHistoryTime = "Not checked for the current local identity.";
        FirmwareHistoryLinkMessage = "No release page opened for this snapshot.";
        PropertyChanged?.Invoke(this, new(nameof(FirmwareManufacturerCheckTime)));
    }
    private Task CheckFirmwareHistory() => RunDiscoveryAsync(ActivityArea.FirmwareReleaseHistory, "Reading official firmware release history", async token =>
    {
        var identity = firmwareIdentity;
        InvalidateFirmwareHistory();
        if (identity == null) { FirmwareHistoryMessage = "Unknown — read local PC identity first. No request sent."; return CheckState.Unknown; }
        FirmwareHistoryMessage = "Checking only the fixed supported official identity and release-history sources…";
        var result = await firmwareReleaseHistory.CheckAsync(identity, token);
        token.ThrowIfCancellationRequested();
        foreach (var report in result.Items)
        {
            foreach (var release in report.Releases) FirmwareHistoryRows.Add(release);
            foreach (var note in report.Coverage) FirmwareHistoryCoverage.Add(note);
            firmwareHistoryTime = $"Last source attempt UTC: {report.CheckedUtc:u} · scope: {report.ProductLabel}. No applicability or up-to-date verdict.";
        }
        if (result.Items.Count == 0) firmwareHistoryTime = $"Last attempt UTC: {DateTimeOffset.UtcNow:u}. No verified release-history result.";
        PropertyChanged?.Invoke(this, new(nameof(FirmwareManufacturerCheckTime)));
        FirmwareHistoryMessage = Stamp(result.State, result.Message);
        return result.State;
    }, error =>
    {
        FirmwareHistoryRows.Clear(); FirmwareHistoryCoverage.Clear(); SelectedFirmwareRelease = null;
        FirmwareHistoryMessage = error + " Available BIOS/applicability remain Unknown.";
        firmwareHistoryTime = $"Attempt ended UTC: {DateTimeOffset.UtcNow:u}. No completed source result.";
        PropertyChanged?.Invoke(this, new(nameof(FirmwareManufacturerCheckTime)));
    });
    private void ReviewFirmwareHistorySource()
    {
        if (selectedFirmwareRelease is not { } release || !SurfaceFirmwareHistorySources.IsTrustedHistoryUri(release.SourceUri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(release.SourceUri.AbsoluteUri) { UseShellExecute = true });
            FirmwareHistoryLinkMessage = "Requested the official release-history page in your browser. Verify power/battery and recovery-key prerequisites there; no firmware asset, installation or restart requested by MadClean.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        { FirmwareHistoryLinkMessage = "Windows could not open the official source. No firmware download, install or restart occurred."; }
    }
}
