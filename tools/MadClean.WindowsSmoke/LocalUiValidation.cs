using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Validation;

namespace MadClean.WindowsSmoke;

// Explicit local neutral startup/navigation observation. No inventory/action command is invoked.
internal static class LocalUiValidation
{
    internal static int Run()
        => RunLocal(false);

    internal static int RunRepairReview()
        => RunLocal(true);

    // A closed local workflow choice, not a caller-supplied action or service callback.
    private static int RunLocal(bool repairReview)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64) return 2;
        string repository;
        try
        {
            repository = Path.GetFullPath(Environment.CurrentDirectory);
            CheckLocalFixedPath(repository);
            CheckAncestors(repository);
        }
        catch (Exception) { return 2; }
        if (!File.Exists(Path.Combine(repository, "MadClean.sln")) ||
            !Directory.Exists(Path.Combine(repository, "src", "MadClean.App"))) return 2;
        string output;
        try { output = ReserveOutput(repository, repairReview); }
        catch (Exception) { return 1; }
        DateTimeOffset started = DateTimeOffset.UtcNow;
        bool? observed = null, initial = null, wide = null, compact = null, navigation = null,
            final = null, closed = null, settled = null;
        int? error = null;
        bool reportSaved = false;
        int reportExit = 1;
        MainWindow? window = null;
        MainViewModel? owner = null;
        var repairObservation = repairReview ? new LocalRepairReviewObservation() : null;
        var application = new MadClean.App.App();
        void Fail(Exception exception) { error ??= exception.HResult; }
        int SaveReport()
        {
            var facts = new LocalUiCheckFacts(observed, initial, wide, compact, navigation, final, closed, settled);
            object report;
            string state;
            if (repairReview)
            {
                var value = LocalRepairReviewEvidence.Create(started, DateTimeOffset.UtcNow,
                    facts, repairObservation!.Facts, error);
                if (!LocalRepairReviewEvidence.IsConsistent(value, DateTimeOffset.UtcNow)) return 1;
                report = value; state = value.State;
            }
            else
            {
                var value = LocalUiValidationEvidence.Create(started, DateTimeOffset.UtcNow, facts, error);
                if (!LocalUiValidationEvidence.IsConsistent(value, DateTimeOffset.UtcNow)) return 1;
                report = value; state = value.State;
            }
            try
            {
                CheckAncestors(output);
                using var file = new FileStream(Path.Combine(output, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, report, report.GetType(), new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                reportSaved = true;
                reportExit = state switch { "passed" => 0, "inconclusive" => 2, _ => 1 };
                return reportExit;
            }
            catch (Exception) { return 1; }
        }
        async Task WaitForSettlement()
        {
            while (owner != null)
            {
                bool? pending = Pending(owner);
                if (pending == false) return;
                // Unknown ownership is a permanent conservative hold, not a close/retry loop.
                if (pending == null) await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
                await Task.Delay(100);
            }
        }
        async Task Finish()
        {
            // A stop request is never substituted for native completion. Keep this dispatcher
            // and the owning model alive while any actual operation remains pending.
            if (owner != null && Pending(owner) != false)
            {
                settled = false;
                error ??= unchecked((int)0x80004005);
                SaveReport();
            }
            await WaitForSettlement();
            if (window != null && closed != true)
            {
                window.Close();
                await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (closed != true) throw new InvalidOperationException();
            }
            if (!reportSaved) settled = owner == null ? null : closed == true;
            application.Shutdown(error == null && closed == true ? 0 : 1);
        }
        try
        {
            application.InitializeComponent();
            application.StartupUri = new Uri("/MadClean;component/MainWindow.xaml", UriKind.Relative);
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    if (sender is not MainWindow loaded || window != null) return;
                    window = loaded;
                    loaded.IsEnabled = false; // Disable physical input before observing the normal window.
                    owner = loaded.DataContext as MainViewModel;
                    observed = true;
                    loaded.Closed += (_, _) => closed = true;
                }), true);
            application.DispatcherUnhandledException += (_, args) =>
            {
                Fail(args.Exception);
                args.Handled = true;
                // Do not force shutdown: the ordinary finish path retains pending owners.
            };
            application.Startup += async (_, _) =>
            {
                try
                {
                    await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (window == null || owner == null || !ReferenceEquals(application.MainWindow, window))
                    { initial = window == null ? null : false; throw new InvalidOperationException(); }
                    initial = Neutral(owner);
                    if (initial != true) throw new InvalidOperationException();
                    window.WindowState = WindowState.Normal;
                    window.Width = 1200; window.Height = 900;
                    await Idle(window);
                    wide = Layout(window, 1200, 900);
                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    await Idle(window);
                    compact = Layout(window, window.MinWidth, window.MinHeight);
                    navigation = await Navigate(window, owner);
                    if (repairObservation != null && navigation == true)
                        await repairObservation.ObserveAsync(window, owner);
                    final = Neutral(owner);
                }
                catch (Exception exception) { Fail(exception); }
                finally
                {
                    try { await Finish(); }
                    catch (Exception exception)
                    {
                        Fail(exception);
                        if (!reportSaved) { settled = false; SaveReport(); }
                        await WaitForSettlement();
                        // Failure exit only after actual busy/pending state is known clear.
                        // Shutdown may raise Closed normally; never fabricate that observation.
                        application.Shutdown(1);
                    }
                }
            };
            application.Run();
        }
        catch (Exception exception) { Fail(exception); }
        finally
        {
            // If the dispatcher departs unexpectedly, preserve the CLR and actual owner.
            // This fence reads managed busy/pending facts only; it never cancels/kills work.
            if (owner != null && Pending(owner) != false)
            {
                settled = false;
                error ??= unchecked((int)0x80004005);
                SaveReport(); // Preserve failure while retaining the CLR; never overwrite it later.
                while (true)
                {
                    bool? pending = Pending(owner);
                    if (pending == false) break;
                    if (pending == null) Thread.Sleep(Timeout.Infinite);
                    Thread.Sleep(100);
                }
            }
            if (!reportSaved && owner != null) settled = closed == true;
            GC.KeepAlive(owner);
            GC.KeepAlive(window);
            GC.KeepAlive(application);
        }
        int exit = reportSaved ? reportExit : SaveReport();
        Console.WriteLine("Local report: artifacts/" + (repairReview ? "local-repair-review-validation/" : "local-ui-validation/") + Path.GetFileName(output) + "/report.json");
        Console.WriteLine(repairReview
            ? "Local repair-review draft/binding observation finished; provider, maintenance and physical-input validation remain unverified."
            : "Local neutral UI observation finished; provider, maintenance and physical-input validation remain unverified.");
        return exit;
    }

    private static bool? Pending(MainViewModel model)
    {
        try
        {
            return model.IsBusy || typeof(MainViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.Name.StartsWith("HasPending", StringComparison.Ordinal) && property.PropertyType == typeof(bool))
                .Any(property => property.GetValue(model) is not false);
        }
        catch (Exception) { return null; } // Unknown ownership cannot authorize close/CLR exit.
    }

    private static bool Neutral(MainViewModel model)
    {
        string[] collections = ["Activity", "Disks", "CleanupRows", "StartupRows", "SecurityRows", "AppRows", "SystemRows",
            "InstalledApplications", "PackagedApplicationRows", "LeftoverCandidates", "LeftoverContentRows", "LeftoverContentIssues",
            "MsiRegistrationRows", "HardwareFacts", "FirmwareIdentityRows", "FirmwareOffers", "FirmwareHistoryRows", "DriverOffers",
            "DriverOfferRows", "WindowsSoftwareRows", "AppUpdateOffers", "DeviceDriverRows", "DiskIdentityRows", "DiskMapIssues",
            "DuplicateRows", "DuplicateIssues", "ShortcutRows", "ShortcutIssues", "DiagnosticRows", "WerRows", "ManufacturerServiceRows",
            "CleanupReviewSelections", "CleanupReviewRows", "StartupRegistrations", "StartupBackups"];
        return Pending(model) == false && collections.All(name =>
            typeof(MainViewModel).GetProperty(name)?.GetValue(model) is ICollection { Count: 0 });
    }

    private static async Task<bool> Navigate(MainWindow window, MainViewModel model)
    {
        var primary = Descendants<ListBox>(window).Single(control => AutomationProperties.GetName(control) == "Main navigation");
        var tools = Descendants<ComboBox>(window).Single(control => Equals(control.Tag, "More tools"));
        var host = Descendants<PageHost>(window).Single();
        bool BindingsPresent() =>
            primary.GetBindingExpression(Selector.SelectedIndexProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.PrimaryNavigationIndex) &&
            tools.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedToolNavigation);
        if (!BindingsPresent()) return false;
        try
        {
            int[] pages = [0, 1, 3, 2, 4];
            for (int index = 0; index < pages.Length; index++)
            {
                primary.SetCurrentValue(Selector.SelectedIndexProperty, index);
                await Idle(window);
                if (model.SelectedPage != pages[index] || host.SelectedIndex != pages[index] || !Neutral(model) || !BindingsPresent()) return false;
            }
            foreach (var link in model.ToolNavigationItems)
            {
                tools.SetCurrentValue(Selector.SelectedItemProperty, link);
                await Idle(window);
                if (model.SelectedPage != link.Page || host.SelectedIndex != link.Page || !Neutral(model) || !BindingsPresent()) return false;
            }
        }
        finally
        {
            primary.SetCurrentValue(Selector.SelectedIndexProperty, 0);
            await Idle(window);
        }
        return model.SelectedPage == 0 && host.SelectedIndex == 0 && tools.SelectedItem == null && Neutral(model) && BindingsPresent();
    }

    private static bool Layout(MainWindow window, double width, double height)
    {
        if (Math.Abs(window.ActualWidth - width) > 2 || Math.Abs(window.ActualHeight - height) > 2 ||
            window.Content is not FrameworkElement client || !Size(client)) return false;
        // Coarse shell bounds only: no pixel fidelity, text clipping or physical-input claim.
        var shell = Descendants<FrameworkElement>(window).Where(element => element is PageHost ||
            element is ListBox && AutomationProperties.GetName(element) == "Main navigation" ||
            element is ComboBox combo && Equals(combo.Tag, "More tools")).ToArray();
        return shell.Length == 3 && shell.All(element =>
            {
                Point point = element.TranslatePoint(new Point(), client);
                return element.IsVisible && Size(element) && double.IsFinite(point.X) && double.IsFinite(point.Y) &&
                    point.X >= -1 && point.Y >= -1 && point.X + element.ActualWidth <= client.ActualWidth + 1 &&
                    point.Y + element.ActualHeight <= client.ActualHeight + 1;
            });
    }

    private static bool Size(FrameworkElement element) => double.IsFinite(element.ActualWidth) &&
        double.IsFinite(element.ActualHeight) && element.ActualWidth > 0 && element.ActualHeight > 0;
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
    private static void CheckLocalFixedPath(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            root[2] != Path.DirectorySeparatorChar || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(root).DriveType != DriveType.Fixed) throw new IOException();
    }
    private static string ReserveOutput(string repository, bool repairReview)
    {
        CheckAncestors(repository);
        string parent = repository;
        foreach (string name in new[] { "artifacts", repairReview ? "local-repair-review-validation" : "local-ui-validation" })
        {
            CheckAncestors(parent);
            string child = Path.Combine(parent, name);
            var attributes = ExistingAttributes(child);
            if (attributes is { } existing && (existing & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException();
            if (attributes == null) Directory.CreateDirectory(child); // Exactly one validated-parent component.
            CheckAncestors(child);
            parent = child;
        }
        string output = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        CheckAncestors(parent);
        if (ExistingAttributes(output) != null) throw new IOException();
        Directory.CreateDirectory(output);
        CheckAncestors(output);
        if (Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException();
        return output;
    }
    private static FileAttributes? ExistingAttributes(string path)
    {
        // Parent ancestry has already been checked. Inspect the child itself before writes,
        // including dangling links; never follow a known reparse entry to create beneath it.
        if (new DirectoryInfo(path).LinkTarget != null || new FileInfo(path).LinkTarget != null) throw new IOException();
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static void CheckAncestors(string path)
    {
        string full = Path.GetFullPath(path);
        CheckLocalFixedPath(full);
        var ancestry = new Stack<string>();
        for (DirectoryInfo? directory = new(full); directory != null; directory = directory.Parent)
            ancestry.Push(directory.FullName);
        foreach (string component in ancestry)
        {
            var attributes = ExistingAttributes(component);
            if (attributes is not { } value || (value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException();
        }
    }
}
