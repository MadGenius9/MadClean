using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly ICleanupFileEvidence cleanupFileEvidence = new WindowsCleanupFileEvidence();
    private CleanupReport? cleanupEvidenceSnapshot;
    private DateTimeOffset cleanupEvidenceCompletedUtc;
    private IReadOnlyList<CleanupExclusion> cleanupEvidenceExclusions = Array.Empty<CleanupExclusion>();
    private long cleanupEvidenceGeneration;
    private string cleanupFileEvidenceMessage = "Not checked. Select one actual cleanup candidate, then recheck its current information.";
    private string cleanupFileEvidenceDetails = CleanupFileEvidenceLimits;
    private const string CleanupFileEvidenceLimits = "This reads metadata for exactly the selected temporary file. It does not read contents or rescan folders. Current identity is a first observation; the earlier scan cannot establish identity continuity. Active use and application ownership remain unknown. Nothing is deleted and no read handle remains protected after publication.";
    public ICommand RecheckCleanupFileCommand { get; private set; } = null!;
    public string CleanupFileEvidenceMessage { get => cleanupFileEvidenceMessage; private set => Set(ref cleanupFileEvidenceMessage, value); }
    public string CleanupFileEvidenceDetails { get => cleanupFileEvidenceDetails; private set => Set(ref cleanupFileEvidenceDetails, value); }

    private CleanupCandidate? CurrentCleanupEvidenceCandidate() => SelectedCleanupRow is { } row && CleanupRows.Contains(row)
        ? cleanupPreview.FirstOrDefault(candidate => string.Equals(candidate.Path, row.Name, StringComparison.OrdinalIgnoreCase)) : null;
    private void InitializeCleanupFileEvidence() => RecheckCleanupFileCommand = new AsyncCommand(RecheckCleanupFile,
        () => !IsBusy && cleanupEvidenceSnapshot != null && CurrentCleanupEvidenceCandidate() != null);
    private void InvalidateCleanupFileEvidence()
    {
        cleanupEvidenceGeneration++;
        CleanupFileEvidenceMessage = "Not checked for this selection. Any earlier current-file observation is invalidated.";
        CleanupFileEvidenceDetails = CleanupFileEvidenceLimits;
        CommandManager.InvalidateRequerySuggested();
    }
    private void InvalidateCleanupEvidenceSnapshot()
    {
        InvalidateCleanupSelectionReview();
        cleanupEvidenceSnapshot = null;
        cleanupEvidenceCompletedUtc = default;
        cleanupEvidenceExclusions = Array.Empty<CleanupExclusion>();
        InvalidateCleanupFileEvidence();
    }
    private void PublishCleanupEvidenceSnapshot(CleanupReport report, IReadOnlyList<CleanupExclusion> exclusions, DateTimeOffset completedUtc)
    {
        InvalidateCleanupSelectionReview();
        InvalidateCleanupFileEvidence();
        cleanupEvidenceSnapshot = report;
        cleanupEvidenceCompletedUtc = completedUtc;
        cleanupEvidenceExclusions = Array.AsReadOnly(exclusions.Select(item => item with { }).ToArray());
        CommandManager.InvalidateRequerySuggested();
    }
    private Task RecheckCleanupFile()
    {
        var selected = CurrentCleanupEvidenceCandidate();
        var row = SelectedCleanupRow;
        var snapshot = cleanupEvidenceSnapshot;
        var exclusions = cleanupEvidenceExclusions;
        var completedUtc = cleanupEvidenceCompletedUtc;
        long generation = cleanupEvidenceGeneration;
        bool Current() => generation == cleanupEvidenceGeneration && ReferenceEquals(snapshot, cleanupEvidenceSnapshot) &&
            ReferenceEquals(row, SelectedCleanupRow) && ReferenceEquals(selected, CurrentCleanupEvidenceCandidate());
        return RunDiscoveryAsync(ActivityArea.CleanupFileEvidence, "Rechecking the selected temporary file", async token =>
        {
            CleanupFileEvidenceMessage = "Checking current temporary roots, saved exclusions and exactly the selected file's metadata…";
            CleanupFileEvidenceDetails = "In progress. No current file or deletion eligibility is established.";
            var prepared = await Task.Run(() => CleanupFileEvidenceScope.Create(snapshot, selected, completedUtc, exclusions, DateTimeOffset.UtcNow, token), token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            if (prepared.Items.Count != 1)
            {
                if (Current())
                {
                    CleanupFileEvidenceMessage = Stamp(prepared.State, prepared.Message);
                    CleanupFileEvidenceDetails = "No native file query started. Scan again or select a supported current candidate. " + CleanupFileEvidenceLimits;
                }
                return prepared.State;
            }
            var scope = prepared.Items[0];
            var result = await cleanupFileEvidence.ReadAsync(scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            CleanupFileEvidenceMessage = Stamp(result.State, result.Message);
            if (result.Items.Count != 1)
            {
                CleanupFileEvidenceDetails = "No accepted current metadata report was returned. Missing, excluded, changed or inaccessible files do not establish cleanup eligibility. " + CleanupFileEvidenceLimits;
                return result.State;
            }
            var data = result.Items[0];
            if (!CleanupFileEvidenceValidation.IsConsistent(result, scope, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("Current cleanup metadata or stream evidence was inconsistent.");
            string streamSummary = data.StreamScope is { } streams
                ? $"Stream scope: {streams.State}; reason: {streams.Reason}; observed stream count: {streams.StreamCount?.ToString() ?? "Unknown"}; matching bookends: {streams.BookendsMatch?.ToString() ?? "Unknown"}. No stream names or contents are read for display. Alternate or unknown streams block qualification for later cleanup. This does not establish inactive use or deletion permission."
                : "Stream scope: Unknown (not checked). No stream eligibility or deletion permission is established.";
            string changed = data.HistoricalSizeMatches && data.HistoricalModifiedMatches
                ? "The observed size and modification time match the earlier scan. This does not prove unchanged identity or uninterrupted history."
                : "The file's size or modification time differs from the scan. Review the current observation and scan again.";
            CleanupFileEvidenceDetails = $"Current path: {data.File.Path}\nSupported temporary root: {data.RootPath}\nCurrent unnamed-stream logical bytes: {data.File.Bytes:N0}; reported links: {data.File.LinkCount}\nModified UTC: {data.File.LastWriteUtc:u}\nCreation UTC: {data.File.CreationUtc:u}; change UTC: {data.File.ChangeUtc:u}\nCheck interval UTC: {data.QueryStartedUtc:u} — {data.QueryCompletedUtc:u}\nHistorical scan coverage: {data.SourceCoverageState}; current exclusion coverage: {data.ExclusionCoverageState}\n\n{changed}\n\n{streamSummary}\n\n{string.Join("\n", data.Coverage)}\n\n{CleanupFileEvidenceLimits}";
            return result.State;
        }, error =>
        {
            if (!Current()) return;
            CleanupFileEvidenceMessage = error;
            CleanupFileEvidenceDetails = "No completed current-file observation is claimed. A native metadata read may still finish under its worker's ownership. " + CleanupFileEvidenceLimits;
        });
    }
}
