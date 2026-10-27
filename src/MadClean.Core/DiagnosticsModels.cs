namespace MadClean.Core;

public sealed record DiagnosticFinding(string Channel, string Provider, int EventId, int Version, string Level,
    int Count, DateTimeOffset FirstUtc, DateTimeOffset LastUtc, IReadOnlyList<long> RecordIds,
    string Observation, string Uncertainty, string Impact, string NextStep);
public sealed record DiagnosticReport(DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<DiagnosticFinding> Findings, IReadOnlyList<string> Coverage);
public interface IDiagnosticsInventory
{
    Task<CheckResult<DiagnosticReport>> ReadAsync(int days, CancellationToken token);
}
