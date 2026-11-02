using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed record CleanupSelectionReviewRow(CleanupCandidate Candidate, CheckState State, string Status,
    long? CurrentBytes, DateTimeOffset? CheckedUtc, string Details)
{
    public string Path => Candidate.Path;
    public long PriorBytes => Candidate.Bytes;
    public bool CanDelete => false;
}

public sealed partial class MainViewModel
{
    private const string CleanupReviewLimits = "In-memory read-only worksheet; no deletion or persistence. Current file identities are first observations, not historical continuity or immutability. Active use, ownership and deletion safety remain unknown. No handle survives publication. Matching exclusion observations do not prove uninterrupted settings stability or exclude an ABA change. Logical-byte arithmetic is not unique, physical or reclaimable storage.";
    private long cleanupReviewGeneration;
    private bool cleanupReviewRequiresRescan;
    private bool cleanupReviewInterrupted;
    private Task<CleanupExclusionSettings?>? cleanupReviewPreferencesTask;
    private Task<CheckResult<CleanupFileEvidenceReport>>? cleanupReviewProviderTask;
    private CleanupSelectionReviewRow? selectedCleanupReview;
    private string cleanupReviewMessage = "No worksheet selections. Explicitly add at most eight actual cleanup candidates; nothing is selected automatically.";
    public ObservableCollection<CleanupCandidate> CleanupReviewSelections { get; } = [];
    public ObservableCollection<CleanupSelectionReviewRow> CleanupReviewRows { get; } = [];
    public ICommand AddSelectedCleanupReviewCommand { get; private set; } = null!;
    public ICommand RemoveCleanupReviewCommand { get; private set; } = null!;
    public ICommand ClearCleanupReviewCommand { get; private set; } = null!;
    public ICommand ReviewCleanupSelectionCommand { get; private set; } = null!;
    public string CleanupReviewMessage { get => cleanupReviewMessage; private set => Set(ref cleanupReviewMessage, value); }
    public CleanupSelectionReviewRow? SelectedCleanupReview
    {
        get => selectedCleanupReview;
        set
        {
            if (value != null && !CleanupReviewRows.Any(row => ReferenceEquals(row, value))) return;
            Set(ref selectedCleanupReview, value); NotifyCleanupReview();
        }
    }
    public string CleanupReviewDetails => selectedCleanupReview?.Details ?? CleanupReviewLimits;
    public string CleanupReviewByteSummary
    {
        get
        {
            if (CleanupReviewRows.Count == 0) return "Observed unnamed logical-byte sum: Unknown — no selections.";
            if (cleanupReviewInterrupted) return "Current worksheet logical-byte sum: Unknown — review interrupted or context not revalidated. Earlier per-entry sizes are historical observations only.";
            try
            {
                long total = 0;
                foreach (var row in CleanupReviewRows)
                {
                    if (row.CurrentBytes is not long bytes) return "Observed unnamed logical-byte sum: Unknown — one or more entries have no accepted current size.";
                    total = checked(total + bytes);
                }
                return $"Observed unnamed logical-byte arithmetic: {total:N0} bytes across {CleanupReviewRows.Count} observations. Not unique, physical, safe-to-delete or reclaimable storage.";
            }
            catch (OverflowException) { return "Observed unnamed logical-byte sum: Unknown — arithmetic exceeded its bound."; }
        }
    }
    public bool CleanupReviewRequiresRescan => cleanupReviewRequiresRescan;
    public bool HasPendingCleanupSelectionReviewWork => WindowsCleanupFileEvidence.HasPendingMetadataRead ||
        cleanupReviewPreferencesTask is { IsCompleted: false } || cleanupReviewProviderTask is { IsCompleted: false };
    private bool CleanupReviewReadPending => HasPendingCleanupSelectionReviewWork;
    private bool CanEditCleanupReview => !IsBusy && !CleanupReviewReadPending;
    private void InitializeCleanupSelectionReview()
    {
        AddSelectedCleanupReviewCommand = new ActionCommand(AddSelectedCleanupReview,
            () => CanEditCleanupReview && !cleanupReviewRequiresRescan && cleanupEvidenceSnapshot != null && CurrentCleanupEvidenceCandidate() != null && CleanupReviewSelections.Count < CleanupReviewSelection.MaximumSelections);
        RemoveCleanupReviewCommand = new ActionCommand(RemoveCleanupReview, () => CanEditCleanupReview && SelectedCleanupReview != null);
        ClearCleanupReviewCommand = new ActionCommand(() => { if (CanEditCleanupReview) InvalidateCleanupSelectionReview(); },
            () => CanEditCleanupReview && CleanupReviewSelections.Count > 0);
        ReviewCleanupSelectionCommand = new AsyncCommand(ReviewCleanupSelection,
            () => CanEditCleanupReview && !cleanupReviewRequiresRescan && cleanupEvidenceSnapshot != null && CleanupReviewSelections.Count > 0);
    }
    private void InvalidateCleanupSelectionReview()
    {
        cleanupReviewGeneration++; cleanupReviewRequiresRescan = false; cleanupReviewInterrupted = false;
        CleanupReviewSelections.Clear(); CleanupReviewRows.Clear(); SelectedCleanupReview = null;
        CleanupReviewMessage = "Worksheet cleared or source changed. Explicitly add actual candidates again; no previous current-file observation is retained.";
        NotifyCleanupReview();
    }
    private void ResetCleanupReviewRows()
    {
        cleanupReviewGeneration++; cleanupReviewInterrupted = false; CleanupReviewRows.Clear(); SelectedCleanupReview = null;
        foreach (var candidate in CleanupReviewSelections)
            CleanupReviewRows.Add(new(candidate, CheckState.Unknown, "Not checked", null, null,
                $"Selected path: {candidate.Path}\nPrior unnamed logical bytes: {candidate.Bytes:N0}\nPrior modified UTC: {candidate.ModifiedUtc:u}\nNot checked for this worksheet selection.\n\n{CleanupReviewLimits}"));
        NotifyCleanupReview();
    }
    private void AddSelectedCleanupReview()
    {
        if (!CanEditCleanupReview || cleanupReviewRequiresRescan || cleanupEvidenceSnapshot == null || CurrentCleanupEvidenceCandidate() is not { } candidate) return;
        if (CleanupReviewSelections.Any(item => ReferenceEquals(item, candidate)))
        { CleanupReviewMessage = "This actual candidate is already in the worksheet; no duplicate selection or query was added."; return; }
        if (CleanupReviewSelections.Count >= CleanupReviewSelection.MaximumSelections)
        { CleanupReviewMessage = "The worksheet is limited to eight explicit selections."; return; }
        CleanupReviewSelections.Add(candidate); ResetCleanupReviewRows();
        CleanupReviewMessage = $"{CleanupReviewSelections.Count} explicit selection(s); review has not run. No automatic selection or deletion.";
    }
    private void RemoveCleanupReview()
    {
        if (!CanEditCleanupReview || SelectedCleanupReview is not { } selected) return;
        var candidate = CleanupReviewSelections.FirstOrDefault(item => ReferenceEquals(item, selected.Candidate));
        if (candidate == null) return;
        CleanupReviewSelections.Remove(candidate); ResetCleanupReviewRows();
        CleanupReviewMessage = "Selection removed from the in-memory worksheet only; remaining entries require a new review.";
    }
    private async Task<CleanupExclusionSettings?> LoadCleanupReviewPreferences(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (cleanupReviewPreferencesTask is { IsCompleted: false }) throw new InvalidOperationException("A settings read remains pending.");
        var context = SynchronizationContext.Current;
        var task = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return cleanupExclusionStore?.Load();
        }, CancellationToken.None);
        cleanupReviewPreferencesTask = task;
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            try { context?.Post(_ => CommandManager.InvalidateRequerySuggested(), null); }
            catch (InvalidOperationException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(token);
    }
    private void InvalidateCleanupReviewForPreferences()
    {
        InvalidateCleanupSelectionReview(); cleanupReviewRequiresRescan = true;
        CleanupReviewMessage = "Worksheet invalidated: current exclusions were unreadable or differed from the captured source. Inspect again to obtain a fresh source before selecting candidates.";
        NotifyCleanupReview();
    }
    private Task ReviewCleanupSelection()
    {
        long generation = -1;
        CleanupReport? snapshot = null;
        DateTimeOffset completedUtc = default;
        bool Current() => generation == cleanupReviewGeneration && ReferenceEquals(snapshot, cleanupEvidenceSnapshot) && completedUtc == cleanupEvidenceCompletedUtc;
        return RunDiscoveryAsync(ActivityArea.CleanupFileEvidence, "Reviewing selected temporary-file metadata", async token =>
        {
            if (CleanupReviewReadPending) { CleanupReviewMessage = "A metadata/settings worker is still pending; no new read started."; return CheckState.Unknown; }
            snapshot = cleanupEvidenceSnapshot; completedUtc = cleanupEvidenceCompletedUtc;
            ResetCleanupReviewRows(); generation = cleanupReviewGeneration;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var linked = deadline.Token;
            var selected = CleanupReviewSelections.ToArray();
            var capturedExclusions = cleanupEvidenceExclusions;
            var prepared = await Task.Run(() => CleanupReviewSelection.Create(snapshot, selected, completedUtc,
                capturedExclusions, DateTimeOffset.UtcNow, linked), linked);
            linked.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            if (prepared.Items.Count != 1) { CleanupReviewMessage = prepared.Message; return prepared.State; }
            var plan = prepared.Items[0];
            CleanupReviewMessage = "Reading current exclusions, then selected files serially. No current worksheet or deletion permission is established.";
            bool interrupted = false, cancelled = false;
            try
            {
                var before = await LoadCleanupReviewPreferences(linked);
                if (!Current()) return CheckState.Cancelled;
                if (before == null || before.Error != null || !plan.MatchesExclusions(before.Items, linked))
                { InvalidateCleanupReviewForPreferences(); return CheckState.Unknown; }
                foreach (var candidate in plan.Selections)
                {
                    linked.ThrowIfCancellationRequested();
                    if (!Current()) return CheckState.Cancelled;
                    if (WindowsCleanupFileEvidence.HasPendingMetadataRead) { interrupted = true; break; }
                    var qualified = await Task.Run(() => plan.Prepare(candidate, DateTimeOffset.UtcNow, linked), linked);
                    linked.ThrowIfCancellationRequested();
                    if (!Current()) return CheckState.Cancelled;
                    if (qualified.Items.Count != 1)
                    {
                        SetCleanupReviewRow(candidate, new(candidate, CheckState.Unknown, "Scope unavailable", null, null,
                            $"Selected path: {candidate.Path}\nPrior unnamed logical bytes: {candidate.Bytes:N0}\n{qualified.Message}\n\n{CleanupReviewLimits}"));
                        interrupted = true; break;
                    }
                    var scope = qualified.Items[0];
                    cleanupReviewProviderTask = cleanupFileEvidence.ReadAsync(scope, linked);
                    var result = await cleanupReviewProviderTask;
                    linked.ThrowIfCancellationRequested();
                    if (!Current()) return CheckState.Cancelled;
                    if (WindowsCleanupFileEvidence.HasPendingMetadataRead) { interrupted = true; break; }
                    if (!CleanupFileEvidenceValidation.IsConsistent(result, scope, DateTimeOffset.UtcNow))
                    {
                        var state = result.State is CheckState.Failed or CheckState.Cancelled or CheckState.Unavailable or CheckState.Unsupported ? result.State : CheckState.Unknown;
                        SetCleanupReviewRow(candidate, new(candidate, state, "No accepted current metadata", null, null,
                            $"Selected path: {candidate.Path}\nPrior unnamed logical bytes: {candidate.Bytes:N0}\nProvider status: {result.State}\n{result.Message}\nCurrent metadata was unavailable, inconsistent or outside supported production scope.\n\n{CleanupReviewLimits}"));
                        if (state == CheckState.Cancelled) { interrupted = true; cancelled = true; break; }
                        continue;
                    }
                    var data = result.Items[0]; var streams = data.StreamScope;
                    string streamDetails = streams == null ? "Stream scope: Unknown / not reported; whole-file review is blocked."
                        : $"Stream scope: {streams.State}; reason: {streams.Reason}; observed stream count: {streams.StreamCount?.ToString() ?? "Unknown"}; unnamed-stream bytes: {streams.UnnamedStreamBytes?.ToString() ?? "Unknown"}. Alternate or unknown stream scope blocks whole-file cleanup review.";
                    SetCleanupReviewRow(candidate, new(candidate, result.State, "Metadata observed", data.File.Bytes, data.QueryCompletedUtc,
                        $"Selected path: {candidate.Path}\nPrior unnamed logical bytes: {candidate.Bytes:N0}; prior modified UTC: {candidate.ModifiedUtc:u}\nCurrent unnamed logical bytes: {data.File.Bytes:N0}; links: {data.File.LinkCount}\nCurrent modified UTC: {data.File.LastWriteUtc:u}\nQuery interval UTC: {data.QueryStartedUtc:u} — {data.QueryCompletedUtc:u}\nHistorical scan coverage: {data.SourceCoverageState}; current exclusion coverage: {data.ExclusionCoverageState}\nHistorical size matches: {data.HistoricalSizeMatches}; historical modified time matches: {data.HistoricalModifiedMatches}\n{streamDetails}\n\n{string.Join("\n", data.Coverage)}\n\n{CleanupReviewLimits}"));
                }
                if (!interrupted)
                {
                    linked.ThrowIfCancellationRequested();
                    var after = await LoadCleanupReviewPreferences(linked);
                    if (!Current()) return CheckState.Cancelled;
                    if (after == null || after.Error != null || !plan.MatchesExclusions(after.Items, linked))
                    { InvalidateCleanupReviewForPreferences(); return CheckState.Unknown; }
                    if (!plan.IsFresh(DateTimeOffset.UtcNow))
                    { interrupted = true; cleanupReviewRequiresRescan = true; }
                }
            }
            catch (OperationCanceledException) { interrupted = true; cancelled = true; }
            if (!Current()) return CheckState.Cancelled;
            if (interrupted) MarkCleanupReviewHistorical();
            int observedCount = CleanupReviewRows.Count(row => row.CurrentBytes.HasValue);
            CleanupReviewMessage = interrupted
                ? $"Review interrupted: {observedCount} earlier metadata observation(s) retained as historical only; final exclusions/context were not revalidated. Unprocessed entries remain Not checked. No current/complete worksheet is claimed."
                : $"{observedCount} of {CleanupReviewSelections.Count} selected entries have accepted point-in-time metadata; exclusion observations matched before/after. Matching reads do not prove uninterrupted stability, inactivity or deletion safety.";
            NotifyCleanupReview();
            return interrupted ? cancelled ? CheckState.Cancelled : CheckState.Unknown : observedCount == 0 ? CheckState.Unknown
                : observedCount != CleanupReviewRows.Count || CleanupReviewRows.Any(row => row.State != CheckState.Available) ? CheckState.Partial : CheckState.Available;
        }, error =>
        {
            if (!Current()) return;
            MarkCleanupReviewHistorical();
            CleanupReviewMessage = error + " Earlier per-entry observations, if any, are historical only; final settings/context were not revalidated. Unprocessed entries remain Not checked; no current or complete worksheet is claimed.";
            NotifyCleanupReview();
        });
    }
    private void MarkCleanupReviewHistorical()
    {
        if (cleanupReviewInterrupted) return;
        cleanupReviewInterrupted = true;
        foreach (var row in CleanupReviewRows.Where(row => row.CurrentBytes.HasValue).ToArray())
            SetCleanupReviewRow(row.Candidate, row with
            {
                Status = "Historical · interrupted review",
                Details = "Earlier interrupted-batch observation only. Final exclusion/context observations were not revalidated; this is not a current or complete worksheet.\n\n" + row.Details
            });
        NotifyCleanupReview();
    }
    private void SetCleanupReviewRow(CleanupCandidate candidate, CleanupSelectionReviewRow row)
    {
        int index = -1; for (int i = 0; i < CleanupReviewRows.Count; i++) if (ReferenceEquals(CleanupReviewRows[i].Candidate, candidate)) { index = i; break; }
        if (index < 0) return;
        bool selected = ReferenceEquals(SelectedCleanupReview?.Candidate, candidate);
        CleanupReviewRows[index] = row; if (selected) SelectedCleanupReview = row;
        NotifyCleanupReview();
    }
    private void NotifyCleanupReview()
    {
        PropertyChanged?.Invoke(this, new(nameof(CleanupReviewDetails)));
        PropertyChanged?.Invoke(this, new(nameof(CleanupReviewByteSummary)));
        PropertyChanged?.Invoke(this, new(nameof(HasPendingCleanupSelectionReviewWork)));
        PropertyChanged?.Invoke(this, new(nameof(CleanupReviewRequiresRescan)));
        CommandManager.InvalidateRequerySuggested();
    }
}
