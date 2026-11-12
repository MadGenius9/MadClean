using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IPackagedApplicationInventory packagedApplicationInventory = new WindowsPackagedApplicationInventory();
    private PackagedApplication? selectedPackagedApplication;
    private bool packagedApplicationsPending;
    private string packagedApplicationsMessage = "Not checked. Read Main/Bundle registrations for the current Windows profile; no update, uninstall or repair runs.";
    private string packagedApplicationsAttempt = "Not checked";
    public ObservableCollection<PackagedApplication> PackagedApplicationRows { get; } = [];
    public ICollectionView PackagedApplicationsView { get; private set; } = null!;
    public ICommand ReadPackagedApplicationsCommand { get; private set; } = null!;
    public string PackagedApplicationsMessage { get => packagedApplicationsMessage; private set => Set(ref packagedApplicationsMessage, value); }
    public string PackagedApplicationsAttempt { get => packagedApplicationsAttempt; private set => Set(ref packagedApplicationsAttempt, value); }
    private PackagedApplication? CurrentPackagedApplication => ApplicationSelectionPolicy.IsCurrentPackage(
        packagedSettingsSource, PackagedApplicationRows, selectedPackagedApplication, installedAppSearch) ? selectedPackagedApplication : null;
    public PackagedApplication? SelectedPackagedApplication
    {
        get => CurrentPackagedApplication;
        set
        {
            if (value != null && !ApplicationSelectionPolicy.IsCurrentPackage(
                packagedSettingsSource, PackagedApplicationRows, value, installedAppSearch))
            { PropertyChanged?.Invoke(this, new(nameof(SelectedPackagedApplication))); return; }
            if (ReferenceEquals(selectedPackagedApplication, value)) return;
            InvalidatePackagedSettings(clearSource: false);
            selectedPackagedApplication = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedPackagedApplication)));
            PropertyChanged?.Invoke(this, new(nameof(PackagedApplicationDetails)));
        }
    }
    public string PackagedApplicationDetails => CurrentPackagedApplication is { } package
        ? $"Package identity name: {package.Name}\nFull identity: {package.FullName}\nFamily identity: {package.FamilyName}\nReported version: {package.Version}\nPublisher metadata: {package.Publisher}\nArchitecture: {package.Architecture}; type: {package.PackageType}\nReported signing kind: {package.SignatureKind}\nProvider non-removable flag: {Flag(package.NonRemovable)}\nFramework: {Flag(package.IsFramework)}; resource package: {Flag(package.IsResourcePackage)}\nSource: current-user Windows Appx Main/Bundle metadata.\n\nThese are registration observations, not independent signature/publisher authentication, running/health/update status or removal permission. Other users and provisioning are outside coverage. A bundle and main records can describe the same software; no unique-app count or ARP baseline is inferred. No package mutation is implemented here; a separate explicit preview can request Windows' family-level options page."
        : "Select a package record to inspect its exact public package identities and reported metadata. No user SID, manifest content, installation path or dependencies are collected. Non-removable and signing fields are provider reports; removal eligibility remains unknown.";
    public string PackagedApplicationSearchSummary => !installedAppSearch.IsValid
        ? "Search accepts up to 256 characters of literal text without control or hidden formatting characters. Clear it to show loaded package rows."
        : packagedApplicationsPending
            ? "Package metadata read in progress; counts are shown after publication finishes. The local filter applies as rows arrive."
            : $"{PackagedApplicationsView.Cast<PackagedApplication>().Count():N0} of {PackagedApplicationRows.Count:N0} loaded package rows shown. These are not unique apps or a combined desktop-app count. No match is not absence proof.";
    private static string Flag(bool? value) => value switch { true => "Yes", false => "No", _ => "Unknown" };
    private void InitializePackagedApplications()
    {
        InitializePackagedAppSettings();
        PackagedApplicationsView = CollectionViewSource.GetDefaultView(PackagedApplicationRows);
        PackagedApplicationsView.Filter = item => item is PackagedApplication package && installedAppSearch.MatchesText(package.Name, package.Publisher);
        ReadPackagedApplicationsCommand = new AsyncCommand(ReadPackagedApplications, () => !IsBusy && !HasPendingPackagedSettingsWork);
    }
    private void RefreshPackagedApplicationSearch()
    {
        if (PackagedApplicationsView == null) return;
        InvalidatePackagedSettings(clearSource: false);
        if (selectedPackagedApplication != null && !installedAppSearch.MatchesText(selectedPackagedApplication.Name, selectedPackagedApplication.Publisher))
            SelectedPackagedApplication = null;
        PackagedApplicationsView.Refresh();
        NotifyPackagedApplicationSearch();
    }
    private void NotifyPackagedApplicationSearch() => PropertyChanged?.Invoke(this, new(nameof(PackagedApplicationSearchSummary)));
    private Task ReadPackagedApplications() => RunDiscoveryAsync(ActivityArea.PackagedApplications, "Reading current-user Windows package metadata", async token =>
    {
        InvalidatePackagedSettings(clearSource: true);
        packagedApplicationsPending = true;
        PackagedApplicationRows.Clear(); SelectedPackagedApplication = null;
        NotifyPackagedApplicationSearch();
        PackagedApplicationsAttempt = DateTimeOffset.Now.ToString("g");
        PackagedApplicationsMessage = "Reading bounded current-user Main/Bundle metadata only…";
        var result = await packagedApplicationInventory.ReadAsync(token);
        token.ThrowIfCancellationRequested();
        var receivedUtc = DateTimeOffset.UtcNow;
        if (result.Items.Count > PackagedApplicationParser.MaximumRecords)
            throw new InvalidOperationException("Package projection exceeded its fixed bound.");
        int published = 0;
        foreach (var package in result.Items)
        {
            token.ThrowIfCancellationRequested(); PackagedApplicationRows.Add(package);
            if (++published % 64 == 0) await Task.Delay(1, token);
        }
        token.ThrowIfCancellationRequested();
        PublishPackagedSettingsSource(result, receivedUtc);
        PackagedApplicationsMessage = Stamp(result.State, result.Message);
        packagedApplicationsPending = false; NotifyPackagedApplicationSearch();
        return result.State;
    }, error =>
    {
        InvalidatePackagedSettings(clearSource: true);
        PackagedApplicationRows.Clear(); SelectedPackagedApplication = null;
        packagedApplicationsPending = false; NotifyPackagedApplicationSearch();
        PackagedApplicationsMessage = error + " No completed package inventory is claimed; desktop registrations and baselines are unchanged.";
    });
}
