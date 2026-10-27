namespace MadClean.Core;

/// <summary>Bounds the caller's wait without pretending a synchronous native WER call was aborted.</summary>
public static class WerMetadataWait
{
    public static async Task<CheckResult<WerMetadataReport>> AwaitAsync(Task<CheckResult<WerMetadataReport>> worker,
        TimeSpan callerLimit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(worker);
        if (callerLimit <= TimeSpan.Zero || callerLimit > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(callerLimit));
        // A caller may leave before the native worker returns. Observe late faults without logging
        // their details, abandoning ownership, freeing buffers or attempting to terminate native work.
        _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await worker.WaitAsync(callerLimit, token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return CheckResult<WerMetadataReport>.Empty(CheckState.Unknown,
                "The caller's WER wait limit elapsed; no completed store report is shown. " + NativeState(worker));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return CheckResult<WerMetadataReport>.Empty(CheckState.Cancelled,
                "Cancellation was requested for this view; no completed store report is shown. This is not proof a native call was stopped. " + NativeState(worker));
        }
    }
    private static string NativeState(Task worker) => worker.IsCompleted
        ? "The underlying WER worker has returned. No metadata result was inferred from the interrupted wait."
        : "A native WER call may still be running. Its worker retains store/key/buffer ownership; another native query is blocked until it actually returns. No force-abort was attempted.";
}
