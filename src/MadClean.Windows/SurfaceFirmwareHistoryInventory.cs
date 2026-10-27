using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit online published-history check; never used by ordinary local inspection.</summary>
public sealed class SurfaceFirmwareHistoryInventory : IFirmwareReleaseHistory, IDisposable
{
    private readonly SurfaceFirmwareHistoryClient client = new();
    public Task<CheckResult<SurfaceFirmwareHistoryReport>> CheckAsync(FirmwareIdentity identity, CancellationToken token) =>
        client.CheckAsync(identity, token);
    public void Dispose() => client.Dispose();
}
