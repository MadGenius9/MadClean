namespace MadClean.Core;

/// <summary>
/// Checks already-returned metadata against a factory-selected scope. This is lexical/structural
/// consistency only, not filesystem identity, ownership, inactivity or removal permission.
/// </summary>
public static class LeftoverContentEvidence
{
    public static bool IsConsistent(LeftoverContentScope? scope, CheckResult<LeftoverContentReport>? result)
    {
        if (scope == null || result == null || result.State is not (CheckState.Available or CheckState.Partial) ||
            result.Items is not { Count: 1 } || result.Items[0] is not { } report)
            return false;
        string? root = InstalledApplicationPaths.NormalizeLocalDirectory(scope.RootPath);
        string? reportedRoot = InstalledApplicationPaths.NormalizeLocalDirectory(report.RootPath);
        if (root == null || reportedRoot == null || !root.Equals(reportedRoot, StringComparison.OrdinalIgnoreCase) ||
            report.Entries == null || report.Issues == null || report.Coverage == null ||
            report.VisitedFiles is < 0 or > LeftoverContentPolicy.MaximumFiles ||
            report.VisitedDirectories is < 1 or > LeftoverContentPolicy.MaximumDirectories ||
            report.Entries.Count > LeftoverContentPolicy.MaximumFiles + LeftoverContentPolicy.MaximumDirectories - 1 ||
            report.Issues.Count > LeftoverContentPolicy.MaximumIssues || report.Coverage.Count is < 1 or > 32 ||
            result.State == CheckState.Available && (report.Truncated || report.Issues.Count != 0))
            return false;

        int files = 0, directories = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in report.Entries)
        {
            if (entry == null || !TryEntryPath(root, entry.Path, entry.IsDirectory, out string? path) || !paths.Add(path!) ||
                !LeftoverContentPolicy.SafeText(entry.Identity, 128) || entry.ActiveUse != "Unknown" ||
                (entry.IsDirectory ? entry.Bytes != null : entry.Bytes is not >= 0)) return false;
            if (entry.IsDirectory) directories++; else files++;
        }
        if (files > report.VisitedFiles || directories > report.VisitedDirectories - 1) return false;
        foreach (var issue in report.Issues)
            if (issue == null || !IssueWithin(root, issue.Path) || !LeftoverContentPolicy.SafeText(issue.Reason, 8192)) return false;
        return report.Coverage.All(note => LeftoverContentPolicy.SafeText(note, 16_384));
    }

    private static bool TryEntryPath(string root, string? value, bool isDirectory, out string? normalized)
    {
        normalized = null;
        if (value == null || value.Length > 32767) return false;
        string path = value.Replace('/', '\\');
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        string relative = path[(root.Length + 1)..];
        // Native depth/name bounds keep this suffix small even when the recorded root is long.
        // Reuse the existing Windows syntax policy without its full-root 8192-character limit.
        string? tail = InstalledApplicationPaths.NormalizeLocalDirectory("C:\\_\\" + relative);
        if (tail == null || !tail.StartsWith("C:\\_\\", StringComparison.Ordinal)) return false;
        string[] parts = tail[5..].Split('\\');
        if ((isDirectory ? parts.Length : parts.Length - 1) > LeftoverContentPolicy.MaximumDepth) return false;
        for (int index = 0; index < parts.Length; index++)
            if (LeftoverContentPolicy.IsProtectedDescendant(parts[index], index < parts.Length - 1 || isDirectory)) return false;
        normalized = root + tail[4..];
        return true;
    }

    private static bool IssueWithin(string root, string? value)
    {
        if (value == null || value.Length is 0 or > 32767 || value.Any(char.IsControl)) return false;
        string path = value.Replace('/', '\\');
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        string relative = path[(root.Length + 1)..];
        // Excluded names may intentionally fail accepted-entry syntax. They remain issue text,
        // never accepted observations, but must still be lexically inside this selected root.
        return relative.Length != 0 && relative.Split('\\').All(part => part.Length != 0 && part is not ("." or ".."));
    }
}
