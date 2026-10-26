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

    public Task<CleanupReport> ScanAsync(IEnumerable<CleanupLocation> locations, CancellationToken token) =>
        Task.Run(() => Scan(locations, token), token);

    private CleanupReport Scan(IEnumerable<CleanupLocation> locations, CancellationToken token)
    {
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
            string root;
            try
            {
                root = Path.GetFullPath(location.Path);
                // Check every ancestor: a safe-looking root under a junction is unsafe too.
                for (DirectoryInfo? ancestor = new(root); ancestor != null; ancestor = ancestor.Parent)
                    if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0 || ProtectedNames.Contains(ancestor.Name))
                        throw new IOException("Excluded ancestor");
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
                        if (++visited > limit) { truncated = true; break; }
                        if (!seen.Add(path)) continue;
                        try
                        {
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0 || ProtectedNames.Contains(Path.GetFileName(path)) || SensitiveNameParts.Any(part => Path.GetFileName(path).Contains(part, StringComparison.OrdinalIgnoreCase)))
                            { excluded++; continue; }
                            if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                            var info = new FileInfo(path);
                            if (info.LastWriteTimeUtc > cutoff || ProtectedExtensions.Contains(info.Extension) ||
                                (attributes & (FileAttributes.Encrypted | FileAttributes.Offline | FileAttributes.System)) != 0)
                            { excluded++; continue; }
                            // Sharing violation means active/locked: exclude rather than guess. Never read contents.
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
