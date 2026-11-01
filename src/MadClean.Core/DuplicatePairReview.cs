using System.Globalization;
using System.Text;

namespace MadClean.Core;

public enum DuplicatePairReviewState { Incomplete, Blocked, Preview }
public sealed record DuplicatePairReview(DuplicatePairReviewState State, string Reason, CheckState SourceCoverageState,
    DuplicateEntry? Candidate, DuplicateEntry? RetainedEntry)
{
    public bool ExecutionSupported => false;
}

/// <summary>
/// In-memory worksheet only. Observation age is a display-review bound, never a freshness permission,
/// retained-copy lease, removal authorization or evidence that either current file remains unchanged.
/// </summary>
public static class DuplicatePairReviewBuilder
{
    public const int MaximumEntries = 512;
    public static readonly TimeSpan MaximumObservationAge = TimeSpan.FromMinutes(10);
    public static DuplicatePairReview Build(CheckResult<DuplicateReport>? snapshot, DuplicateEntry? candidate,
        DuplicateEntry? retainedEntry, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var sourceState = snapshot?.State ?? CheckState.Unknown;
        DuplicatePairReview Result(DuplicatePairReviewState state, string reason) => new(state, reason, sourceState, null, null);
        DuplicatePairReview Block(string reason) => Result(DuplicatePairReviewState.Blocked, reason);
        if (snapshot == null || sourceState is not (CheckState.Available or CheckState.Partial))
            return Result(DuplicatePairReviewState.Incomplete, "A completed Available/Partial duplicate observation snapshot is required; no current-file or keeper protection was established.");
        if (candidate == null || retainedEntry == null)
            return Result(DuplicatePairReviewState.Incomplete, "Explicitly select one candidate and a distinct retained entry from the current duplicate snapshot. No action is available.");
        if (nowUtc == default || nowUtc.Offset != TimeSpan.Zero)
            return Block("A valid UTC review time is required; no worksheet was created.");
        try
        {
            if (snapshot.Items == null || snapshot.Items.Count != 1 || snapshot.Items[0] is not { } report ||
                report.Entries == null || report.Issues == null || report.Entries.Count is < 2 or > MaximumEntries ||
                report.Issues.Count > 1000 || report.VisitedFiles is < 1 or > 10000 || report.Entries.Count > report.VisitedFiles ||
                report.SkippedFiles < 0 || report.SkippedFiles > report.VisitedFiles - report.Entries.Count || !SafeText(report.Coverage, 16384, token) ||
                sourceState == CheckState.Available && (report.Truncated || report.Issues.Count != 0 || report.SkippedFiles != 0))
                return Block("The source snapshot has malformed, inconsistent or excessive coverage metadata; refresh duplicate discovery.");
            foreach (var issue in report.Issues)
            {
                token.ThrowIfCancellationRequested();
                if (issue == null || issue.Path == null || issue.Path.Length > 32767 || !SafeText(issue.Reason, 4096, token))
                    return Block("The source issue metadata is malformed; refresh duplicate discovery before review.");
            }
            List<DuplicateEntry> entries = [];
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identityGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int candidates = 0, retained = 0;
            foreach (var entry in report.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= MaximumEntries || entry == null || !SafeText(entry.GroupId, 128, token) ||
                    !SafePath(entry.Path, token) || !SafeText(entry.Identity, 256, token) || entry.Bytes < 0 ||
                    !SafeText(entry.Relationship, 4096, token) || !SafeText(entry.Verification, 4096, token) ||
                    !paths.Add(entry.Path))
                    return Block("Duplicate entry metadata or paths are malformed or repeated; refresh discovery before review.");
                if (identityGroups.TryGetValue(entry.Identity, out string? group) && group != entry.GroupId)
                    return Block("One observed file identity appears in contradictory groups; no pair can be reviewed from this snapshot.");
                identityGroups[entry.Identity] = entry.GroupId;
                if (ReferenceEquals(entry, candidate)) candidates++;
                if (ReferenceEquals(entry, retainedEntry)) retained++;
                entries.Add(entry);
            }
            if (entries.Count != report.Entries.Count || candidates != 1 || retained != 1)
                return Block("The selections are stale or are not the exact entry references in the current snapshot; select both entries again.");
            if (ReferenceEquals(candidate, retainedEntry) || candidate.Path.Equals(retainedEntry.Path, StringComparison.OrdinalIgnoreCase) ||
                candidate.Identity.Equals(retainedEntry.Identity, StringComparison.OrdinalIgnoreCase) || candidate.GroupId != retainedEntry.GroupId || candidate.Bytes != retainedEntry.Bytes)
                return Block("The selected names do not form a same-group equal-length pair of distinct observed file identities.");

            foreach (var group in entries.GroupBy(entry => entry.GroupId, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var first = group.First();
                var anchors = new Dictionary<string, DuplicateEntry>(StringComparer.OrdinalIgnoreCase);
                var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int independent = 0, aliasAnchors = 0;
                foreach (var entry in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Bytes != first.Bytes || entry.ObservedUtc is not DateTimeOffset observed || observed == default ||
                        observed.Offset != TimeSpan.Zero || observed > nowUtc || nowUtc - observed > MaximumObservationAge ||
                        observed != first.ObservedUtc || entry.ObservedLinkCount is not uint links || links == 0)
                        return Block("Group sizes, observation times or link-count evidence are missing, stale or contradictory; refresh discovery.");
                    if (entry.EvidenceKind == DuplicateEvidenceKind.SameFileAlias)
                        aliases[entry.Identity] = aliases.GetValueOrDefault(entry.Identity) + 1;
                    else if (entry.EvidenceKind is DuplicateEvidenceKind.ByteComparedIndependentFile or DuplicateEvidenceKind.AliasAnchor)
                    {
                        if (!anchors.TryAdd(entry.Identity, entry))
                            return Block("A file identity has multiple contradictory anchor observations; no worksheet was created.");
                        if (entry.EvidenceKind == DuplicateEvidenceKind.ByteComparedIndependentFile) independent++; else aliasAnchors++;
                    }
                    else return Block("Typed independent byte-comparison evidence is unavailable; descriptive verification text does not authorize review.");
                }
                if (independent != 0 ? independent < 2 || aliasAnchors != 0 : aliasAnchors != 1 || aliases.Count != 1)
                    return Block("The source group does not contain a consistent independent-copy or same-file-alias observation structure.");
                foreach (var entry in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (!anchors.TryGetValue(entry.Identity, out var anchor) || anchor.ObservedLinkCount != entry.ObservedLinkCount ||
                        1L + aliases.GetValueOrDefault(entry.Identity) > anchor.ObservedLinkCount!.Value)
                        return Block("Alias and anchor identity/link-count evidence conflicts; refresh duplicate discovery.");
                }
            }
            token.ThrowIfCancellationRequested();
            if (candidate.EvidenceKind != DuplicateEvidenceKind.ByteComparedIndependentFile ||
                retainedEntry.EvidenceKind != DuplicateEvidenceKind.ByteComparedIndependentFile || candidate.ObservedLinkCount != 1 ||
                retainedEntry.ObservedLinkCount is not uint retainedLinks || retainedLinks == 0)
                return Block("Both selections require typed independent byte comparison; the candidate must have exactly one observed link and the retained entry a known link count. Aliases are blocked.");
            return new(DuplicatePairReviewState.Preview,
                "Read-only selected-pair worksheet from past observations. Partial coverage stays Partial. Current file identity/content, retained-copy protection, deletion safety and reclaimable space were not established; execution is unavailable.",
                sourceState, candidate with { }, retainedEntry with { });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            token.ThrowIfCancellationRequested();
            return Block("The bounded source snapshot changed or could not be validated; refresh discovery and select the pair again.");
        }
    }
    private static bool SafeText(string? value, int maximum, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) return false;
        int offset = 0;
        while (offset < value.Length)
        {
            token.ThrowIfCancellationRequested();
            var status = Rune.DecodeFromUtf16(value.AsSpan(offset), out var rune, out int consumed);
            if (status != System.Buffers.OperationStatus.Done || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
                return false;
            offset += consumed;
        }
        return true;
    }
    private static bool SafePath(string? path, CancellationToken token)
    {
        if (!SafeText(path, 32767, token) || path!.StartsWith("\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        bool drive = path.Length >= 4 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        string[] parts;
        if (drive)
        {
            if (path.Contains('/') || path.IndexOf(':', 2) >= 0) return false;
            parts = path[3..].Split('\\');
        }
        else
        {
            if (!Path.IsPathFullyQualified(path) || path.Contains('\\') || path.Contains(':') || path.Length <= 1) return false;
            parts = path[1..].Split('/');
        }
        foreach (string part in parts)
        {
            token.ThrowIfCancellationRequested();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')) return false;
        }
        return true;
    }
}
