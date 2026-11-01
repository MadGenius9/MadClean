namespace MadClean.Core;

// No deletion API exists. An inventory candidate is never a promise that deletion is safe.
public sealed class CleanupScanner
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Downloads", "Documents", "Desktop", "Pictures", "Videos", "Music", "Saved Games", "Saves",
        "Credentials", "Vault", "Protect", "Profiles", "User Data", "Default", "Login Data", ".ssh", ".gnupg",
        "Chrome", "Chromium", "Edge", "Firefox", "Brave-Browser", "Opera", "Cookies", "Sessions", "Session Storage", "Local Storage"
    };
    private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".db", ".sqlite", ".sqlite3", ".kdbx", ".pem", ".key", ".pfx", ".p12", ".sav" };
    private static readonly string[] SensitiveNameParts = ["credential", "password", "token", "secret", "login", "cookie", "session", "wallet"];
    private readonly TimeSpan minimumAge;
    private readonly int limit;
    public CleanupScanner(TimeSpan? minimumAge = null, int limit = 100_000)
    {
        this.minimumAge = minimumAge ?? TimeSpan.FromDays(7);
        if (this.minimumAge < TimeSpan.Zero || limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        this.limit = limit;
    }

    public Task<CleanupReport> ScanAsync(IEnumerable<CleanupLocation> locations, CancellationToken token, IReadOnlyList<CleanupExclusion>? exclusions = null)
    {
        // Snapshot small UI-owned settings before work moves to a background thread.
        CleanupExclusion[]? captured = exclusions == null ? [] : exclusions.Count > CleanupExclusionRules.MaximumItems ? null : exclusions.ToArray();
        return Task.Run(() => Scan(locations, token, captured), token);
    }

    private CleanupReport Scan(IEnumerable<CleanupLocation> locations, CancellationToken token, IReadOnlyList<CleanupExclusion>? exclusions)
    {
        token.ThrowIfCancellationRequested();
        if (!CleanupExclusionRules.TryNormalize(exclusions, out var normalized))
            return new([], [new("User exclusions", "", "Cleanup exclusions are invalid or exceed their safety limit. Review settings before scanning; no inventory performed.")], 0, false);
        var rules = new CleanupExclusionRules(normalized);
        List<CleanupCandidate> candidates = []; List<ScanIssue> issues = [];
        long excluded = 0; int visited = 0; bool truncated = false;
        var cutoff = DateTime.UtcNow - minimumAge;
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Issue(CleanupLocation location, string path, string reason)
        {
            if (issues.Count < 1000) issues.Add(new(location.Category, path, reason));
            else truncated = true;
        }
        foreach (var location in locations)
        {
            token.ThrowIfCancellationRequested();
            if (truncated) break;
            string root;
            try
            {
                if (string.IsNullOrWhiteSpace(location.Path) || !Path.IsPathFullyQualified(location.Path))
                {
                    Issue(location, location.Path, "Cleanup root is unavailable or not fully qualified; check the Windows known-folder location. No inventory performed.");
                    continue;
                }
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Path));
                if (rules.ExcludesDirectory(root)) { excluded++; continue; }
                // Build paths lexically first: probing the selected root before its parents could
                // already follow a parent junction. Missing/inaccessible ancestors stop validation.
                var ancestors = new Stack<string>();
                for (string? ancestor = root; !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
                    ancestors.Push(ancestor);
                while (ancestors.Count > 0)
                {
                    var ancestor = ancestors.Pop();
                    var attributes = File.GetAttributes(ancestor);
                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(ancestor));
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0 || ProtectedNames.Contains(name))
                        throw new IOException("Excluded ancestor");
                }
                if (!Directory.Exists(root)) { Issue(location, root, "Location missing; no inventory performed."); continue; }
            }
            catch (Exception ex) when (IsFileError(ex))
            { Issue(location, location.Path, "Root inaccessible, missing, protected, or a reparse point."); continue; }
            var directories = new Stack<string>(); directories.Push(root);
            while (directories.Count > 0 && !truncated)
            {
                token.ThrowIfCancellationRequested();
                var directory = directories.Pop();
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { excluded++; continue; }
                    // Lazy enumeration keeps cancellation responsive in very large directories.
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (truncated) break;
                        if (!seen.Add(path)) continue;
                        if (++visited > limit) { truncated = true; break; }
                        try
                        {
                            if (rules.ExcludesDirectory(path)) { excluded++; continue; }
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0 || ProtectedNames.Contains(Path.GetFileName(path)) || SensitiveNameParts.Any(part => Path.GetFileName(path).Contains(part, StringComparison.OrdinalIgnoreCase)))
                            { excluded++; continue; }
                            if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                            if (rules.ExcludesFile(path)) { excluded++; continue; }
                            var info = new FileInfo(path);
                            if (info.LastWriteTimeUtc > cutoff || ProtectedExtensions.Contains(info.Extension) ||
                                (attributes & (FileAttributes.Encrypted | FileAttributes.Offline | FileAttributes.System)) != 0)
                            { excluded++; continue; }
                            // Exclude sharing conflicts. A successful open is not inactive-use proof:
                            // existing mapped views can outlive ordinary handles. Never read contents.
                            using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { excluded++; continue; }
                            candidates.Add(new(location.Category, path, handle.Length, info.LastWriteTimeUtc));
                        }
                        catch (Exception ex) when (IsFileError(ex))
                        { excluded++; Issue(location, path, "Locked, inaccessible, or changed during scan; excluded."); }
                    }
                }
                catch (Exception ex) when (IsFileError(ex))
                { Issue(location, directory, "Directory inaccessible or changed during scan; skipped."); }
            }
            if (truncated) break;
        }
        return new(candidates, issues, excluded, truncated);
    }
    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
