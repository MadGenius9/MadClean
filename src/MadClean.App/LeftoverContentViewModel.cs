using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly ILeftoverContentInventory leftoverContentInventory = new WindowsLeftoverContentInventory();
    private long leftoverContentGeneration;
    private DateTimeOffset? leftoverContentAttemptedUtc, leftoverRegistrationsReceivedUtc, leftoverScopeValidatedUtc,
        leftoverMetadataReceivedUtc, leftoverContentInterruptedUtc;
    private CheckState leftoverContentSourceState = CheckState.Unknown;
    private bool leftoverContentReading;
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
    public string LeftoverContentQueryDetails => leftoverContentAttemptedUtc is not { } attempted
        ? "Not checked for this selection. Registration refresh, scope validation and metadata preview require an explicit request."
        : $"App request attempt UTC: {attempted:u}\n" +
            $"Accepted registrations response UTC: {leftoverRegistrationsReceivedUtc?.ToString("u") ?? "Not received"}\n" +
            $"Scope validation stage UTC: {leftoverScopeValidatedUtc?.ToString("u") ?? "Not established"}\n" +
            $"Metadata adapter response UTC: {leftoverMetadataReceivedUtc?.ToString("u") ?? "Not received"}; source state: {leftoverContentSourceState}.\n" +
            (leftoverContentReading ? "The current request is still being observed. No completed metadata report is displayed."
                : leftoverContentInterruptedUtc is { } interrupted
                    ? $"Interruption observed UTC: {interrupted:u}; no completed metadata report is accepted."
                    : leftoverMetadataReceivedUtc.HasValue
                        ? "Adapter receipt may be an Unknown/Failed response; it does not prove native work finished or that current metadata was accepted."
                        : "No metadata adapter response was accepted; inspect the validation/source status before retrying.") +
            "\nThese are app/stage observations, not the baseline inventory time, uninstall completion, simultaneous file observations, ownership or removal eligibility.";
    private bool IsCurrentLeftoverContentCandidate(LeftoverCandidate candidate) =>
        LeftoverCandidates.Any(row => ReferenceEquals(row, candidate));
    private LeftoverContentEntry? CurrentLeftoverContentEntry => selectedLeftoverContent is { } entry &&
        LeftoverContentRows.Any(row => ReferenceEquals(row, entry)) ? entry : null;
    public LeftoverContentEntry? SelectedLeftoverContent
    {
        get => CurrentLeftoverContentEntry;
        set
        {
            if (value != null && !LeftoverContentRows.Any(row => ReferenceEquals(row, value)))
            { PropertyChanged?.Invoke(this, new(nameof(SelectedLeftoverContent))); return; }
            if (ReferenceEquals(selectedLeftoverContent, value)) return;
            selectedLeftoverContent = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedLeftoverContent)));
            PropertyChanged?.Invoke(this, new(nameof(LeftoverContentDetails)));
        }
    }
    public string LeftoverContentDetails => CurrentLeftoverContentEntry is { } item
        ? $"{item.Path}\n{(item.IsDirectory ? "Directory; descendant size not measured" : item.Bytes is long bytes ? Display.Bytes(bytes) + " observed logical bytes" : "Size unknown")}\nActive use: {item.ActiveUse}. Ownership and removal eligibility: Unknown.\nObserved metadata identity: {item.Identity}\nSource: supported Windows handle metadata for the explicitly selected former install-folder scope. This is not content verification, proof an uninstall completed, proven junk, physical allocation or permission to remove anything."
        : "Select an observed file or folder to copy its exact path and metadata. Protected descendants are excluded; shared/user/save/settings data and unknown ownership must never be treated as removable junk.";
    private void InitializeLeftoverContentReview() => PreviewLeftoverContentsCommand = new AsyncCommand(PreviewLeftoverContents,
        () => !IsBusy && applicationBaseline != null && selectedLeftover is { } candidate && IsCurrentLeftoverContentCandidate(candidate));
    private void ClearLeftoverContentResult()
    {
        LeftoverContentRows.Clear(); LeftoverContentIssues.Clear(); LeftoverContentCoverage.Clear(); SelectedLeftoverContent = null;
        LeftoverContentScope = "No current validated metadata scope.";
    }
    private void InvalidateLeftoverContents()
    {
        leftoverContentGeneration++;
        ClearLeftoverContentResult();
        leftoverContentAttemptedUtc = leftoverRegistrationsReceivedUtc = leftoverScopeValidatedUtc =
            leftoverMetadataReceivedUtc = leftoverContentInterruptedUtc = null;
        leftoverContentSourceState = CheckState.Unknown; leftoverContentReading = false;
        LeftoverContentMessage = "No current metadata preview. Select a candidate and choose Preview selected folder contents; registrations will be refreshed first.";
        LeftoverContentAttempt = "Not checked for the current selection.";
        NotifyLeftoverContentQuery();
    }
    private void NotifyLeftoverContentQuery()
    {
        PropertyChanged?.Invoke(this, new(nameof(LeftoverContentQueryDetails)));
        CommandManager.InvalidateRequerySuggested();
    }
    private Task PreviewLeftoverContents()
    {
        var candidate = selectedLeftover;
        var baseline = applicationBaseline;
        long generation = -1;
        bool refreshStarted = false, refreshPublished = false;
        bool Current() => generation == leftoverContentGeneration && ReferenceEquals(candidate, selectedLeftover) &&
            ReferenceEquals(baseline, applicationBaseline) && candidate != null && IsCurrentLeftoverContentCandidate(candidate);
        return RunDiscoveryAsync(ActivityArea.LeftoverContentReview, "Reviewing selected leftover-folder metadata", async token =>
        {
            InvalidateLeftoverContents();
            generation = leftoverContentGeneration;
            if (candidate == null || baseline == null || !Current())
            { LeftoverContentMessage = "Select a current surviving-directory candidate from a complete in-session baseline/review first. No content preview ran."; return CheckState.Unknown; }
            var attempted = DateTimeOffset.UtcNow;
            leftoverContentAttemptedUtc = attempted; leftoverContentReading = true;
            LeftoverContentAttempt = $"App request attempt UTC: {attempted:u}";
            LeftoverContentMessage = "Refreshing covered app registrations before validating the exact selected scope…";
            NotifyLeftoverContentQuery();
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
            var registrationsReceived = DateTimeOffset.UtcNow;
            if (registrationsReceived < attempted) throw new InvalidOperationException("Registration response time is inconsistent.");
            await PublishInstalledApplications(after, token);
            refreshPublished = true;
            if (!Current()) return CheckState.Cancelled;
            leftoverRegistrationsReceivedUtc = registrationsReceived;
            var before = new CheckResult<InstalledApplication>(CheckState.Available, "Complete in-session captured baseline; not a current inventory.", baseline);
            var validated = LeftoverContentReview.ValidateScope(candidate, before, after, DateTimeOffset.UtcNow, token);
            var scope = validated.Items.SingleOrDefault();
            if (scope == null)
            {
                leftoverContentReading = false; leftoverContentSourceState = validated.State;
                LeftoverContentMessage = Stamp(validated.State, validated.Message); NotifyLeftoverContentQuery();
                return validated.State;
            }
            leftoverScopeValidatedUtc = scope.ValidatedUtc;
            LeftoverContentScope = $"Selected request root: {scope.RootPath}\nPrior registration: {scope.RegistrationIdentity}\nScope validation UTC: {scope.ValidatedUtc:u}; not baseline age, uninstall completion or metadata acceptance.";
            LeftoverContentMessage = "Reading bounded selected-folder metadata only. Active use and ownership remain Unknown; no file contents or application commands are read.";
            NotifyLeftoverContentQuery();
            var result = await leftoverContentInventory.PreviewAsync(scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            var received = DateTimeOffset.UtcNow;
            if (received < scope.ValidatedUtc) throw new InvalidOperationException("Metadata response time is inconsistent.");
            leftoverMetadataReceivedUtc = received; leftoverContentReading = false;
            if (!LeftoverContentEvidence.IsConsistent(scope, result))
            {
                ClearLeftoverContentResult();
                bool emptyFailure = Enum.IsDefined(result.State) && result.Items.Count == 0 &&
                    (result.State is CheckState.Unknown or CheckState.Unavailable or CheckState.Unsupported or CheckState.Failed or CheckState.Cancelled);
                leftoverContentSourceState = emptyFailure ? result.State : CheckState.Unknown;
                string reason = emptyFailure && result.Message is { Length: > 0 and <= 4096 } message &&
                    !message.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                        ? message : "The response did not match the validated selected-folder scope and metadata contract. Refresh registrations and review the candidate again.";
                LeftoverContentMessage = Stamp(leftoverContentSourceState, reason + " No current file report, ownership or removal verdict was accepted.");
                NotifyLeftoverContentQuery(); return leftoverContentSourceState;
            }
            foreach (var report in result.Items)
            {
                foreach (var entry in report.Entries) LeftoverContentRows.Add(entry);
                foreach (var issue in report.Issues) LeftoverContentIssues.Add(new(issue.Path, issue.Reason, "Unknown / excluded"));
                foreach (var note in report.Coverage) LeftoverContentCoverage.Add(note);
            }
            leftoverContentSourceState = result.State;
            LeftoverContentMessage = Stamp(result.State, result.Message);
            NotifyLeftoverContentQuery(); return result.State;
        }, error =>
        {
            // This shared installed-app publication still needs cleanup even after the
            // leftover selection was invalidated. It cannot replace the new leftover scope.
            if (refreshStarted && !refreshPublished) FailInstalledApplicationsRefresh(error);
            if (!Current()) return;
            ClearLeftoverContentResult();
            // Keep completed stages from this same attempt as historical observations.
            // Clearing the current scope/report does not undo a completed registration publication.
            leftoverContentReading = false; leftoverContentInterruptedUtc = DateTimeOffset.UtcNow;
            leftoverContentSourceState = cancellation?.IsCancellationRequested == true ? CheckState.Cancelled : CheckState.Failed;
            LeftoverContentMessage = error + " No completed file preview or removal verdict is claimed. Cancellation ends the view's wait; any pending native worker retains its handles and prevents another preview until it finishes.";
            NotifyLeftoverContentQuery();
        });
    }
}
