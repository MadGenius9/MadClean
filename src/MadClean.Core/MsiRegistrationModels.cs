namespace MadClean.Core;

public enum MsiRegistrationContext { UserManaged = 1, UserUnmanaged = 2, Machine = 4 }
public enum MsiObservedProductState { Unknown, Advertised, Installed }

/// <summary>A selected read-only lookup scope, never an uninstall or maintenance authorization.</summary>
public sealed class MsiRegistrationScope
{
    internal MsiRegistrationScope(InstalledApplication application, string productCode, int expectedContextMask,
        CheckState snapshotState, DateTimeOffset validatedUtc)
    {
        Application = application; ProductCode = productCode; ExpectedContextMask = expectedContextMask;
        SnapshotState = snapshotState; ValidatedUtc = validatedUtc;
    }
    public InstalledApplication Application { get; }
    public string ProductCode { get; }
    public int ExpectedContextMask { get; }
    public CheckState SnapshotState { get; }
    public DateTimeOffset ValidatedUtc { get; }
}
public sealed record MsiRegistrationObservation(string ProductCode, MsiRegistrationContext Context,
    MsiObservedProductState ProductState, string Name, string Version, string Publisher, DateTimeOffset ObservedUtc);
public sealed record MsiRegistrationReport(string RegistrationIdentity, string ProductCode, CheckState SnapshotState,
    bool? NoRemove, IReadOnlyList<MsiRegistrationObservation> Instances, DateTimeOffset CheckedUtc, IReadOnlyList<string> Coverage);
public interface IMsiRegistrationInventory
{
    Task<CheckResult<MsiRegistrationReport>> VerifyAsync(MsiRegistrationScope scope, CancellationToken token);
}
