using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit selected-product MSI metadata lookup, never installer activation or action eligibility.</summary>
public sealed class WindowsMsiRegistrationInventory : IMsiRegistrationInventory
{
    private static int activeWorker;
    internal static bool TryBeginRead() => Interlocked.CompareExchange(ref activeWorker, 1, 0) == 0;
    internal static void EndRead() => Volatile.Write(ref activeWorker, 0);
    internal static bool PendingRead => Volatile.Read(ref activeWorker) != 0;
    public bool HasPendingNativeRead => Volatile.Read(ref activeWorker) != 0;
    public async Task<CheckResult<MsiRegistrationReport>> VerifyAsync(MsiRegistrationScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return CheckResult<MsiRegistrationReport>.Empty(CheckState.Unsupported, "Selected MSI registration metadata is supported on Windows only; no query ran.");
        if (!Fresh(scope))
            return CheckResult<MsiRegistrationReport>.Empty(CheckState.Unknown, "The selected MSI registration scope is missing, stale or invalid. Refresh registrations and select it again; no query ran.");
        if (!TryBeginRead())
            return CheckResult<MsiRegistrationReport>.Empty(CheckState.Unknown, "A previous native MSI metadata lookup is still pending. No new query started; wait before retrying.");
        // Keep all enumeration/property calls on one synchronous worker thread. Cancellation cannot
        // prevent this delegate's finally from running after native work actually returns.
        var worker = Task.Run(() =>
        {
            try
            {
                if (!Fresh(scope))
                    return CheckResult<MsiRegistrationReport>.Empty(CheckState.Unknown, "The selected MSI scope expired before the worker started; no query ran. Refresh registrations and retry.");
                return new WindowsMsiRegistrationInventoryNative(scope, token).Read();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return CheckResult<MsiRegistrationReport>.Empty(CheckState.Cancelled, "The MSI metadata worker observed cancellation; no completed verification is shown. No installer action occurred."); }
            catch (WindowsMsiRegistrationBudgetException)
            { return CheckResult<MsiRegistrationReport>.Empty(CheckState.Unknown, "The MSI metadata worker's cooperative 20-second budget elapsed; no completed verification is shown. Retry when the system is responsive."); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { return CheckResult<MsiRegistrationReport>.Empty(CheckState.Unsupported, "The required Windows Installer metadata entry points are unavailable; no completed verification is shown."); }
            finally { EndRead(); }
        }, CancellationToken.None);
        _ = worker.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var result = await worker.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (TimeoutException)
        { return CheckResult<MsiRegistrationReport>.Empty(CheckState.Unknown, "The caller's 30-second MSI metadata wait limit elapsed; no completed verification is shown. " + NativeState(worker)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return CheckResult<MsiRegistrationReport>.Empty(CheckState.Cancelled, "Cancellation was requested for this view; no completed verification is shown. This does not prove a synchronous MSI call stopped. " + NativeState(worker)); }
    }
    private static bool Fresh(MsiRegistrationScope? scope)
    {
        var now = DateTimeOffset.UtcNow;
        return scope != null && scope.ValidatedUtc.Offset == TimeSpan.Zero && scope.ValidatedUtc != default &&
            scope.ValidatedUtc <= now && now - scope.ValidatedUtc <= MsiRegistrationReview.ScopeLifetime &&
            scope.ExpectedContextMask is 3 or 4 && Guid.TryParseExact(scope.ProductCode, "B", out var code) && code != Guid.Empty;
    }
    private static string NativeState(Task worker) => worker.IsCompleted
        ? "The underlying MSI worker has returned; no result was inferred from the interrupted wait."
        : "A synchronous MSI metadata call may still be running. Its worker retains all buffer ownership and blocks another lookup until it actually returns; no force-abort was attempted.";
}
