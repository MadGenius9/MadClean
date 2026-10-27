using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.Windows;

/// <summary>Explicit, bounded Windows-only content observations. No deletion or persistent hashes.</summary>
public sealed class WindowsDuplicateInventory : IDuplicateFileInventory
{
    public Task<CheckResult<DuplicateReport>> ScanAsync(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult<DuplicateReport>.Empty(CheckState.Unsupported,
                "Duplicate discovery requires supported Windows file identities and stable read handles. No content scan ran on this platform."));
        return Task.Run(() => new DuplicateDiscoveryEngine().Scan(root, new WindowsAccess(), token), token);
    }

    private sealed class WindowsAccess : IDuplicateFileAccess
    {
        private const int EntryLimit = 10_000, DepthLimit = 32;
        private readonly List<string> protectedRoots = [];
        private int entries;
        private string selectedRoot = "";
        private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "WindowsApps",
            "Cache", "Caches", "Temp", "Temporary Internet Files", ".git", ".svn", ".hg", ".nuget", "node_modules",
            "Credentials", "Vault", "Protect", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".config", ".codex", ".agents", "Saves", "Saved Games", "steamapps",
            "Profiles", "User Data", "Chrome", "Chromium", "Edge", "Firefox", "Brave-Browser", "Opera"
        };
        private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".sys", ".msi", ".msp", ".com", ".scr", ".ps1", ".bat", ".cmd", ".lnk", ".url",
            ".key", ".pem", ".pfx", ".p12", ".kdbx", ".sav", ".db", ".sqlite", ".sqlite3"
        };
        private static readonly string[] SensitiveNames = ["credential", "password", "token", "secret", "login", "cookie", "session", "wallet"];

        public IEnumerable<DuplicateScanItem> Enumerate(string root, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string? rootError = null;
            try
            {
                selectedRoot = NormalizeLocalRoot(root);
                AddKnownRoots();
                if (Protected(selectedRoot)) rootError = "The selected root is a protected/system/application/cache/credential location; select a different local data folder.";
            }
            catch (Exception ex) when (ReadError(ex)) { rootError = "Select an existing absolute fixed-local data folder, not a drive root, network/device path or protected area."; }
            if (rootError != null) { yield return new(root ?? "", false, rootError); yield break; }

            bool registrationIncomplete = AddRegisteredRoots(token);
            if (Protected(selectedRoot))
            { yield return new(selectedRoot, false, "The selected root is within a known registered application installation; excluded without traversal."); yield break; }
            if (registrationIncomplete)
                yield return new(selectedRoot, false, "Registered application-location exclusions are incomplete or limited. Known locations remain protected; unknown ownership is not evidence of safe removal.");
            foreach (var item in Walk(selectedRoot, 0, token)) yield return item;
        }

        public IDuplicateReadHandle Open(string path, bool content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Within(path, selectedRoot) || Protected(path) || ProtectedExtensions.Contains(Path.GetExtension(path)) || Sensitive(path))
                throw new DuplicateReadException("Path is outside the selected scope or protected by the default application/credential policy.");
            return DuplicateWindowsNative.Open(path, content, token);
        }

        private IEnumerable<DuplicateScanItem> Walk(string directory, int depth, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            DuplicateWindowsNative.DirectoryGuard? guard = null;
            string? error = null;
            try
            {
                guard = DuplicateWindowsNative.GuardDirectories(directory, token);
                if ((DuplicateWindowsNative.DirectoryAttributes(guard) & DuplicateWindowsNative.Unsafe) != 0)
                    error = "Protected/hidden/offline/recall/encrypted directory excluded without content reads.";
            }
            catch (Exception ex) when (ReadError(ex)) { error = "Folder/ancestor is inaccessible, locked, reparse/offline or changed; subtree was not scanned."; }
            if (error != null) { guard?.Dispose(); yield return new(directory, false, error); yield break; }
            using (guard)
            {
                using var iterator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    bool next = false; string path = "";
                    try { next = iterator.MoveNext(); if (next) path = iterator.Current; }
                    catch (Exception ex) when (ReadError(ex)) { error = "Directory enumeration failed or changed; remaining coverage is unknown."; }
                    if (error != null) { yield return new(directory, false, error); yield break; }
                    if (!next) break;
                    if (++entries > EntryLimit)
                    { yield return new(directory, false, "The directory-entry limit was reached. Select a smaller folder; unvisited entries are unknown.", true); yield break; }
                    FileAttributes attributes = default;
                    try { attributes = File.GetAttributes(path); }
                    catch (Exception ex) when (ReadError(ex)) { error = "Entry metadata is inaccessible or changed; it was not opened for contents."; }
                    if (error != null) { yield return new(path, false, error); error = null; continue; }
                    bool file = (attributes & FileAttributes.Directory) == 0;
                    if ((attributes & DuplicateWindowsNative.Unsafe) != 0 || Protected(path) || Sensitive(path) || (file && ProtectedExtensions.Contains(Path.GetExtension(path))))
                    { yield return new(path, file, "Entry excluded by protected/application/credential/reparse/offline/active-content policy; no contents read."); continue; }
                    if (!file)
                    {
                        if (depth >= DepthLimit) { yield return new(path, false, "The directory-depth limit was reached. Descendants remain unchecked.", true); continue; }
                        foreach (var item in Walk(path, depth + 1, token)) yield return item;
                        if (entries > EntryLimit) yield break;
                    }
                    else yield return new(path, true);
                }
            }
        }

        private static string NormalizeLocalRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || root.Length > 32767 || root.Any(char.IsControl) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException();
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var volume = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(volume) || full.Length <= volume.Length || full[2..].Contains(':') || new DriveInfo(volume).DriveType != DriveType.Fixed)
                throw new ArgumentException();
            return full;
        }
        private void AddKnownRoots()
        {
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
            {
                var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
                if (Path.IsPathFullyQualified(path)) protectedRoots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
        }
        private bool AddRegisteredRoots(CancellationToken token)
        {
            bool incomplete = false; int visited = 0;
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in hive == RegistryHive.CurrentUser ? new[] { RegistryView.Default } : new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var registrations = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", false);
                    if (registrations == null) continue;
                    if (registrations.SubKeyCount > 10_000) { incomplete = true; continue; }
                    foreach (var name in registrations.GetSubKeyNames())
                    {
                        token.ThrowIfCancellationRequested();
                        if (++visited > 10_000 || protectedRoots.Count >= 2048) return true;
                        try
                        {
                            using var registration = registrations.OpenSubKey(name, false);
                            if (registration == null) { incomplete = true; continue; }
                            var raw = registration.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            if (raw == null || raw is string empty && string.IsNullOrWhiteSpace(empty)) continue;
                            if (raw is not string location || location.Length > 4096 || location.Any(char.IsControl) || !Path.IsPathFullyQualified(location) || location.StartsWith(@"\\", StringComparison.Ordinal))
                            { incomplete = true; continue; }
                            protectedRoots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)));
                        }
                        catch (Exception ex) when (ReadError(ex)) { incomplete = true; }
                    }
                }
                catch (Exception ex) when (ReadError(ex)) { incomplete = true; }
            }
            return incomplete;
        }
        private bool Protected(string path)
        {
            if (protectedRoots.Any(root => Within(path, root))) return true;
            for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (ProtectedNames.Contains(Path.GetFileName(Path.TrimEndingDirectorySeparator(current)))) return true;
            return false;
        }
        private static bool Sensitive(string path) => SensitiveNames.Any(part => Path.GetFileName(path).Contains(part, StringComparison.OrdinalIgnoreCase));
        private static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static bool ReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or InvalidOperationException;
    }
}
