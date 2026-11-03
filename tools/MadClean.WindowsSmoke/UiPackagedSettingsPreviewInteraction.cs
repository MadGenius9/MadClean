using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // Passive preview only, after the existing actual package read/search and every capture.
    // Never invoke Open, read packages again, inject rows, or capture loaded package values.
    private static async Task CheckPackagedSettingsPreviewInteraction(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-packaged-settings-preview-interaction";
        void Require(bool condition, string code) { if (!condition) throw new SmokeAssertionException(code); }
        if (model.IsBusy || model.HasPendingPackagedSettingsWork)
        {
            report.Unknown(check, "Earlier work or shell ownership is pending; no package preview interaction started. No query, handoff or package action was requested.");
            return;
        }
        var sourceRows = model.PackagedApplicationRows.ToArray();
        CheckState coverage = model.PackagedApplicationsMessage.StartsWith("Available · ", StringComparison.Ordinal) ? CheckState.Available :
            model.PackagedApplicationsMessage.StartsWith("Partial · ", StringComparison.Ordinal) ? CheckState.Partial : CheckState.Unknown;
        if (coverage == CheckState.Unknown || sourceRows.Length is < 1 or > PackagedApplicationParser.MaximumRecords)
        {
            report.Unknown(check, "The existing real current-user metadata read supplied no accepted bounded nonempty source for preview. No rows, source report, current observation or Settings action was substituted.");
            return;
        }
        model.SelectedPage = 6; await Idle(window);
        var sources = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Installed application sources");
        sources.SelectedIndex = 1; await Idle(window);
        var grid = Descendants<DataGrid>(window).Single(control => AutomationProperties.GetName(control) == "Loaded current-user Windows package registrations");
        var preview = Descendants<Button>(window).Single(control => AutomationProperties.GetName(control) == "Preview selected package family Settings destination");
        var open = Descendants<Button>(window).Single(control => AutomationProperties.GetName(control) == "Open reviewed package family Settings destination");
        var details = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Reviewed package family destination and limitations");
        var status = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetName(control) == "Package Settings handoff status");
        var search = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Search loaded Windows packages by name or publisher");
        var clear = Descendants<Button>(window).Single(control => Equals(control.Content, "Clear package search"));
        Require(ReferenceEquals(grid.ItemsSource, model.PackagedApplicationsView) &&
            grid.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedPackagedApplication) &&
            ReferenceEquals(preview.Command, model.PreviewPackagedSettingsCommand) && ReferenceEquals(open.Command, model.OpenPackagedSettingsCommand) &&
            details.IsReadOnly && details.Text == model.PackagedSettingsPreview &&
            details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.PackagedSettingsPreview) &&
            status.Text == model.PackagedSettingsMessage &&
            status.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.PackagedSettingsMessage) &&
            search.Text.Length == 0 && model.SelectedPackagedApplication == null && grid.SelectedItem == null &&
            !open.IsEnabled && !model.OpenPackagedSettingsCommand.CanExecute(null), "ui-package-settings-preview-bindings");
        var activity = model.Activity.ToArray();
        var desktop = model.InstalledApplications.ToArray();
        var desktopSource = model.InstalledApplicationsView.SourceCollection;
        var packageSource = model.PackagedApplicationsView.SourceCollection;
        string packageMessage = model.PackagedApplicationsMessage, packageAttempt = model.PackagedApplicationsAttempt;
        string overallStatus = model.Status, installedMessage = model.InstalledMessage, baseline = model.ApplicationBaselineMessage;
        string leftovers = model.LeftoverMessage;
        bool Unchanged() => !model.IsBusy && !model.HasPendingPackagedSettingsWork && model.Activity.SequenceEqual(activity) &&
            model.PackagedApplicationRows.Count == sourceRows.Length && sourceRows.Zip(model.PackagedApplicationRows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            ReferenceEquals(model.PackagedApplicationsView.SourceCollection, packageSource) &&
            model.PackagedApplicationsMessage == packageMessage && model.PackagedApplicationsAttempt == packageAttempt &&
            model.InstalledApplications.Count == desktop.Length && desktop.Zip(model.InstalledApplications).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            ReferenceEquals(model.InstalledApplicationsView.SourceCollection, desktopSource) &&
            model.Status == overallStatus && model.InstalledMessage == installedMessage && model.ApplicationBaselineMessage == baseline && model.LeftoverMessage == leftovers;
        async Task InvokePreview()
        {
            preview.BringIntoView(); await Idle(window);
            Require(preview.IsVisible && preview.IsEnabled && model.PreviewPackagedSettingsCommand.CanExecute(null), "ui-package-settings-preview-disabled");
            var peer = UIElementAutomationPeer.CreatePeerForElement(preview) ?? new ButtonAutomationPeer(preview);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new SmokeAssertionException("ui-package-settings-preview-invoke-unavailable");
            invoke.Invoke(); await Idle(window);
        }
        PackagedApplication? supported = null;
        int attempted = 0;
        foreach (var candidate in sourceRows.Take(16))
        {
            attempted++;
            grid.SelectedItem = candidate; await Idle(window);
            Require(ReferenceEquals(model.SelectedPackagedApplication, candidate) && !open.IsEnabled &&
                !model.OpenPackagedSettingsCommand.CanExecute(null) && Unchanged(), "ui-package-settings-selection-must-invalidate");
            await InvokePreview();
            Require(details.Text == model.PackagedSettingsPreview && status.Text == model.PackagedSettingsMessage && Unchanged(), "ui-package-settings-preview-only");
            if (model.OpenPackagedSettingsCommand.CanExecute(null)) { supported = candidate; break; }
        }
        if (supported == null)
        {
            grid.SelectedItem = null; await Idle(window);
            Require(!open.IsEnabled && Unchanged(), "ui-package-settings-unsupported-preview-clear");
            report.Unknown(check, $"actualRows={sourceRows.Length}; previewedCandidates={attempted}; supportedPreview=False. The bounded actual candidates did not yield a current supported family preview. No sample identity, source report, additional read, Settings request or successful compatibility claim was substituted.");
            return;
        }
        string expectedUri = "ms-settings:appsfeatures-app?" + supported.FamilyName;
        Require(open.IsEnabled && details.Text.Contains("Selected registration: " + supported.FullName + "\n", StringComparison.Ordinal) &&
            details.Text.Contains("Family destination: " + supported.FamilyName + "\n", StringComparison.Ordinal) &&
            details.Text.Contains("Exact URI: " + expectedUri + "\n", StringComparison.Ordinal) &&
            details.Text.Contains("coverage: " + coverage, StringComparison.Ordinal) &&
            details.Text.Contains("Original source received UTC:", StringComparison.Ordinal) &&
            details.Text.Contains("not this exact version", StringComparison.Ordinal) &&
            details.Text.Contains("does not guarantee an uninstall control", StringComparison.Ordinal) &&
            details.Text.Contains("No uninstall, reset, repair", StringComparison.Ordinal) &&
            details.Text.Contains("No elevation is requested", StringComparison.Ordinal) && Unchanged(), "ui-package-settings-actual-destination-limits");
        grid.SelectedItem = null; await Idle(window);
        Require(model.SelectedPackagedApplication == null && !open.IsEnabled && !model.OpenPackagedSettingsCommand.CanExecute(null) &&
            details.Text == model.PackagedSettingsPreview && details.Text.StartsWith("No current navigation preview", StringComparison.Ordinal) && Unchanged(),
            "ui-package-settings-clear-invalidates");
        var other = sourceRows.FirstOrDefault(row => !ReferenceEquals(row, supported));
        if (other == null)
        {
            report.Unknown(check, $"actualRows={sourceRows.Length}; actualPreviewAndClear=True; alternateSelectionVerified=False. No second actual row was available to qualify selection replacement. No extra read, Settings handoff or package operation occurred.");
            return;
        }
        grid.SelectedItem = supported; await Idle(window); await InvokePreview();
        Require(open.IsEnabled && model.OpenPackagedSettingsCommand.CanExecute(null) && Unchanged(), "ui-package-settings-repreview");
        grid.SelectedItem = other; await Idle(window);
        Require(ReferenceEquals(model.SelectedPackagedApplication, other) && !open.IsEnabled &&
            !model.OpenPackagedSettingsCommand.CanExecute(null) && details.Text.StartsWith("No current navigation preview", StringComparison.Ordinal) && Unchanged(),
            "ui-package-settings-replacement-invalidates");
        grid.SelectedItem = supported; await Idle(window); await InvokePreview();
        // A real filter change invalidates even when the supported row remains visible.
        search.Text = supported.Name; await Idle(window);
        Require(model.PackagedApplicationsView.Contains(supported) && !open.IsEnabled &&
            !model.OpenPackagedSettingsCommand.CanExecute(null) && details.Text.StartsWith("No current navigation preview", StringComparison.Ordinal) && Unchanged(),
            "ui-package-settings-search-invalidates");
        clear.Command.Execute(null); await Idle(window);
        grid.SelectedItem = null; await Idle(window);
        Require(search.Text.Length == 0 && model.SelectedPackagedApplication == null && !open.IsEnabled &&
            model.PackagedApplicationsView.Cast<object>().Count() == sourceRows.Length && Unchanged(), "ui-package-settings-restored-source");
        report.Pass(check, $"actualRows={sourceRows.Length}; previewedCandidates={attempted}; actualFamilyPreview=True; bareUriAndCoverageVerified=True; clearReplacementAndSearchInvalidate=True; originalRowsAndActivityRetained=True. Actual loaded current-user metadata and the real bound preview button were used after all captures. Open was never invoked: no fresh membership query, Windows Settings request, deployment, uninstall, reset, repair or elevation occurred. No identities, URI, provider text or loaded-data screenshots exported; native handler/page availability and actual handoff remain unverified.");
    }
}
