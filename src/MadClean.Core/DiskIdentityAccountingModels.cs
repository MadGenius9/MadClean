namespace MadClean.Core;

public sealed record DiskIdentityAccountingObservation(string Path, long SnapshotBytes, string Identity,
    uint LinkCount, int ObservedAliasCount);
public sealed record DiskIdentityAccountingIssue(string Path, string Reason);
public sealed record DiskIdentityAccountingReport(long PathLogicalBytes, long ObservedPathLogicalBytes,
    long ObservedUniqueFileLogicalBytes, IReadOnlyList<DiskIdentityAccountingObservation> Observations,
    IReadOnlyList<DiskIdentityAccountingIssue> Issues, bool Truncated, string Coverage);
public interface IDiskIdentityAccounting
{
    Task<CheckResult<DiskIdentityAccountingReport>> AnalyzeAsync(DiskUsageReport snapshot, CancellationToken token);
}

// Adapter boundary. Test adapters supply synthetic identities; production uses only native handle metadata.
public sealed record DiskIdentityAccountingStamp(string Identity, long Bytes, uint LinkCount, bool Stable);
public interface IDiskIdentityAccountingProbe
{
    DiskIdentityAccountingStamp Probe(string path, CancellationToken token);
}
