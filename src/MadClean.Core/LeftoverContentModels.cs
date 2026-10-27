namespace MadClean.Core;

/// <summary>Factory-validated in-session metadata scope, never an ownership or removal authorization.</summary>
public sealed class LeftoverContentScope
{
    internal LeftoverContentScope(string rootPath, string registrationIdentity, DateTimeOffset validatedUtc,
        string sourceEvidence, string sourceCaution, string ownershipCoverage)
    {
        RootPath = rootPath; RegistrationIdentity = registrationIdentity; ValidatedUtc = validatedUtc;
        SourceEvidence = sourceEvidence; SourceCaution = sourceCaution; OwnershipCoverage = ownershipCoverage;
    }
    public string RootPath { get; }
    public string RegistrationIdentity { get; }
    public DateTimeOffset ValidatedUtc { get; }
    public string SourceEvidence { get; }
    public string SourceCaution { get; }
    public string OwnershipCoverage { get; }
}
public sealed record LeftoverContentEntry(string Path, bool IsDirectory, long? Bytes, string Identity, string ActiveUse = "Unknown");
public sealed record LeftoverContentIssue(string Path, string Reason);
public sealed record LeftoverContentReport(string RootPath, IReadOnlyList<LeftoverContentEntry> Entries,
    IReadOnlyList<LeftoverContentIssue> Issues, int VisitedFiles, int VisitedDirectories, bool Truncated, IReadOnlyList<string> Coverage);
public interface ILeftoverContentInventory
{
    Task<CheckResult<LeftoverContentReport>> PreviewAsync(LeftoverContentScope scope, CancellationToken token);
}
