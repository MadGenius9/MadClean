using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit bounded metadata preview; never an uninstall or cleanup authorization.</summary>
public sealed class WindowsLeftoverContentInventory : ILeftoverContentInventory
{
    private static int activeWorker;
    public bool HasPendingNativeRead => Volatile.Read(ref activeWorker) != 0;
    public async Task<CheckResult<LeftoverContentReport>> PreviewAsync(LeftoverContentScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return CheckResult<LeftoverContentReport>.Empty(CheckState.Unsupported, "Supported Windows handle metadata is required; no leftover folder was inspected.");
        if (scope == null || scope.ValidatedUtc.Offset != TimeSpan.Zero || scope.ValidatedUtc > DateTimeOffset.UtcNow ||
            DateTimeOffset.UtcNow - scope.ValidatedUtc > LeftoverContentPolicy.ScopeLifetime)
            return CheckResult<LeftoverContentReport>.Empty(CheckState.Unknown, "The typed selected-folder scope is missing or stale. Refresh registrations and validate the selection again; no folder was inspected.");
        if (Interlocked.CompareExchange(ref activeWorker, 1, 0) != 0)
            return CheckResult<LeftoverContentReport>.Empty(CheckState.Unknown, "A native leftover metadata worker is still pending; wait before retrying. No new folder inspection started.");
        var worker = Task.Run(() =>
        {
            try { return new WindowsLeftoverContentInventoryNative(scope, token).Read(); }
            catch (OperationCanceledException)
            { return CheckResult<LeftoverContentReport>.Empty(CheckState.Cancelled, "The metadata worker observed cancellation; no completed leftover preview is shown."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            { return CheckResult<LeftoverContentReport>.Empty(CheckState.Failed, "The selected local folder could not be inspected safely. Check access, refresh registrations and retry; no completed preview is shown."); }
            finally { Volatile.Write(ref activeWorker, 0); }
        }, CancellationToken.None);
        _ = worker.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(false); }
        catch (TimeoutException)
        { return CheckResult<LeftoverContentReport>.Empty(CheckState.Unknown, "The caller wait limit elapsed. A native metadata operation may still be pending; its worker retains all handles and prevents another read until it returns. No completed preview is shown."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return CheckResult<LeftoverContentReport>.Empty(CheckState.Cancelled, "Cancellation was requested for this view; it does not prove a native operation stopped. The pending worker retains its handles and blocks another read until return. No completed preview is shown."); }
    }
}
