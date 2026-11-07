using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    public static void Run(string output, SmokeReport report)
    {
        bool completed = false;
        MainViewModel? hardwareQueryOwner = null;
        // Managed construction only. Every provider observes the same static
        // identity worker, even if the App dispatcher exits unexpectedly.
        ISystemToolActions identityOwnership = new MadClean.Windows.SystemToolActionsProvider();
        var application = new MadClean.App.App();
        application.InitializeComponent();
        // StartupUri rejects null. Let the real App create its one startup window; qualify the
        // resource assembly because this development harness, rather than MadClean, is the entry assembly.
        application.StartupUri = new Uri("/MadClean;component/MainWindow.xaml", UriKind.Relative);
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        application.DispatcherUnhandledException += (_, args) =>
        {
            string context = args.Exception is SmokeAssertionException assertion ? "Fixed invariant " + assertion.Code : SmokeAssertionException.SafeContext(args.Exception);
            report.Fail("ui-dispatcher", $"WPF raised {context} (0x{args.Exception.HResult:X8}); no complete UI capture is claimed.");
            args.Handled = true;
            application.Shutdown(1);
        };
        application.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                // Startup is raised before WPF performs its default StartupUri action.
                await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window = application.MainWindow as MainWindow ?? throw new SmokeAssertionException("ui-startup-window");
                window.Width = 1420; window.Height = 1000; window.Left = 0; window.Top = 0; window.WindowState = WindowState.Normal;
                using var sizing = new OwnWindowSizing(window);
                sizing.TryResize(1420, 1000, out int wideResizeError);
                await Idle(window);
                if (window.DataContext is not MainViewModel model) throw new SmokeAssertionException("ui-data-context");
                hardwareQueryOwner = model;
                AssertNoInventory(model);
                if (window.ActualWidth < 1160)
                    report.Unknown("ui-wide-size", $"The actual window remains constrained after own-HWND resizing (Win32 code {wideResizeError}, limit messages {sizing.LimitsMessages}); the captured dimensions are actual and wide-layout validation remains inconclusive.");
                else report.Pass("ui-wide-size", "The actual shown application window reaches the wide-layout breakpoint; no display settings, visual reparenting or synthetic layout were used.");
                Capture(window, output, "overview-wide.png", report);
                AssertVisibleControls(window);
                report.Pass("ui-initial-state", "Actual production WPF Overview loaded with unchecked values and zero activity events; no scan command was executed.");

                window.Width = 980; window.Height = 650;
                sizing.TryResize(980, 650, out _);
                await Idle(window);
                AssertVisibleControls(window);
                Capture(window, output, "overview-compact.png", report);
                if (Math.Abs(window.ActualWidth - 980) > 1 || Math.Abs(window.ActualHeight - 650) > 1)
                    report.Unknown("ui-compact-layout", "The actual window did not reach the requested 980x650 compact size. Captured dimensions are actual; compact-size proof remains inconclusive.");
                else report.Pass("ui-compact-layout", "The actual shown window reaches 980x650 within one DIP; main navigation and inspection action have finite visible bounds. Scrolling/DPI/fidelity still require manual checks.");

                window.Width = 1420; window.Height = 1000;
                sizing.TryResize(1420, 1000, out _);
                await Idle(window);
                var primary = Descendants<ListBox>(window).Single(list => AutomationProperties.GetName(list) == "Main navigation");
                var tools = Descendants<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "More tools");
                int[] destinations = [0, 1, 3, 2, 4];
                for (int index = 0; index < destinations.Length; index++)
                {
                    primary.SelectedIndex = index;
                    await Idle(window);
                    if (model.SelectedPage != destinations[index] || model.PrimaryNavigationIndex != index)
                        throw new SmokeAssertionException("ui-primary-route");
                }
                primary.SelectedIndex = 1;
                await Idle(window);
                var cleanupRecheck = Descendants<Button>(window).Single(button => AutomationProperties.GetName(button) == "Recheck selected cleanup file metadata");
                var cleanupCurrent = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Current cleanup file metadata and uncertainty");
                var cleanupStatus = Descendants<TextBlock>(window).Single(block => AutomationProperties.GetName(block) == "Current cleanup file metadata status");
                cleanupCurrent.BringIntoView();
                await Idle(window);
                if (!ReferenceEquals(cleanupRecheck.Command, model.RecheckCleanupFileCommand) || cleanupRecheck.IsEnabled ||
                    !cleanupCurrent.IsReadOnly || cleanupCurrent.Text != model.CleanupFileEvidenceDetails ||
                    cleanupCurrent.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.CleanupFileEvidenceDetails) ||
                    cleanupStatus.Text != model.CleanupFileEvidenceMessage || !cleanupStatus.Text.StartsWith("Not checked", StringComparison.Ordinal))
                    throw new SmokeAssertionException("ui-cleanup-file-evidence-bindings");
                if (window.Content is not FrameworkElement cleanupClient) throw new SmokeAssertionException("ui-cleanup-file-evidence-client");
                foreach (FrameworkElement control in new FrameworkElement[] { cleanupRecheck, cleanupCurrent, cleanupStatus })
                {
                    Point position = control.TranslatePoint(new Point(), cleanupClient);
                    if (!control.IsVisible || !ValidSize(control) || !double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
                        position.X < -1 || position.Y < -1 || position.X + control.ActualWidth > cleanupClient.ActualWidth + 1 ||
                        position.Y + control.ActualHeight > cleanupClient.ActualHeight + 1)
                        throw new SmokeAssertionException("ui-cleanup-file-evidence-bounds");
                }
                AssertNoInventory(model);
                Capture(window, output, "tool-28.png", report);
                report.Pass("ui-cleanup-file-evidence", "Actual initial selected-file command/status/details bindings, disabled-without-selection and in-client bounds passed without file queries or sample data. Populated selection/cancellation and external-setting races remain unverified.");
                primary.SelectedIndex = 4;
                await Idle(window);
                var updates = Descendants<TabControl>(window).Single(control => control.Items.OfType<TabItem>().Any(item => Equals(item.Header, "BIOS / Firmware")));
                updates.SelectedItem = updates.Items.OfType<TabItem>().Single(item => Equals(item.Header, "BIOS / Firmware"));
                await Idle(window);
                AssertSectionTitleContrast(window);
                var historyCheck = Descendants<Button>(window).Single(button => Equals(button.Content, "Check supported official history"));
                var historyReview = Descendants<Button>(window).Single(button => Equals(button.Content, "Review official release notes"));
                if (!ReferenceEquals(historyCheck.Command, model.CheckFirmwareHistoryCommand) ||
                    !ReferenceEquals(historyReview.Command, model.ReviewFirmwareHistorySourceCommand) ||
                    historyCheck.IsEnabled || historyReview.IsEnabled)
                    throw new SmokeAssertionException("ui-firmware-history-bindings");
                AssertNoInventory(model);
                // Scroll the actual live BIOS/Firmware content, without replacing/reparenting it.
                // This capture is a nested subview, not a new top-level tool route.
                historyCheck.BringIntoView();
                await Idle(window);
                if (!historyCheck.IsVisible || !ValidSize(historyCheck) || !historyReview.IsVisible || !ValidSize(historyReview))
                    throw new SmokeAssertionException("ui-firmware-history-layout");
                Capture(window, output, "tool-18.png", report);
                report.Pass("ui-primary-navigation", "All five primary sections select their preserved pages through the ListBox binding. The actual BIOS/Firmware subtab exposes bound, initially disabled history/release-note commands with empty unchecked data; its scrolled client view is captured without queries or browser actions. The resolved Update center title wraps and exceeds4.5:1 against every actual root gradient stop; this is a scoped style check, not an accessibility audit.");

                var links = model.ToolNavigationItems.ToArray();
                if (links.Length is < 7 or > 24 || links.Select(link => link.Page).Distinct().Count() != links.Length)
                    throw new SmokeAssertionException("ui-tool-routes");
                foreach (var link in links)
                {
                    if (link.Page is < 5 or > 63) throw new SmokeAssertionException("ui-tool-destination");
                    tools.SelectedItem = link;
                    await Idle(window);
                    if (model.SelectedPage != link.Page || model.SelectedToolNavigation?.Page != link.Page || primary.SelectedIndex != -1)
                        throw new SmokeAssertionException("ui-tool-route");
                    if (link.Page == 6)
                    {
                        var verify = Descendants<Button>(window).Single(button => Equals(button.Content, "Verify selected MSI metadata"));
                        if (!ReferenceEquals(verify.Command, model.VerifyMsiRegistrationCommand) || verify.IsEnabled ||
                            !verify.IsVisible || !ValidSize(verify))
                            throw new SmokeAssertionException("ui-msi-command-binding");
                        AssertNoInventory(model);
                        var title = Descendants<TextBlock>(window).Single(block => block.IsVisible && block.Text == "Windows Installer registration evidence");
                        var details = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Windows Installer source, restrictions and verification limits");
                        // Bringing only the heading into view can leave the rest of this panel
                        // below the viewport. Scroll the actual trailing detail control instead.
                        details.BringIntoView();
                        await Idle(window);
                        if (window.Content is not FrameworkElement client || !title.IsVisible || !ValidSize(title))
                            throw new SmokeAssertionException("ui-msi-panel-layout");
                        Point origin = title.TranslatePoint(new Point(), client);
                        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || origin.X < -1 || origin.Y < -1 ||
                            origin.X + title.ActualWidth > client.ActualWidth + 1 || origin.Y + title.ActualHeight > client.ActualHeight + 1)
                            throw new SmokeAssertionException("ui-msi-panel-bounds");
                        var rows = Descendants<DataGrid>(window).Single(grid => AutomationProperties.GetName(grid) == "Selected Windows Installer context and reported metadata");
                        if (!ReferenceEquals(rows.ItemsSource, model.MsiRegistrationRows) || rows.Items.Count != 0)
                            throw new SmokeAssertionException("ui-msi-rows-binding");
                        foreach (FrameworkElement control in new FrameworkElement[] { rows, details })
                        {
                            Point position = control.TranslatePoint(new Point(), client);
                            if (!control.IsVisible || !ValidSize(control) || !double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
                                position.X < -1 || position.Y < -1 || position.X + control.ActualWidth > client.ActualWidth + 1 ||
                                position.Y + control.ActualHeight > client.ActualHeight + 1)
                                throw new SmokeAssertionException("ui-msi-panel-body-bounds");
                        }
                        // Actual scrolled subview, not a new navigation route or synthetic data.
                        Capture(window, output, "tool-20.png", report);
                        var sources = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Installed application sources");
                        if (sources.SelectedIndex != 0 || sources.Items.Count != 2 ||
                            sources.Items[0] is not TabItem { Header: "Desktop registrations" } || sources.Items[1] is not TabItem { Header: "Windows packages" })
                            throw new SmokeAssertionException("ui-package-source-tabs");
                        sources.SelectedIndex = 1;
                        await Idle(window);
                        AssertNoInventory(model);
                        var readPackages = Descendants<Button>(window).Single(button => Equals(button.Content, "Read Windows packages"));
                        if (!ReferenceEquals(readPackages.Command, model.ReadPackagedApplicationsCommand) || !readPackages.IsEnabled ||
                            !readPackages.IsVisible || !ValidSize(readPackages))
                            throw new SmokeAssertionException("ui-package-command-binding");
                        var packages = Descendants<DataGrid>(window).Single(grid => AutomationProperties.GetName(grid) == "Loaded current-user Windows package registrations");
                        if (!ReferenceEquals(packages.ItemsSource, model.PackagedApplicationsView) || packages.Items.Count != 0)
                            throw new SmokeAssertionException("ui-package-rows-binding");
                        Capture(window, output, "tool-21.png", report);
                        // The later actual-ARP search proof uses the desktop textbox. Restore
                        // the real nested source before any data load; never capture loaded data.
                        sources.SelectedIndex = 0;
                        await Idle(window);
                        if (sources.SelectedIndex != 0) throw new SmokeAssertionException("ui-package-source-restore");
                    }
                    if (link.Page == 8) Capture(window, output, "tool-19.png", report);
                    if (link.Page == 13)
                    {
                        var evidenceButton = Descendants<Button>(window).Single(button => Equals(button.Content, "Inspect selected file evidence"));
                        var evidenceDetails = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Selected shortcut file identity, target and uncertainty");
                        var daclSummary = Descendants<TextBlock>(window).Single(block => AutomationProperties.GetName(block) == "Recorded shortcut access-control state and uncertainty");
                        evidenceDetails.BringIntoView();
                        await Idle(window);
                        if (!ReferenceEquals(evidenceButton.Command, model.InspectShortcutFileEvidenceCommand) || evidenceButton.IsEnabled ||
                            !evidenceDetails.IsReadOnly || evidenceDetails.Text != model.ShortcutFileEvidenceDetails ||
                            evidenceDetails.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.ShortcutFileEvidenceDetails) ||
                            !model.ShortcutFileEvidenceMessage.StartsWith("Not checked", StringComparison.Ordinal) ||
                            daclSummary.Text != model.ShortcutDaclSummary ||
                            daclSummary.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.ShortcutDaclSummary) ||
                            !daclSummary.Text.StartsWith("Access controls: Not checked", StringComparison.Ordinal))
                            throw new SmokeAssertionException("ui-shortcut-file-evidence-binding");
                        if (window.Content is not FrameworkElement shortcutClient) throw new SmokeAssertionException("ui-shortcut-file-evidence-client");
                        foreach (var element in new FrameworkElement[] { evidenceButton, evidenceDetails, daclSummary })
                        {
                            Point point = element.TranslatePoint(new Point(), shortcutClient);
                            if (!element.IsVisible || !ValidSize(element) || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < -1 || point.Y < -1 ||
                                point.X + element.ActualWidth > shortcutClient.ActualWidth + 1 || point.Y + element.ActualHeight > shortcutClient.ActualHeight + 1)
                                throw new SmokeAssertionException("ui-shortcut-file-evidence-bounds");
                        }
                        AssertNoInventory(model);
                        Capture(window, output, "tool-26.png", report);
                        report.Pass("ui-shortcut-file-evidence", "Actual unchecked selected-file evidence panel, disabled command binding and readable in-view controls verified without inventory or sample data. Populated selection/cancellation lifecycle is not asserted by this initial panel check.");
                    }
                    if (link.Page == 16)
                    {
                        var identitySummary = Descendants<TextBlock>(window).Single(block => AutomationProperties.GetName(block) == "Microsoft tool identity observation summary");
                        identitySummary.BringIntoView();
                        await Idle(window);
                        if (!identitySummary.IsVisible || !ValidSize(identitySummary) || identitySummary.Text != model.SystemToolIdentitySummary ||
                            identitySummary.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.SystemToolIdentitySummary) ||
                            !identitySummary.Text.Contains("not checked", StringComparison.OrdinalIgnoreCase))
                            throw new SmokeAssertionException("ui-system-tool-identity-summary-binding");
                        if (window.Content is not FrameworkElement client) throw new SmokeAssertionException("ui-system-tool-identity-summary-client");
                        Point position = identitySummary.TranslatePoint(new Point(), client);
                        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < -1 || position.Y < -1 ||
                            position.X + identitySummary.ActualWidth > client.ActualWidth + 1 ||
                            position.Y + identitySummary.ActualHeight > client.ActualHeight + 1)
                            throw new SmokeAssertionException("ui-system-tool-identity-summary-bounds");
                        var sfcButton = Descendants<Button>(window).Single(button => AutomationProperties.GetAutomationId(button) == "SfcVerificationPreviewButton");
                        var sfcDetails = Descendants<TextBox>(window).Single(box => AutomationProperties.GetAutomationId(box) == "SfcVerificationPreviewDetails");
                        var sfcMessage = Descendants<TextBlock>(window).Single(block => AutomationProperties.GetAutomationId(block) == "SfcVerificationPreviewMessage");
                        if (!ReferenceEquals(sfcButton.Command, model.PrepareSfcVerificationPreviewCommand) || sfcButton.IsEnabled ||
                            model.PrepareSfcVerificationPreviewCommand.CanExecute(null) || !sfcDetails.IsReadOnly ||
                            sfcDetails.Text != model.SfcVerificationPreviewDetails || sfcMessage.Text != model.SfcVerificationPreviewMessage ||
                            sfcDetails.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.SfcVerificationPreviewDetails) ||
                            sfcMessage.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.SfcVerificationPreviewMessage) ||
                            !sfcMessage.Text.StartsWith("Not prepared", StringComparison.Ordinal))
                            throw new SmokeAssertionException("ui-sfc-preview-unchecked-binding");
                        // A fixed operation selection without accepted identity must
                        // never enable this review action; no identity query is run.
                        model.SelectedSystemTool = SystemToolActionsCatalog.Find(SystemToolOperation.SfcVerifyOnly);
                        await Idle(window);
                        if (model.PrepareSfcVerificationPreviewCommand.CanExecute(null) || sfcButton.IsEnabled)
                            throw new SmokeAssertionException("ui-sfc-preview-missing-identity");
                        model.SelectedSystemTool = null;
                        sfcMessage.BringIntoView();
                        await Idle(window);
                        foreach (FrameworkElement element in new FrameworkElement[] { sfcButton, sfcDetails, sfcMessage })
                        {
                            Point location = element.TranslatePoint(new Point(), client);
                            if (!element.IsVisible || !ValidSize(element) || !double.IsFinite(location.X) || !double.IsFinite(location.Y) ||
                                location.X < -1 || location.Y < -1 || location.X + element.ActualWidth > client.ActualWidth + 1 ||
                                location.Y + element.ActualHeight > client.ActualHeight + 1)
                                throw new SmokeAssertionException("ui-sfc-preview-bounds");
                        }
                        AssertNoInventory(model);
                    }
                    if (link.Page == 14)
                    {
                        var evidenceGrid = Descendants<DataGrid>(window).Single(grid => AutomationProperties.GetName(grid) == "Local inspection evidence and source states");
                        var evidenceDetails = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Inspection source, time, uncertainty and impact");
                        evidenceDetails.BringIntoView();
                        await Idle(window);
                        if (!ReferenceEquals(evidenceGrid.ItemsSource, model.InspectionEvidenceRows) || evidenceGrid.SelectedItem != null ||
                            model.SelectedInspectionEvidence != null || !evidenceDetails.IsReadOnly || evidenceDetails.Text != model.InspectionEvidenceDetails ||
                            evidenceDetails.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.InspectionEvidenceDetails))
                            throw new SmokeAssertionException("ui-inspection-evidence-binding");
                        if (window.Content is not FrameworkElement evidenceClient) throw new SmokeAssertionException("ui-inspection-evidence-client");
                        foreach (var element in new FrameworkElement[] { evidenceGrid, evidenceDetails })
                        {
                            Point point = element.TranslatePoint(new Point(), evidenceClient);
                            if (!element.IsVisible || !ValidSize(element) || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < -1 || point.Y < -1 ||
                                point.X + element.ActualWidth > evidenceClient.ActualWidth + 1 || point.Y + element.ActualHeight > evidenceClient.ActualHeight + 1)
                                throw new SmokeAssertionException("ui-inspection-evidence-bounds");
                        }
                        AssertNoInventory(model);
                        Capture(window, output, "tool-25.png", report);
                        var counts = Descendants<TextBlock>(window).Single(block => AutomationProperties.GetName(block) == "WER current-store counts and uncertainty");
                        counts.BringIntoView();
                        await Idle(window);
                        if (!counts.IsVisible || !ValidSize(counts) || counts.Text != model.WerCountSummary ||
                            counts.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.WerCountSummary) ||
                            !counts.Text.Contains("not checked", StringComparison.OrdinalIgnoreCase) ||
                            model.WerRows.Count != 0 || model.WerCoverage.Count != 0)
                            throw new SmokeAssertionException("ui-wer-count-summary-binding");
                        if (window.Content is not FrameworkElement client) throw new SmokeAssertionException("ui-wer-count-client");
                        Point position = counts.TranslatePoint(new Point(), client);
                        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < -1 || position.Y < -1 ||
                            position.X + counts.ActualWidth > client.ActualWidth + 1 || position.Y + counts.ActualHeight > client.ActualHeight + 1)
                            throw new SmokeAssertionException("ui-wer-count-summary-bounds");
                        AssertNoInventory(model);
                    }
                    if (link.Page >= 11) Capture(window, output, $"tool-{link.Page:D2}.png", report);
                    if (link.Page == 12)
                    {
                        var retained = Descendants<ListBox>(window).Single(list => AutomationProperties.GetName(list) == "Duplicate snapshot copy to retain");
                        var candidate = Descendants<ListBox>(window).Single(list => AutomationProperties.GetName(list) == "Duplicate snapshot candidate to review");
                        var preview = Descendants<Button>(window).Single(button => Equals(button.Content, "Preview selected pair"));
                        var clearPair = Descendants<Button>(window).Single(button => Equals(button.Content, "Clear pair selection"));
                        var pairDetails = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Duplicate snapshot pair preview and limits");
                        if (!ReferenceEquals(retained.ItemsSource, model.DuplicateRows) || !ReferenceEquals(candidate.ItemsSource, model.DuplicateRows) ||
                            retained.Items.Count != 0 || candidate.Items.Count != 0 || retained.SelectedItem != null || candidate.SelectedItem != null ||
                            !ReferenceEquals(preview.Command, model.PreviewDuplicatePairCommand) || !ReferenceEquals(clearPair.Command, model.ClearDuplicatePairCommand) ||
                            preview.IsEnabled || clearPair.IsEnabled || !pairDetails.IsReadOnly || pairDetails.Text != model.DuplicatePairDetails)
                            throw new SmokeAssertionException("ui-duplicate-pair-initial-bindings");
                        pairDetails.BringIntoView();
                        await Idle(window);
                        if (window.Content is not FrameworkElement client) throw new SmokeAssertionException("ui-duplicate-pair-client");
                        foreach (FrameworkElement control in new FrameworkElement[] { retained, candidate, pairDetails })
                        {
                            Point position = control.TranslatePoint(new Point(), client);
                            if (!control.IsVisible || !ValidSize(control) || !double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
                                position.X < -1 || position.Y < -1 || position.X + control.ActualWidth > client.ActualWidth + 1 ||
                                position.Y + control.ActualHeight > client.ActualHeight + 1)
                                throw new SmokeAssertionException("ui-duplicate-pair-controls-bounds");
                        }
                        AssertNoInventory(model);
                        Capture(window, output, "tool-22.png", report);
                        var freshPair = Descendants<Button>(window).Single(button => Equals(button.Content, "Recheck selected pair"));
                        var freshDetails = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Current duplicate pair comparison, time and uncertainty");
                        var exclusions = Descendants<TextBlock>(window).Single(block => AutomationProperties.GetName(block) == "Current duplicate pair exclusion coverage and ownership uncertainty");
                        freshDetails.BringIntoView();
                        await Idle(window);
                        if (!ReferenceEquals(freshPair.Command, model.RecheckDuplicatePairCommand) || freshPair.IsEnabled ||
                            !freshDetails.IsReadOnly || freshDetails.Text != model.DuplicatePairRecheckDetails ||
                            freshDetails.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.DuplicatePairRecheckDetails) ||
                            !model.DuplicatePairRecheckMessage.StartsWith("Not checked", StringComparison.Ordinal) || exclusions.Text != model.DuplicatePairExclusionSummary ||
                            exclusions.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.DuplicatePairExclusionSummary) ||
                            !exclusions.Text.StartsWith("Current exclusions: Not checked", StringComparison.Ordinal))
                            throw new SmokeAssertionException("ui-duplicate-pair-recheck-binding");
                        foreach (FrameworkElement control in new FrameworkElement[] { freshPair, freshDetails, exclusions })
                        {
                            Point position = control.TranslatePoint(new Point(), client);
                            if (!control.IsVisible || !ValidSize(control) || !double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
                                position.X < -1 || position.Y < -1 || position.X + control.ActualWidth > client.ActualWidth + 1 ||
                                position.Y + control.ActualHeight > client.ActualHeight + 1)
                                throw new SmokeAssertionException("ui-duplicate-pair-recheck-bounds");
                        }
                        AssertNoInventory(model);
                        Capture(window, output, "tool-27.png", report);
                        report.Pass("ui-duplicate-pair-recheck", "Actual initial fresh-pair command/details bindings, disabled-without-selection state and in-client bounds verified without content reads or sample data. Populated pair-selection/cancellation lifecycle remains unverified.");
                    }
                    if (link.Page == 13)
                    {
                        var recheck = Descendants<Button>(window).Single(button => Equals(button.Content, "Recheck selected shortcut"));
                        var recheckDetails = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Selected shortcut recheck evidence and limits");
                        if (!ReferenceEquals(recheck.Command, model.RecheckSelectedShortcutCommand) || recheck.IsEnabled ||
                            !recheckDetails.IsReadOnly || recheckDetails.Text != model.ShortcutRecheckDetails)
                            throw new SmokeAssertionException("ui-shortcut-recheck-initial-bindings");
                        recheckDetails.BringIntoView();
                        await Idle(window);
                        if (window.Content is not FrameworkElement client) throw new SmokeAssertionException("ui-shortcut-recheck-client");
                        foreach (FrameworkElement control in new FrameworkElement[] { recheck, recheckDetails })
                        {
                            Point position = control.TranslatePoint(new Point(), client);
                            if (!control.IsVisible || !ValidSize(control) || !double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
                                position.X < -1 || position.Y < -1 || position.X + control.ActualWidth > client.ActualWidth + 1 ||
                                position.Y + control.ActualHeight > client.ActualHeight + 1)
                                throw new SmokeAssertionException("ui-shortcut-recheck-controls-bounds");
                        }
                        AssertNoInventory(model);
                        Capture(window, output, "tool-23.png", report);
                    }
                    AssertNoInventory(model);
                }
                report.Pass("ui-tool-navigation", $"All {links.Length} secondary tools select their preserved pages through the actual ComboBox binding without starting inventory. Actual unchecked MSI and current-user package subviews were captured with production command/empty-row bindings; the desktop source was restored without queries before later search validation. The new SFC preview has actual empty/read-only bindings, visible controls and remains disabled after selecting SFC without accepted identity; populated preview/expiry behavior remains separately unverified.");

                model.SelectedPage = 0;
                await Idle(window);
                if (primary.SelectedIndex != 0 || tools.SelectedItem != null) throw new SmokeAssertionException("ui-selector-sync");
                tools.IsDropDownOpen = true;
                await Idle(window);
                if (tools.Template.FindName("PART_Popup", tools) is not Popup { IsOpen: true, Child: { IsVisible: true } })
                    throw new SmokeAssertionException("ui-tool-popup");
                foreach (var link in links)
                {
                    if (tools.ItemContainerGenerator.ContainerFromItem(link) is not ComboBoxItem item ||
                        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(item) is not { } peer ||
                        !string.Equals(peer.GetName(), link.Title, StringComparison.Ordinal))
                        throw new SmokeAssertionException("ui-tool-popup-accessible-name");
                }
                tools.IsDropDownOpen = false;
                AssertNoInventory(model);
                report.Pass("ui-tool-popup", "The real More tools popup opens; every generated tool item exposes its actual title through a WPF automation peer, and programmatic page changes synchronize both selectors. Physical keyboard and screen-reader interaction are not asserted.");
                // All captures and unchecked/navigation assertions are complete. This separate
                // explicit CI metadata workflow must not produce any later screenshots.
                await CheckMadCleanReleaseSourceConfiguration(window, model, report);
                await CheckInstalledAppSearch(window, model, report);
                await CheckPackagedAppSearch(window, model, report);
                if (!report.Failed && !model.IsBusy && !model.HasPendingPackagedSettingsWork)
                    await CheckPackagedSettingsPreviewInteraction(window, model, report);
                else
                    report.Unknown("ui-packaged-settings-preview-interaction", "Earlier GUI preparation or pending ownership prevented passive package preview interaction. No metadata read, source injection, Settings handoff or package operation was substituted.");
                await CheckInspectionEvidence(window, model, report);
                if (!report.Failed) await CheckCleanupFileEvidenceInteraction(window, model, report);
                else
                {
                    report.Unknown("ui-cleanup-file-evidence-interaction", "A prior GUI invariant failed; no owned temporary-file interaction started.");
                    report.Unknown("ui-cleanup-file-evidence-selection-race", "A prior GUI invariant failed; no selection-race proof is claimed.");
                    report.Unknown("ui-cleanup-file-evidence-cancellation", "A prior GUI invariant failed; no cancellation proof is claimed.");
                }
                if (!report.HasCheck("ui-cleanup-selection-review-interaction"))
                    report.Unknown("ui-cleanup-selection-review-interaction", "Earlier GUI preparation or source checks did not reach the real worksheet interaction; no selection, current metadata or source invalidation proof is claimed.");
                if (!report.Failed && !model.IsBusy)
                    await CheckDiagnosticChannelCoverageInteraction(window, model, report);
                else
                    report.Unknown("ui-diagnostic-channel-coverage-interaction", "A prior GUI failure or pending query prevents a separate Event Log interaction; no channel coverage proof claimed.");
                if (!report.Failed && !model.IsBusy && DiagnosticChannelInteractionSettled)
                    await CheckComponentStorePreviewInteraction(window, model, report);
                else
                    report.Unknown("ui-component-store-preview-interaction", "Earlier GUI preparation or pending work prevented passive recorded-status preview interaction. No host, check or servicing operation was requested.");
                if (!report.Failed && !model.IsBusy && DiagnosticChannelInteractionSettled)
                    await CheckMsiActionBindingInteraction(window, model, report);
                else
                    report.Unknown("ui-msi-action-binding-interaction", "A previous GUI query or invariant is unsettled; no selected MSI interaction started.");
                if (!report.Failed && !model.IsBusy && DiagnosticChannelInteractionSettled && MsiActionBindingInteractionSettled)
                    await CheckDeviceDriverInventoryInteraction(window, model, report);
                else
                    report.Unknown("ui-device-driver-inventory-interaction", "A previous GUI query or invariant is unsettled; no explicit device-property interaction started.");
                if (!report.Failed && !model.IsBusy && DiagnosticChannelInteractionSettled &&
                    MsiActionBindingInteractionSettled && DeviceDriverInteractionSettled)
                    await CheckSfcPreviewInteraction(window, model, report);
                else
                    report.Unknown("ui-sfc-preview-interaction", "Earlier GUI work or native ownership is unsettled; no identity or SFC preview workflow was started. No command execution or populated-preview proof is claimed.");
                if (!report.Failed && !model.IsBusy && DiagnosticChannelInteractionSettled &&
                    MsiActionBindingInteractionSettled && DeviceDriverInteractionSettled)
                    await CheckHardwareInventoryInteraction(window, model, report);
                else
                    report.Unknown("ui-hardware-inventory-interaction", "Earlier GUI work or native ownership is unsettled; no hardware read, populated selection or refresh workflow was started. No compatibility result is inferred.");
                completed = true;
            }
            catch (SmokeAssertionException ex)
            {
                report.Fail("ui-capture", $"Fixed invariant {ex.Code} failed (0x{ex.HResult:X8}); inspect the source and available captures rather than assuming visual correctness.");
            }
            catch (Exception ex)
            {
                report.Fail("ui-capture", $"The production WPF smoke check raised {SmokeAssertionException.SafeContext(ex)} (0x{ex.HResult:X8}); no arbitrary exception message/payload is included.");
            }
            finally
            {
                if (!report.HasCheck("ui-packaged-settings-preview-interaction"))
                    report.Unknown("ui-packaged-settings-preview-interaction", "Earlier GUI preparation or an invariant failure did not complete the passive package preview interaction. No successful preview, extra query or Settings handoff is claimed.");
                if (!report.HasCheck("ui-component-store-preview-interaction"))
                    report.Unknown("ui-component-store-preview-interaction", "Earlier GUI preparation or an invariant failure did not complete passive recorded-status preview interaction. No host or Windows servicing operation was requested.");
                if (!report.HasCheck("ui-sfc-preview-interaction"))
                    report.Unknown("ui-sfc-preview-interaction", "Earlier GUI preparation or an invariant failure did not complete the identity-bound SFC preview workflow. No execution, populated preview or expiry proof is claimed.");
                if (!report.HasCheck("ui-hardware-inventory-interaction"))
                    report.Unknown("ui-hardware-inventory-interaction", "Earlier GUI preparation or an invariant failure did not complete the real hardware inventory interaction. No populated facts, current selection or refresh proof is claimed.");
                window?.Close();
                application.Shutdown(report.Failed ? 1 : 0);
            }
        };
        try { application.Run(); }
        finally
        {
            if (identityOwnership.HasPendingIdentityWork)
                report.Fail("ui-identity-owner-teardown", "The App dispatcher ended while an actual shared identity worker remained pending. The CLR stays alive until that owner returns; no late preview qualification, force-close or native retry is claimed.");
            if (hardwareQueryOwner?.HasPendingHardwareQuery == true)
                report.Fail("ui-hardware-owner-teardown", "The App dispatcher ended while the actual hardware query task remained pending. The CLR stays alive until the owned query/stream task returns; no late UI qualification or CIM-service completion is inferred.");
            // Do not use the departed dispatcher, UI busy latch or failed layout.
            // Observe only actual worker tasks/admission after dispatcher exit.
            while (identityOwnership.HasPendingIdentityWork || hardwareQueryOwner?.HasPendingHardwareQuery == true)
                System.Threading.Thread.Sleep(100);
        }
        if (!completed && !report.Failed) report.Fail("ui-completion", "The application exited before all scoped UI checks completed.");
    }

    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task CheckInstalledAppSearch(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-installed-app-search";
        model.SelectedPage = 6;
        await Idle(window);
        var search = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Search loaded installed apps by name or publisher");
        var clear = Descendants<Button>(window).Single(button => Equals(button.Content, "Clear search"));
        var rows = Descendants<DataGrid>(window).Single(grid => AutomationProperties.GetName(grid) == "Loaded installed-app registrations");
        if (!ReferenceEquals(rows.ItemsSource, model.InstalledApplicationsView) ||
            !ReferenceEquals(model.InstalledApplicationsView.SourceCollection, model.InstalledApplications) ||
            !ReferenceEquals(clear.Command, model.ClearInstalledAppSearchCommand) || clear.IsEnabled ||
            search.MaxLength != InstalledApplicationSearch.MaximumQueryCharacters || search.Text.Length != 0 ||
            model.InstalledApplications.Count != 0 || !model.RefreshInstalledApplicationsCommand.CanExecute(null))
            throw new SmokeAssertionException("ui-installed-search-initial-bindings");
        model.RefreshInstalledApplicationsCommand.Execute(null);
        var watch = Stopwatch.StartNew();
        while (model.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(45))
        { await Task.Delay(50); await Idle(window); }
        if (model.IsBusy)
        {
            model.Cancel();
            var cancellationWait = Stopwatch.StartNew();
            while (model.IsBusy && cancellationWait.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(50); await Idle(window); }
            report.Unknown(check, "The explicit CI registration refresh did not finish its bounded UI wait. Cancellation was requested; a native registry read may still finish. No search proof, forced abort or data capture is claimed.");
            return;
        }
        var unfiltered = model.InstalledApplications.ToArray();
        var candidate = unfiltered.FirstOrDefault(app =>
        {
            var query = InstalledApplicationSearch.Create(app.Name);
            return query.IsValid && !query.IsEmpty;
        });
        if (candidate == null)
        {
            report.Unknown(check, "The explicit CI registration refresh supplied no usable actual name within the literal search bounds. Search interaction remains unverified; no app absence or sample-data conclusion was substituted.");
            return;
        }
        int activityCount = model.Activity.Count;
        string inventoryMessage = model.InstalledMessage, baselineMessage = model.ApplicationBaselineMessage;
        search.Text = candidate.Name;
        await Idle(window);
        var positiveFilter = InstalledApplicationSearch.Create(candidate.Name);
        int matches = unfiltered.Count(positiveFilter.Matches);
        if (model.InstalledAppSearchText != candidate.Name || !clear.IsEnabled ||
            !model.InstalledApplicationsView.Contains(candidate) || model.InstalledApplicationsView.Cast<object>().Count() != matches)
            throw new SmokeAssertionException("ui-installed-search-positive-binding");
        rows.SelectedItem = candidate;
        await Idle(window);
        if (!ReferenceEquals(model.SelectedInstalledApplication, candidate))
            throw new SmokeAssertionException("ui-installed-search-selected-row-binding");
        string absentQuery = "MadClean-search-fixture-" + Guid.NewGuid().ToString("N");
        var negativeFilter = InstalledApplicationSearch.Create(absentQuery);
        if (unfiltered.Any(negativeFilter.Matches))
            throw new SmokeAssertionException("ui-installed-search-negative-fixture-not-distinct");
        search.Text = absentQuery;
        await Idle(window);
        if (model.InstalledAppSearchText != absentQuery || !model.InstalledApplicationsView.IsEmpty || rows.Items.Count != 0 ||
            model.SelectedInstalledApplication != null || model.PreviewInstalledAppPlanCommand.CanExecute(null) ||
            model.ReviewInstalledAppHandoffCommand.CanExecute(null) || model.VerifyMsiRegistrationCommand.CanExecute(null) ||
            !model.InstalledApplications.SequenceEqual(unfiltered) || model.InstalledMessage != inventoryMessage ||
            model.ApplicationBaselineMessage != baselineMessage || model.Activity.Count != activityCount || model.IsBusy)
            throw new SmokeAssertionException("ui-installed-search-hidden-selection-or-snapshot");
        clear.Command.Execute(null);
        await Idle(window);
        if (search.Text.Length != 0 || model.InstalledAppSearchText.Length != 0 || clear.IsEnabled ||
            model.InstalledApplicationsView.Cast<object>().Count() != unfiltered.Length ||
            !model.InstalledApplications.SequenceEqual(unfiltered) || model.InstalledMessage != inventoryMessage ||
            model.ApplicationBaselineMessage != baselineMessage || model.Activity.Count != activityCount || model.IsBusy)
            throw new SmokeAssertionException("ui-installed-search-clear-or-snapshot");
        report.Pass(check, $"Actual loadedRegistrationCount={unfiltered.Length}; positiveMatchCount={matches}; textboxBindingVerified=true; clearRestoresLoadedRows=true; hiddenSelectionCleared=true; fullInventoryAndBaselineUnchanged=true; noExtraActivity=true. One explicit bounded CI ARP refresh supplied real metadata; filtering started no further query/action. No screenshots were taken after metadata loading, and no names, publishers, query text, identities or provider output are exported. Coverage remains the original inventory state; no-match is not absence proof.");
    }

    private static void AssertNoInventory(MainViewModel model)
    {
        if (model.InspectionEvidenceRows.Count != 5 || model.SelectedInspectionEvidence != null ||
            model.InspectionEvidenceRows.Select(row => row.Area).Distinct().Count() != 5 ||
            !model.InspectionEvidenceRows.Select(row => row.Area).Order().SequenceEqual(Enum.GetValues<MadClean.Core.DiagnosticEvidenceArea>().Order()) ||
            model.InspectionEvidenceRows.Any(row => row.State != MadClean.Core.CheckState.Unknown || row.AttemptedUtc != null || row.CompletedUtc != null ||
                !row.Observation.StartsWith("Not checked", StringComparison.Ordinal)))
            throw new SmokeAssertionException("ui-inspection-evidence-unchecked");
        if (model.DeviceDriverRows.Count != 0 || model.SelectedDeviceDriver != null || model.HasPendingDeviceDriverRead ||
            !model.DeviceDriverMessage.StartsWith("Not checked", StringComparison.Ordinal) ||
            !model.DeviceDriverQueryDetails.StartsWith("Not checked", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-device-driver-automatic-query");
        if (model.IsBusy || model.Activity.Count != 0 || model.Disks.Count != 0 || model.CleanupRows.Count != 0 || model.StartupRows.Count != 0)
            throw new SmokeAssertionException("ui-navigation-provider-activity");
        if (model.FirmwareHistoryRows.Count != 0 || model.FirmwareHistoryCoverage.Count != 0 || model.SelectedFirmwareRelease != null ||
            model.FirmwareIdentityRows.Count != 0 || model.FirmwareOffers.Count != 0 ||
            !model.FirmwareComparison.StartsWith("Unknown", StringComparison.Ordinal) ||
            !model.FirmwareHistoryMessage.StartsWith("Not checked", StringComparison.Ordinal) ||
            !model.FirmwareManufacturerCheckTime.StartsWith("Not checked", StringComparison.Ordinal) ||
            model.CheckFirmwareHistoryCommand == null || model.ReviewFirmwareHistorySourceCommand == null ||
            model.CheckFirmwareHistoryCommand.CanExecute(null) || model.ReviewFirmwareHistorySourceCommand.CanExecute(null))
            throw new SmokeAssertionException("ui-firmware-history-unchecked");
        if (model.PreviewLeftoverContentsCommand == null || model.PreviewLeftoverContentsCommand.CanExecute(null) || model.LeftoverContentRows.Count != 0 || model.LeftoverContentIssues.Count != 0 || model.LeftoverContentCoverage.Count != 0 || model.SelectedLeftoverCandidate != null || model.SelectedLeftoverContent != null)
            throw new SmokeAssertionException("ui-leftover-content-unchecked");
        if (model.AnalyzeDiskIdentitiesCommand == null || model.AnalyzeDiskIdentitiesCommand.CanExecute(null) || model.DiskIdentityRows.Count != 0 || model.DiskIdentityCoverage.Count != 0)
            throw new SmokeAssertionException("ui-disk-identity-unchecked");
        if (model.PreviewInstalledAppPlanCommand == null || model.ReviewInstalledAppHandoffCommand == null ||
            model.PreviewInstalledAppPlanCommand.CanExecute(null) || model.ReviewInstalledAppHandoffCommand.CanExecute(null))
            throw new SmokeAssertionException("ui-installed-app-no-plan");
        if (model.VerifyMsiRegistrationCommand == null || model.VerifyMsiRegistrationCommand.CanExecute(null) ||
            model.MsiRegistrationRows.Count != 0 || model.MsiRegistrationCoverage.Count != 0)
            throw new SmokeAssertionException("ui-msi-registration-unchecked");
        if (model.ReadPackagedApplicationsCommand == null || !model.ReadPackagedApplicationsCommand.CanExecute(null) ||
            model.PackagedApplicationRows.Count != 0 || model.PackagedApplicationsView == null || !model.PackagedApplicationsView.IsEmpty ||
            model.SelectedPackagedApplication != null || !model.PackagedApplicationsMessage.StartsWith("Not checked", StringComparison.Ordinal) ||
            !model.PackagedApplicationsAttempt.StartsWith("Not checked", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-packaged-applications-unchecked");
        if (model.DuplicateRows.Count != 0 || model.DuplicateCandidate != null || model.DuplicateRetainedEntry != null ||
            model.PreviewDuplicatePairCommand == null || model.ClearDuplicatePairCommand == null ||
            model.PreviewDuplicatePairCommand.CanExecute(null) || model.ClearDuplicatePairCommand.CanExecute(null) ||
            !model.DuplicatePairMessage.StartsWith("Not prepared", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-duplicate-pair-unprepared");
        if (model.RecheckDuplicatePairCommand == null || model.RecheckDuplicatePairCommand.CanExecute(null) ||
            !model.DuplicatePairRecheckMessage.StartsWith("Not checked", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-duplicate-pair-recheck-unchecked");
        if (model.ShortcutRows.Count != 0 || model.SelectedShortcutObservation != null || model.RecheckSelectedShortcutCommand == null ||
            model.RecheckSelectedShortcutCommand.CanExecute(null) || !model.ShortcutRecheckMessage.StartsWith("Not checked", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-shortcut-recheck-unchecked");
        if (model.InspectShortcutFileEvidenceCommand == null || model.InspectShortcutFileEvidenceCommand.CanExecute(null) ||
            !model.ShortcutFileEvidenceMessage.StartsWith("Not checked", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-shortcut-file-evidence-unchecked");
        if (!model.SystemToolIdentitySummary.Contains("not checked", StringComparison.OrdinalIgnoreCase))
            throw new SmokeAssertionException("ui-system-tool-identity-unchecked");
    }

    private static void AssertSectionTitleContrast(MainWindow window)
    {
        var title = Descendants<TextBlock>(window).Single(block => block.IsVisible && block.Text == "Update center");
        if (title.Foreground is not SolidColorBrush foreground || foreground.Color.A != 255 || foreground.Opacity != 1 ||
            title.TextWrapping != TextWrapping.Wrap || window.Background is not GradientBrush background || background.Opacity != 1)
            throw new SmokeAssertionException("ui-section-title-style");
        static double Luminance(Color color)
        {
            static double Linear(byte value) => value / 255d <= 0.04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double text = Luminance(foreground.Color);
        if (background.GradientStops.Count == 0 || background.GradientStops.Any(stop => stop.Color.A != 255 ||
            (Math.Max(text, Luminance(stop.Color)) + 0.05) / (Math.Min(text, Luminance(stop.Color)) + 0.05) < 4.5))
            throw new SmokeAssertionException("ui-section-title-contrast");
        // Scoped resolved brush/style assertion, not a full accessibility or pixel/DPI audit.
    }

    private static void AssertVisibleControls(MainWindow window)
    {
        if (window.Content is not FrameworkElement content || !ValidSize(content)) throw new SmokeAssertionException("ui-content-layout");
        var action = Descendants<Button>(window).Single(button => AutomationProperties.GetName(button) == "Inspect this PC");
        var navigation = Descendants<ListBox>(window).Single(list => AutomationProperties.GetName(list) == "Main navigation");
        foreach (var control in new FrameworkElement[] { action, navigation })
        {
            if (!control.IsVisible || !ValidSize(control)) throw new SmokeAssertionException(ReferenceEquals(control, action) ? "ui-inspect-button-visibility" : "ui-navigation-visibility");
            Point origin = control.TranslatePoint(new Point(), content);
            if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || origin.X < -1 || origin.Y < -1 ||
                origin.X + control.ActualWidth > content.ActualWidth + 1 || origin.Y + control.ActualHeight > content.ActualHeight + 1)
                throw new SmokeAssertionException(ReferenceEquals(control, action) ? "ui-inspect-button-bounds" : "ui-navigation-bounds");
        }
    }

    private static bool ValidSize(FrameworkElement element) => double.IsFinite(element.ActualWidth) && double.IsFinite(element.ActualHeight) && element.ActualWidth > 0 && element.ActualHeight > 0;

    private static void Capture(MainWindow window, string output, string name, SmokeReport report)
    {
        if (window.Content is not FrameworkElement content || !ValidSize(content)) throw new SmokeAssertionException("ui-capture-content");
        int width = checked((int)Math.Ceiling(content.ActualWidth)), height = checked((int)Math.Ceiling(content.ActualHeight));
        if (width is < 100 or > 3000 || height is < 100 or > 3000) throw new SmokeAssertionException("ui-capture-size");
        if (window.Background == null) throw new SmokeAssertionException("ui-capture-background");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var composition = new DrawingVisual();
        var outputBounds = new Rect(0, 0, width, height);
        var contentBounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
        using (var drawing = composition.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(outputBounds));
            // The client Grid is transparent. Render its actual Window brush first, rather than
            // inventing a capture-only color or omitting the application's real background.
            drawing.DrawRectangle(window.Background, null, outputBounds);
            var client = new VisualBrush(content)
            {
                AutoLayoutContent = false,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = contentBounds,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = contentBounds,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            drawing.DrawRectangle(client, null, contentBounds);
            drawing.Pop();
        }
        bitmap.Render(composition);
        // This app's client background is opaque; a transparent export is not evidence of its look.
        var pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(pixels, checked(width * 4), 0);
        for (int offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] != byte.MaxValue) throw new SmokeAssertionException("ui-capture-opacity");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(output, name);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(file);
        using var read = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(read)).ToLowerInvariant();
        report.Image(new(name, width, height, hash));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
