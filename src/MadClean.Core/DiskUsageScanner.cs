namespace MadClean.Core;

public sealed record DiskUsageNode(string Name, string Path, long Bytes, IReadOnlyList<DiskUsageNode> Children, bool IsDirectory);
public sealed record DiskUsageIssue(string Path, string Reason);
public sealed record DiskUsageReport(DiskUsageNode Root, IReadOnlyList<DiskUsageIssue> Issues, int SkippedEntries, bool Truncated);

/// <summary>
/// Builds a bounded, read-only map of logical file lengths under one explicitly selected folder.
/// This is not an allocated-space calculation or a classification of files that are safe to delete.
/// </summary>
public sealed class DiskUsageScanner
{
    private const int IssueLimit = 1000;
    // These documented Windows attributes are not named by every supported FileAttributes enum.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes ExcludedAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline |
        FileAttributes.Encrypted | FileAttributes.System | RecallOnOpen | RecallOnDataAccess;
    private readonly int maxEntries;
    private readonly int maxDepth;

    public DiskUsageScanner(int maxEntries = 100_000, int maxDepth = 64)
    {
        if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        // Traversal is recursive, so also bound callers' requested depth to protect the worker stack.
        if (maxDepth < 0 || maxDepth > 256) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        this.maxEntries = maxEntries;
        this.maxDepth = maxDepth;
    }

    public Task<DiskUsageReport> ScanAsync(string root, CancellationToken token) =>
        Task.Run(() => Scan(root, token), token);

