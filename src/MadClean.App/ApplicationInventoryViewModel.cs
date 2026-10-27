using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
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
    public InstalledApplication? SelectedInstalledApplication
    {
        get => selectedApplication;
        set { Set(ref selectedApplication, value); InvalidateInstalledAppPlan(); PropertyChanged?.Invoke(this, new(nameof(SelectedApplicationDetails))); }
    }
    public string SelectedApplicationDetails => selectedApplication == null ? "Select a registration to review its reported metadata and uninstall capability hint." :
        $"{selectedApplication.Name} · {selectedApplication.Version} · publisher: {selectedApplication.Publisher}\nSource: {selectedApplication.Source}\nRegistration: {selectedApplication.Identity}\nInstall location: {selectedApplication.InstallLocation ?? "Unknown/unsupported"}\n{selectedApplication.UninstallSupport}. No uninstall command is collected or run; restricted registrations are not eligible for a future removal action.";
    public LeftoverCandidate? SelectedLeftoverCandidate
    {
        get => selectedLeftover;
        set { if (ReferenceEquals(selectedLeftover, value)) return; Set(ref selectedLeftover, value); InvalidateLeftoverContents(); PropertyChanged?.Invoke(this, new(nameof(SelectedLeftoverDetails))); }
    }
    public string SelectedLeftoverDetails => selectedLeftover == null ? "Select a candidate to inspect the evidence and cautions. No path here is authorized for deletion." :
        selectedLeftover.Path + "\n" + selectedLeftover.Evidence + "\n" + selectedLeftover.Caution;
    public ICommand ViewLeftoverReviewCommand { get; private set; } = null!;
    public ICommand RefreshInstalledApplicationsCommand { get; private set; } = null!;
    public ICommand CaptureApplicationBaselineCommand { get; private set; } = null!;
    public ICommand ReviewLeftoversCommand { get; private set; } = null!;
    private void InitializeApplicationInventory()
    {
        InitializeInstalledAppPlans();
        InitializeLeftoverContentReview();
        ViewLeftoverReviewCommand = new ActionCommand(() => SelectedPage = 8, () => true);
        RefreshInstalledApplicationsCommand = new AsyncCommand(() => RefreshInstalledApplications(false), () => !IsBusy);
        CaptureApplicationBaselineCommand = new AsyncCommand(() => RefreshInstalledApplications(true), () => !IsBusy);
        ReviewLeftoversCommand = new AsyncCommand(ReviewLeftovers, () => !IsBusy && applicationBaseline != null);
    }
    private Task RefreshInstalledApplications(bool capture) => RunDiscoveryAsync(ActivityArea.InstalledApplications, "Reading installed app registrations", async token =>
    {
        InvalidateLeftoverContents();
        latestInstalledSnapshot = null; InvalidateInstalledAppPlan();
        if (capture)
        {
            applicationBaseline = null; LeftoverCandidates.Clear(); SelectedLeftoverCandidate = null;
            LeftoverMessage = "The previous review was invalidated. Capture a complete new baseline before reviewing registration changes.";
            ApplicationBaselineMessage = "Capturing a new baseline; a complete successful inventory is required.";
        }
        InstalledMessage = "Reading covered Add/Remove Programs registrations…";
        var result = await installedApplications.ScanAsync(token); token.ThrowIfCancellationRequested();
        await PublishInstalledApplications(result, token);
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
    }, error => { InstalledMessage = error; if (capture) ApplicationBaselineMessage = "No baseline captured. " + error; });
    private async Task PublishInstalledApplications(CheckResult<InstalledApplication> result, CancellationToken token)
    {
        InstalledApplications.Clear(); SelectedInstalledApplication = null;
        int count = 0;
        foreach (var app in result.Items)
        {
            token.ThrowIfCancellationRequested(); InstalledApplications.Add(app);
            if (++count % 128 == 0) await Task.Delay(1, token);
        }
        latestInstalledSnapshot = new(result.State, result.Message, result.Items.ToArray());
        InstalledMessage = Stamp(result.State, result.Message);
    }
    private Task ReviewLeftovers() => RunDiscoveryAsync(ActivityArea.LeftoverReview, "Reviewing registration changes", async token =>
    {
        InvalidateLeftoverContents();
        latestInstalledSnapshot = null; InvalidateInstalledAppPlan();
        LeftoverCandidates.Clear(); SelectedLeftoverCandidate = null;
        var baseline = applicationBaseline;
        if (baseline == null) { LeftoverMessage = "Capture a complete baseline first."; return CheckState.Unknown; }
        LeftoverMessage = "Refreshing covered registrations; incomplete inventories will not be treated as missing apps…";
        var result = await installedApplications.ScanAsync(token); token.ThrowIfCancellationRequested();
        await PublishInstalledApplications(result, token);
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
    }, error => LeftoverMessage = error);
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
