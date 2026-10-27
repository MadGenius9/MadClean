using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MadClean.App;

namespace MadClean.WindowsSmoke;

internal static class UiCapture
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
                await Idle(window);
                if (window.DataContext is not MainViewModel model) throw new SmokeAssertionException("ui-data-context");
                AssertNoInventory(model);
                if (window.ActualWidth < 1160) report.Unknown("ui-wide-size", "The desktop constrained the requested wide window; the captured dimensions are actual, but wide-layout validation is inconclusive.");
                Capture(window, output, "overview-wide.png", report);
                AssertVisibleControls(window);
                report.Pass("ui-initial-state", "Actual production WPF Overview loaded with unchecked values and zero activity events; no scan command was executed.");

                window.Width = 980; window.Height = 650;
                await Idle(window);
                AssertVisibleControls(window);
                Capture(window, output, "overview-compact.png", report);
                report.Pass("ui-compact-layout", "Client content rendered at the minimum 980x650 window size; main navigation and inspection action have finite visible bounds. Scrolling/DPI/fidelity still require manual checks.");

                window.Width = 1420; window.Height = 1000;
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
                report.Pass("ui-primary-navigation", "All five displayed primary sections select the corresponding preserved page through the actual ListBox binding.");

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
                    if (link.Page >= 11) Capture(window, output, $"tool-{link.Page:D2}.png", report);
                    AssertNoInventory(model);
                }
                report.Pass("ui-tool-navigation", $"All {links.Length} secondary tools select their preserved pages through the actual ComboBox binding without starting inventory. New-module/H1 pages were captured.");

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

    private static void AssertNoInventory(MainViewModel model)
    {
        if (model.IsBusy || model.Activity.Count != 0 || model.Disks.Count != 0 || model.CleanupRows.Count != 0 || model.StartupRows.Count != 0)
            throw new InvalidOperationException("Navigation started provider activity.");
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
