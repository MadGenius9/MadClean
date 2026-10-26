namespace MadClean.Core;

public enum CheckState { Unknown, Available, Unavailable, Unsupported, Failed, Partial, Cancelled }
public sealed record CheckResult<T>(CheckState State, string Message, IReadOnlyList<T> Items)
{
    public static CheckResult<T> Empty(CheckState state, string message) => new(state, message, Array.Empty<T>());
}
public sealed record DiskInfo(string Name, long TotalBytes, long FreeBytes);
public sealed record CleanupLocation(string Category, string Path);
public sealed record CleanupCandidate(string Category, string Path, long Bytes, DateTime ModifiedUtc);
public sealed record ScanIssue(string Category, string Path, string Reason);
public sealed record CleanupReport(IReadOnlyList<CleanupCandidate> Candidates, IReadOnlyList<ScanIssue> Issues,
    long ExcludedCount, bool Truncated)
{
    public long TotalBytes => Candidates.Sum(x => x.Bytes);
}
public sealed record StartupEntry(string Name, string Source, string Publisher, string Detail);
public sealed record DefenderInfo(bool? AntivirusEnabled, bool? RealTimeEnabled, bool? ServiceEnabled,
    string Mode, string SignatureVersion, string SignatureUpdated, string FindingsState, IReadOnlyList<DefenderFinding> Findings);
public sealed record DefenderFinding(string ThreatId, string ActionSuccess, string LastStatusChange);
public sealed record AppUpdate(string Name, string Id, string InstalledVersion, string AvailableVersion, string Source, CheckState Status);
public sealed record SystemUpdate(string Title, string Kind, string Identity, bool RebootRequired,
    string DriverClass = "Unknown", string PackageDate = "Unknown", string Description = "Unknown");

public interface IComputerInventory
{
    Task<CheckResult<DiskInfo>> DisksAsync(CancellationToken token);
    Task<CheckResult<StartupEntry>> StartupAsync(CancellationToken token);
    Task<CheckResult<DefenderInfo>> DefenderAsync(CancellationToken token);
    Task<CheckResult<AppUpdate>> AppsAsync(CancellationToken token);
    Task<CheckResult<SystemUpdate>> SystemUpdatesAsync(CancellationToken token);
    IReadOnlyList<CleanupLocation> CleanupLocations { get; }
}

public static class Display
{
    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value; int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
}
