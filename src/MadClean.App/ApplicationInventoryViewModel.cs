using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private const string RegistryCancellationCaveat = " Cancellation ends the wait; Windows may still finish a pending registry read. Another registration scan is blocked until that read returns.";
    private readonly IInstalledApplicationInventory installedApplications = new InstalledApplicationInventory();
    private IReadOnlyList<InstalledApplication>? applicationBaseline;
    private CheckResult<InstalledApplication>? latestInstalledSnapshot;
    private string installedMessage = "Not checked. Refresh the covered Add/Remove Programs registrations to begin.";
    private string baselineMessage = "No baseline captured. Capture a complete inventory before uninstalling an app through Windows or its supported uninstaller outside MadClean.";
    private string leftoverMessage = "Not checked. After a complete baseline, use Review registration changes to inspect surviving prior install paths. Candidates are not proven junk.";
    private InstalledApplication? selectedApplication;
    private LeftoverCandidate? selectedLeftover;
    public ObservableCollection<InstalledApplication> InstalledApplications { get; } = [];
    public ObservableCollection<LeftoverCandidate> LeftoverCandidates { get; } = [];
    public string InstalledMessage { get => installedMessage; private set => Set(ref installedMessage, value); }
    public string ApplicationBaselineMessage { get => baselineMessage; private set => Set(ref baselineMessage, value); }
    public string LeftoverMessage { get => leftoverMessage; private set => Set(ref leftoverMessage, value); }
    private InstalledApplication? CurrentInstalledApplication => ApplicationSelectionPolicy.IsCurrentDesktop(
        latestInstalledSnapshot, InstalledApplications, selectedApplication, installedAppSearch) ? selectedApplication : null;
    public InstalledApplication? SelectedInstalledApplication
    {
        get => CurrentInstalledApplication;
        set
        {
            // Collection bindings may send a departed row after refresh/filter changes.
            // Rebind to the qualified current row without invalidating its valid preview.
            if (value != null && !ApplicationSelectionPolicy.IsCurrentDesktop(
                latestInstalledSnapshot, InstalledApplications, value, installedAppSearch))
            { PropertyChanged?.Invoke(this, new(nameof(SelectedInstalledApplication))); return; }
            if (ReferenceEquals(selectedApplication, value) || MsiUninstallDispatchReserved) return;
            InvalidateInstalledAppPlan();
            if (MsiUninstallDispatchReserved) return;
            // InstalledApplication is a record: value equality cannot replace reference ownership.
            selectedApplication = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedInstalledApplication)));
            PropertyChanged?.Invoke(this, new(nameof(SelectedApplicationDetails)));
        }
    }
    public string SelectedApplicationDetails => CurrentInstalledApplication is not { } selected ? "Select a registration to review its reported metadata and uninstall capability hint." :
        $"{selected.Name} · {selected.Version} · publisher: {selected.Publisher}\nSource: {selected.Source}\nRegistration: {selected.Identity}\nInstall location: {selected.InstallLocation ?? "Unknown/unsupported"}\n{selected.UninstallSupport}. No registered uninstall command is collected. A separately prepared and confirmed MSI request uses the fixed Windows Installer flow; removal restrictions remain visible.";
    public LeftoverCandidate? SelectedLeftoverCandidate
    {
        get => selectedLeftover is { } candidate && LeftoverCandidates.Any(row => ReferenceEquals(row, candidate)) ? candidate : null;
        set
        {
            if (value != null && !LeftoverCandidates.Any(row => ReferenceEquals(row, value)))
            { PropertyChanged?.Invoke(this, new(nameof(SelectedLeftoverCandidate))); return; }
            if (ReferenceEquals(selectedLeftover, value)) return;
            selectedLeftover = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedLeftoverCandidate)));
            InvalidateLeftoverContents();
            PropertyChanged?.Invoke(this, new(nameof(SelectedLeftoverDetails)));
        }
    }
    public string SelectedLeftoverDetails => SelectedLeftoverCandidate is not { } candidate ? "Select a candidate to inspect the evidence and cautions. No path here is authorized for deletion." :
        candidate.Path + "\n" + candidate.Evidence + "\n" + candidate.Caution;
    public ICommand ViewLeftoverReviewCommand { get; private set; } = null!;
    public ICommand RefreshInstalledApplicationsCommand { get; private set; } = null!;
    public ICommand CaptureApplicationBaselineCommand { get; private set; } = null!;
    public ICommand ReviewLeftoversCommand { get; private set; } = null!;
    private void InitializeApplicationInventory()
    {
        InitializeInstalledAppSearch();
        InitializePackagedApplications();
        InitializeInstalledAppPlans();
        InitializeMsiRegistrationWatch();
        InitializeLeftoverContentReview();
        ViewLeftoverReviewCommand = new ActionCommand(() => SelectedPage = 8, () => true);
        RefreshInstalledApplicationsCommand = new AsyncCommand(() => RefreshInstalledApplications(false), () => !IsBusy);
        CaptureApplicationBaselineCommand = new AsyncCommand(() => RefreshInstalledApplications(true), () => !IsBusy);
        ReviewLeftoversCommand = new AsyncCommand(ReviewLeftovers, () => !IsBusy && applicationBaseline != null);
    }
    private Task RefreshInstalledApplications(bool capture)
    {
        bool refreshStarted = false, refreshPublished = false;
        return RunDiscoveryAsync(ActivityArea.InstalledApplications, "Reading installed app registrations", async token =>
        {
            InvalidateLeftoverContents();
            LeftoverCandidates.Clear(); SelectedLeftoverCandidate = null;
            LeftoverMessage = applicationBaseline == null
                ? "The prior registration-change review was invalidated. Capture a complete baseline before reviewing changes; no current absence conclusion is claimed."
                : "The prior registration-change review was invalidated by this inventory refresh. Review changes again against the retained baseline; no current absence conclusion is claimed.";
            BeginInstalledApplicationsRefresh();
            refreshStarted = true;
            if (capture)
            {
                applicationBaseline = null;
                LeftoverMessage = "The previous review was invalidated. Capture a complete new baseline before reviewing registration changes.";
                ApplicationBaselineMessage = "Capturing a new baseline; a complete successful inventory is required.";
            }
            var result = await installedApplications.ScanAsync(token); token.ThrowIfCancellationRequested();
            await PublishInstalledApplications(result, token);
            refreshPublished = true;
            if (capture)
            {
                if (result.State == CheckState.Available)
                {
                    applicationBaseline = result.Items.ToArray();
                    ApplicationBaselineMessage = $"Complete baseline: {applicationBaseline.Count} covered registrations captured {DateTime.Now:g}. In memory for this session only.";
                    LeftoverMessage = "Baseline captured. A later complete review can compare registration changes and probe only recorded install paths; no uninstall is performed by MadClean.";
                }
                else ApplicationBaselineMessage = "No baseline captured: the inventory was incomplete or unavailable. Resolve its errors before inferring missing registrations.";
            }
            return result.State;
        }, error =>
        {
            if (refreshStarted && !refreshPublished) FailInstalledApplicationsRefresh(error);
            if (capture)
            {
                applicationBaseline = null;
                ApplicationBaselineMessage = "No baseline captured. " + error + RegistryCancellationCaveat;
                LeftoverMessage = "No baseline captured. The previous review is invalidated; capture a complete baseline before reviewing registration changes.";
            }
        });
    }
    private void BeginInstalledApplicationsRefresh(string message = "Reading covered Add/Remove Programs registrations; no complete current inventory is displayed…")
    {
        SetInstalledAppSearchRefreshPending(true);
        latestInstalledSnapshot = null; InvalidateInstalledAppPlan();
        InstalledApplications.Clear(); SelectedInstalledApplication = null;
        InstalledMessage = message;
    }
    private void FailInstalledApplicationsRefresh(string error)
    {
        latestInstalledSnapshot = null; InvalidateInstalledAppPlan();
        InstalledApplications.Clear(); SelectedInstalledApplication = null;
        InstalledMessage = error + " No complete current installed-app inventory is displayed." + RegistryCancellationCaveat;
        SetInstalledAppSearchRefreshPending(false);
    }
    private async Task PublishInstalledApplications(CheckResult<InstalledApplication> result, CancellationToken token)
    {
        BeginInstalledApplicationsRefresh("Publishing covered registrations; displayed rows are incomplete until publication finishes…");
        try
        {
            int count = 0;
            foreach (var app in result.Items)
            {
                token.ThrowIfCancellationRequested(); InstalledApplications.Add(app);
                if (++count % 128 == 0) await Task.Delay(1, token);
            }
            var items = result.Items.ToArray();
            token.ThrowIfCancellationRequested();
            latestInstalledSnapshot = new(result.State, result.Message, items);
            InstalledMessage = Stamp(result.State, result.Message);
            SetInstalledAppSearchRefreshPending(false);
        }
        catch (OperationCanceledException)
        {
            FailInstalledApplicationsRefresh("Cancelled · installed-app publication did not complete.");
            throw;
        }
        catch (Exception)
        {
            FailInstalledApplicationsRefresh("Failed · installed-app publication did not complete.");
            throw;
        }
    }
    private Task ReviewLeftovers()
    {
        bool refreshStarted = false, refreshPublished = false;
        return RunDiscoveryAsync(ActivityArea.LeftoverReview, "Reviewing registration changes", async token =>
        {
            InvalidateLeftoverContents();
            LeftoverCandidates.Clear(); SelectedLeftoverCandidate = null;
            var baseline = applicationBaseline;
            if (baseline == null) { LeftoverMessage = "Capture a complete baseline first."; return CheckState.Unknown; }
            LeftoverMessage = "Refreshing covered registrations; incomplete inventories will not be treated as missing apps…";
            BeginInstalledApplicationsRefresh();
            refreshStarted = true;
            var result = await installedApplications.ScanAsync(token); token.ThrowIfCancellationRequested();
            await PublishInstalledApplications(result, token);
            refreshPublished = true;
            if (result.State != CheckState.Available)
            {
                LeftoverMessage = "Unavailable for comparison: the refreshed inventory is incomplete. The complete baseline is retained; no absence or junk conclusion was made.";
                return CheckState.Partial;
            }
            var review = await Task.Run(() => LeftoverReview.Compare(baseline, result.Items, path =>
            { token.ThrowIfCancellationRequested(); return ProbeRecordedInstallDirectory(path); }), token);
            token.ThrowIfCancellationRequested();
            foreach (var item in review.Items) LeftoverCandidates.Add(item);
            // A file-level preview requires a separate explicit row selection.
            SelectedLeftoverCandidate = null;
            LeftoverMessage = Stamp(review.State, review.Message);
            return review.State;
        }, error =>
        {
            if (refreshStarted && !refreshPublished) FailInstalledApplicationsRefresh(error);
            LeftoverMessage = error + RegistryCancellationCaveat;
        });
    }
    private static bool? ProbeRecordedInstallDirectory(string path)
    {
        if (InstalledApplicationPaths.NormalizeLocalDirectory(path) is not string local || !Path.IsPathFullyQualified(local)) return null;
        try
        {
            if (new DriveInfo(Path.GetPathRoot(local)!).DriveType != DriveType.Fixed) return null;
            // Revalidate current ancestors. Registry metadata and a prior fixed-drive check are not identity guarantees.
            var ancestors = new Stack<DirectoryInfo>();
            for (DirectoryInfo? directory = new(local); directory != null; directory = directory.Parent) ancestors.Push(directory);
            foreach (var directory in ancestors)
                if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0) return null;
            return (File.GetAttributes(local) & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException) { return null; }
    }
}
