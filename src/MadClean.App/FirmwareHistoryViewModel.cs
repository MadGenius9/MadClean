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
    private DateTimeOffset? firmwareHistoryAttemptedUtc, firmwareHistoryReceivedUtc, firmwareHistoryInterruptedUtc, firmwareHistoryProviderCheckedUtc;
    private CheckState firmwareHistorySourceState = CheckState.Unknown;
    private bool firmwareHistoryReading;
    private string firmwareHistoryMessage = "Not checked. Explicit online release-history review is separate from Windows Update and local identity inventory. Exact supported coverage is limited; unknown models receive no compatibility verdict.";
    private string firmwareHistoryTime = "Not checked — no manufacturer release history retrieved.";
    private string firmwareHistoryLinkMessage = "No release page opened.";
    public ObservableCollection<SurfaceFirmwareReleaseObservation> FirmwareHistoryRows { get; } = [];
    public ObservableCollection<string> FirmwareHistoryCoverage { get; } = [];
    public ICommand CheckFirmwareHistoryCommand { get; private set; } = null!;
    public ICommand ReviewFirmwareHistorySourceCommand { get; private set; } = null!;
    public string FirmwareHistoryMessage { get => firmwareHistoryMessage; private set => Set(ref firmwareHistoryMessage, value); }
    public string FirmwareHistoryLinkMessage { get => firmwareHistoryLinkMessage; private set => Set(ref firmwareHistoryLinkMessage, value); }
    public DateTimeOffset? FirmwareHistoryAttemptedUtc => firmwareHistoryAttemptedUtc;
    public DateTimeOffset? FirmwareHistoryReceivedUtc => firmwareHistoryReceivedUtc;
    public string FirmwareHistoryQueryDetails => firmwareHistoryAttemptedUtc is not { } attempted
        ? "No official-history request for the current identity. An explicit check is required; no automatic request runs."
        : $"App request attempt UTC: {attempted:u}\n" +
            (firmwareHistoryReading ? "Waiting for the supported history adapter; no completed response from this attempt."
                : firmwareHistoryReceivedUtc is { } received
                    ? $"App response received UTC: {received:u}; source state: {firmwareHistorySourceState}. Response receipt is not proof a website was retrieved."
                    : $"Attempt interruption observed UTC: {firmwareHistoryInterruptedUtc:u}; state: {firmwareHistorySourceState}. No completed source response is displayed.") +
            (firmwareHistoryProviderCheckedUtc is { } checkedUtc
                ? $"\nProvider report check UTC: {checkedUtc:u}. Individual release observations retain their own retrieval times; neither date is the firmware publication date."
                : "\nNo qualified provider report check time or release retrieval is established.") +
            "\nThese times do not establish current-device applicability, latest BIOS or an installed-version comparison.";
    private bool IsCurrentFirmwareRelease(SurfaceFirmwareReleaseObservation release) =>
        FirmwareHistoryRows.Any(row => ReferenceEquals(row, release));
    private SurfaceFirmwareReleaseObservation? CurrentFirmwareRelease =>
        selectedFirmwareRelease is { } release && IsCurrentFirmwareRelease(release) ? release : null;
    public SurfaceFirmwareReleaseObservation? SelectedFirmwareRelease
    {
        get => CurrentFirmwareRelease;
        set
        {
            if (value != null && !IsCurrentFirmwareRelease(value))
            {
                PropertyChanged?.Invoke(this, new(nameof(SelectedFirmwareRelease)));
                return;
            }
            if (ReferenceEquals(selectedFirmwareRelease, value)) return;
            selectedFirmwareRelease = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedFirmwareRelease)));
            PropertyChanged?.Invoke(this, new(nameof(FirmwareHistoryDetails)));
            PropertyChanged?.Invoke(this, new(nameof(SelectedFirmwarePrerequisites)));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SelectedFirmwarePrerequisites => CurrentFirmwareRelease is { } release && !string.IsNullOrWhiteSpace(release.PublishedPrerequisites)
        ? release.PublishedPrerequisites
        : "Unknown — select a dated official release observation to read its published OS requirements.";
    public string FirmwareHistoryDetails => CurrentFirmwareRelease is { } release
        ? $"Published release: {release.PublishedDate}\n{release.Heading}\n\nPublished OS requirements: {SelectedFirmwarePrerequisites}\nLocal prerequisites and applicability: Unknown — this check does not assess installed Windows version, device configuration, rollout eligibility, power or recovery-key readiness.\n\nPublished observations: {release.Summary}\nPublished UEFI version: {release.PublishedUefiVersion ?? "Unknown / no explicit UEFI version in this release"}\nSource section: {release.SourceSection}\nOfficial source: {release.SourceUri}\nRetrieved UTC: {release.RetrievedUtc:u}\n\nThese are official published release observations, not a verified update applicable to this device, latest BIOS determination or installed-version comparison. Verify exact device configuration and official prerequisites. No firmware file is fetched or installed."
        : "Select a dated official release observation. Driver releases do not establish a new BIOS; historical UEFI versions do not prove applicability or whether this PC needs an update.";

    private void InitializeFirmwareHistory()
    {
        CheckFirmwareHistoryCommand = new AsyncCommand(CheckFirmwareHistory, () => !IsBusy && firmwareIdentity != null);
        ReviewFirmwareHistorySourceCommand = new ActionCommand(ReviewFirmwareHistorySource,
            () => !IsBusy && CurrentFirmwareRelease is { } release && SurfaceFirmwareHistorySources.IsTrustedHistoryUri(release.SourceUri));
    }
    private void InvalidateFirmwareHistory()
    {
        ClearCurrentFirmwareHistoryRows();
        firmwareHistoryAttemptedUtc = firmwareHistoryReceivedUtc = firmwareHistoryInterruptedUtc = firmwareHistoryProviderCheckedUtc = null;
        firmwareHistorySourceState = CheckState.Unknown; firmwareHistoryReading = false;
        FirmwareHistoryMessage = "Local identity changed. No manufacturer release history checked for this snapshot.";
        firmwareHistoryTime = "Not checked for the current local identity.";
        FirmwareHistoryLinkMessage = "No release page opened for this snapshot.";
        NotifyFirmwareHistoryQuery();
    }
    private void ClearCurrentFirmwareHistoryRows()
    {
        FirmwareHistoryRows.Clear(); FirmwareHistoryCoverage.Clear(); SelectedFirmwareRelease = null;
    }
    private void NotifyFirmwareHistoryQuery()
    {
        foreach (var property in new[] { nameof(FirmwareHistoryAttemptedUtc), nameof(FirmwareHistoryReceivedUtc),
            nameof(FirmwareHistoryQueryDetails), nameof(FirmwareManufacturerCheckTime) })
            PropertyChanged?.Invoke(this, new(property));
        CommandManager.InvalidateRequerySuggested();
    }
    private Task CheckFirmwareHistory() => RunDiscoveryAsync(ActivityArea.FirmwareReleaseHistory, "Reading official firmware release history", async token =>
    {
        var identity = firmwareIdentity;
        InvalidateFirmwareHistory();
        if (identity == null) { FirmwareHistoryMessage = "Unknown — read local PC identity first. No request sent."; return CheckState.Unknown; }
        var attemptedUtc = DateTimeOffset.UtcNow;
        firmwareHistoryAttemptedUtc = attemptedUtc; firmwareHistoryReading = true;
        firmwareHistoryTime = $"App request attempt UTC: {attemptedUtc:u}. No completed source result yet.";
        FirmwareHistoryMessage = "Checking only the fixed supported official identity and release-history sources…";
        NotifyFirmwareHistoryQuery();
        var result = await firmwareReleaseHistory.CheckAsync(identity, token);
        token.ThrowIfCancellationRequested();
        var receivedUtc = DateTimeOffset.UtcNow;
        bool positive = result.State is CheckState.Available or CheckState.Partial;
        if (!ReferenceEquals(identity, firmwareIdentity) || !Enum.IsDefined(result.State) || receivedUtc < attemptedUtc ||
            result.Items.Count > 1 || !positive && result.Items.Count != 0 || positive && result.Items.Count != 1)
            throw new InvalidOperationException("Firmware history response scope or state is inconsistent.");
        if (result.Items.Count == 1)
        {
            var report = result.Items[0];
            if (report.CheckedUtc == default || report.CheckedUtc.Offset != TimeSpan.Zero ||
                report.CheckedUtc < attemptedUtc || report.CheckedUtc > receivedUtc ||
                report.Releases.Any(release => release.RetrievedUtc == default || release.RetrievedUtc.Offset != TimeSpan.Zero ||
                    release.RetrievedUtc < attemptedUtc || release.RetrievedUtc > report.CheckedUtc ||
                    !SurfaceFirmwareHistorySources.IsTrustedHistoryUri(release.SourceUri)))
                throw new InvalidOperationException("Firmware history source times are inconsistent.");
            foreach (var release in report.Releases) FirmwareHistoryRows.Add(release);
            foreach (var note in report.Coverage) FirmwareHistoryCoverage.Add(note);
            firmwareHistoryProviderCheckedUtc = report.CheckedUtc;
            firmwareHistoryTime = $"Provider report check UTC: {report.CheckedUtc:u}; app response received UTC: {receivedUtc:u} · scope: {report.ProductLabel}. No applicability or up-to-date verdict.";
        }
        else firmwareHistoryTime = $"App response received UTC: {receivedUtc:u}; state: {result.State}. No verified release-history result or source retrieval is established.";
        firmwareHistoryReading = false; firmwareHistoryReceivedUtc = receivedUtc; firmwareHistorySourceState = result.State;
        FirmwareHistoryMessage = Stamp(result.State, result.Message);
        NotifyFirmwareHistoryQuery();
        return result.State;
    }, error =>
    {
        ClearCurrentFirmwareHistoryRows();
        firmwareHistoryReading = false; firmwareHistoryReceivedUtc = firmwareHistoryProviderCheckedUtc = null;
        firmwareHistoryInterruptedUtc = DateTimeOffset.UtcNow;
        firmwareHistorySourceState = cancellation?.IsCancellationRequested == true ? CheckState.Cancelled : CheckState.Failed;
        FirmwareHistoryMessage = error + " Available BIOS/applicability remain Unknown.";
        firmwareHistoryTime = $"Attempt interruption observed UTC: {firmwareHistoryInterruptedUtc:u}; state: {firmwareHistorySourceState}. No completed source result.";
        NotifyFirmwareHistoryQuery();
    });
    private void ReviewFirmwareHistorySource()
    {
        if (IsBusy || CurrentFirmwareRelease is not { } release || !SurfaceFirmwareHistorySources.IsTrustedHistoryUri(release.SourceUri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(release.SourceUri.AbsoluteUri) { UseShellExecute = true });
            FirmwareHistoryLinkMessage = "Requested the official release-history page in your browser. Verify power/battery and recovery-key prerequisites there; no firmware asset, installation or restart requested by MadClean.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        { FirmwareHistoryLinkMessage = "Windows could not open the official source. No firmware download, install or restart occurred."; }
    }
}
