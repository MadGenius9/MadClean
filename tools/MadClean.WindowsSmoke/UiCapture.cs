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
                    }
                    AssertNoInventory(model);
                }
                report.Pass("ui-tool-navigation", $"All {links.Length} secondary tools select their preserved pages through the actual ComboBox binding without starting inventory. Actual unchecked MSI and current-user package subviews were captured with production command/empty-row bindings; the desktop source was restored without queries before later search validation.");

                model.SelectedPage = 0;
                await Idle(window);
                if (primary.SelectedIndex != 0 || tools.SelectedItem != null) throw new SmokeAssertionException("ui-selector-sync");
                tools.IsDropDownOpen = true;
                await Idle(window);
                if (tools.Template.FindName("PART_Popup", tools) is not Popup { IsOpen: true, Child: { IsVisible: true } })
                    throw new SmokeAssertionException("ui-tool-popup");
                tools.IsDropDownOpen = false;
                AssertNoInventory(model);
                report.Pass("ui-tool-popup", "The real More tools popup opens, and programmatic page changes synchronize both selectors. Physical keyboard and screen-reader interaction are not asserted.");
                // All captures and unchecked/navigation assertions are complete. This separate
                // explicit CI metadata workflow must not produce any later screenshots.
                await CheckInstalledAppSearch(window, model, report);
                await CheckPackagedAppSearch(window, model, report);
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
                window?.Close();
                application.Shutdown(report.Failed ? 1 : 0);
            }
        };
        application.Run();
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