    private DiskUsageReport Scan(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = new ScanState(token);
        string selectedPath = root ?? string.Empty;
        DiskUsageNode EmptyRoot() => EmptyDirectory(selectedPath);
        DiskUsageReport Rejected(string reason)
        {
            token.ThrowIfCancellationRequested();
            state.AddIssue(selectedPath, reason);
            return new(EmptyRoot(), state.Issues.ToArray(), state.SkippedEntries, state.Truncated);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(selectedPath) || !Path.IsPathFullyQualified(selectedPath))
                return Rejected("Select an absolute local folder. No disk map was collected for this path.");
            selectedPath = Path.GetFullPath(selectedPath);
            if (OperatingSystem.IsWindows())
            {
                // Refuse UNC/device paths and mapped network drives; this feature does not contact remote shares.
                if (selectedPath.StartsWith(@"\\", StringComparison.Ordinal) || selectedPath.StartsWith("//", StringComparison.Ordinal) ||
                    new DriveInfo(Path.GetPathRoot(selectedPath)!).DriveType == DriveType.Network)
                    return Rejected("Network and device paths are outside this local disk map. Select a local folder.");
            }
            // Build ancestry from path syntax only, then validate from the volume/root downward.
            // Probing a descendant first could already follow an unchecked ancestor junction.
            var ancestry = new Stack<string>();
            for (DirectoryInfo? ancestor = new(selectedPath); ancestor != null; ancestor = ancestor.Parent)
            {
                token.ThrowIfCancellationRequested();
                ancestry.Push(ancestor.FullName);
            }
            while (ancestry.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(ancestry.Pop()) & FileAttributes.ReparsePoint) != 0)
                    return Rejected("The selected folder or an ancestor is a reparse point/link. Select its local physical folder explicitly instead.");
            }
            var attributes = File.GetAttributes(selectedPath);
            if ((attributes & FileAttributes.Directory) == 0)
                return Rejected("The selected path is not a folder. Select a local folder to map.");
            if ((attributes & ExcludedAttributes) != 0)
                return Rejected("The selected folder is a protected, offline, encrypted or recall-placeholder entry; no traversal was performed.");
        }
        catch (Exception ex) when (IsPathError(ex))
        {
            return Rejected("The selected folder is missing, inaccessible or changed. Check the path and folder permissions before retrying.");
        }

        var map = VisitDirectory(selectedPath, 0, state);
        token.ThrowIfCancellationRequested();
        return new(map, state.Issues.ToArray(), state.SkippedEntries, state.Truncated);
    }

    private DiskUsageNode VisitDirectory(string path, int depth, ScanState state)
    {
        state.Token.ThrowIfCancellationRequested();
        List<DiskUsageNode> children = [];
        try
        {
            // Recheck immediately before enumeration; a directory may have changed since its parent's scan.
            if ((File.GetAttributes(path) & ExcludedAttributes) != 0)
            {
                state.Skip(path, "Directory became a reparse point, protected entry or offline placeholder; skipped without traversal.");
                return EmptyDirectory(path);
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                state.Token.ThrowIfCancellationRequested();
                if (state.Stop) break;
                if (state.VisitedEntries >= maxEntries)
                {
                    state.SkippedEntries++;
                    state.StopAtLimit(entry, "The entry limit was reached. Unscanned entries have unknown size; choose a smaller folder or map a child folder separately.");
                    break;
                }
                state.VisitedEntries++;
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & ExcludedAttributes) != 0)
                    {
                        state.Skip(entry, "Reparse link, system/encrypted file, offline or recall-placeholder entry; excluded from the known logical total.");
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth >= maxDepth)
                        {
                            state.SkippedEntries++;
                            state.Truncated = true;
                            state.AddIssue(entry, "The depth limit was reached. Descendant sizes are unknown; select this folder separately for a new map.");
                            children.Add(EmptyDirectory(entry));
                        }
                        else
                        {
                            children.Add(VisitDirectory(entry, depth + 1, state));
                        }
                        continue;
                    }

                    // FileInfo queries metadata; it does not open or read the file's data stream.
                    var info = new FileInfo(entry);
                    info.Refresh();
                    if ((info.Attributes & (ExcludedAttributes | FileAttributes.Directory)) != 0)
                    {
                        state.Skip(entry, "Entry changed into an excluded file or directory during the metadata query; skipped.");
                        continue;
                    }
                    long bytes = info.Length;
                    if (bytes < 0 || bytes > long.MaxValue - state.KnownFileBytes)
                    {
                        state.SkippedEntries++;
                        state.StopAtLimit(entry, "Known logical sizes exceeded the supported numeric range. This entry and remaining entries were not counted.");
                        break;
                    }
                    state.KnownFileBytes = checked(state.KnownFileBytes + bytes);
                    children.Add(new(info.Name, entry, bytes, Array.Empty<DiskUsageNode>(), false));
                }
                catch (Exception ex) when (IsPathError(ex))
                {
                    state.Skip(entry, "Metadata is inaccessible or the entry changed. Check folder/file permissions and retry; its size remains unknown.");
                }
            }
        }
        catch (Exception ex) when (IsPathError(ex))
        {
            state.Skip(path, "Directory could not be enumerated or changed during the map. Check folder permissions and retry; unobserved entries have unknown size.");
        }
        state.Token.ThrowIfCancellationRequested();
        long total = 0;
        foreach (var child in children)
        {
            state.Token.ThrowIfCancellationRequested();
            // Each file is accounted once in KnownFileBytes, which is checked before accepting a file.
            total = checked(total + child.Bytes);
        }
        var sorted = children.OrderByDescending(node => node.Bytes)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        state.Token.ThrowIfCancellationRequested();
        return new(DirectoryName(path), path, total, sorted, true);
    }

    private static DiskUsageNode EmptyDirectory(string path) => new(DirectoryName(path), path, 0, Array.Empty<DiskUsageNode>(), true);
    private static string DirectoryName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? string.IsNullOrEmpty(path) ? "Selected folder" : path : name;
    }
    private static bool IsPathError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or ArgumentException or NotSupportedException;

    private sealed class ScanState(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public List<DiskUsageIssue> Issues { get; } = [];
        public int VisitedEntries { get; set; }
        public int SkippedEntries { get; set; }
        public long KnownFileBytes { get; set; }
        public bool Truncated { get; set; }
        public bool Stop { get; private set; }

        public void Skip(string path, string reason)
        {
            SkippedEntries++;
            AddIssue(path, reason);
        }
        public void AddIssue(string path, string reason)
        {
            if (Issues.Count < IssueLimit) Issues.Add(new(path, reason));
            if (Issues.Count >= IssueLimit) { Truncated = true; Stop = true; }
        }
        public void StopAtLimit(string path, string reason)
        {
            Truncated = true;
            Stop = true;
            AddIssue(path, reason);
        }
    }
}
