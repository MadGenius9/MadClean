using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IShortcutActionPreview shortcutFileEvidence = new WindowsShortcutInventory();
    private string shortcutFileEvidenceMessage = "Not checked. Select a shortcut and explicitly inspect its file evidence.";
    private string shortcutFileEvidenceDetails = ShortcutFileEvidenceLimits;
    private string shortcutDaclSummary = "Access controls: Not checked. Effective permissions remain unverified.";
    private const string ShortcutFileEvidenceLimits = "This separate read reads the selected shortcut's bounded contents, recorded access-control facts and current supported target metadata. It does not launch or resolve a target. File identity, byte and access-control observations concern this read only; continuity since the original scan, immutable contents, ownership, effective permissions, privacy and safe removal remain unverified. All read handles close before the result is shown. Moving and removal remain unavailable.";
    public ICommand InspectShortcutFileEvidenceCommand { get; private set; } = null!;
    public string ShortcutFileEvidenceMessage { get => shortcutFileEvidenceMessage; private set => Set(ref shortcutFileEvidenceMessage, value); }
    public string ShortcutFileEvidenceDetails { get => shortcutFileEvidenceDetails; private set => Set(ref shortcutFileEvidenceDetails, value); }
    public string ShortcutDaclSummary { get => shortcutDaclSummary; private set => Set(ref shortcutDaclSummary, value); }

    private void InitializeShortcutFileEvidence() => InspectShortcutFileEvidenceCommand = new AsyncCommand(InspectShortcutFileEvidence,
        () => !IsBusy && shortcutRecheckSnapshot != null && SelectedShortcutObservation != null);

    private void InvalidateShortcutFileEvidence()
    {
        ShortcutFileEvidenceMessage = "Not checked for this selection. Any earlier file evidence is invalidated.";
        ShortcutFileEvidenceDetails = ShortcutFileEvidenceLimits;
        ShortcutDaclSummary = "Access controls: Not checked for this selection. Effective permissions remain unverified.";
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
            ShortcutFileEvidenceMessage = "Reading the selected shortcut's bounded contents, recorded access controls and supported target metadata…";
            ShortcutFileEvidenceDetails = "In progress. No completed file observation or action permission is claimed.";
            ShortcutDaclSummary = "Access controls: In progress; no completed observation is claimed.";
            var prepared = ShortcutRecheckScope.Create(root, snapshot, selected, token);
            token.ThrowIfCancellationRequested();
            if (prepared.Items.Count != 1)
            {
                if (Current())
                {
                    ShortcutFileEvidenceMessage = Stamp(prepared.State, prepared.Message);
                    ShortcutFileEvidenceDetails = "No file-evidence read started. Refresh inventory and select one supported entry. " + ShortcutFileEvidenceLimits;
                    ShortcutDaclSummary = "Access controls: No query started. Effective permissions remain unverified.";
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
                ShortcutDaclSummary = "Access controls: No accepted completed observation. Effective permissions remain unverified.";
                return result.State;
            }
            var preview = result.Items[0];
            if (preview.CanMove || preview.Prior != scope.PriorObservation || !Enum.IsDefined(preview.ReviewState) ||
                !Enum.IsDefined(preview.Protection) || preview.SourceCoverageState != scope.SourceCoverageState ||
                preview.Current is { } observed && !string.Equals(observed.ShortcutPath, scope.ShortcutPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Selected shortcut file evidence was inconsistent.");
            ShortcutDaclSummary = preview.Evidence?.Dacl is { } dacl
                ? $"Access controls: {dacl.State}. Effective permissions, ownership and privacy remain unverified."
                : "Access controls: No accepted observation for this result. Effective permissions remain unverified.";
            string current = preview.Current is { } observation
                ? $"Current target observation: {observation.State}\nStored target: {observation.TargetPath}\nChecked UTC: {observation.CheckedAt:u}\nReason: {observation.Reason}\nSource: {observation.Source}"
                : "No accepted current target observation. Protection, access or read consistency may have prevented completion; see the check status.";
            string file = preview.Evidence is { } evidence
                ? $"Observed logical bytes: {evidence.Bytes:N0}\nObserved hard-link count: {evidence.LinkCount}\nFull file identity observed: {evidence.FileId != Guid.Empty}\nRead consistency: {evidence.Consistency} (point-in-time observations only)\nRead interval UTC: {evidence.ReadStartedUtc:u} — {evidence.ReadCompletedUtc:u}"
                : "No accepted file identity/content evidence is retained in this result.";
            ShortcutFileEvidenceDetails = $"Shortcut: {scope.ShortcutPath}\nHistorical target state: {preview.Prior.State}; checked UTC: {preview.Prior.CheckedAt:u}\nOriginal inventory coverage: {preview.SourceCoverageState}\n\n{current}\n\n{file}\n\n{FormatShortcutDacl(preview.Evidence?.Dacl)}\n\nScope protection: {preview.ProtectionReason}\nReview: {preview.ReviewState}; moving/removal unavailable\nCoverage: {preview.Coverage}\n\n{ShortcutFileEvidenceLimits}";
            return result.State;
        }, error =>
        {
            if (!Current()) return;
            ShortcutFileEvidenceMessage = error;
            ShortcutFileEvidenceDetails = "No completed file-evidence read is claimed. " + ShortcutFileEvidenceLimits;
            ShortcutDaclSummary = "Access controls: No accepted completed observation. Effective permissions remain unverified.";
        });
    }

    private static string FormatShortcutDacl(ShortcutDaclObservation? dacl)
    {
        const string limits = "This records configured access-control facts, not effective permissions, ownership or privacy. Access controls can change during the read; file sharing does not lock them. No security settings are changed.";
        if (dacl == null) return "Access controls: Not checked. " + limits;
        string Flag(bool? flag) => flag.HasValue ? flag.Value ? "Set" : "Not set" : "Unknown";
        string facts = dacl.State != CheckState.Available ? "No accepted access-control facts."
            : dacl.DaclPresent == false ? "No DACL present; this is distinct from an empty ACL."
            : dacl.NullDacl == true ? "NULL DACL: no discretionary ACL restriction is recorded; other access rules remain unverified."
            : dacl.AceCount == 0 ? "Empty DACL: zero access-control entries."
            : $"DACL present: {dacl.AceCount} access-control entries.";
        string native = dacl.NativeResult is uint code ? $"0x{code:X8}" : "Not observed";
        string time = dacl.QueryStartedUtc is { } start && dacl.QueryCompletedUtc is { } end
            ? $"{start:u} — {end:u}" : "Not established";
        string reason = dacl.Reason switch
        {
            ShortcutDaclReason.NotChecked => "No access-control query was made.",
            ShortcutDaclReason.Observed => "A bounded descriptor summary was observed and its native allocation released.",
            ShortcutDaclReason.NativeQueryFailed => "Windows could not complete the access-control read; check file access or choose another supported file.",
            ShortcutDaclReason.DescriptorReleaseUnconfirmed => "Read cleanup could not be confirmed; further access-control reads are blocked for this app session.",
            ShortcutDaclReason.Unsupported => "This access-control query is unsupported or unavailable in the current environment.",
            ShortcutDaclReason.Cancelled => "No completed access-control observation is claimed after cancellation.",
            _ => "The descriptor facts, bounds or observation times could not be validated."
        };
        return $"Recorded access controls: {dacl.State}\n{facts}\nInheritance-protection flag: {Flag(dacl.DaclProtected)}\nDefaulted flag: {Flag(dacl.DaclDefaulted)}\nQuery interval UTC: {time}\nSource: Windows GetSecurityInfo / DACL only / same open file\nNative query result: {native}\n{reason}\n{limits}";
    }
}
