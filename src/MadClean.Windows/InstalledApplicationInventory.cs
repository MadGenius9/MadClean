using System.Security;
using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.Windows;

/// <summary>
/// Read-only Add/Remove Programs registration inventory. Raw uninstall command values are never read.
/// HKCU Software is shared on supported Windows; HKLM Software has separate 32/64-bit views.
/// </summary>
public sealed class InstalledApplicationInventory : IInstalledApplicationInventory
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int MaximumEntries = 20_000;
    private const int MaximumDisplayedIssues = 20;

    public Task<CheckResult<InstalledApplication>> ScanAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult<InstalledApplication>.Empty(CheckState.Unsupported,
                "Add/Remove Programs inventory is supported on Windows only."));
        return Task.Run(() => Scan(token), token);
    }

    private static CheckResult<InstalledApplication> Scan(CancellationToken token)
    {
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
            token.ThrowIfCancellationRequested();
            try
            {
                using var hive = RegistryKey.OpenBaseKey(source.Hive, source.View);
                using var registrations = hive.OpenSubKey(UninstallKey, writable: false);
                if (registrations == null) { completedSources++; continue; }
                foreach (string subkeyName in registrations.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    if (inspected++ == MaximumEntries) { limited = true; break; }
                    string identity = source.Identity + "/" + UninstallKey + "\\" + subkeyName;
                    string issueLabel = source.Identity + "/" + SafeIssueLabel(subkeyName);
                    try
                    {
                        using var entry = registrations.OpenSubKey(subkeyName, writable: false);
                        if (entry == null) { Issue(issueLabel, "registration changed during inventory; entry not read"); continue; }
                        // Only flags and documented display/install metadata are read. In particular,
                        // GetValue is NEVER called for UninstallString or QuietUninstallString.
                        if (entry.GetValue("SystemComponent", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int component && component == 1)
                        { hidden++; continue; }
                        var rawName = entry.GetValue("DisplayName", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (rawName == null || rawName is string emptyName && string.IsNullOrWhiteSpace(emptyName)) { unnamed++; continue; }
                        if (rawName is not string name || name.Length > 1_024)
                        { Issue(issueLabel, "DisplayName is unsupported or too long; entry omitted"); continue; }

                        string ReadText(string valueName, int maximum)
                        {
                            var raw = entry.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            if (raw == null || raw is string blank && string.IsNullOrWhiteSpace(blank)) return "Unknown";
                            if (raw is string text && text.Length <= maximum) return text;
                            Issue(issueLabel, valueName + " has an unsupported metadata type or length; value omitted");
                            return "Unknown";
                        }
                        string version = ReadText("DisplayVersion", 256);
                        string publisher = ReadText("Publisher", 512);
                        string? installLocation = null;
                        var rawLocation = entry.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (rawLocation is string location && !string.IsNullOrWhiteSpace(location))
                        {
                            installLocation = InstalledApplicationPaths.NormalizeLocalDirectory(location);
                            if (installLocation != null)
                            {
                                try
                                {
                                    var drive = new DriveInfo(installLocation[..3]);
                                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
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
                        var names = entry.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                        bool hasRegisteredUninstaller = names.Contains("UninstallString") || names.Contains("QuietUninstallString");
                        var rawInstaller = entry.GetValue("WindowsInstaller", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        bool? installer = rawInstaller switch { null => false, 0 => false, 1 => true, _ => null };
                        if (installer == null) Issue(issueLabel, "WindowsInstaller has an unsupported flag value; mechanism remains unknown");
                        // ARPNOREMOVE writes NoRemove and disables supported ARP removal even when
                        // WindowsInstaller or stale uninstaller value names are still present.
                        var rawNoRemove = entry.GetValue("NoRemove", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        bool? removalRestricted = rawNoRemove switch { null => false, 0 => false, 1 => true, _ => null };
                        if (removalRestricted == null)
                            Issue(issueLabel, "NoRemove has an unsupported flag value; uninstall support remains unknown");
                        string support = removalRestricted.HasValue && installer.HasValue
                            ? InstalledApplicationSupport.Describe(installer.Value, hasRegisteredUninstaller, removalRestricted.Value)
                            : "Unknown";
                        var evidence = new InstalledAppRegistrationEvidence(identity, source.Label, removalRestricted, installer,
                            hasRegisteredUninstaller, installer == true ? InstalledAppActionPlans.ProductCodeFromSubkey(subkeyName) : null, observedUtc);
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
        return new(issueCount > 0 || limited ? CheckState.Partial : CheckState.Available, message, applications);
    }

    private static string SafeIssueLabel(string value) => new(value.Take(160).Select(c => char.IsControl(c) ? '?' : c).ToArray());
    private static bool IsReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or
        ArgumentException or NotSupportedException or InvalidOperationException;
}
