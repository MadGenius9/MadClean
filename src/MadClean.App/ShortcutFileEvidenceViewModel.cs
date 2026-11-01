using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IShortcutActionPreview shortcutFileEvidence = new WindowsShortcutInventory();
    private string shortcutFileEvidenceMessage = "Not checked. Select a shortcut and explicitly inspect its file evidence.";
    private string shortcutFileEvidenceDetails = ShortcutFileEvidenceLimits;
    private const string ShortcutFileEvidenceLimits = "This separate read reads the selected shortcut's bounded contents and current supported target metadata. It does not launch or resolve a target. File identity and repeated byte observations concern this read only; continuity since the original scan, immutable contents, ownership, permissions and safe removal remain unverified. All read handles close before the result is shown. Moving and removal remain unavailable.";
    public ICommand InspectShortcutFileEvidenceCommand { get; private set; } = null!;
    public string ShortcutFileEvidenceMessage { get => shortcutFileEvidenceMessage; private set => Set(ref shortcutFileEvidenceMessage, value); }
    public string ShortcutFileEvidenceDetails { get => shortcutFileEvidenceDetails; private set => Set(ref shortcutFileEvidenceDetails, value); }

    private void InitializeShortcutFileEvidence() => InspectShortcutFileEvidenceCommand = new AsyncCommand(InspectShortcutFileEvidence,
        () => !IsBusy && shortcutRecheckSnapshot != null && SelectedShortcutObservation != null);

    private void InvalidateShortcutFileEvidence()
    {
        ShortcutFileEvidenceMessage = "Not checked for this selection. Any earlier file evidence is invalidated.";
        ShortcutFileEvidenceDetails = ShortcutFileEvidenceLimits;
    }

    private Task InspectShortcutFileEvidence()
    {
        var selected = SelectedShortcutObservation;
        var snapshot = shortcutRecheckSnapshot;
        string root = ShortcutFolder;
        long generation = shortcutRecheckGeneration;
        bool Current() => generation == shortcutRecheckGeneration && ReferenceEquals(selected, SelectedShortcutObservation) &&
            ReferenceEquals(snapshot, shortcutRecheckSnapshot) && root == ShortcutFolder;
        return RunDiscoveryAsync(ActivityArea.ShortcutFileEvidence, "Reading selected shortcut file evidence", async token =>
        {
            ShortcutFileEvidenceMessage = "Reading only the selected shortcut's bounded contents and supported target metadata…";
            ShortcutFileEvidenceDetails = "In progress. No completed file observation or action permission is claimed.";
            var prepared = ShortcutRecheckScope.Create(root, snapshot, selected, token);
            token.ThrowIfCancellationRequested();
            if (prepared.Items.Count != 1)
            {
                if (Current())
                {
                    ShortcutFileEvidenceMessage = Stamp(prepared.State, prepared.Message);
                    ShortcutFileEvidenceDetails = "No file-evidence read started. Refresh inventory and select one supported entry. " + ShortcutFileEvidenceLimits;
                }
                return prepared.State;
            }
            var scope = prepared.Items[0];
            var result = await shortcutFileEvidence.InspectAsync(scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            ShortcutFileEvidenceMessage = Stamp(result.State, result.Message);
            if (result.Items.Count != 1 || result.State is not (CheckState.Available or CheckState.Partial or CheckState.Unavailable or CheckState.Unknown))
            {
                ShortcutFileEvidenceDetails = "No completed file-evidence result was returned. A pending native read retains its own handles; interrupted or unavailable data is not a missing-target finding. " + ShortcutFileEvidenceLimits;
                return result.State;
            }
            var preview = result.Items[0];
            if (preview.CanMove || preview.Prior != scope.PriorObservation || !Enum.IsDefined(preview.ReviewState) ||
                !Enum.IsDefined(preview.Protection) || preview.SourceCoverageState != scope.SourceCoverageState ||
                preview.Current is { } observed && !string.Equals(observed.ShortcutPath, scope.ShortcutPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Selected shortcut file evidence was inconsistent.");
            string current = preview.Current is { } observation
                ? $"Current target observation: {observation.State}\nStored target: {observation.TargetPath}\nChecked UTC: {observation.CheckedAt:u}\nReason: {observation.Reason}\nSource: {observation.Source}"
                : "No accepted current target observation. Protection, access or read consistency may have prevented completion; see the check status.";
            string file = preview.Evidence is { } evidence
                ? $"Observed logical bytes: {evidence.Bytes:N0}\nObserved hard-link count: {evidence.LinkCount}\nFull file identity observed: {evidence.FileId != Guid.Empty}\nRead consistency: {evidence.Consistency} (point-in-time observations only)\nRead interval UTC: {evidence.ReadStartedUtc:u} — {evidence.ReadCompletedUtc:u}"
                : "No accepted file identity/content evidence is retained in this result.";
            ShortcutFileEvidenceDetails = $"Shortcut: {scope.ShortcutPath}\nHistorical target state: {preview.Prior.State}; checked UTC: {preview.Prior.CheckedAt:u}\nOriginal inventory coverage: {preview.SourceCoverageState}\n\n{current}\n\n{file}\n\nScope protection: {preview.ProtectionReason}\nPermissions: {preview.DaclAssessment}\nReview: {preview.ReviewState}; moving/removal unavailable\nCoverage: {preview.Coverage}\n\n{ShortcutFileEvidenceLimits}";
            return result.State;
        }, error =>
        {
            if (!Current()) return;
            ShortcutFileEvidenceMessage = error;
            ShortcutFileEvidenceDetails = "No completed file-evidence read is claimed. " + ShortcutFileEvidenceLimits;
        });
    }
}
