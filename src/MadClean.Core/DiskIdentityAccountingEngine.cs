namespace MadClean.Core;

/// <summary>Projects bounded, already selected paths; never enumerates folders or reads content.</summary>
public sealed class DiskIdentityAccountingEngine(IDiskIdentityAccountingProbe probe)
{
    public const int MaxFiles = 4096, MaxNodes = 8192, MaxIssues = 256;
    public CheckResult<DiskIdentityAccountingReport> Analyze(DiskUsageReport snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var files = new List<DiskUsageNode>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(DiskUsageNode Node, string? Parent)>();
        long total = 0;
        int nodes = 0;
        if (snapshot?.Root == null || !snapshot.Root.IsDirectory || snapshot.Issues == null || snapshot.SkippedEntries < 0)
            return Rejected("Select an existing folder disk map before identity accounting.");
        pending.Push((snapshot.Root, null));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (node, parent) = pending.Pop();
            if (++nodes > MaxNodes || node == null || node.Children == null || node.Bytes < 0 ||
                string.IsNullOrWhiteSpace(node.Path) || node.Path.Length > 32767 || node.Path.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(node.Path))
                return Rejected("The selected map is malformed or exceeds the bounded 8192-node scope; create a smaller fresh map.");
            string normalized;
            try { normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(node.Path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            { return Rejected("The selected map contains an invalid path; create a fresh map."); }
            if (!string.Equals(normalized, Path.TrimEndingDirectorySeparator(node.Path), StringComparison.OrdinalIgnoreCase) ||
                !paths.Add(normalized) || (parent != null && !string.Equals(Path.GetDirectoryName(normalized), parent, StringComparison.OrdinalIgnoreCase)))
                return Rejected("The selected map repeats or redirects paths, or contains non-child entries; no identity queries ran.");
            if (node.IsDirectory)
            {
                if (node.Children.Count > MaxNodes - nodes - pending.Count)
                    return Rejected("The selected map exceeds the bounded node scope; create a smaller map.");
                foreach (var child in node.Children) pending.Push((child, normalized));
            }
            else
            {
                if (node.Children.Count != 0 || files.Count >= MaxFiles || node.Bytes > long.MaxValue - total)
                    return Rejected("The selected map exceeds the 4096-file or logical-byte range, or contains malformed files; no identity queries ran.");
                total += node.Bytes;
                files.Add(node);
            }
        }

        var issues = new List<DiskIdentityAccountingIssue>();
        var known = new List<(DiskUsageNode File, DiskIdentityAccountingStamp Stamp)>();
        bool truncated = false;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (issues.Count >= MaxIssues) { truncated = true; break; }
            try
            {
                var stamp = probe.Probe(file.Path, token);
                if (stamp == null || !SafeIdentity(stamp.Identity) || !stamp.Stable || stamp.Bytes != file.Bytes || stamp.LinkCount == 0)
                    issues.Add(new(file.Path, "Identity/length changed, is unsupported or is incomplete; excluded from observed totals. Create a fresh map and retry."));
                else known.Add((file, stamp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
            {
                issues.Add(new(file.Path, "Handle metadata is inaccessible, changed, protected, offline or unsupported; excluded from observed totals. Check access and retry."));
            }
        }
        long observed = 0, unique = 0;
        int allocationKnown = 0, allocationUnknown = 0;
        var observations = new List<DiskIdentityAccountingObservation>();
        foreach (var group in known.GroupBy(x => x.Stamp.Identity, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var first = group.First().Stamp;
            int count = group.Count();
            if (group.Any(x => x.Stamp.Bytes != first.Bytes || x.Stamp.LinkCount != first.LinkCount) || count > first.LinkCount)
            {
                if (issues.Count < MaxIssues) issues.Add(new(group.First().File.Path,
                    "Aliases reported inconsistent lengths/link counts or more observed names than links; this identity is changing and excluded entirely."));
                else truncated = true;
                continue;
            }
            unique = checked(unique + first.Bytes);
            // Allocation is independent of the qualified logical identity observations.
            // Alias names repeat the same reported stream allocation; never sum these rows.
            long? allocation = first.ReportedAllocationBytes is >= 0 &&
                group.All(item => item.Stamp.ReportedAllocationBytes == first.ReportedAllocationBytes)
                    ? first.ReportedAllocationBytes : null;
            if (allocation.HasValue) allocationKnown++; else allocationUnknown++;
            foreach (var item in group)
            {
                observed = checked(observed + item.File.Bytes);
                observations.Add(new(item.File.Path, item.File.Bytes, first.Identity, first.LinkCount, count, allocation));
            }
        }
        token.ThrowIfCancellationRequested();
        bool partial = snapshot.Truncated || snapshot.SkippedEntries != 0 || snapshot.Issues.Count != 0 || issues.Count != 0 || truncated;
        var allocationState = allocationKnown == 0 ? CheckState.Unknown
            : partial || allocationUnknown != 0 ? CheckState.Partial : CheckState.Available;
        string coverage = "Explicit selected-map paths only; metadata observations are not an atomic filesystem snapshot. " +
            "Path logical bytes are the selected map's file lengths. Observed totals exclude unknown/changed paths; unique-file logical bytes count each observed volume+128-bit identity once. " +
            "Links outside this map, skipped files and unobserved identity relationships remain unknown. Unnamed-stream logical lengths only: no physical allocation, alternate-stream total, reclaimable space, content equality or deletion safety is established. " +
            "Reported default-stream allocation is separate nullable FileStandardInfo metadata. Missing, negative or conflicting alias observations remain Unknown without invalidating logical measurements. " +
            "Allocation counts cover accepted logical identities only; unobserved files remain outside coverage. Alias rows repeat allocation and must not be summed. No allocation aggregate or unique physical/reclaimable total is provided.";
        var report = new DiskIdentityAccountingReport(total, observed, unique, observations.ToArray(), issues.ToArray(), truncated || snapshot.Truncated, coverage,
            allocationState, allocationKnown, allocationUnknown);
        return new(partial ? CheckState.Partial : CheckState.Available,
            partial ? "Selected-path identity accounting has incomplete coverage; unknown bytes are not zero." : "Selected-path identity observations completed; outside-scope links and physical allocation remain unknown.", [report]);
    }
    private static bool SafeIdentity(string identity) => identity != null && identity.Length == 49 && identity[16] == ':' &&
        identity.Where((_, i) => i != 16).All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') && identity[17..].Any(c => c != '0');
    private static CheckResult<DiskIdentityAccountingReport> Rejected(string message) => CheckResult<DiskIdentityAccountingReport>.Empty(CheckState.Unknown, message);
}
