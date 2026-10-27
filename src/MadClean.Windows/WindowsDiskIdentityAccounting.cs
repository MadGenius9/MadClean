using System.Diagnostics;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit, metadata-only identity accounting of a previously selected map.</summary>
public sealed class WindowsDiskIdentityAccounting : IDiskIdentityAccounting
{
    private static int activeWorker;
    public bool HasPendingNativeRead => Volatile.Read(ref activeWorker) != 0;
    public async Task<CheckResult<DiskIdentityAccountingReport>> AnalyzeAsync(DiskUsageReport snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return CheckResult<DiskIdentityAccountingReport>.Empty(CheckState.Unsupported, "Supported Windows file-ID metadata is required; no identity queries ran.");
        if (Interlocked.CompareExchange(ref activeWorker, 1, 0) != 0)
            return CheckResult<DiskIdentityAccountingReport>.Empty(CheckState.Unknown, "A previous native identity read is still pending; wait before retrying. No new queries started.");
        var worker = Task.Run(() =>
        {
            try
            {
                var watch = Stopwatch.StartNew();
                return new DiskIdentityAccountingEngine(new WindowsDiskIdentityAccountingNative(watch)).Analyze(snapshot, token);
            }
            catch (OperationCanceledException)
            { return CheckResult<DiskIdentityAccountingReport>.Empty(CheckState.Cancelled, "Identity-accounting cancellation was observed by the worker; no completed report is shown."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            { return CheckResult<DiskIdentityAccountingReport>.Empty(CheckState.Failed, "Selected-path metadata accounting failed; no completed report is shown. Check local folder access and retry."); }
            finally { Volatile.Write(ref activeWorker, 0); }
        }, CancellationToken.None);
        _ = worker.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(false); }
        catch (TimeoutException)
        { return CheckResult<DiskIdentityAccountingReport>.Empty(CheckState.Unknown, "The caller wait limit elapsed; a native metadata call may still be pending. Its worker retains ownership of handles until return; retry is blocked while pending. No completed report is shown."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return CheckResult<DiskIdentityAccountingReport>.Empty(CheckState.Cancelled, "Cancellation was requested for this view; this does not prove a native metadata call stopped. The worker retains its handles and blocks another read while pending. No completed report is shown."); }
    }
}
