using System.Diagnostics;
using System.Security;
using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.Windows;

/// <summary>
/// Read-only Add/Remove Programs registration inventory. Raw uninstall command values are never read.
/// HKCU Software is shared on supported Windows; HKLM Software has separate 32/64-bit views.
/// Registry enumeration/query buffers have fixed per-field bounds; no full value/name arrays are read.
/// Caller timeout/cancellation does not abort synchronous native calls. The worker alone owns
/// RegistryKeys until return; interrupted waits cannot become complete baselines.
/// </summary>
public sealed class InstalledApplicationInventory : IInstalledApplicationInventory
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int MaximumEntries = 20_000;
    private const int MaximumDisplayedIssues = 20;

    private static int activeWorker;
    internal static bool TryBeginRead() => Interlocked.CompareExchange(ref activeWorker, 1, 0) == 0;
    internal static void EndRead() => Volatile.Write(ref activeWorker, 0);
    internal static bool PendingRead => Volatile.Read(ref activeWorker) != 0;
    public bool HasPendingNativeRead => PendingRead;

    public async Task<CheckResult<InstalledApplication>> ScanAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return CheckResult<InstalledApplication>.Empty(CheckState.Unsupported,
                "Add/Remove Programs inventory is supported on Windows only.");
        if (!TryBeginRead())
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
            finally { EndRead(); }
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
                bool sourceComplete = false;
                var observedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (uint index = 0; ; index++)
                {
                    Check();
                    if (inspected >= MaximumEntries) { limited = true; break; }
                    var keyName = BoundedRegistrationMetadata.EnumerateKey(registrations.Handle, index, Check);
                    if (keyName.End) { sourceComplete = true; break; }
                    inspected++;
                    if (keyName.Name == null)
                    {
                        Issue(source.Identity, keyName.Issue + $" (native code {keyName.NativeError})");
                        // A malformed returned name is one omitted entry and the index advances.
                        // A native enumeration failure cannot establish the rest of this source.
                        if (keyName.NativeError != 0) break;
                        continue;
                    }
                    string subkeyName = keyName.Name;
                    if (!observedNames.Add(subkeyName))
                    { Issue(source.Identity, "registration name repeated during non-atomic enumeration; repeated entry omitted"); continue; }
                    string identity = source.Identity + "/" + UninstallKey + "\\" + subkeyName;
                    string issueLabel = source.Identity + "/" + SafeIssueLabel(subkeyName);
                    try
                    {
                        Check();
                        using var entry = registrations.OpenSubKey(subkeyName, writable: false);
                        Check();
                        if (entry == null) { Issue(issueLabel, "registration changed during inventory; entry not read"); continue; }
                        bool? ReadFlag(string valueName)
                        {
                            var result = BoundedRegistrationMetadata.ReadFlag(entry.Handle, valueName, Check, token);
                            if (result.State == RegistrationMetadataState.Missing) return false;
                            if (result.State == RegistrationMetadataState.Valid) return result.Value;
                            Issue(issueLabel, valueName + ": " + result.Issue + $" (native code {result.NativeError}); flag remains unknown");
                            return null;
                        }
                        // Every helper enforces a fixed field whitelist. Uninstall command contents
                        // cannot pass ReadText/ReadFlag and are never requested from the registry.
                        bool? component = ReadFlag("SystemComponent");
                        if (component == true) { hidden++; continue; }
                        if (component == null) continue; // Unknown hidden-component status is not displayed as a normal app.
                        var rawName = BoundedRegistrationMetadata.ReadText(entry.Handle, "DisplayName", Check, token);
                        if (rawName.State == RegistrationMetadataState.Missing ||
                            rawName.State == RegistrationMetadataState.Valid && string.IsNullOrWhiteSpace(rawName.Value))
                        { unnamed++; continue; }
                        if (rawName.State != RegistrationMetadataState.Valid || rawName.Value == null)
                        { Issue(issueLabel, "DisplayName: " + rawName.Issue + $" (native code {rawName.NativeError}); entry omitted"); continue; }
                        string name = rawName.Value;

                        string ReadText(string valueName)
                        {
                            var result = BoundedRegistrationMetadata.ReadText(entry.Handle, valueName, Check, token);
                            if (result.State == RegistrationMetadataState.Invalid)
                                Issue(issueLabel, valueName + ": " + result.Issue + $" (native code {result.NativeError}); value remains unknown");
                            return result.State == RegistrationMetadataState.Valid && !string.IsNullOrWhiteSpace(result.Value) ? result.Value : "Unknown";
                        }
                        string version = ReadText("DisplayVersion");
                        string publisher = ReadText("Publisher");
                        string? installLocation = null;
                        var rawLocation = BoundedRegistrationMetadata.ReadText(entry.Handle, "InstallLocation", Check, token);
                        if (rawLocation.State == RegistrationMetadataState.Invalid)
                            Issue(issueLabel, "InstallLocation: " + rawLocation.Issue + $" (native code {rawLocation.NativeError}); path omitted");
                        else if (rawLocation.State == RegistrationMetadataState.Valid && !string.IsNullOrWhiteSpace(rawLocation.Value))
                        {
                            installLocation = InstalledApplicationPaths.NormalizeLocalDirectory(rawLocation.Value);
                            if (installLocation != null)
                            {
                                try
                                {
                                    Check();
                                    var drive = new DriveInfo(installLocation[..3]);
                                    var driveType = drive.DriveType;
                                    Check();
                                    if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)) installLocation = null;
                                }
                                catch (Exception ex) when (IsReadError(ex)) { installLocation = null; }
                            }
                            if (installLocation == null) Issue(issueLabel, "InstallLocation is not a verified local absolute directory; path omitted");
                        }
                        // lpData stays NULL for these two exact presence queries. Value contents,
                        // even huge or malformed commands, are neither allocated nor inspected.
                        var uninstall = BoundedRegistrationMetadata.ProbeUninstallerName(entry.Handle, "UninstallString", Check);
                        var quiet = BoundedRegistrationMetadata.ProbeUninstallerName(entry.Handle, "QuietUninstallString", Check);
                        if (uninstall.Exists == null) Issue(issueLabel, uninstall.Issue + $" (native code {uninstall.NativeError})");
                        if (quiet.Exists == null) Issue(issueLabel, quiet.Issue + $" (native code {quiet.NativeError})");
                        bool? hasRegisteredUninstaller = uninstall.Exists == true || quiet.Exists == true ? true :
                            uninstall.Exists.HasValue && quiet.Exists.HasValue ? false : null;
                        bool? installer = ReadFlag("WindowsInstaller");
                        bool? removalRestricted = ReadFlag("NoRemove");
                        string support = removalRestricted == true ? InstalledApplicationSupport.Describe(false, false, true) :
                            removalRestricted.HasValue && installer.HasValue && hasRegisteredUninstaller.HasValue
                            ? InstalledApplicationSupport.Describe(installer.Value, hasRegisteredUninstaller.Value, removalRestricted.Value)
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
                if (sourceComplete) completedSources++;
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
