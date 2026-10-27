using System.Diagnostics;
using System.Security;
using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.Windows;

/// <summary>
/// Read-only Add/Remove Programs registration inventory. Raw uninstall command values are never read.
/// HKCU Software is shared on supported Windows; HKLM Software has separate 32/64-bit views.
/// Caller timeout/cancellation does not abort synchronous registry calls or bound their allocations:
/// GetSubKeyNames/GetValueNames still allocate complete per-call arrays before the cooperative checks.
/// The worker alone owns RegistryKeys until return; interrupted waits cannot become complete baselines.
/// </summary>
public sealed class InstalledApplicationInventory : IInstalledApplicationInventory
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int MaximumEntries = 20_000;
    private const int MaximumDisplayedIssues = 20;

    private static int activeWorker;
    public bool HasPendingNativeRead => Volatile.Read(ref activeWorker) != 0;

    public async Task<CheckResult<InstalledApplication>> ScanAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return CheckResult<InstalledApplication>.Empty(CheckState.Unsupported,
                "Add/Remove Programs inventory is supported on Windows only.");
        if (Interlocked.CompareExchange(ref activeWorker, 1, 0) != 0)
            return CheckResult<InstalledApplication>.Empty(CheckState.Unknown,
                "A previous native registry inventory is still pending; wait before retrying. No new inventory started, and no complete baseline or absence was established.");
        // Never pass the caller token as a scheduling token: even pre-start cancellation must
        // run the worker's finally and release the gate after its owned keys have been disposed.
        var worker = Task.Run(() =>
        {
            try { return Scan(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return CheckResult<InstalledApplication>.Empty(CheckState.Cancelled,
                "The registry worker observed cancellation; no completed inventory, baseline or registration-absence conclusion is shown."); }
            catch (InventoryBudgetExceededException)
            { return CheckResult<InstalledApplication>.Empty(CheckState.Unknown,
                "The registry worker's cooperative 20-second budget elapsed; no completed inventory, baseline or registration-absence conclusion is shown. Retry when the system is responsive."); }
            finally { Volatile.Write(ref activeWorker, 0); }
        }, CancellationToken.None);
        // Observe late faults without logging registry/provider text or abandoning key ownership.
        _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var result = await worker.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (TimeoutException)
        { return CheckResult<InstalledApplication>.Empty(CheckState.Unknown,
            "The caller's 30-second registry wait limit elapsed; no completed inventory, baseline or registration-absence conclusion is shown. " + NativeState(worker)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return CheckResult<InstalledApplication>.Empty(CheckState.Cancelled,
            "Cancellation was requested for this view; no completed inventory, baseline or registration-absence conclusion is shown. This does not prove a synchronous registry call stopped. " + NativeState(worker)); }
    }

    private static CheckResult<InstalledApplication> Scan(CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= TimeSpan.FromSeconds(20)) throw new InventoryBudgetExceededException();
        }
        Check();
        List<InstalledApplication> applications = [];
        List<string> issues = [];
        int issueCount = 0, inspected = 0, hidden = 0, unnamed = 0, completedSources = 0;
        bool limited = false;
        DateTimeOffset observedUtc = DateTimeOffset.UtcNow;
        void Issue(string location, string description)
        {
            issueCount++;
            if (issues.Count < MaximumDisplayedIssues) issues.Add($"{location}: {description}");
        }

        var sources = new List<(RegistryHive Hive, RegistryView View, string Identity, string Label)>
        {
            (RegistryHive.CurrentUser, RegistryView.Default, "HKCU/shared", "HKCU / shared / Add or Remove Programs")
        };
        if (Environment.Is64BitOperatingSystem)
        {
            sources.Add((RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM/64", "HKLM / 64-bit / Add or Remove Programs"));
            sources.Add((RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM/32", "HKLM / 32-bit / Add or Remove Programs"));
        }
        else sources.Add((RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM/32", "HKLM / 32-bit / Add or Remove Programs"));

        foreach (var source in sources)
        {
            Check();
            try
            {
                Check();
                using var hive = RegistryKey.OpenBaseKey(source.Hive, source.View);
                Check();
                using var registrations = hive.OpenSubKey(UninstallKey, writable: false);
                Check();
                if (registrations == null) { completedSources++; continue; }
                // The array allocation itself is not bounded by the caller wait/entry count.
                Check();
                var subkeys = registrations.GetSubKeyNames();
                Check();
                foreach (string subkeyName in subkeys)
                {
                    Check();
                    if (inspected++ == MaximumEntries) { limited = true; break; }
                    string identity = source.Identity + "/" + UninstallKey + "\\" + subkeyName;
                    string issueLabel = source.Identity + "/" + SafeIssueLabel(subkeyName);
                    try
                    {
                        Check();
                        using var entry = registrations.OpenSubKey(subkeyName, writable: false);
                        Check();
                        if (entry == null) { Issue(issueLabel, "registration changed during inventory; entry not read"); continue; }
                        object? ReadValue(string valueName)
                        {
                            Check();
                            var value = entry.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            Check();
                            return value;
                        }
                        // Only flags and documented display/install metadata are read. In particular,
                        // GetValue is NEVER called for UninstallString or QuietUninstallString.
                        if (ReadValue("SystemComponent") is int component && component == 1)
                        { hidden++; continue; }
                        var rawName = ReadValue("DisplayName");
                        if (rawName == null || rawName is string emptyName && string.IsNullOrWhiteSpace(emptyName)) { unnamed++; continue; }
                        if (rawName is not string name || name.Length > 1_024)
                        { Issue(issueLabel, "DisplayName is unsupported or too long; entry omitted"); continue; }

                        string ReadText(string valueName, int maximum)
                        {
                            var raw = ReadValue(valueName);
                            if (raw == null || raw is string blank && string.IsNullOrWhiteSpace(blank)) return "Unknown";
                            if (raw is string text && text.Length <= maximum) return text;
                            Issue(issueLabel, valueName + " has an unsupported metadata type or length; value omitted");
                            return "Unknown";
                        }
                        string version = ReadText("DisplayVersion", 256);
                        string publisher = ReadText("Publisher", 512);
                        string? installLocation = null;
                        var rawLocation = ReadValue("InstallLocation");
                        if (rawLocation is string location && !string.IsNullOrWhiteSpace(location))
                        {
                            installLocation = InstalledApplicationPaths.NormalizeLocalDirectory(location);
                            if (installLocation != null)
                            {
                                try
                                {
                                    Check();
                                    var drive = new DriveInfo(installLocation[..3]);
                                    var driveType = drive.DriveType;
                                    Check();
                                    if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                                        installLocation = null;
                                }
                                catch (Exception ex) when (IsReadError(ex)) { installLocation = null; }
                            }
                            if (installLocation == null) Issue(issueLabel, "InstallLocation is not a verified local absolute directory; path omitted");
                        }
                        else if (rawLocation != null && rawLocation is not string)
                            Issue(issueLabel, "InstallLocation has an unsupported metadata type; path omitted");

                        // Presence is a hint only: the command might be empty, broken, obsolete or blocked.
                        // No command text, arguments, executable or registry estimate is collected.
                        Check();
                        var valueNames = entry.GetValueNames();
                        Check();
                        var names = valueNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        Check();
                        bool hasRegisteredUninstaller = names.Contains("UninstallString") || names.Contains("QuietUninstallString");
                        var rawInstaller = ReadValue("WindowsInstaller");
                        bool? installer = rawInstaller switch { null => false, 0 => false, 1 => true, _ => null };
                        if (installer == null) Issue(issueLabel, "WindowsInstaller has an unsupported flag value; mechanism remains unknown");
                        // ARPNOREMOVE writes NoRemove and disables supported ARP removal even when
                        // WindowsInstaller or stale uninstaller value names are still present.
                        var rawNoRemove = ReadValue("NoRemove");
                        bool? removalRestricted = rawNoRemove switch { null => false, 0 => false, 1 => true, _ => null };
                        if (removalRestricted == null)
                            Issue(issueLabel, "NoRemove has an unsupported flag value; uninstall support remains unknown");
                        string support = removalRestricted.HasValue && installer.HasValue
                            ? InstalledApplicationSupport.Describe(installer.Value, hasRegisteredUninstaller, removalRestricted.Value)
                            : "Unknown";
                        var evidence = new InstalledAppRegistrationEvidence(identity, source.Label, removalRestricted, installer,
                            hasRegisteredUninstaller, installer == true ? InstalledAppActionPlans.ProductCodeFromSubkey(subkeyName) : null, observedUtc);
                        Check();
                        applications.Add(new(identity, name, version, publisher, source.Label, installLocation, support, evidence));
                    }
                    catch (Exception ex) when (IsReadError(ex))
                    { Issue(issueLabel, $"entry could not be read (code 0x{ex.HResult:X8}); no action was attempted"); }
                }
                if (limited) break;
                completedSources++;
            }
            catch (Exception ex) when (IsReadError(ex))
            { Issue(source.Identity, $"registry source could not be read (code 0x{ex.HResult:X8}); check current-user access"); }
        }
        if (limited) Issue("Inventory", "entry limit reached; additional registrations were not read");
        string message = $"Read-only Add/Remove Programs inventory: {applications.Count} entries; {completedSources}/{sources.Count} sources completed. " +
            $"Skipped {hidden} SystemComponent=1 and {unnamed} entries without DisplayName. " +
            "Coverage excludes portable/unregistered apps and packaged applications without these registrations. Uninstaller hints indicate registration only; no uninstall action or command was executed, and no size was inferred. " +
            $"{issueCount} metadata/source/entry issues.";
        if (issues.Count > 0) message += " " + string.Join(" | ", issues);
        if (issueCount > issues.Count) message += $" | {issueCount - issues.Count} additional issues omitted from this bounded report.";
        Check();
        return new(issueCount > 0 || limited ? CheckState.Partial : CheckState.Available, message, applications);
    }

    private sealed class InventoryBudgetExceededException : Exception { }
    private static string NativeState(Task worker) => worker.IsCompleted
        ? "The underlying registry worker has returned; no result was inferred from the interrupted wait."
        : "A synchronous registry call may still be running. Its worker retains all RegistryKeys and blocks another inventory until it actually returns; no force-close or abort was attempted.";

    private static string SafeIssueLabel(string value) => new(value.Take(160).Select(c => char.IsControl(c) ? '?' : c).ToArray());
    private static bool IsReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or
        ArgumentException or NotSupportedException or InvalidOperationException;
}
