namespace MadClean.Core;

/// <summary>A bounded in-session worksheet selection, never a native root grant or deletion capability.</summary>
public sealed class CleanupReviewSelection
{
    public const int MaximumSelections = 8;
    public CleanupReport SourceSnapshot { get; }
    public IReadOnlyList<CleanupCandidate> Selections { get; }
    public DateTimeOffset CompletedUtc { get; }
    public IReadOnlyList<CleanupExclusion> CapturedExclusions { get; }
    public CheckState SourceCoverageState { get; }
    public bool CanDelete => false;

    private CleanupReviewSelection(CleanupReport snapshot, CleanupCandidate[] selections, DateTimeOffset completed,
        CleanupExclusion[] exclusions, CheckState sourceState)
    {
        SourceSnapshot = snapshot; Selections = Array.AsReadOnly(selections); CompletedUtc = completed;
        CapturedExclusions = Array.AsReadOnly(exclusions); SourceCoverageState = sourceState;
    }

    public static CheckResult<CleanupReviewSelection> Create(CleanupReport? snapshot, IReadOnlyList<CleanupCandidate>? selections,
        DateTimeOffset completedUtc, IReadOnlyList<CleanupExclusion>? capturedExclusions, DateTimeOffset nowUtc,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        CheckResult<CleanupReviewSelection> Reject(string reason) => CheckResult<CleanupReviewSelection>.Empty(CheckState.Unavailable, reason);
        if (snapshot == null || selections == null || selections.Count is < 1 or > MaximumSelections)
            return Reject("Explicitly select one to eight candidates from one completed cleanup scan. No file query or action was authorized.");
        if (!CleanupFileEvidenceScope.TryNormalizeExclusions(capturedExclusions, out var exclusions, token))
            return Reject("Captured exclusions are missing or unsupported. Reload preferences and scan again before reviewing files.");
        var originals = new CleanupCandidate[selections.Count];
        var references = new HashSet<CleanupCandidate>(ReferenceEqualityComparer.Instance);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CheckState sourceState = CheckState.Unknown;
        for (int index = 0; index < originals.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = selections[index];
            var result = CleanupFileEvidenceScope.Create(snapshot, candidate, completedUtc, exclusions, nowUtc, token);
            if (result.State is not (CheckState.Available or CheckState.Partial) || result.Items.Count != 1)
                return Reject("Every review choice must be an exact supported current candidate reference from the same recent scan and exclusion set. Refresh the scan or remove unsupported choices.");
            if (!references.Add(candidate) || !paths.Add(candidate.Path))
                return Reject("A file cannot appear twice in the review selection. Choose unique exact candidate references and paths.");
            originals[index] = candidate; sourceState = result.Items[0].SourceCoverageState;
        }
        token.ThrowIfCancellationRequested();
        return new(sourceState, "Exact cleanup choices captured for read-only review. No handle, current identity continuity, inactive-use finding or deletion permission is granted.",
            [new(snapshot, originals, completedUtc, exclusions, sourceState)]);
    }

    public bool IsFresh(DateTimeOffset nowUtc) => nowUtc != default && nowUtc.Offset == TimeSpan.Zero &&
        CompletedUtc != default && CompletedUtc.Offset == TimeSpan.Zero && CompletedUtc <= nowUtc &&
        nowUtc - CompletedUtc <= CleanupFileEvidenceScope.SnapshotLifetime;

    // The caller must separately reject a settings Load.Error; an empty returned item list
    // is not proof that an unreadable store matches a genuinely empty captured preference set.
    public bool MatchesExclusions(IReadOnlyList<CleanupExclusion>? items, CancellationToken token = default)
    {
        if (!CleanupFileEvidenceScope.TryNormalizeExclusions(items, out var current, token) || current.Length != CapturedExclusions.Count) return false;
        foreach (var item in current)
        {
            token.ThrowIfCancellationRequested();
            if (!CapturedExclusions.Any(prior => prior.IncludeDescendants == item.IncludeDescendants &&
                string.Equals(prior.Path, item.Path, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    public CheckResult<CleanupFileEvidenceScope> Prepare(CleanupCandidate? selectedRef, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (selectedRef == null || !Selections.Any(item => ReferenceEquals(item, selectedRef)) || !IsFresh(nowUtc))
            return CheckResult<CleanupFileEvidenceScope>.Empty(CheckState.Unavailable, "This exact file is absent from the recent worksheet selection. Refresh the scan and review choices; no native query started.");
        return CleanupFileEvidenceScope.Create(SourceSnapshot, selectedRef, CompletedUtc, CapturedExclusions, nowUtc, token);
    }
}
