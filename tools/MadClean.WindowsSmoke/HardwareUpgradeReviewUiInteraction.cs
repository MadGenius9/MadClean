using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // Actual planning input controls only, after every capture. No hardware or source query,
    // provider result injection, recommendation, purchase or mutation command is invoked.
    private static async Task CheckHardwareUpgradePrerequisites(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-hardware-upgrade-prerequisites";
        bool Neutral() => model.UpgradeReviewArea == null && model.UpgradeReviewBudgetText.Length == 0 &&
            model.UpgradeReviewCurrency.Length == 0 && model.UpgradeReviewRegion.Length == 0 &&
            model.UpgradeReviewUseCase.Length == 0 && model.UpgradeReviewRequirements.Count == 0 &&
            model.UpgradeReviewReasons.Count == 0 && model.UpgradeReviewSummary.StartsWith("Not reviewed", StringComparison.Ordinal) &&
            model.UpgradeReviewSummary.Contains("Compatibility remains Unknown", StringComparison.Ordinal);
        if (report.Failed || model.IsBusy || model.HasPendingHardwareQuery || !Neutral())
        {
            report.Unknown(check, "Earlier work or existing planning inputs prevented a neutral worksheet interaction. No preferences, hardware results or source evidence were replaced, and no query was invoked.");
            return;
        }

        var activity = model.Activity.ToArray();
        var hardware = model.HardwareFacts.ToArray();
        var observation = model.HardwareLastObservation;
        var attempted = model.HardwareAttemptedUtc;
        var received = model.HardwareCompletedUtc;
        var selectedFact = model.SelectedHardwareFact;
        string hardwareMessage = model.HardwareMessage;
        var service = model.ManufacturerServiceRows.ToArray();
        var selectedService = model.SelectedManufacturerServiceFact;
        var serviceCoverage = model.ManufacturerServiceCoverage.ToArray();
        string serviceMessage = model.ManufacturerServiceMessage, serviceTime = model.ManufacturerServiceTime;
        bool Quiet() => !model.IsBusy && !model.HasPendingHardwareQuery && activity.SequenceEqual(model.Activity) &&
            ReferenceEquals(observation, model.HardwareLastObservation) && attempted == model.HardwareAttemptedUtc &&
            received == model.HardwareCompletedUtc && ReferenceEquals(selectedFact, model.SelectedHardwareFact) &&
            hardwareMessage == model.HardwareMessage && hardware.Length == model.HardwareFacts.Count &&
            hardware.Zip(model.HardwareFacts).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            service.Length == model.ManufacturerServiceRows.Count &&
            service.Zip(model.ManufacturerServiceRows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            ReferenceEquals(selectedService, model.SelectedManufacturerServiceFact) && serviceCoverage.SequenceEqual(model.ManufacturerServiceCoverage) &&
            serviceMessage == model.ManufacturerServiceMessage && serviceTime == model.ManufacturerServiceTime;

        var host = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Main content");
        var page = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Hardware Upgrades"));
        host.SelectedItem = page; await Idle(window);
        if (model.SelectedPage != host.Items.IndexOf(page)) throw new SmokeAssertionException("ui-upgrade-review-navigation");
        var area = Descendants<ComboBox>(window).Single(control =>
            control.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.UpgradeReviewArea));
        TextBox Input(string path) => Descendants<TextBox>(window).Single(control =>
            control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == path);
        var budget = Input(nameof(MainViewModel.UpgradeReviewBudgetText));
        var currency = Input(nameof(MainViewModel.UpgradeReviewCurrency));
        var region = Input(nameof(MainViewModel.UpgradeReviewRegion));
        var useCase = Input(nameof(MainViewModel.UpgradeReviewUseCase));
        var review = Descendants<Button>(window).Single(button => ReferenceEquals(button.Command, model.ReviewUpgradePrerequisitesCommand));
        var clear = Descendants<Button>(window).Single(button => ReferenceEquals(button.Command, model.ResetUpgradeReviewCommand));
        var grid = Descendants<DataGrid>(window).Single(control =>
            control.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.UpgradeReviewRequirements));
        var reasons = Descendants<ItemsControl>(window).Single(control =>
            control.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.UpgradeReviewReasons));
        var summary = Descendants<TextBlock>(window).Single(control =>
            control.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.UpgradeReviewSummary));
        if (area.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Mode != BindingMode.TwoWay ||
            !grid.IsReadOnly || string.IsNullOrWhiteSpace(AutomationProperties.GetName(area)) ||
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(grid)) ||
            review.GetBindingExpression(Button.CommandProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.ReviewUpgradePrerequisitesCommand) ||
            clear.GetBindingExpression(Button.CommandProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.ResetUpgradeReviewCommand))
            throw new SmokeAssertionException("ui-upgrade-review-binding");
        foreach (string path in new[] { nameof(HardwareUpgradeReviewRequirement.Title), nameof(HardwareUpgradeReviewRequirement.State),
            nameof(HardwareUpgradeReviewRequirement.Requirement) })
            if (!grid.Columns.OfType<DataGridTextColumn>().Any(column => column.Binding is Binding binding && binding.Path?.Path == path))
                throw new SmokeAssertionException("ui-upgrade-review-requirement-column-binding");
        foreach (var (input, limit) in new[] { (budget, 32), (currency, 3), (region, 2), (useCase, 1024) })
            if (input.IsReadOnly || input.MaxLength != limit || string.IsNullOrWhiteSpace(AutomationProperties.GetName(input)) ||
                input.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Mode != BindingMode.TwoWay ||
                input.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.UpdateSourceTrigger != UpdateSourceTrigger.PropertyChanged)
                throw new SmokeAssertionException("ui-upgrade-review-input-binding");

        bool Bound() => Quiet() && summary.Text == model.UpgradeReviewSummary &&
            ReferenceEquals(grid.ItemsSource, model.UpgradeReviewRequirements) && ReferenceEquals(reasons.ItemsSource, model.UpgradeReviewReasons);
        bool Invalidated() => Bound() && model.UpgradeReviewRequirements.Count == 0 && model.UpgradeReviewReasons.Count == 0 &&
            model.UpgradeReviewSummary.StartsWith("Not reviewed", StringComparison.Ordinal);
        bool UnknownChecklist(params HardwareCompatibilityCheckKind[] required) => Bound() &&
            model.UpgradeReviewSummary.StartsWith("Compatibility remains Unknown", StringComparison.Ordinal) &&
            model.UpgradeReviewRequirements.Count > 0 && model.UpgradeReviewRequirements.All(row => row.State == CheckState.Unknown) &&
            required.All(kind => model.UpgradeReviewRequirements.Any(row => row.Kind == kind)) &&
            model.UpgradeReviewReasons.Any(reason => reason.Contains("Exact-model scope remains unknown", StringComparison.Ordinal));
        if (!Neutral() || !Invalidated() || area.SelectedItem != null || new[] { budget, currency, region, useCase }.Any(input => input.Text.Length != 0))
            throw new SmokeAssertionException("ui-upgrade-review-neutral");

        await InvokeUpgradeReviewButton(window, review);
        if (!Bound() || model.UpgradeReviewRequirements.Count != 0 ||
            !model.UpgradeReviewSummary.StartsWith("Compatibility remains Unknown", StringComparison.Ordinal) ||
            !model.UpgradeReviewReasons.Any(reason => reason.Contains("Choose an upgrade area", StringComparison.Ordinal)))
            throw new SmokeAssertionException("ui-upgrade-review-missing-area");

        area.SetCurrentValue(Selector.SelectedItemProperty, HardwareCompatibilityUpgradeArea.Memory); await Idle(window);
        await SetUpgradeReviewInput(window, budget, "150.50");
        await SetUpgradeReviewInput(window, currency, "usd");
        await SetUpgradeReviewInput(window, region, "us");
        await SetUpgradeReviewInput(window, useCase, "Development");
        if (!Invalidated() || model.UpgradeReviewArea != HardwareCompatibilityUpgradeArea.Memory ||
            model.UpgradeReviewBudgetText != budget.Text || model.UpgradeReviewCurrency != currency.Text ||
            model.UpgradeReviewRegion != region.Text || model.UpgradeReviewUseCase != useCase.Text)
            throw new SmokeAssertionException("ui-upgrade-review-input-propagation");
        await InvokeUpgradeReviewButton(window, review);
        if (!UnknownChecklist(HardwareCompatibilityCheckKind.ManufacturerSpecification,
            HardwareCompatibilityCheckKind.MemoryTypeLimitsAndPopulation, HardwareCompatibilityCheckKind.ReplaceabilityOrSolderedDesign))
            throw new SmokeAssertionException("ui-upgrade-review-memory-unknown");

        // Each real input edit must invalidate a previously reviewed worksheet.
        foreach (var (input, text) in new[] { (budget, "200.00"), (currency, "eur"), (region, "ca"), (useCase, "Editing") })
        {
            await SetUpgradeReviewInput(window, input, text);
            if (!Invalidated()) throw new SmokeAssertionException("ui-upgrade-review-edit-invalidation");
            await InvokeUpgradeReviewButton(window, review);
            if (!UnknownChecklist(HardwareCompatibilityCheckKind.MemoryTypeLimitsAndPopulation))
                throw new SmokeAssertionException("ui-upgrade-review-edit-stays-unknown");
        }
        area.SetCurrentValue(Selector.SelectedItemProperty, HardwareCompatibilityUpgradeArea.Gpu); await Idle(window);
        if (!Invalidated() || model.UpgradeReviewArea != HardwareCompatibilityUpgradeArea.Gpu)
            throw new SmokeAssertionException("ui-upgrade-review-area-invalidation");
        await InvokeUpgradeReviewButton(window, review);
        if (!UnknownChecklist(HardwareCompatibilityCheckKind.PsuCapacityAndConnectors, HardwareCompatibilityCheckKind.CaseAndGpuClearance))
            throw new SmokeAssertionException("ui-upgrade-review-gpu-unknown");

        await SetUpgradeReviewInput(window, budget, "1,500");
        if (!Invalidated()) throw new SmokeAssertionException("ui-upgrade-review-budget-edit-invalidation");
        await InvokeUpgradeReviewButton(window, review);
        if (!UnknownChecklist(HardwareCompatibilityCheckKind.PsuCapacityAndConnectors, HardwareCompatibilityCheckKind.CaseAndGpuClearance) ||
            !model.UpgradeReviewReasons.Any(reason => reason.StartsWith("Enter a positive budget", StringComparison.Ordinal)))
            throw new SmokeAssertionException("ui-upgrade-review-ambiguous-budget");

        await InvokeUpgradeReviewButton(window, clear);
        if (!Neutral() || !Invalidated() || area.SelectedItem != null || new[] { budget, currency, region, useCase }.Any(input => input.Text.Length != 0))
            throw new SmokeAssertionException("ui-upgrade-review-clear");
        report.Pass(check, "actualAreaAndTextInputBindings=true; actualReviewAndClearButtonsInvoked=true; missingAreaRemainsUnknown=true; memoryAndGpuRequirementsRemainUnknown=true; eachInputEditInvalidates=true; ambiguousBudgetRejected=true; clearRestoresBlankInputs=true; currentHardwareAndManufacturerObservationsUnchanged=true; activityUnchanged=true; noHardwareOrSourceCommandInvoked=true. Only fixed benign planning inputs were entered into normal controls; no hardware/provider result was injected. This qualifies the managed worksheet interaction only, not hardware compatibility, physical keyboard/screen-reader behavior, network instrumentation, manufacturer evidence or purchase/installation. No preferences, provider values or screenshots were exported.");
    }

    private static async Task SetUpgradeReviewInput(MainWindow window, TextBox input, string value)
    {
        input.BringIntoView(); await Idle(window);
        var peer = UIElementAutomationPeer.CreatePeerForElement(input) ?? new TextBoxAutomationPeer(input);
        if (!input.IsEnabled || input.IsReadOnly || peer.GetPattern(PatternInterface.Value) is not IValueProvider provider)
            throw new SmokeAssertionException("ui-upgrade-review-value-pattern");
        provider.SetValue(value); await Idle(window);
        if (input.Text != value) throw new SmokeAssertionException("ui-upgrade-review-value-input");
    }

    private static async Task InvokeUpgradeReviewButton(MainWindow window, Button button)
    {
        button.BringIntoView(); await Idle(window);
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (!button.IsEnabled || button.Command?.CanExecute(null) != true || string.IsNullOrWhiteSpace(peer.GetName()) ||
            peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new SmokeAssertionException("ui-upgrade-review-invoke");
        invoke.Invoke(); await Idle(window);
    }
}
