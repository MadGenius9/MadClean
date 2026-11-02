namespace MadClean.Core;

public enum CleanupRootKind { UserTemporary, WindowsTemporary }
public enum CleanupEvidenceSourceKind { WindowsTemporary, OwnedDevelopmentFixture }
public sealed record CleanupFileStamp(string Path, string Identity, long Bytes, uint LinkCount,
    DateTimeOffset CreationUtc, DateTimeOffset LastWriteUtc, DateTimeOffset ChangeUtc, uint Attributes);

// First fresh identity observation only. No handle survives publication and no deletion is supported.
public sealed record CleanupFileEvidenceReport(CleanupCandidate Prior, CleanupRootKind RootKind,
    CleanupEvidenceSourceKind SourceKind, CleanupFileStamp File, string RootPath, string RootIdentity,
    DateTimeOffset QueryStartedUtc, DateTimeOffset QueryCompletedUtc, bool HistoricalPathMatches,
    bool HistoricalSizeMatches, bool HistoricalModifiedMatches, CheckState ExclusionCoverageState,
    CheckState SourceCoverageState, IReadOnlyList<string> Coverage)
{
    public CleanupStreamScopeObservation? StreamScope { get; init; }
    public string ActiveUse => "Unknown";
    public bool CanDelete => false;
}

public interface ICleanupFileEvidence
{
    Task<CheckResult<CleanupFileEvidenceReport>> ReadAsync(CleanupFileEvidenceScope scope, CancellationToken token);
}

/// <summary>Immutable exact historical selection for a new metadata query, never a root authorization or action capability.</summary>
public sealed class CleanupFileEvidenceScope
{
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ScopeLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(7);
    public const int MaximumCandidates = 100_000, MaximumIssues = 1000, MaximumPathCharacters = 258;
    public CleanupCandidate Candidate { get; }
    public CleanupRootKind RootKind { get; }
    public DateTimeOffset CompletedUtc { get; }
    public DateTimeOffset ValidatedUtc { get; }
    public CheckState SourceCoverageState { get; }
    public IReadOnlyList<CleanupExclusion> CapturedExclusions { get; }

    private CleanupFileEvidenceScope(CleanupCandidate candidate, CleanupRootKind rootKind, DateTimeOffset completed,
        DateTimeOffset validated, CheckState sourceState, CleanupExclusion[] exclusions)
    {
        Candidate = candidate with { }; RootKind = rootKind; CompletedUtc = completed; ValidatedUtc = validated;
        SourceCoverageState = sourceState; CapturedExclusions = Array.AsReadOnly(exclusions);
    }

