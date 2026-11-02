using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MadClean.WindowsSmoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Unsupported: this harness requires Windows and WPF; no platform checks ran.");
            return 2;
        }
        if (Environment.GetEnvironmentVariable("MADCLEAN_WINDOWS_VALIDATION") != "1")
        {
            Console.Error.WriteLine("Not enabled: set MADCLEAN_WINDOWS_VALIDATION=1 only in a disposable Windows runner/VM.");
            return 2;
        }
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        if (!File.Exists(Path.Combine(repository, "MadClean.sln")) || !Directory.Exists(Path.Combine(repository, "src", "MadClean.App")))
        {
            Console.Error.WriteLine("Run from the existing MadClean repository root; no checks ran.");
            return 2;
        }
        // Fixed development read-only online mode, separate from GUI/native fixture lifetime.
        if (args.Length == 1 && args[0] == "--update-discovery")
        {
            using var updateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(220));
            return UpdateDiscoveryFixture.RunAsync(repository, updateDeadline.Token).GetAwaiter().GetResult();
        }
        // Fixed inert child protocol only; unknown arguments never reset captures or query.
        if (args.Length != 0)
            return InertProcessChild.TryRun(repository, args, out int childExit) ? childExit : 2;
        string output = Path.Combine(repository, "artifacts", "windows-validation");
        if (!PrepareOutput(output))
        {
            Console.Error.WriteLine("Output directory unavailable or has a reparse ancestor; no checks ran.");
            return 1;
        }
        var report = new SmokeReport(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var watchdog = new Timer(_ =>
        {
            report.Fail("harness-deadline", "The development harness exceeded its four-minute deadline. Native/provider/GUI completion is not claimed.");
            try { report.Save(); } catch (Exception) { }
            Environment.Exit(1);
        }, null, TimeSpan.FromMinutes(4), Timeout.InfiniteTimeSpan);
        try
        {
            // This setting affects only this development process and does not change Windows graphics policy.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            UiCapture.Run(output, report);
            // Application.Run can leave the closed dispatcher context installed on this STA.
            SynchronizationContext.SetSynchronizationContext(null);
            if (!report.Failed) NativeFixtures.Run(repository, report, deadline.Token);
            const string startupCheck = "published-executable-startup";
            if (!report.Failed && NativeFixtures.NativeFixtureTreeCleanupConfirmed)
            {
                string source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "";
                if (source.Length != 40 || source.Any(c => !Uri.IsHexDigit(c)))
                    report.Unknown(startupCheck, "Current source commit is unavailable; no published executable was started.");
                else
                {
                    var startup = PublishedExecutableStartupFixture.RunAsync(repository, source.ToLowerInvariant(), deadline.Token).GetAwaiter().GetResult();
                    string detail = $"state={startup.State}; descriptor={startup.DescriptorValidated}; leased={startup.ExecutableLeased}; started={startup.ProcessStarted}; ownedWindow={startup.OwnedWindowObserved}; neutralOverview={startup.NeutralOverviewObserved}; noDataOverview={startup.OverviewNoDataObserved}; ownedBundledRuntime={startup.OwnedBundledRuntimeObserved}; closeRequested={startup.CloseRequested}; exitObserved={startup.ExitObserved}; exitCode={startup.ExitCode}; cleanup={startup.CleanupConfirmed}; stage={startup.Stage}; nativeError={startup.NativeError}. Scoped own-process startup only; no inventory actions or captures, and no distribution upload.";
                    if (detail.Length > 1000)
                    {
                        if (startup.State == PublishedExecutableStartupState.Failed) report.Fail(startupCheck, "A published-startup invariant failed; diagnostic export exceeded its bound.");
                        else report.Unknown(startupCheck, "Published-startup diagnostic export exceeded its bound; no success claimed.");
                    }
                    else if (startup.State == PublishedExecutableStartupState.Observed && startup.CleanupConfirmed && !PublishedExecutableStartupFixture.HasPendingStartup && !PublishedExecutableStartupFixture.RequiresRetention)
                        report.Pass(startupCheck, detail);
                    else if (startup.State == PublishedExecutableStartupState.Failed) report.Fail(startupCheck, detail);
                    else report.Unknown(startupCheck, detail);
                }
            }
            else report.Unknown(startupCheck, "Earlier validation failed or native tree teardown is uncertain; no published executable was started.");
            UpdateDiscoveryFixture.ReadIntoReport(repository, Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "", report);
        }
        catch (Exception ex)
        {
            report.Fail("harness", $"The harness raised {SmokeAssertionException.SafeContext(ex)} (0x{ex.HResult:X8}); no broad Windows verification is claimed.");
        }
        finally
        {
            try { report.Save(); }
            catch (Exception ex) { Console.Error.WriteLine($"Could not write verification report (0x{ex.HResult:X8})."); Environment.ExitCode = 1; }
        }
        int exit = Environment.ExitCode == 1 ? 1 : report.ExitCode;
        Console.WriteLine($"MadClean Windows validation: {(exit == 0 ? "passed scoped checks" : exit == 2 ? "inconclusive" : "failed")}. See artifacts/windows-validation/report.json.");
        return exit;
    }

    private static bool PrepareOutput(string output)
    {
        try
        {
            foreach (var ancestor in Ancestors(output))
            {
                try
                {
                    if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) return false;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            Directory.CreateDirectory(output);
            foreach (var ancestor in Ancestors(output))
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var stack = new Stack<string>();
        for (string? current = path; current != null; current = Directory.GetParent(current)?.FullName) stack.Push(current);
        return stack;
    }
}
