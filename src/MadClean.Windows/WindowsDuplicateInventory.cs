using MadClean.Core;
using Microsoft.Win32;

namespace MadClean.Windows;

/// <summary>Explicit, bounded Windows-only content observations. No deletion or persistent hashes.</summary>
public sealed partial class WindowsDuplicateInventory : IDuplicateFileInventory
{
    private static int activeFileRead;
    public static bool HasPendingFileRead => Volatile.Read(ref activeFileRead) != 0;
    public static bool HasPendingPairRead => HasPendingFileRead;

    public Task<CheckResult<DuplicateReport>> ScanAsync(string root, CancellationToken token)
        => RunOwnedFileReadAsync(stop => new DuplicateDiscoveryEngine().Scan(root, new WindowsAccess(), stop), token);

    private static async Task<CheckResult<T>> RunOwnedFileReadAsync<T>(Func<CancellationToken, CheckResult<T>> read,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return CheckResult<T>.Empty(CheckState.Unsupported,
                "Duplicate observations require supported Windows file identities and guarded read handles. No content read ran on this platform.");
        if (Interlocked.CompareExchange(ref activeFileRead, 1, 0) != 0)
            return CheckResult<T>.Empty(CheckState.Unavailable,
                "A duplicate read worker is still finishing. Wait for it to release its handles before trying again.");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<CheckResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Cancellation must never prevent the owner from starting and releasing its gate.
        _ = Task.Run(() =>
        {
            CheckResult<T> result;
            try
            {
                result = read(stop.Token);
                stop.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            { result = CheckResult<T>.Empty(CheckState.Cancelled, "The duplicate read was cancelled; no action ran."); }
            catch (Exception)
            { result = CheckResult<T>.Empty(CheckState.Failed, "The duplicate read worker failed unexpectedly. Retry the query; no action ran and no complete result was accepted."); }
            finally { stop.Dispose(); Volatile.Write(ref activeFileRead, 0); }
            completion.TrySetResult(result);
        }, CancellationToken.None);
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (TimeoutException)
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return CheckResult<T>.Empty(CheckState.Unavailable,
                "The caller wait expired. The duplicate read worker retains its handles until its pending read finishes; no action ran.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return CheckResult<T>.Empty(CheckState.Cancelled,
                "Cancellation was requested. The duplicate read worker retains its handles until its pending read finishes; no action ran.");
        }
    }

    private sealed class WindowsAccess : IDuplicateFileAccess
    {
        private const int EntryLimit = 10_000, DepthLimit = 32;
        private readonly List<string> protectedRoots = [];
        private int entries;
        private string selectedRoot = "";
        public CheckState CurrentExclusionCoverage { get; private set; } = CheckState.Unknown;
        private DuplicatePairReadStage firstStage = DuplicatePairReadStage.NotStarted;
        private DuplicatePairReadReason firstReason = DuplicatePairReadReason.None;
        private int? firstRegistryResult;
        private int knownFoldersChecked, registrationKeysVisited, registeredLocationsAccepted, exclusionIssues;
        public DuplicatePairReadDiagnostic PreparationDiagnostic => new(firstStage, firstReason, firstRegistryResult,
            knownFoldersChecked, registrationKeysVisited, registeredLocationsAccepted, exclusionIssues);
        private void PreparationIssue(DuplicatePairReadStage stage, DuplicatePairReadReason reason, int? registryResult = null)
        {
            exclusionIssues++;
            if (firstReason != DuplicatePairReadReason.None) return;
            firstStage = stage; firstReason = reason; firstRegistryResult = registryResult;
        }
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
            bool exclusionsIncomplete = false;
            try { exclusionsIncomplete = PrepareScope(root, token, requireEssentialKnownRoots: false); }
            catch (Exception ex) when (ReadError(ex))
            { rootError = ex is DuplicateReadException ? ex.Message : "Select an existing absolute fixed-local data folder, not a drive root, network/device path or protected area."; }
            if (rootError != null) { yield return new(root ?? "", false, rootError); yield break; }
            if (exclusionsIncomplete)
                yield return new(selectedRoot, false, "Known-folder or registered application-location exclusions are incomplete or limited. Known locations remain protected; unknown ownership is not evidence of safe removal.");
            foreach (var item in Walk(selectedRoot, 0, token)) yield return item;
        }

