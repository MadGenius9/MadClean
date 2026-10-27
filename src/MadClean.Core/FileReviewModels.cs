namespace MadClean.Core;

public sealed record FileReviewIssue(string Path, string Reason);
public sealed record DuplicateEntry(string GroupId, string Path, long Bytes, string Identity, string Relationship, string Verification);
public sealed record DuplicateReport(IReadOnlyList<DuplicateEntry> Entries, IReadOnlyList<FileReviewIssue> Issues,
    int VisitedFiles, int SkippedFiles, bool Truncated, string Coverage);
public interface IDuplicateFileInventory
{
    Task<CheckResult<DuplicateReport>> ScanAsync(string root, CancellationToken token);
}
public enum ShortcutTargetState { TargetExists, LocalTargetMissing, Unknown, Unsupported, UnresolvedMetadata }
public sealed record ShortcutObservation(string ShortcutPath, string TargetPath, ShortcutTargetState State,
    string Reason, string Source, DateTimeOffset CheckedAt);
public sealed record ShortcutReport(IReadOnlyList<ShortcutObservation> Entries, IReadOnlyList<FileReviewIssue> Issues,
    int VisitedFiles, int SkippedFiles, bool Truncated, string Coverage);
public interface IShortcutInventory
{
    Task<CheckResult<ShortcutReport>> ScanAsync(string root, CancellationToken token);
}