    public static CheckResult<CleanupFileEvidenceScope> Create(CleanupReport? snapshot, CleanupCandidate? selected,
        DateTimeOffset completedUtc, IReadOnlyList<CleanupExclusion>? capturedExclusions, DateTimeOffset nowUtc,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        CheckResult<CleanupFileEvidenceScope> Reject(string reason) => CheckResult<CleanupFileEvidenceScope>.Empty(CheckState.Unavailable, reason);
        if (!Utc(nowUtc) || !Utc(completedUtc) || completedUtc > nowUtc || nowUtc - completedUtc > SnapshotLifetime)
            return Reject("Cleanup selection has no recent completed UTC observation. Scan again and select one actual candidate.");
        if (snapshot?.Candidates == null || snapshot.Issues == null || selected == null ||
            snapshot.Candidates.Count is < 1 or > MaximumCandidates || snapshot.Issues.Count > MaximumIssues || snapshot.ExcludedCount < 0)
            return Reject("A bounded completed cleanup inventory and exact selected candidate are required; no query was authorized.");
        if (!TryNormalizeExclusions(capturedExclusions, out var exclusions, token))
            return Reject("Captured cleanup exclusions are missing or invalid; reload settings and scan again.");
        int references = 0, paths = 0;
        foreach (var candidate in snapshot.Candidates)
        {
            token.ThrowIfCancellationRequested();
            if (candidate == null || !SafeText(candidate.Category, 256, false) || !SafeText(candidate.Path, 32767, false) ||
                candidate.Bytes < 0 || candidate.ModifiedUtc.Kind != DateTimeKind.Utc || candidate.ModifiedUtc == default || candidate.ModifiedUtc > completedUtc.UtcDateTime)
                return Reject("Cleanup inventory contains malformed candidate metadata; refresh it before selecting a query.");
            if (ReferenceEquals(candidate, selected)) references++;
            if (string.Equals(candidate.Path, selected.Path, StringComparison.OrdinalIgnoreCase)) paths++;
        }
        foreach (var issue in snapshot.Issues)
        {
            token.ThrowIfCancellationRequested();
            // Issue paths can describe missing/nonabsolute roots. They are never file-read selectors.
            if (issue == null || !SafeText(issue.Category, 256, false) || !SafeText(issue.Path, 32767, true) || !SafeText(issue.Reason, 4096, false))
                return Reject("Cleanup issue metadata is malformed; refresh the inventory.");
        }
        if (references != 1 || paths != 1 || !TryRootKind(selected.Category, out var rootKind) ||
            NormalizeFilePath(selected.Path) is not { } path || completedUtc.UtcDateTime - selected.ModifiedUtc < MinimumAge)
            return Reject("The exact uniquely selected supported temporary-file observation is absent, ambiguous, recent or unsupported.");
        string name = path[(path.LastIndexOf('\\') + 1)..];
        if (CleanupSafetyPolicy.IsProtectedName(name) || CleanupSafetyPolicy.IsSensitiveName(name) ||
            CleanupSafetyPolicy.IsProtectedExtension(Path.GetExtension(name)) || Excludes(exclusions, path))
            return Reject("The selected file is protected or excluded. No new file query is authorized.");
        token.ThrowIfCancellationRequested();
        var state = snapshot.Truncated || snapshot.Issues.Count > 0 ? CheckState.Partial : CheckState.Available;
        return new(state, "Exact historical selection accepted for one fresh metadata read only. Windows must recheck actual roots, current exclusions and protected scopes; identity continuity, active use and deletion safety are unknown.",
            [new(selected, rootKind, completedUtc, nowUtc, state, exclusions)]);
    }

    // Pure Windows grammar; portable tests must not reinterpret C:\ paths as Linux filenames.
    public static string? NormalizeFilePath(string? value) => NormalizePath(value, MaximumPathCharacters, false);
    private static string? NormalizePath(string? value, int maximum, bool allowRoot)
    {
        if (value == null || value.Length > maximum || value.Contains('/')) return null;
        string? normalized = ShortcutPathPolicy.NormalizeLocalPath(value, allowRoot);
        return normalized != null && string.Equals(normalized, value, StringComparison.OrdinalIgnoreCase) ? normalized : null;
    }
    public static bool TryNormalizeExclusions(IReadOnlyList<CleanupExclusion>? items, out CleanupExclusion[] normalized,
        CancellationToken token = default)
    {
        normalized = [];
        token.ThrowIfCancellationRequested();
        if (items == null || items.Count > CleanupExclusionRules.MaximumItems) return false;
        List<CleanupExclusion> copy = [];
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (item?.Path == null || item.Path.Length > 32767) return false;
            string trimmed = item.Path.Length > 3 ? item.Path.TrimEnd('\\') : item.Path;
            string? path = NormalizePath(trimmed, 32767, true);
            if (path == null) return false;
            if (!copy.Any(prior => prior.IncludeDescendants == item.IncludeDescendants && string.Equals(prior.Path, path, StringComparison.OrdinalIgnoreCase)))
                copy.Add(new(path, item.IncludeDescendants));
        }
        normalized = copy.ToArray(); return true;
    }
    public bool MatchesCapturedExclusions(string path) => Excludes(CapturedExclusions, path);
    private static bool Excludes(IReadOnlyList<CleanupExclusion> exclusions, string path) => exclusions.Any(item =>
        string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase) || item.IncludeDescendants &&
        path.StartsWith(item.Path.EndsWith('\\') ? item.Path : item.Path + "\\", StringComparison.OrdinalIgnoreCase));
    private static bool TryRootKind(string category, out CleanupRootKind kind)
    {
        kind = CleanupRootKind.UserTemporary;
        if (category == "User temporary files") return true;
        if (category != "Windows temporary files") return false;
        kind = CleanupRootKind.WindowsTemporary; return true;
    }
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool SafeText(string? value, int maximum, bool empty) => value != null && value.Length <= maximum &&
        (empty || !string.IsNullOrWhiteSpace(value)) && ShortcutPathPolicy.HasSafeUnicode(value);
}