        public bool PrepareScope(string root, CancellationToken token, bool requireEssentialKnownRoots)
        {
            token.ThrowIfCancellationRequested();
            protectedRoots.Clear(); entries = 0; CurrentExclusionCoverage = CheckState.Unknown;
            firstStage = DuplicatePairReadStage.NotStarted; firstReason = DuplicatePairReadReason.None; firstRegistryResult = null;
            knownFoldersChecked = registrationKeysVisited = registeredLocationsAccepted = exclusionIssues = 0;
            try { selectedRoot = NormalizeLocalRoot(root); }
            catch (Exception exception) when (ReadError(exception))
            {
                PreparationIssue(DuplicatePairReadStage.ScopeValidation, DuplicatePairReadReason.ScopeUnsupported);
                throw new DuplicateReadException("The selected scope is not an available supported fixed-local folder; no selected file contents were read.");
            }
            bool knownIncomplete = AddKnownRoots(requireEssentialKnownRoots, token);
            if (Protected(selectedRoot))
            {
                PreparationIssue(DuplicatePairReadStage.ScopeValidation, DuplicatePairReadReason.SelectedRootProtected);
                throw new DuplicateReadException("The selected root is a protected/system/application/cache/credential location; select a different local data folder.");
            }
            bool registrationIncomplete = AddRegisteredRoots(token);
            CurrentExclusionCoverage = knownIncomplete ? CheckState.Unknown
                : registrationIncomplete ? CheckState.Partial : CheckState.Available;
            if (Protected(selectedRoot))
            {
                PreparationIssue(DuplicatePairReadStage.ScopeValidation, DuplicatePairReadReason.SelectedRootProtected);
                throw new DuplicateReadException("The selected root is within a known registered application installation; excluded without traversal.");
            }
            if (knownIncomplete && requireEssentialKnownRoots)
                throw new DuplicateReadException("Required known-folder exclusions could not be resolved completely; no selected file contents were read. Retry when that metadata is available.");
            // This is read-only evidence: incomplete registration metadata remains
            // Partial and never establishes absence of application ownership or action safety.
            return knownIncomplete || registrationIncomplete;
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
        private bool AddKnownRoots(bool requireLocalRoots, CancellationToken token)
        {
            bool incomplete = false;
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
            {
                token.ThrowIfCancellationRequested();
                knownFoldersChecked++;
                try
                {
                    var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
                    if (!Path.IsPathFullyQualified(path))
                    {
                        incomplete = true;
                        PreparationIssue(DuplicatePairReadStage.KnownFolderExclusions, DuplicatePairReadReason.KnownFolderMissing);
                        continue;
                    }
                    protectedRoots.Add(requireLocalRoots ? NormalizeLocalRoot(path) : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
                }
                catch (Exception ex) when (ReadError(ex))
                {
                    incomplete = true;
                    PreparationIssue(DuplicatePairReadStage.KnownFolderExclusions, DuplicatePairReadReason.KnownFolderUnsupported);
                }
            }
            return incomplete;
        }
        private bool AddRegisteredRoots(CancellationToken token)
        {
            bool incomplete = false;
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in hive == RegistryHive.CurrentUser ? new[] { RegistryView.Default } : new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var registrations = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", false);
                    if (registrations == null) continue;
                    for (uint index = 0; ; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        var key = BoundedRegistrationMetadata.EnumerateKey(registrations.Handle, index, token.ThrowIfCancellationRequested);
                        if (key.End) break;
                        registrationKeysVisited++;
                        if (registrationKeysVisited > 10_000 || protectedRoots.Count >= 2048)
                        {
                            PreparationIssue(DuplicatePairReadStage.RegistrationExclusions, DuplicatePairReadReason.ExclusionLimit);
                            return true;
                        }
                        if (key.Name == null)
                        {
                            incomplete = true;
                            PreparationIssue(DuplicatePairReadStage.RegistrationExclusions, DuplicatePairReadReason.RegistrationEnumerationInvalid, key.NativeError);
                            continue;
                        }
                        try
                        {
                            using var registration = registrations.OpenSubKey(key.Name, false);
                            if (registration == null)
                            {
                                incomplete = true;
                                PreparationIssue(DuplicatePairReadStage.RegistrationExclusions, DuplicatePairReadReason.RegistrationEntryUnavailable);
                                continue;
                            }
                            var raw = BoundedRegistrationMetadata.ReadText(registration.Handle, "InstallLocation", token.ThrowIfCancellationRequested, token);
                            if (raw.State == RegistrationMetadataState.Missing) continue;
                            if (raw.State != RegistrationMetadataState.Valid || raw.Value is not string location)
                            {
                                incomplete = true;
                                PreparationIssue(DuplicatePairReadStage.RegistrationExclusions, DuplicatePairReadReason.RegistrationLocationInvalid, raw.NativeError);
                                continue;
                            }
                            if (string.IsNullOrWhiteSpace(location)) continue;
                            string? normalized = NormalizeRegisteredRoot(location);
                            if (normalized == null)
                            {
                                incomplete = true;
                                PreparationIssue(DuplicatePairReadStage.RegistrationExclusions, DuplicatePairReadReason.RegistrationLocationUnsupported);
                                continue;
                            }
                            protectedRoots.Add(normalized); registeredLocationsAccepted++;
                        }
                        catch (Exception ex) when (ReadError(ex))
                        {
                            incomplete = true;
                            PreparationIssue(DuplicatePairReadStage.RegistrationExclusions, DuplicatePairReadReason.RegistrationEntryUnavailable);
                        }
                    }
                }
                catch (Exception ex) when (ReadError(ex))
                {
                    incomplete = true;
                    PreparationIssue(DuplicatePairReadStage.RegistrationExclusions, DuplicatePairReadReason.RegistrationSourceUnavailable);
                }
            }
            return incomplete;
        }
        private static string? NormalizeRegisteredRoot(string location)
        {
            // Preserve unexpanded metadata as unknown. Never interpret environment,
            // device, ADS or malformed components as a complete exclusion source.
            if (location.Length > 4096 || location != location.Trim()) return null;
            string path = location.Replace('/', '\\');
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
                path.AsSpan(2).IndexOfAny(':', '%') >= 0) return null;
            string trimmed = Path.TrimEndingDirectorySeparator(path);
            if (trimmed.Length == 3) return char.ToUpperInvariant(path[0]) + ":\\";
            foreach (string part in trimmed[3..].Split('\\'))
            {
                // Reuse the strict Unicode/device/component policy without imposing
                // its short *whole shortcut target* cap on a bounded registry path.
                string component = "C:\\" + part;
                if (part.Length == 0 || !string.Equals(component,
                    ShortcutPathPolicy.NormalizeLocalPath(component, false), StringComparison.Ordinal)) return null;
            }
            return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
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
