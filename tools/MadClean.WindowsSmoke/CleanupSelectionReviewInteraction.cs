using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using MadClean.App;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // The caller owns the real Temp candidate and all teardown guards. False means
    // that it must retain that scope; this helper never creates or removes files.
    private static async Task<bool> CheckCleanupSelectionReviewInteraction(
        MainWindow window, MainViewModel model, ResultRow ownedCandidate, SmokeReport report)
    {
        const string check = "ui-cleanup-selection-review-interaction";
        bool Settled() => !model.IsBusy && !model.HasPendingCleanupSelectionReviewWork &&
            !WindowsCleanupFileEvidence.HasPendingMetadataRead;
        async Task<bool> Wait()
        {
            var clock = Stopwatch.StartNew();
            while (!Settled() && clock.Elapsed < TimeSpan.FromSeconds(45))
            { await Task.Delay(25); await Idle(window); }
            if (Settled()) return true;
            if (model.IsBusy && model.CancelCommand.CanExecute(null)) model.CancelCommand.Execute(null);
            // A deadline is never upgraded by late completion; the caller retains
            // its owned tree even if a subsequent dispatcher turn settles work.
            return false;
        }
        void Require(bool condition, string reason)
        { if (!condition) throw new SmokeAssertionException(reason); }
        void Invoke(ICommand command)
        {
            var button = Descendants<Button>(window).Single(item => ReferenceEquals(item.Command, command));
            Require(button.IsEnabled && command.CanExecute(button.CommandParameter), "ui-cleanup-worksheet-command-disabled");
            var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new SmokeAssertionException("ui-cleanup-worksheet-invoke-unavailable");
            invoke.Invoke();
        }
        if (!Settled())
        {
            report.Unknown(check, "Earlier UI or native metadata ownership remains pending; no worksheet command or additional query started.");
            return false;
        }
        var sourceGrid = Descendants<DataGrid>(window).Single(item => ReferenceEquals(item.ItemsSource, model.CleanupRows));
        Require(model.CleanupRows.Any(row => ReferenceEquals(row, ownedCandidate)), "ui-cleanup-worksheet-owned-source-missing");
        Require(model.CleanupReviewSelections.Count == 0 && model.CleanupReviewRows.Count == 0,
            "ui-cleanup-worksheet-not-initially-empty");
        var expander = Descendants<Expander>(window).Single(item =>
            AutomationProperties.GetName(item) == "Selected cleanup metadata worksheet");
        expander.IsExpanded = true; await Idle(window);
        var grid = Descendants<DataGrid>(window).Single(item => ReferenceEquals(item.ItemsSource, model.CleanupReviewRows));
        var details = Descendants<TextBox>(window).Single(item =>
            AutomationProperties.GetName(item) == "Selected cleanup worksheet path, metadata and blockers");
        var summary = Descendants<TextBlock>(window).Single(item =>
            AutomationProperties.GetName(item) == "Cleanup worksheet logical byte arithmetic and uncertainty");
        var status = Descendants<TextBlock>(window).Single(item =>
            AutomationProperties.GetName(item) == "Cleanup metadata worksheet status");
        Require(grid.IsReadOnly && details.IsReadOnly &&
            details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.CleanupReviewDetails),
            "ui-cleanup-worksheet-display-binding");

        sourceGrid.SelectedItem = ownedCandidate; await Idle(window);
        Require(ReferenceEquals(model.SelectedCleanupRow, ownedCandidate), "ui-cleanup-worksheet-source-binding");
        Invoke(model.AddSelectedCleanupReviewCommand); await Idle(window);
        Require(model.CleanupReviewSelections.Count == 1 && model.CleanupReviewRows.Count == 1,
            "ui-cleanup-worksheet-add-count");
        var actualCandidate = model.CleanupReviewSelections[0];
        Require(string.Equals(actualCandidate.Path, ownedCandidate.Name, StringComparison.OrdinalIgnoreCase) &&
            ReferenceEquals(model.CleanupReviewRows[0].Candidate, actualCandidate), "ui-cleanup-worksheet-source-lineage");
        Invoke(model.AddSelectedCleanupReviewCommand); await Idle(window);
        Require(model.CleanupReviewSelections.Count == 1 && model.CleanupReviewRows.Count == 1 &&
            ReferenceEquals(model.CleanupReviewSelections[0], actualCandidate), "ui-cleanup-worksheet-duplicate-selection");
        grid.SelectedItem = model.CleanupReviewRows[0]; await Idle(window);
        Require(ReferenceEquals(model.SelectedCleanupReview, model.CleanupReviewRows[0]) &&
            details.Text == model.CleanupReviewDetails && details.Text.Contains(actualCandidate.Path, StringComparison.Ordinal),
            "ui-cleanup-worksheet-detail-selection");
        grid.SelectedItem = null; sourceGrid.SelectedItem = null; await Idle(window);
        Require(model.SelectedCleanupReview == null && model.SelectedCleanupRow == null &&
            model.CleanupReviewSelections.Count == 1 && ReferenceEquals(model.CleanupReviewSelections[0], actualCandidate) &&
            model.CleanupReviewRows.Count == 1 && !model.RemoveCleanupReviewCommand.CanExecute(null),
            "ui-cleanup-worksheet-display-selection-mutated-set");
        grid.SelectedItem = model.CleanupReviewRows[0]; await Idle(window);
        Invoke(model.RemoveCleanupReviewCommand); await Idle(window);
        Require(model.CleanupReviewSelections.Count == 0 && model.CleanupReviewRows.Count == 0,
            "ui-cleanup-worksheet-remove-count");
        sourceGrid.SelectedItem = ownedCandidate; await Idle(window);
        Invoke(model.AddSelectedCleanupReviewCommand); await Idle(window);
        Invoke(model.ClearCleanupReviewCommand); await Idle(window);
        Require(model.CleanupReviewSelections.Count == 0 && model.CleanupReviewRows.Count == 0,
            "ui-cleanup-worksheet-clear-count");
        Invoke(model.AddSelectedCleanupReviewCommand); await Idle(window);
        Require(model.CleanupReviewSelections.Count == 1 && ReferenceEquals(model.CleanupReviewSelections[0], actualCandidate),
            "ui-cleanup-worksheet-readd-lineage");
        var before = DateTimeOffset.UtcNow;
        Invoke(model.ReviewCleanupSelectionCommand); await Idle(window);
        if (!await Wait())
        {
            report.Unknown(check, "Actual worksheet review exceeded the bounded wait or left pending ownership; the owned temporary scope must be retained.");
            return false;
        }
        var after = DateTimeOffset.UtcNow;
        if (model.CleanupReviewRequiresRescan && model.CleanupReviewSelections.Count == 0 && model.CleanupReviewRows.Count == 0)
        {
            Require(model.SelectedCleanupReview == null, "ui-cleanup-worksheet-invalid-context-selection-retained");
            report.Unknown(check, "Actual source/exclusion validity checks cleared the worksheet and require a rescan. Ownership settled; no accepted metadata or complete review was substituted.");
            return true;
        }
        Require(model.CleanupReviewSelections.Count == 1 && model.CleanupReviewRows.Count == 1 &&
            ReferenceEquals(model.CleanupReviewSelections[0], actualCandidate), "ui-cleanup-worksheet-review-mutated-set");
        var observation = model.CleanupReviewRows[0];
        Require(ReferenceEquals(observation.Candidate, actualCandidate) && !observation.CanDelete &&
            Enum.IsDefined(observation.State), "ui-cleanup-worksheet-readonly-lineage");
        bool observed = !model.CleanupReviewRequiresRescan && (observation.State is CheckState.Available or CheckState.Partial) &&
            observation.CurrentBytes is >= 0 && observation.CheckedUtc is { } completed &&
            completed.Offset == TimeSpan.Zero && completed >= before && completed <= after;
        grid.SelectedItem = observation; await Idle(window);
        Require(ReferenceEquals(model.SelectedCleanupReview, observation) && details.Text == observation.Details &&
            status.Text == model.CleanupReviewMessage && summary.Text == model.CleanupReviewByteSummary &&
            details.Text.Contains(actualCandidate.Path, StringComparison.Ordinal) &&
            details.Text.Contains("Active use, ownership and deletion safety remain unknown", StringComparison.Ordinal),
            "ui-cleanup-worksheet-observed-display");
        CleanupInteractionActivity(model);

        // Real source invalidation is last: the earlier single-file interaction's
        // historical-row assertions have already completed. No row is injected.
        Require(model.ScanCommand.CanExecute(null), "ui-cleanup-worksheet-source-scan-disabled");
        model.ScanCommand.Execute(null);
        Require(model.CleanupReviewSelections.Count == 0 && model.CleanupReviewRows.Count == 0 &&
            model.SelectedCleanupReview == null && !model.ReviewCleanupSelectionCommand.CanExecute(null) &&
            !model.AddSelectedCleanupReviewCommand.CanExecute(null), "ui-cleanup-worksheet-source-not-invalidated");
        // Stop only an actually observed live inventory operation; this is not a
        // claimed worksheet-cancellation proof and never kills a servicing client.
        if (model.IsBusy && model.CancelCommand.CanExecute(null)) model.CancelCommand.Execute(null);
        if (!await Wait())
        {
            report.Unknown(check, "Actual source invalidation cleared the worksheet, but inventory/UI ownership did not settle; the owned temporary scope must be retained.");
            return false;
        }
        Require(model.CleanupReviewSelections.Count == 0 && model.CleanupReviewRows.Count == 0 &&
            model.SelectedCleanupReview == null && !model.ReviewCleanupSelectionCommand.CanExecute(null),
            "ui-cleanup-worksheet-stale-source-result");
        await Idle(window);
        Require(!details.Text.Contains(actualCandidate.Path, StringComparison.Ordinal), "ui-cleanup-worksheet-stale-details");
        CleanupInteractionActivity(model);
        string facts = $"Actual selected candidates: 1; metadata state: {observation.State}; native metadata observed: {observed}; selection operations and immediate source invalidation observed: True; deletion permission: False.";
        if (observed) report.Pass(check, facts + " Real bound Add/remove/clear/review commands and actual Windows metadata were used; display selection retained the explicit set. No row injection, screenshots after data, file action or batch-cancellation claim.");
        else report.Unknown(check, facts + " The real review did not establish accepted current metadata; no fabricated provider result or native success was substituted.");
        return true;
    }
}
