namespace MadClean.Core;

public sealed record DiagnosticFinding(string Channel, string Provider, int EventId, int Version, string Level,
    int Count, DateTimeOffset FirstUtc, DateTimeOffset LastUtc, IReadOnlyList<long> RecordIds,
    string Observation, string Uncertainty, string Impact, string NextStep);
public sealed record DiagnosticReport(DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<DiagnosticFinding> Findings, IReadOnlyList<string> Coverage)
{
    public IReadOnlyList<DiagnosticChannelCoverage> ChannelCoverage { get; init; } = [];
}
public enum DiagnosticChannelCoverageReason
{
    NotChecked, NoCompletedReport, Queried, AccessDenied, Unavailable, Disabled, Failed,
    MissingResponse, AmbiguousResponse, MalformedResponse, UnknownStatus
}
public enum DiagnosticRetentionState { Unknown, Available, Empty }
public sealed record DiagnosticChannelCoverage(string Channel, CheckState State, DiagnosticChannelCoverageReason Reason,
    int? UsableMatchingRecords, bool? Truncated, int? RejectedRecords, int? DuplicateRecords,
    long? WholeLogRecordCount, DiagnosticRetentionState RetentionState, DateTimeOffset? OldestRetainedUtc,
    DateTimeOffset? NewestRetainedUtc, string Explanation);
public interface IDiagnosticsInventory
{
    Task<CheckResult<DiagnosticReport>> ReadAsync(int days, CancellationToken token);
}
