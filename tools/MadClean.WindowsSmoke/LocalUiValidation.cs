using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        LayoutObservation? wideLayout = null, compactLayout = null;
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
                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                byte[] primaryBytes = JsonSerializer.SerializeToUtf8Bytes(report, report.GetType(), options);
                using (var file = new FileStream(Path.Combine(output, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    file.Write(primaryBytes);
                // The primary file is closed before it is marked saved. Optional
                // diagnostic failures cannot change its bytes, facts or outcome.
                reportSaved = true;
                reportExit = state switch { "passed" => 0, "inconclusive" => 2, _ => 1 };
                if (repairReview)
                {
                    try
                    {
                        var companion = new LayoutCompanion(1, "local-repair-review-layout",
                            Convert.ToHexStringLower(SHA256.HashData(primaryBytes)), "unverified", "unverified",
                            "unverified", "unverified", wideLayout, compactLayout);
                        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(companion, options);
                        if (bytes.Length <= 16_384)
                        {
                            CheckAncestors(output);
                            using var diagnostic = new FileStream(Path.Combine(output, "layout-observations.json"),
                                FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            diagnostic.Write(bytes);
                        }
                    }
                    catch (Exception) { /* Missing/invalid companion is unknown; never rewrite the primary report. */ }
                }
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
                    wide = Layout(window, 1200, 900, repairReview, out wideLayout);
                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    await Idle(window);
                    compact = Layout(window, window.MinWidth, window.MinHeight, repairReview, out compactLayout);
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
        string[] collections = ["Activity", "Disks", "CleanupRows", "StartupRows", "SecurityRows", "AppRows", "SystemRows", "WindowsUpdateHistoryRows",
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

    private static bool Layout(MainWindow window, double width, double height, bool recordLayout,
        out LayoutObservation? observation)
    {
        observation = null;
        LayoutCapture? capture = null;
        try { if (recordLayout) capture = new(width, height); }
        catch (Exception) { /* Optional measurement allocation is not a primary assertion. */ }
        try
        {
            double actualWidth = window.ActualWidth, actualHeight = window.ActualHeight;
            capture?.SetOuter(actualWidth, actualHeight);
            if (Math.Abs(actualWidth - width) > 2 || Math.Abs(actualHeight - height) > 2 ||
                window.Content is not FrameworkElement client) return false;
            double clientWidth = client.ActualWidth, clientHeight = client.ActualHeight;
            capture?.SetClient(client, clientWidth, clientHeight);
            if (!Size(clientWidth, clientHeight)) return false;
            // Coarse shell bounds only: no pixel fidelity, text clipping or physical-input claim.
            var shell = Shell(window).ToArray();
            try { capture?.SetShell(shell); } catch (Exception) { capture = null; }
            int index = 0;
            return shell.Length == 3 && shell.All(element =>
            {
                Point point = element.TranslatePoint(new Point(), client);
                bool visible = element.IsVisible;
                double itemWidth = element.ActualWidth, itemHeight = element.ActualHeight;
                try { capture?.Record(index, visible, point, itemWidth, itemHeight); }
                catch (Exception) { capture = null; }
                index++;
                return visible && Size(itemWidth, itemHeight) && double.IsFinite(point.X) && double.IsFinite(point.Y) &&
                    point.X >= -1 && point.Y >= -1 && point.X + itemWidth <= clientWidth + 1 &&
                    point.Y + itemHeight <= clientHeight + 1;
            });
        }
        finally
        {
            // Retain exactly the original predicate's dimensions/points. Read only
            // unvisited fields for diagnostics, without another layout/resize/await.
            try { if (capture != null) observation = capture.Complete(window); }
            catch (Exception) { observation = null; }
        }
    }

    private static IEnumerable<FrameworkElement> Shell(MainWindow window) =>
        Descendants<FrameworkElement>(window).Where(element => element is PageHost ||
            element is ListBox && AutomationProperties.GetName(element) == "Main navigation" ||
            element is ComboBox combo && Equals(combo.Tag, "More tools"));
    private static bool Size(double width, double height) => double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;

    private sealed record LayoutCompanion(int FormatVersion, string Context, string PrimaryReportSha256,
        string SourceAuthenticity, string WindowsProviders, string Maintenance, string PhysicalInput,
        LayoutObservation? Wide, LayoutObservation? Compact);
    private sealed record LayoutObservation(double? RequestedWidth, double? RequestedHeight,
        double? ActualWidth, double? ActualHeight, double? ClientWidth, double? ClientHeight,
        double? WorkAreaWidth, double? WorkAreaHeight, double? DpiScaleX, double? DpiScaleY,
        bool? OuterSizeMatched, int? ShellCount, bool ShellCountOverLimit, IReadOnlyList<ShellObservation>? Shell);
    private sealed record ShellObservation(string Kind, bool? Visible, double? X, double? Y,
        double? Width, double? Height, bool? WithinClient);

    private sealed class LayoutCapture(double requestedWidth, double requestedHeight)
    {
        private double? actualWidth, actualHeight, clientWidth, clientHeight;
        private FrameworkElement? client;
        private FrameworkElement[]? shell;
        private ShellObservation?[]? records;

        internal void SetOuter(double width, double height)
        { actualWidth = Dimension(width); actualHeight = Dimension(height); }
        internal void SetClient(FrameworkElement value, double width, double height)
        { client = value; clientWidth = Dimension(width); clientHeight = Dimension(height); }
        internal void SetShell(FrameworkElement[] value)
        {
            var slots = new ShellObservation?[Math.Min(value.Length, 3)];
            shell = value; records = slots;
        }
        internal void Record(int index, bool visible, Point point, double width, double height)
        {
            if (shell == null || records == null || index < 0 || index >= records.Length) return;
            double? x = Coordinate(point.X), y = Coordinate(point.Y), w = Dimension(width), h = Dimension(height);
            records[index] = new(Kind(shell[index]), visible, x, y, w, h,
                WithinClient(x, y, w, h, clientWidth, clientHeight));
        }
        internal LayoutObservation Complete(MainWindow window)
        {
            // Supplementary reads never replace already captured primary values.
            if (client == null)
                try
                {
                    if (window.Content is FrameworkElement value)
                        SetClient(value, value.ActualWidth, value.ActualHeight);
                }
                catch (Exception) { }
            if (shell == null)
                try { SetShell(LocalUiValidation.Shell(window).Take(4).ToArray()); }
                catch (Exception) { shell = null; records = null; }
            if (shell != null && records != null)
            {
                for (int index = 0; index < records.Length; index++)
                {
                    if (records[index] != null) continue;
                    var element = shell[index];
                    bool? visible = null;
                    double? x = null, y = null, width = null, height = null;
                    try { visible = element.IsVisible; } catch (Exception) { }
                    try { width = Dimension(element.ActualWidth); height = Dimension(element.ActualHeight); } catch (Exception) { }
                    try
                    {
                        if (client != null)
                        { var point = element.TranslatePoint(new Point(), client); x = Coordinate(point.X); y = Coordinate(point.Y); }
                    }
                    catch (Exception) { }
                    records[index] = new(Kind(element), visible, x, y, width, height,
                        WithinClient(x, y, width, height, clientWidth, clientHeight));
                }
            }
            double? workWidth = null, workHeight = null, dpiX = null, dpiY = null;
            try
            {
                // SystemParameters describes the PRIMARY work area, not a promise
                // about the current window's monitor or available test viewport.
                var work = SystemParameters.WorkArea;
                workWidth = Dimension(work.Width); workHeight = Dimension(work.Height);
            }
            catch (Exception) { }
            try { var dpi = VisualTreeHelper.GetDpi(window); dpiX = Dpi(dpi.DpiScaleX); dpiY = Dpi(dpi.DpiScaleY); }
            catch (Exception) { }
            double? requestedW = requestedWidth > 0 ? Dimension(requestedWidth) : null,
                requestedH = requestedHeight > 0 ? Dimension(requestedHeight) : null;
            bool? outer = requestedW is > 0 && requestedH is > 0 && actualWidth.HasValue && actualHeight.HasValue
                ? Math.Abs(actualWidth.Value - requestedW.Value) <= 2 && Math.Abs(actualHeight.Value - requestedH.Value) <= 2 : null;
            bool overLimit = shell is { Length: > 3 };
            return new(requestedW, requestedH, actualWidth, actualHeight, clientWidth, clientHeight,
                workWidth, workHeight, dpiX, dpiY, outer, overLimit ? null : shell?.Length,
                overLimit, records?.Select(row => row!).ToArray());
        }
        private static string Kind(FrameworkElement element) => element is PageHost ? "page-host" : element is ListBox ? "navigation" : "tools";
        private static double? Dimension(double value) => double.IsFinite(value) && value is >= 0 and <= 32768 ? value : null;
        private static double? Coordinate(double value) => double.IsFinite(value) && value is >= -32768 and <= 32768 ? value : null;
        private static double? Dpi(double value) => double.IsFinite(value) && value is >= 0.125 and <= 16 ? value : null;
        private static bool? WithinClient(double? x, double? y, double? width, double? height, double? clientWidth, double? clientHeight)
        {
            if (width == 0 || height == 0 || clientWidth == 0 || clientHeight == 0) return false;
            if (!x.HasValue || !y.HasValue || !width.HasValue || !height.HasValue || !clientWidth.HasValue || !clientHeight.HasValue) return null;
            return x >= -1 && y >= -1 && x + width <= clientWidth + 1 && y + height <= clientHeight + 1;
        }
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
