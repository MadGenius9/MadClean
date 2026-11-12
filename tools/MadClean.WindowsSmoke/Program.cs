using System.Diagnostics;
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
        // Explicit local neutral UI observation, independent of disposable CI qualification.
        if (args.Length == 1 && args[0] == "--local-ui-validation")
            return LocalUiValidation.Run();
        // Separate local draft/binding observation only; never a repair qualification mode.
        if (args.Length == 1 && args[0] == "--local-repair-review-validation")
            return LocalUiValidation.RunRepairReview();
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
        // Separate explicit disposable-runner action qualification. It is outside the
        // passive harness watchdog and can touch only its fresh inert owned MSI product.
        if (args.Length == 1 && args[0] == "--msi-request-validation")
            return MsiRequestQualificationFixture.Run(repository);
        // Separate explicit non-repair recorded component-store flag qualification.
        // It is outside the passive watchdog; no query-process kill runner is reused.
        if (args.Length == 1 && args[0] == "--component-store-status-validation")
            return ComponentStoreStatusQualification.Run(repository);
        // Separate explicit fresh component-store scan; never passive inventory or a repair.
        if (args.Length == 1 && args[0] == "--component-store-scan-validation")
            return ComponentStoreStatusQualification.Run(repository, MadClean.Core.ComponentStoreCheckKind.ScanHealth);
        // Separate hosted-runner client-lifecycle test of the fixed non-repair
        // SFC operation. Public App execution remains disabled; no passive hook.
        if (args.Length == 1 && args[0] == "--sfc-client-validation")
            return SfcClientQualification.Run(repository);
        // Published-layout SDK qualification is separate from passive fixtures and does not distribute a package.
        if (args.Length == 1 && args[0] == "--structured-winget-published-parent")
        {
            try { return StructuredWingetPublishedQualification.RunParentAsync(repository, CancellationToken.None).GetAwaiter().GetResult(); }
            finally { StructuredWingetPublishedQualification.WaitForParentCompletionAsync().GetAwaiter().GetResult(); }
        }
        if (args.Length == 1 && args[0] == "--structured-winget-published-validation")
            return StructuredWingetPublishedQualification.RunChildAsync(repository, CancellationToken.None).GetAwaiter().GetResult();
        // Separate actual published MadClean app, using its normal UI only.
        if (args.Length == 1 && args[0] == "--published-app-update-validation")
            return PublishedAppUpdateQualification.RunAsync(repository).GetAwaiter().GetResult();
        // Actual app controls and provider, explicit read-only qualification only.
        if (args.Length == 1 && args[0] == "--app-update-ui-validation")
            return AppUpdateUiQualification.RunAsync(repository).GetAwaiter().GetResult();
        // Fixed development read-only online mode, separate from GUI/native fixture lifetime.
        if (args.Length == 1 && args[0] == "--update-discovery")
        {
            using var updateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(220));
            try { return UpdateDiscoveryFixture.RunAsync(repository, updateDeadline.Token).GetAwaiter().GetResult(); }
            finally
            {
                // A static root cannot keep the CLR alive after Main returns. Preserve the
                // actual SDK worker through its real completion without upgrading an earlier
                // pending report or terminating the shared provider. CI VM limits are separate.
                StructuredWingetProbe.WaitForOwnedCompletionAsync().GetAwaiter().GetResult();
            }
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
        // Managed observer only: every instance sees the same actual identity
        // admission, including identity reads from later native fixtures.
        MadClean.Core.ISystemToolActions identityOwnership = new MadClean.Windows.SystemToolActionsProvider();
        var observationBudget = TimeSpan.FromMinutes(4);
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(observationBudget);
        var deadlineFailure = new ValidationDeadline(observationBudget, () =>
        {
            report.Fail("harness-deadline", "The development harness exceeded its four-minute observation budget. Actual owned identity/catalog workers remain held until return; native/provider/GUI completion is not claimed. The separate disposable job limit remains in effect.");
            try { report.Save(); } catch (Exception) { }
        });
        var watchdog = new Timer(_ => deadlineFailure.ObserveElapsed(elapsed.Elapsed),
            null, observationBudget, Timeout.InfiniteTimeSpan);
        try
        {
            // This setting affects only this development process and does not change Windows graphics policy.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            UiCapture.Run(output, report);
            // Application.Run can leave the closed dispatcher context installed on this STA.
            SynchronizationContext.SetSynchronizationContext(null);
            if (!report.Failed && UiCapture.DeviceDriverInteractionSettled) NativeFixtures.Run(repository, report, deadline.Token);
            else if (!UiCapture.DeviceDriverInteractionSettled)
                report.Unknown("fixture-teardown", "The explicit driver UI worker or native cleanup remains unsettled. No owned native fixture tree or later provider query was started.");
            const string startupCheck = "published-executable-startup";
            if (!report.Failed && UiCapture.DeviceDriverInteractionSettled && NativeFixtures.NativeFixtureTreeCleanupConfirmed)
            {
                string source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "";
                if (source.Length != 40 || source.Any(c => !Uri.IsHexDigit(c)))
                    report.Unknown(startupCheck, "Current source commit is unavailable; no published executable was started.");
                else
                {
                    var startup = PublishedExecutableStartupFixture.RunAsync(repository, source.ToLowerInvariant(), deadline.Token).GetAwaiter().GetResult();
                    static string Flag(bool? value) => value switch { true => "yes", false => "no", _ => "?" };
                    static string Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                    string detail = $"state={startup.State}; descriptor={startup.DescriptorValidated}; lease={startup.ExecutableLeased}; started={startup.ProcessStarted}; ownWindow={startup.OwnedWindowObserved}; " +
                        $"runtime={startup.OwnedBundledRuntimeObserved}/{startup.RuntimeRoute}/{startup.RuntimeStage}; runtimeError={Number(startup.RuntimeNativeError)}; modules={Number(startup.RuntimeNeededModuleCount)}/{Flag(startup.RuntimeSnapshotComplete)}/{Flag(startup.RuntimePathsComplete)}; " +
                        $"fxr={Flag(startup.RuntimeHostFxrObserved)}; policy={Flag(startup.RuntimeHostPolicyObserved)}; clr={Flag(startup.RuntimeCoreClrObserved)}; jit={Flag(startup.RuntimeClrJitObserved)}; wpf={Flag(startup.RuntimeWpfNativeObserved)}; apphost={Flag(startup.OwnedAppHostModuleObserved)}; staticProfile={Flag(startup.StaticHostMetadataObserved)}/{startup.StaticHostMetadataStage}; " +
                        $"overview={startup.NeutralOverviewObserved}/{startup.OverviewNoDataObserved}/{startup.OverviewStage}; overviewError={Number(startup.OverviewNativeError)}; close={startup.CloseRequested}; exited={Flag(startup.ExitObserved)}; exit={Number(startup.ExitCode)}; cleanup={startup.CleanupConfirmed}; stage={startup.Stage}; nativeError={Number(startup.NativeError)}. Own process only; no inventory actions, captures or distribution upload.";
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
            AppUpdateUiQualification.ReadIntoReport(repository, Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "", report);
            StructuredWingetPublishedQualification.ReadIntoReport(repository, Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "", report);
        }
        catch (Exception ex)
        {
            report.Fail("harness", $"The harness raised {SmokeAssertionException.SafeContext(ex)} (0x{ex.HResult:X8}); no broad Windows verification is claimed.");
        }
        finally
        {
            // A caller timeout/cancellation and a static root do not keep the CLR
            // alive. Preserve these exact identity/catalog owners, without using a
            // departed dispatcher, terminating a process, or promoting late facts.
            // Other passive fixture owner families are not qualified by this fence.
            while (identityOwnership.HasPendingIdentityWork)
                Thread.Sleep(100);
            NativeFixtures.DrainCatalogWorkersAsync().GetAwaiter().GetResult();
            // Keep the timer active during actual-owner waits, then finish any
            // in-flight callback before the final clock check, save and exit code.
            watchdog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            deadlineFailure.ObserveElapsed(elapsed.Elapsed);
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
