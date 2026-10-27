using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly ILeftoverContentInventory leftoverContentInventory = new WindowsLeftoverContentInventory();
    private long leftoverContentGeneration;
    private string leftoverContentMessage = "Not checked. Select a surviving former install-folder candidate, then explicitly preview its metadata. No uninstall or removal runs.";
    private string leftoverContentScope = "No validated content-review scope.";
    private string leftoverContentAttempt = "Not checked";
    private LeftoverContentEntry? selectedLeftoverContent;
    public ObservableCollection<LeftoverContentEntry> LeftoverContentRows { get; } = [];
    public ObservableCollection<ResultRow> LeftoverContentIssues { get; } = [];
    public ObservableCollection<string> LeftoverContentCoverage { get; } = [];
    public ICommand PreviewLeftoverContentsCommand { get; private set; } = null!;
    public string LeftoverContentMessage { get => leftoverContentMessage; private set => Set(ref leftoverContentMessage, value); }
    public string LeftoverContentScope { get => leftoverContentScope; private set => Set(ref leftoverContentScope, value); }
    public string LeftoverContentAttempt { get => leftoverContentAttempt; private set => Set(ref leftoverContentAttempt, value); }
    public LeftoverContentEntry? SelectedLeftoverContent
    {
        get => selectedLeftoverContent;
        set { Set(ref selectedLeftoverContent, value); PropertyChanged?.Invoke(this, new(nameof(LeftoverContentDetails))); }
    }
    public string LeftoverContentDetails => selectedLeftoverContent is { } item
        ? $"{item.Path}\n{(item.IsDirectory ? "Directory; descendant size not measured" : item.Bytes is long bytes ? Display.Bytes(bytes) + " observed logical bytes" : "Size unknown")}\nActive use: {item.ActiveUse}. Ownership and removal eligibility: Unknown.\nObserved metadata identity: {item.Identity}\nSource: supported Windows handle metadata for the explicitly selected former install-folder scope. This is not content verification, proof an uninstall completed, proven junk, physical allocation or permission to remove anything."
        : "Select an observed file or folder to copy its exact path and metadata. Protected descendants are excluded; shared/user/save/settings data and unknown ownership must never be treated as removable junk.";
    private void InitializeLeftoverContentReview() => PreviewLeftoverContentsCommand = new AsyncCommand(PreviewLeftoverContents,
        () => !IsBusy && applicationBaseline != null && selectedLeftover != null && LeftoverCandidates.Contains(selectedLeftover));
    private void InvalidateLeftoverContents()
    {
        leftoverContentGeneration++;
        LeftoverContentRows.Clear(); LeftoverContentIssues.Clear(); LeftoverContentCoverage.Clear(); SelectedLeftoverContent = null;
        LeftoverContentMessage = "No current metadata preview. Select a candidate and choose Preview selected folder contents; registrations will be refreshed first.";
        LeftoverContentScope = "No current validated scope.";
        LeftoverContentAttempt = "Not checked for the current selection.";
        CommandManager.InvalidateRequerySuggested();
    }
    private Task PreviewLeftoverContents()
    {
        bool refreshStarted = false, refreshPublished = false;
        return RunDiscoveryAsync(ActivityArea.LeftoverContentReview, "Reviewing selected leftover-folder metadata", async token =>
        {
            var candidate = selectedLeftover;
            var baseline = applicationBaseline;
            InvalidateLeftoverContents();
            long generation = leftoverContentGeneration;
            bool Current() => generation == leftoverContentGeneration && ReferenceEquals(candidate, selectedLeftover) && ReferenceEquals(baseline, applicationBaseline);
            if (candidate == null || baseline == null || !LeftoverCandidates.Contains(candidate))
            { LeftoverContentMessage = "Select a current surviving-directory candidate from a complete in-session baseline/review first. No content preview ran."; return CheckState.Unknown; }
            LeftoverContentAttempt = DateTimeOffset.Now.ToString("g");
            LeftoverContentMessage = "Refreshing covered app registrations before validating the exact selected scope…";
            LeftoverMessage = "The candidate list is retained as historical review evidence while registrations are refreshed. Its prior absence/path observations are not a current uninstall or existence verdict; the selected content scope must be validated again below.";
            BeginInstalledApplicationsRefresh();
            refreshStarted = true;
            var after = await installedApplications.ScanAsync(token);
            token.ThrowIfCancellationRequested();
            if (!Current())
            {
                FailInstalledApplicationsRefresh("Cancelled · the leftover selection changed before installed-app publication.");
                return CheckState.Cancelled;
            }
            await PublishInstalledApplications(after, token);
            refreshPublished = true;
            if (!Current()) return CheckState.Cancelled;
            var before = new CheckResult<InstalledApplication>(CheckState.Available, "Complete in-session captured baseline; not a current inventory.", baseline);
            var validated = LeftoverContentReview.ValidateScope(candidate, before, after, DateTimeOffset.UtcNow, token);
            var scope = validated.Items.SingleOrDefault();
            if (scope == null)
            { LeftoverContentMessage = Stamp(validated.State, validated.Message); return validated.State; }
            LeftoverContentScope = $"Selected root: {scope.RootPath}\nPrior registration: {scope.RegistrationIdentity}\nScope validation attempt UTC: {scope.ValidatedUtc:u}; this is not the baseline inventory time or proof of an uninstall.";
            LeftoverContentMessage = "Reading bounded selected-folder metadata only. Active use and ownership remain Unknown; no file contents or application commands are read.";
            var result = await leftoverContentInventory.PreviewAsync(scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            foreach (var report in result.Items)
            {
                foreach (var entry in report.Entries) LeftoverContentRows.Add(entry);
                foreach (var issue in report.Issues) LeftoverContentIssues.Add(new(issue.Path, issue.Reason, "Unknown / excluded"));
                foreach (var note in report.Coverage) LeftoverContentCoverage.Add(note);
            }
            LeftoverContentMessage = Stamp(result.State, result.Message);
            return result.State;
        }, error =>
        {
            if (refreshStarted && !refreshPublished) FailInstalledApplicationsRefresh(error);
            LeftoverContentRows.Clear(); LeftoverContentIssues.Clear(); LeftoverContentCoverage.Clear(); SelectedLeftoverContent = null;
            LeftoverContentMessage = error + " No completed file preview or removal verdict is claimed. Cancellation ends the view's wait; any pending native worker retains its handles and prevents another preview until it finishes.";
        });
    }
}
