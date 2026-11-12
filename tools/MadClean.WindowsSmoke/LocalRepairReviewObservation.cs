using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;
using MadClean.Validation;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

// Actual managed review controls only. The host owns startup, output and lifetime fences.
// This observer never executes history, confirmation, identity or maintenance commands.
internal sealed class LocalRepairReviewObservation
{
    private bool? closedGates, controlsBound, draftsObserved, acknowledgmentsObserved,
        confirmationBlocked, selectionInvalidation, noRequestStateObserved;
    internal LocalRepairReviewFacts Facts => new(closedGates, controlsBound, draftsObserved,
        acknowledgmentsObserved, confirmationBlocked, selectionInvalidation, noRequestStateObserved);

    internal async Task ObserveAsync(MainWindow window, MainViewModel model)
    {
        window.Dispatcher.VerifyAccess();
        // This concrete service construction is managed-only. Check its literal closed
        // boundaries as well as the actual window controller before any interaction.
        var service = new WindowsSystemRepairReviewService();
        bool GatesClosed() => !service.ProductionExecutionValidated && !service.ProductionRecoveryReviewValidated &&
            !model.RepairReview.ExecutionQualified && !model.RepairReview.RecoveryReviewQualified;
        closedGates = GatesClosed();
        if (closedGates != true) return;
        if (window.IsEnabled || model.IsBusy || model.HasPendingRepairReview || model.SelectedSystemTool != null ||
            model.RepairReview.SelectedOperation != null || model.RepairReview.Draft != null || !AcknowledgmentsClear(model) || !NoRequestState(model))
        { controlsBound = false; return; }

        var primary = Descendants<ListBox>(window).Single(control =>
            control.GetBindingExpression(Selector.SelectedIndexProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.PrimaryNavigationIndex));
        var tools = Descendants<ComboBox>(window).Single(control =>
            control.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedToolNavigation));
        int originalPrimary = primary.SelectedIndex;
        if (originalPrimary != 0 || model.SelectedToolNavigation != null ||
            tools.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Mode != BindingMode.TwoWay ||
            !ReferenceEquals(tools.ItemsSource, model.ToolNavigationItems))
        { controlsBound = false; return; }
        var link = model.ToolNavigationItems.Single(item => item.Title == "Windows integrity tools");
        ComboBox? operations = null;
        bool selectedPage = false, completed = false, changedSelectionInvalidated = false;
        CheckBox[]? acknowledgmentControls = null;
        try
        {
            SetSelection(tools, link);
            selectedPage = true;
            await Idle(window);
            var host = Descendants<PageHost>(window).Single();
            var view = Descendants<MadClean.App.Views.SystemToolsView>(window).Single();
            if (!ReferenceEquals(model.SelectedToolNavigation, link) || model.SelectedPage != link.Page ||
                host.SelectedIndex != link.Page || !view.IsVisible ||
                !double.IsFinite(view.ActualWidth) || !double.IsFinite(view.ActualHeight) ||
                view.ActualWidth <= 0 || view.ActualHeight <= 0)
            { controlsBound = false; return; }
            operations = Descendants<ComboBox>(window).Single(control =>
                control.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedSystemTool));
            var prepare = FindButton(window, "SystemRepairPrepare");
            var history = FindButton(window, "SystemRepairHistory");
            var confirm = FindButton(window, "SystemRepairConfirm");
            var draftText = Descendants<TextBox>(window).Single(control =>
                AutomationProperties.GetAutomationId(control) == "SystemRepairDraft");
            string[] ids = ["SystemRepairSelectedAck", "SystemRepairEffectsAck", "SystemRepairElevationAck", "SystemRepairObservationAck"];
            string[] paths = ["RepairReview.OperationSelected", "RepairReview.EffectsAcknowledged", "RepairReview.ElevationAcknowledged", "RepairReview.ObservationLimitsAcknowledged"];
            acknowledgmentControls = ids.Select(id => Descendants<CheckBox>(window).Single(control =>
                AutomationProperties.GetAutomationId(control) == id)).ToArray();
            controlsBound = operations.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Mode == BindingMode.TwoWay &&
                ReferenceEquals(operations.ItemsSource, model.SystemToolDefinitions) &&
                CommandBound(prepare, nameof(MainViewModel.PrepareRepairReviewCommand), model.PrepareRepairReviewCommand) &&
                CommandBound(history, nameof(MainViewModel.ReviewRepairHistoryCommand), model.ReviewRepairHistoryCommand) &&
                CommandBound(confirm, nameof(MainViewModel.ConfirmRepairCommand), model.ConfirmRepairCommand) &&
                draftText.IsReadOnly && draftText.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.RepairDraftDetails) &&
                acknowledgmentControls.Select((control, index) =>
                    control.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path?.Path == paths[index] &&
                    control.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Mode == BindingMode.TwoWay).All(value => value);
            if (controlsBound != true) return;

            foreach (var operation in new[] { SystemToolOperation.DismRestoreHealth, SystemToolOperation.SfcScanNow })
            {
                if (!GatesClosed()) throw new InvalidOperationException("Repair qualification changed during the local observation.");
                var definition = model.SystemToolDefinitions.Single(item => item.Operation == operation);
                var old = model.RepairReview.Draft;
                long oldGeneration = model.RepairReview.Generation;
                SetSelection(operations, definition);
                await Idle(window);
                bool reset = ReferenceEquals(model.SelectedSystemTool, definition) && model.RepairReview.SelectedOperation == operation &&
                    model.RepairReview.Draft == null && model.RepairReview.Generation > oldGeneration &&
                    AcknowledgmentsClear(model) && acknowledgmentControls.All(control => control.IsChecked == false);
                if (!reset || old != null && !old.IsInvalidated)
                { selectionInvalidation = false; return; }
                if (old != null) changedSelectionInvalidated = true;
                if (prepare.Command?.CanExecute(prepare.CommandParameter) != true)
                { draftsObserved = false; return; }
                // The disabled parent window deliberately prevents physical input. Query
                // and invoke only this real bound managed Prepare command, not IsEnabled.
                prepare.Command.Execute(prepare.CommandParameter);
                await Idle(window);
                var draft = model.RepairReview.Draft;
                if (draft == null || !ReferenceEquals(draft.Definition, definition) || draft.SourceGeneration != model.RepairReview.Generation ||
                    draft.DispatchCommitted || draft.IsInvalidated || draftText.Text != model.RepairDraftDetails ||
                    !draftText.Text.Contains(definition.FileName + " " + string.Join(" ", definition.Arguments), StringComparison.Ordinal) ||
                    !draftText.Text.Contains(definition.Effect, StringComparison.Ordinal) || !AcknowledgmentsClear(model))
                { draftsObserved = false; return; }

                for (int index = 0; index < acknowledgmentControls.Length; index++)
                {
                    var control = acknowledgmentControls[index];
                    control.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    control.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                    await Idle(window);
                    bool[] values = Acknowledgments(model);
                    if (!acknowledgmentControls.Take(index + 1).All(item => item.IsChecked == true) ||
                        !values.Take(index + 1).All(value => value) || !ReferenceEquals(model.RepairReview.Draft, draft))
                    { acknowledgmentsObserved = false; return; }
                }
                if (history.Command?.CanExecute(history.CommandParameter) != false || confirm.Command?.CanExecute(confirm.CommandParameter) != false ||
                    model.RepairReview.CanReviewHistory || model.RepairReview.CanConfirm || !GatesClosed())
                { confirmationBlocked = false; return; }
                if (!NoRequestState(model)) { noRequestStateObserved = false; return; }
            }
            // These aggregate facts mean BOTH actual fixed-operation reviews were observed.
            draftsObserved = acknowledgmentsObserved = confirmationBlocked = true;
            completed = true;
        }
        finally
        {
            if (operations != null)
            {
                var old = model.RepairReview.Draft;
                long generation = model.RepairReview.Generation;
                SetSelection(operations, null);
                await Idle(window);
                if (completed)
                    selectionInvalidation = changedSelectionInvalidated && old is { IsInvalidated: true } &&
                        model.SelectedSystemTool == null && model.RepairReview.SelectedOperation == null &&
                        model.RepairReview.Draft == null && model.RepairReview.Generation > generation &&
                        AcknowledgmentsClear(model) && acknowledgmentControls != null && acknowledgmentControls.All(control => control.IsChecked == false);
            }
            if (selectedPage)
            {
                primary.SetCurrentValue(Selector.SelectedIndexProperty, originalPrimary);
                primary.GetBindingExpression(Selector.SelectedIndexProperty)!.UpdateSource();
                await Idle(window);
            }
            // This is only observation of the listed model state, never an instrumented
            // trace proving absence of every possible native/provider invocation.
            if (controlsBound == true && noRequestStateObserved != false) noRequestStateObserved = NoRequestState(model);
        }
    }

    private static bool NoRequestState(MainViewModel model) => !model.RepairReview.HistoryReviewAttempted &&
        !model.RepairReview.ExecutionAttempted && model.RepairReview.HistoricalRecovery == null &&
        model.RepairReview.HistoricalResult == null && model.SystemToolIdentityLastObservation == null;
    private static bool[] Acknowledgments(MainViewModel model) => [model.RepairReview.OperationSelected,
        model.RepairReview.EffectsAcknowledged, model.RepairReview.ElevationAcknowledged, model.RepairReview.ObservationLimitsAcknowledged];
    private static bool AcknowledgmentsClear(MainViewModel model) => Acknowledgments(model).All(value => !value);
    private static bool CommandBound(Button control, string path, System.Windows.Input.ICommand command) =>
        ReferenceEquals(control.Command, command) && control.GetBindingExpression(Button.CommandProperty)?.ParentBinding.Path?.Path == path;
    private static Button FindButton(DependencyObject root, string id) => Descendants<Button>(root).Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void SetSelection(ComboBox control, object? item)
    {
        control.SetCurrentValue(Selector.SelectedItemProperty, item);
        control.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
    }
    private static async Task Idle(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
