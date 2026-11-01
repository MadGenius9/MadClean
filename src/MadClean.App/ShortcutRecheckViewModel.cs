using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IShortcutRecheck shortcutRecheck = new WindowsShortcutInventory();
    private CheckResult<ShortcutReport>? shortcutRecheckSnapshot;
    private long shortcutRecheckGeneration;
    private string shortcutRecheckMessage = "Not checked. Select a shortcut and explicitly recheck it; the folder inventory remains a historical snapshot.";
    private string shortcutRecheckDetails = "A selected recheck reads only that shortcut's current metadata and supported local target availability. It does not resolve or launch the target, prove the shortcut is obsolete, or authorize removal.";
    public ICommand RecheckSelectedShortcutCommand { get; private set; } = null!;
    public string ShortcutRecheckMessage { get => shortcutRecheckMessage; private set => Set(ref shortcutRecheckMessage, value); }
    public string ShortcutRecheckDetails { get => shortcutRecheckDetails; private set => Set(ref shortcutRecheckDetails, value); }
    private void InitializeShortcutRecheck() => RecheckSelectedShortcutCommand = new AsyncCommand(RecheckSelectedShortcut,
        () => !IsBusy && shortcutRecheckSnapshot != null && SelectedShortcutObservation != null);
    private void InvalidateShortcutRecheckSelection()
    {
        shortcutRecheckGeneration++;
        ShortcutRecheckMessage = "Not checked for this selection. Any earlier result is invalidated; an in-flight native read may still finish.";
        ShortcutRecheckDetails = "Select one current inventory entry and choose Recheck selected shortcut. No folder rescan, target activation, network lookup or removal is requested. The original row remains a historical observation.";
        CommandManager.InvalidateRequerySuggested();
    }
    private void InvalidateShortcutRecheckSnapshot()
    {
        shortcutRecheckSnapshot = null;
        InvalidateShortcutRecheckSelection();
    }
    private Task RecheckSelectedShortcut()
    {
        var selected = SelectedShortcutObservation;
        var snapshot = shortcutRecheckSnapshot;
        string root = ShortcutFolder;
        long generation = shortcutRecheckGeneration;
        bool Current() => generation == shortcutRecheckGeneration && ReferenceEquals(selected, SelectedShortcutObservation) &&
            ReferenceEquals(snapshot, shortcutRecheckSnapshot) && root == ShortcutFolder;
        return RunDiscoveryAsync(ActivityArea.ShortcutRecheck, "Rechecking selected shortcut metadata", async token =>
        {
            ShortcutRecheckMessage = "Reading only the selected shortcut and supported local target metadata…";
            ShortcutRecheckDetails = "In progress. No completed current observation is claimed; the original row is historical.";
            var prepared = ShortcutRecheckScope.Create(root, snapshot, selected, token);
            token.ThrowIfCancellationRequested();
            if (prepared.Items.Count != 1)
            {
                if (Current())
                {
                    ShortcutRecheckMessage = Stamp(prepared.State, prepared.Message);
                    ShortcutRecheckDetails = "No selected read was started. Refresh inventory if its scope or selected evidence is unavailable. No absence, obsolescence or removal verdict is inferred.";
                }
                return prepared.State;
            }
            var scope = prepared.Items[0];
            var result = await shortcutRecheck.ReadAsync(scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            ShortcutRecheckMessage = Stamp(result.State, result.Message);
            if (result.Items.Count != 1 || result.State is not (CheckState.Available or CheckState.Partial))
            {
                ShortcutRecheckDetails = "No completed current observation was returned. Prior inventory is historical; unavailable or interrupted metadata is not a missing-target verdict. Any synchronous native call may still finish under its worker's ownership.";
                return result.State;
            }
            var current = result.Items[0];
            if (!string.Equals(current.ShortcutPath, scope.ShortcutPath, StringComparison.OrdinalIgnoreCase) ||
                current.Source != ShortcutTargetClassifier.Source || current.CheckedAt.Offset != TimeSpan.Zero ||
                current.CheckedAt < scope.ValidatedUtc || current.CheckedAt > DateTimeOffset.UtcNow || !Enum.IsDefined(current.State))
                throw new InvalidOperationException("Selected shortcut projection was inconsistent.");
            string comparison = current.State switch
            {
                ShortcutTargetState.TargetExists when scope.PriorObservation.State == ShortcutTargetState.LocalTargetMissing =>
                    "The current stored local target exists. The earlier missing observation is historical and is not a current broken-shortcut finding.",
                ShortcutTargetState.LocalTargetMissing =>
                    "The current stored local target is missing under the supported local check. It may have moved; this does not prove the shortcut obsolete.",
                ShortcutTargetState.TargetExists =>
                    "The current stored local target exists; this does not prove launching it would work.",
                _ => "The current target remains unverified or unsupported. Offline, network, access and ambiguous metadata must not be called broken."
            };
            ShortcutRecheckDetails = $"Prior observation (historical)\nShortcut: {scope.PriorObservation.ShortcutPath}\nStored target: {scope.PriorObservation.TargetPath}\nState: {scope.PriorObservation.State}; checked UTC: {scope.PriorObservation.CheckedAt:u}\n\n" +
                $"New path observation\nShortcut: {current.ShortcutPath}\nStored target: {current.TargetPath}\nState: {current.State}; checked UTC: {current.CheckedAt:u}\nReason: {current.Reason}\nSource: {current.Source}\nOriginal inventory coverage: {scope.SourceCoverageState}\n\n" +
                comparison + "\nThese are observations at the selected path. Continuity of the shortcut file's object identity has not been established; its metadata or target may have changed. No folder-wide baseline is refreshed, no shortcut/target is changed, and removal remains unavailable.";
            return result.State;
        }, error =>
        {
            if (!Current()) return;
            ShortcutRecheckMessage = error;
            ShortcutRecheckDetails = "No completed current recheck is claimed. The original inventory is historical. A native read may still finish; MadClean does not force-close its handles, launch a target or remove anything.";
        });
    }
}
