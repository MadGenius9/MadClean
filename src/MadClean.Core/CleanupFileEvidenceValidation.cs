namespace MadClean.Core;

// Shared production projection boundary for single-file details and a selected-file worksheet.
// Consistency of a read-only report grants no execution, object-continuity or inactivity permission.
public static class CleanupFileEvidenceValidation
{
    public static bool IsConsistent(CheckResult<CleanupFileEvidenceReport>? result, CleanupFileEvidenceScope? scope, DateTimeOffset nowUtc)
    {
        if (result == null || scope == null || result.Items == null || result.Items.Count != 1 ||
            result.State is not (CheckState.Available or CheckState.Partial) || !Utc(nowUtc) ||
            !Utc(scope.CompletedUtc) || !Utc(scope.ValidatedUtc) || scope.CompletedUtc > scope.ValidatedUtc || scope.ValidatedUtc > nowUtc ||
            nowUtc - scope.CompletedUtc > CleanupFileEvidenceScope.SnapshotLifetime || nowUtc - scope.ValidatedUtc > CleanupFileEvidenceScope.ScopeLifetime ||
            scope.SourceCoverageState is not (CheckState.Available or CheckState.Partial)) return false;
        var data = result.Items[0];
        if (data == null || data.CanDelete || data.ActiveUse != "Unknown" || data.SourceKind != CleanupEvidenceSourceKind.WindowsTemporary ||
            data.RootKind != scope.RootKind || data.Prior != scope.Candidate || data.SourceCoverageState != scope.SourceCoverageState ||
            data.ExclusionCoverageState is not (CheckState.Available or CheckState.Partial) ||
            !Utc(data.QueryStartedUtc) || !Utc(data.QueryCompletedUtc) || data.QueryStartedUtc < scope.ValidatedUtc ||
            data.QueryCompletedUtc < data.QueryStartedUtc || data.QueryCompletedUtc > nowUtc ||
            data.File == null || data.File.Bytes < 0 || data.File.LinkCount != 1 || !Identity(data.File.Identity) || !Identity(data.RootIdentity) ||
            !Utc(data.File.CreationUtc) || !Utc(data.File.LastWriteUtc) || !Utc(data.File.ChangeUtc) ||
            data.File.CreationUtc > data.QueryCompletedUtc || data.File.LastWriteUtc > data.QueryCompletedUtc || data.File.ChangeUtc > data.QueryCompletedUtc ||
            data.QueryStartedUtc - data.File.LastWriteUtc < CleanupFileEvidenceScope.MinimumAge ||
            CleanupFileEvidenceScope.NormalizeFilePath(data.File.Path) == null ||
            !string.Equals(data.File.Path, scope.Candidate.Path, StringComparison.OrdinalIgnoreCase) ||
            CleanupFileEvidenceScope.NormalizeFilePath(data.RootPath) == null ||
            !data.File.Path.StartsWith(data.RootPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
            !data.HistoricalPathMatches || data.HistoricalSizeMatches != (data.File.Bytes == scope.Candidate.Bytes) ||
            data.HistoricalModifiedMatches != (data.File.LastWriteUtc.UtcDateTime == scope.Candidate.ModifiedUtc) ||
            !CleanupStreamScope.IsConsistent(data.StreamScope, data.File.Bytes) || !Coverage(data.Coverage)) return false;
        if (result.State == CheckState.Available && (data.SourceCoverageState != CheckState.Available || data.ExclusionCoverageState != CheckState.Available ||
            !data.HistoricalSizeMatches || !data.HistoricalModifiedMatches || data.StreamScope?.State != CleanupStreamScopeState.UnnamedDataOnly)) return false;
        return true;
    }
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Identity(string? value) => value is { Length: 49 } && value[16] == ':' &&
        value.Where((_, index) => index != 16).All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F') && value.AsSpan(17).IndexOfAnyExcept('0') >= 0;
    private static bool Coverage(IReadOnlyList<string>? items)
    {
        if (items == null || items.Count is < 1 or > 32) return false;
        int total = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item) || item.Length > 4096 || !ShortcutPathPolicy.HasSafeUnicode(item) || (total += item.Length) > 32768) return false;
        }
        return true;
    }
}
