using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit online service-document observations, separate from local Windows inventory and all action execution.</summary>
public sealed class SurfaceServiceFactsInventory : IManufacturerServiceFactsInventory, IDisposable
{
    private readonly ManufacturerServiceFactsClient checker = new();
    public Task<CheckResult<ManufacturerServiceFactsReport>> ReadAsync(string manufacturer, string model, string sku, CancellationToken token) =>
        checker.ReadAsync(manufacturer, model, sku, token);
    public void Dispose() => checker.Dispose();
}
