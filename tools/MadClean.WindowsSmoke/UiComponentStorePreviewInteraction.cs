using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MadClean.App;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // Passive production controls only, after every capture. Confirm and Stop are
    // never invoked: this helper does not start a host, module or servicing check.
    private static async Task CheckComponentStorePreviewInteraction(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-component-store-preview-interaction";
        void Require(bool condition, string code) { if (!condition) throw new SmokeAssertionException(code); }
        bool Quiet() => !model.IsBusy && !model.HasPendingComponentStoreStatus && !model.HasPendingMsiUninstallRequest;
        if (!Quiet())
        {
            report.Unknown(check, "Earlier work or retained ownership prevents passive recorded-status preview interaction. No check, source substitution or servicing operation was requested.");
            return;
        }
        var host = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Main content");
        var page = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Windows Diagnostics"));
        host.SelectedItem = page; await Idle(window);
        Require(model.SelectedPage == host.Items.IndexOf(page), "ui-component-preview-navigation-binding");
        var prepare = Descendants<Button>(window).Single(control => AutomationProperties.GetName(control) == "Prepare explicit recorded component-store status check");
        var confirm = Descendants<Button>(window).Single(control => AutomationProperties.GetName(control) == "Confirm explicit recorded component-store status check");
        var stop = Descendants<Button>(window).Single(control => AutomationProperties.GetName(control) == "Request stop of the owned component-store query pipeline");
        var acknowledgement = Descendants<CheckBox>(window).Single(control => AutomationProperties.GetName(control) == "Acknowledge recorded component-store check scope and Windows log writes");
        var details = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Exact component-store status command, permissions and side effects");
        var history = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Historical component-store pipeline, reported flags and transport observations");
        var status = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetName(control) == "Recorded component-store check status");
        var assessment = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetName(control) == "Current accepted recorded component-store assessment");
        var qualification = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetName(control) == "Recorded component-store check qualification");
        Require(ReferenceEquals(prepare.Command, model.PrepareComponentStoreStatusCommand) &&
            ReferenceEquals(confirm.Command, model.ConfirmComponentStoreStatusCommand) && ReferenceEquals(stop.Command, model.RequestComponentStoreStopCommand) &&
            acknowledgement.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.ComponentStoreAcknowledged) &&
            details.IsReadOnly && history.IsReadOnly && details.Text == model.ComponentStorePreviewDetails && history.Text == model.ComponentStoreLastObservation &&
            details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.ComponentStorePreviewDetails) &&
            history.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.ComponentStoreLastObservation) &&
            status.Text == model.ComponentStoreMessage && assessment.Text == model.ComponentStoreCurrentAssessment &&
            qualification.Text == model.ComponentStoreQualification, "ui-component-preview-control-bindings");
        Require(!model.ComponentStoreAcknowledged && acknowledgement.IsChecked == false &&
            !confirm.IsEnabled && !model.ConfirmComponentStoreStatusCommand.CanExecute(null) &&
            !stop.IsEnabled && !model.RequestComponentStoreStopCommand.CanExecute(null) &&
            model.ComponentStoreCurrentAssessment.StartsWith("Not checked", StringComparison.Ordinal) &&
            model.ComponentStoreLastObservation.StartsWith("No returned observation", StringComparison.Ordinal), "ui-component-preview-initially-unchecked");
        if (!qualification.Text.StartsWith("Confirmation is unavailable until", StringComparison.Ordinal))
        {
            report.Unknown(check, "This passive interaction qualifies the disabled production boundary only; a different qualification state was observed. Confirm and Stop were not invoked, and no recorded-status host or operation was substituted.");
            return;
        }

        var activity = model.Activity.ToArray();
        var disks = model.Disks.ToArray(); var cleanup = model.CleanupRows.ToArray(); var startup = model.StartupRows.ToArray();
        var security = model.SecurityRows.ToArray(); var apps = model.AppRows.ToArray(); var system = model.SystemRows.ToArray();
        var installed = model.InstalledApplications.ToArray(); var packages = model.PackagedApplicationRows.ToArray();
        var events = model.DiagnosticRows.ToArray(); var channels = model.DiagnosticChannelRows.ToArray(); var coverage = model.DiagnosticCoverage.ToArray();
        var wer = model.WerRows.ToArray(); var drivers = model.DeviceDriverRows.ToArray();
        string overallStatus = model.Status, eventMessage = model.DiagnosticsMessage, eventAttempt = model.DiagnosticsAttempt;
        string eventWindow = model.DiagnosticsWindow, werMessage = model.WerMessage, werWindow = model.WerWindow;
        string originalHistory = model.ComponentStoreLastObservation, originalAssessment = model.ComponentStoreCurrentAssessment;
        var range = model.SelectedDiagnosticRange;
        bool Same<T>(T[] original, IEnumerable<T> current) where T : class =>
            original.Length == current.Count() && original.Zip(current).All(pair => ReferenceEquals(pair.First, pair.Second));
        bool Unchanged() => Quiet() && model.Activity.SequenceEqual(activity) && model.Status == overallStatus &&
            Same(disks, model.Disks) && Same(cleanup, model.CleanupRows) && Same(startup, model.StartupRows) &&
            Same(security, model.SecurityRows) && Same(apps, model.AppRows) && Same(system, model.SystemRows) &&
            Same(installed, model.InstalledApplications) && Same(packages, model.PackagedApplicationRows) &&
            Same(events, model.DiagnosticRows) && Same(channels, model.DiagnosticChannelRows) && model.DiagnosticCoverage.SequenceEqual(coverage) &&
            Same(wer, model.WerRows) && Same(drivers, model.DeviceDriverRows) &&
            model.DiagnosticsMessage == eventMessage && model.DiagnosticsAttempt == eventAttempt && model.DiagnosticsWindow == eventWindow &&
            model.WerMessage == werMessage && model.WerWindow == werWindow && ReferenceEquals(range, model.SelectedDiagnosticRange) &&
            model.ComponentStoreLastObservation == originalHistory && model.ComponentStoreCurrentAssessment == originalAssessment &&
            !confirm.IsEnabled && !model.ConfirmComponentStoreStatusCommand.CanExecute(null) &&
            !stop.IsEnabled && !model.RequestComponentStoreStopCommand.CanExecute(null);
        async Task Prepare()
        {
            prepare.BringIntoView(); await Idle(window);
            Require(prepare.IsVisible && prepare.IsEnabled && model.PrepareComponentStoreStatusCommand.CanExecute(null), "ui-component-preview-prepare-disabled");
            var peer = UIElementAutomationPeer.CreatePeerForElement(prepare) ?? new ButtonAutomationPeer(prepare);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new SmokeAssertionException("ui-component-preview-invoke-unavailable");
            invoke.Invoke(); await Idle(window);
            Require(details.Text == model.ComponentStorePreviewDetails && status.Text == model.ComponentStoreMessage && Unchanged(),
                "ui-component-preview-only-no-source-change");
        }
        await Prepare();
        Require(details.Text.Contains(@"Dism\Repair-WindowsImage -Online -CheckHealth -NoRestart -ErrorAction Stop", StringComparison.Ordinal) &&
            details.Text.Contains("Prepared UTC:", StringComparison.Ordinal) && details.Text.Contains("Expires UTC:", StringComparison.Ordinal) &&
            details.Text.Contains("No fresh component scan or repair", StringComparison.Ordinal) &&
            details.Text.Contains("effective administrator rights", StringComparison.Ordinal) &&
            details.Text.Contains("Windows may write DISM logs", StringComparison.Ordinal) &&
            details.Text.Contains("No servicing cancellation, rollback or safe forced termination", StringComparison.Ordinal) &&
            !model.ComponentStoreAcknowledged && acknowledgement.IsChecked == false && model.CanAcknowledgeComponentStoreStatus,
            "ui-component-preview-exact-scope-permissions-effects");
        acknowledgement.BringIntoView(); await Idle(window);
        var togglePeer = UIElementAutomationPeer.CreatePeerForElement(acknowledgement) ?? new CheckBoxAutomationPeer(acknowledgement);
        if (togglePeer.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
            throw new SmokeAssertionException("ui-component-preview-toggle-unavailable");
        Require(acknowledgement.IsVisible && acknowledgement.IsEnabled && toggle.ToggleState == ToggleState.Off,
            "ui-component-preview-acknowledgement-disabled");
        toggle.Toggle(); await Idle(window);
        Require(acknowledgement.IsChecked == true && model.ComponentStoreAcknowledged && toggle.ToggleState == ToggleState.On && Unchanged(),
            "ui-component-preview-acknowledgement-must-not-dispatch");
        await Prepare();
        Require(acknowledgement.IsChecked == false && !model.ComponentStoreAcknowledged && toggle.ToggleState == ToggleState.Off && Unchanged(),
            "ui-component-preview-renewal-resets-acknowledgement");
        acknowledgement.BringIntoView(); await Idle(window);
        toggle.Toggle(); await Idle(window); toggle.Toggle(); await Idle(window);
        Require(acknowledgement.IsChecked == false && !model.ComponentStoreAcknowledged && toggle.ToggleState == ToggleState.Off &&
            history.Text == originalHistory && assessment.Text == originalAssessment && Unchanged(), "ui-component-preview-acknowledgement-restored");
        report.Pass(check, "Actual production Prepare and acknowledgement controls were exercised through their own WPF automation peers. Fixed recorded-flags scope, permissions/log writes/stop limits, acknowledgement reset and the disabled confirmation boundary were observed. Existing activity, source rows, diagnostic window and current/historical assessment remained unchanged, with no pending component owner. Confirm and Stop were never invoked; no source injection, screenshot after data, host, DISM invocation, scan, repair, elevation or restart was requested. Native checks, physical keyboard and enabled-production interaction remain separate validation.");
    }
}
