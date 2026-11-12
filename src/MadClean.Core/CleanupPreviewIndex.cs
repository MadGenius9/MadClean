namespace MadClean.Core;

public sealed record CleanupPreviewBinding<TRow>(TRow Row, CleanupCandidate Candidate) where TRow : class;

/// <summary>
/// Immutable display-row binding for a historical cleanup preview, never a file-read or deletion
/// capability. Issue rows have no candidate binding even when their displayed paths coincide.
/// </summary>
public sealed class CleanupPreviewIndex<TRow> where TRow : class
{
    private readonly CleanupReport source;
    private readonly CleanupCandidate[] candidates;
    private readonly int issueCount;
    private readonly Dictionary<TRow, CleanupCandidate> rows;

    private CleanupPreviewIndex(CleanupReport source, CleanupCandidate[] candidates, int issueCount,
        Dictionary<TRow, CleanupCandidate> rows)
    { this.source = source; this.candidates = candidates; this.issueCount = issueCount; this.rows = rows; }

    public static CleanupPreviewIndex<TRow>? Create(CleanupReport? source,
        IReadOnlyList<CleanupPreviewBinding<TRow>>? bindings, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (source?.Candidates == null || source.Issues == null || bindings == null || source.ExcludedCount < 0 ||
            source.Candidates.Count > CleanupFileEvidenceScope.MaximumCandidates || source.Issues.Count > CleanupFileEvidenceScope.MaximumIssues ||
            bindings.Count != source.Candidates.Count) return null;

        var accepted = new CleanupCandidate[source.Candidates.Count];
        var remaining = new HashSet<CleanupCandidate>(ReferenceEqualityComparer.Instance);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        for (int index = 0; index < accepted.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = source.Candidates[index];
            if (candidate == null || !SafeText(candidate.Category, 256, false) || !SafeText(candidate.Path, 32767, false) ||
                candidate.Bytes < 0 || candidate.ModifiedUtc.Kind != DateTimeKind.Utc || candidate.ModifiedUtc == default ||
                candidate.ModifiedUtc > now || !remaining.Add(candidate) || !paths.Add(candidate.Path)) return null;
            accepted[index] = candidate;
        }
        foreach (var issue in source.Issues)
        {
            token.ThrowIfCancellationRequested();
            if (issue == null || !SafeText(issue.Category, 256, false) || !SafeText(issue.Path, 32767, true) ||
                !SafeText(issue.Reason, 4096, false)) return null;
        }

        var rows = new Dictionary<TRow, CleanupCandidate>(ReferenceEqualityComparer.Instance);
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            if (binding?.Row == null || binding.Candidate == null || !remaining.Remove(binding.Candidate) ||
                !rows.TryAdd(binding.Row, binding.Candidate)) return null;
        }
        token.ThrowIfCancellationRequested();
        return remaining.Count == 0 ? new(source, accepted, source.Issues.Count, rows) : null;
    }

    public CleanupCandidate? Find(CleanupReport? currentSource, TRow? selected, IReadOnlyList<TRow>? currentRows)
    {
        if (!ReferenceEquals(source, currentSource) || selected == null || currentRows == null ||
            source.Candidates == null || source.Issues == null || source.Candidates.Count != candidates.Length ||
            source.Issues.Count != issueCount || currentRows.Count != candidates.Length + issueCount) return null;
        for (int index = 0; index < candidates.Length; index++)
            if (!ReferenceEquals(source.Candidates[index], candidates[index])) return null;
        int references = 0;
        foreach (var row in currentRows)
        {
            if (row == null) return null;
            if (ReferenceEquals(row, selected)) references++;
        }
        return references == 1 && rows.TryGetValue(selected, out var candidate) ? candidate : null;
    }

    private static bool SafeText(string? value, int maximum, bool empty) => value != null && value.Length <= maximum &&
        (empty || !string.IsNullOrWhiteSpace(value)) && ShortcutPathPolicy.HasSafeUnicode(value);
}
