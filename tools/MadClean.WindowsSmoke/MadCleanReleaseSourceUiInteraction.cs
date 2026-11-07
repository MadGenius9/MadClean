using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // Managed source-choice controls only, after every capture. Never invoke the
    // release check/review commands or substitute release/deployment observations.
    private static async Task CheckMadCleanReleaseSourceConfiguration(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-madclean-source-configuration";
        string initialStatus = model.MadCleanUpdateStatus;
        if (report.Failed || model.IsBusy || model.HasMadCleanReminder || model.HasMadCleanReleaseNotes ||
            model.MadCleanReleaseNotes.Length != 0 ||
            initialStatus is not ("Update checking not configured" or "Not checked" or "Unknown"))
        {
            report.Unknown(check, "Earlier UI work or existing release observations prevented a neutral source-choice interaction. No source, release result or reminder was injected; no release check or browser command was invoked.");
            return;
        }

        var activity = model.Activity.ToArray();
        string initialMessage = model.MadCleanUpdateMessage;
        bool initialReviewEnabled = model.MadCleanReviewCommand.CanExecute(null);
        var host = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Main content");
        var page = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "MadClean updates"));
        host.SelectedItem = page; await Idle(window);
        if (model.SelectedPage != host.Items.IndexOf(page))
            throw new SmokeAssertionException("ui-madclean-source-navigation");

        var official = Descendants<Button>(window).Single(button => ReferenceEquals(button.Command, model.MadCleanUseOfficialSourceCommand));
        var disable = Descendants<Button>(window).Single(button => ReferenceEquals(button.Command, model.MadCleanDisableSourceCommand));
        var deployment = Descendants<Button>(window).Single(button => ReferenceEquals(button.Command, model.MadCleanUseDeploymentSourceCommand));
        var summary = Descendants<TextBlock>(window).Single(control =>
            control.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.MadCleanSourceSummary));
        var details = Descendants<TextBox>(window).Single(control =>
            control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.MadCleanSourceDetails));
        var status = Descendants<TextBlock>(window).Single(control =>
            control.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.MadCleanUpdateStatus));
        if (!details.IsReadOnly || details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Mode != BindingMode.OneWay ||
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(details)) || string.IsNullOrWhiteSpace(AutomationProperties.GetName(summary)))
            throw new SmokeAssertionException("ui-madclean-source-details-binding");
        foreach (var (button, path) in new[]
        {
            (official, nameof(MainViewModel.MadCleanUseOfficialSourceCommand)),
            (disable, nameof(MainViewModel.MadCleanDisableSourceCommand)),
            (deployment, nameof(MainViewModel.MadCleanUseDeploymentSourceCommand))
        })
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            if (button.GetBindingExpression(Button.CommandProperty)?.ParentBinding.Path?.Path != path ||
                string.IsNullOrWhiteSpace(peer.GetName()))
                throw new SmokeAssertionException("ui-madclean-source-command-binding");
        }

        bool QuietBindings() => !model.IsBusy && activity.SequenceEqual(model.Activity) &&
            !model.HasMadCleanReminder && !model.HasMadCleanReleaseNotes && model.MadCleanReleaseNotes.Length == 0 &&
            summary.Text == model.MadCleanSourceSummary && details.Text == model.MadCleanSourceDetails &&
            status.Text == model.MadCleanUpdateStatus && !string.IsNullOrWhiteSpace(summary.Text) &&
            !string.IsNullOrWhiteSpace(details.Text);
        if (!QuietBindings()) throw new SmokeAssertionException("ui-madclean-source-initial-state");

        await InvokeMadCleanSourceChoice(window, official);
        if (!QuietBindings() || model.MadCleanUpdateStatus != "Not checked" ||
            !model.MadCleanSourceDetails.Contains(MadCleanReleaseSource.TrustedRepository, StringComparison.Ordinal) ||
            !model.MadCleanCheckCommand.CanExecute(null) || !model.MadCleanReviewCommand.CanExecute(null))
            throw new SmokeAssertionException("ui-madclean-source-explicit-choice");

        await InvokeMadCleanSourceChoice(window, disable);
        if (!QuietBindings() || model.MadCleanUpdateStatus != "Update checking not configured" ||
            model.MadCleanReviewCommand.CanExecute(null))
            throw new SmokeAssertionException("ui-madclean-source-disabled-choice");

        await InvokeMadCleanSourceChoice(window, deployment);
        if (!QuietBindings() || model.MadCleanUpdateStatus != initialStatus || model.MadCleanUpdateMessage != initialMessage ||
            model.MadCleanReviewCommand.CanExecute(null) != initialReviewEnabled)
            throw new SmokeAssertionException("ui-madclean-source-deployment-restore");

        report.Pass(check, "normalSourceButtonsInvoked=true; actualCommandAndReadOnlyDetailBindings=true; accessibleControlNamesPresent=true; explicitPinnedChoiceNotChecked=true; disabledChoiceNotConfigured=true; initialDeploymentStatusAndMessageRestored=true; reminderAndNotesRemainEmpty=true; activityUnchanged=true; busyRemainedFalse=true; noCheckOrReviewCommandInvoked=true. This qualifies managed source-choice controls only, not network/file instrumentation, release discovery, clearing a previously confirmed reminder, physical keyboard or screen-reader behavior. No deployment/source value, error text, version, screenshot, browser action or provider result was exported or substituted.");
    }

    private static async Task InvokeMadCleanSourceChoice(MainWindow window, Button button)
    {
        button.BringIntoView(); await Idle(window);
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (!button.IsEnabled || button.Command?.CanExecute(null) != true ||
            peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new SmokeAssertionException("ui-madclean-source-invoke");
        invoke.Invoke(); await Idle(window);
    }
}
