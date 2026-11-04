namespace MadClean.WindowsSmoke;

/// <summary>
/// A one-shot observation-budget failure, shared by the timer and final monotonic
/// elapsed-time check. It neither cancels nor terminates an owned worker.
/// </summary>
internal sealed class ValidationDeadline
{
    private readonly object sync = new();
    private readonly TimeSpan limit;
    private readonly Action recordFailure;
    private bool expired;

    internal ValidationDeadline(TimeSpan limit, Action recordFailure)
    {
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit));
        ArgumentNullException.ThrowIfNull(recordFailure);
        this.limit = limit;
        this.recordFailure = recordFailure;
    }

    internal bool HasExpired { get { lock (sync) return expired; } }

    internal void ObserveElapsed(TimeSpan actualElapsed)
    {
        if (actualElapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(actualElapsed));
        lock (sync)
        {
            if (expired || actualElapsed < limit) return;
            // Latch before invoking, even if publication itself raises. The lock
            // prevents a competing final check from returning before this callback.
            expired = true;
            recordFailure();
        }
    }
}

/// <summary>
/// Keeps the exact fixture task available for process-lifetime observation after
/// a caller's bounded wait ends. Task completion is not native cleanup proof or
/// permission to promote an earlier interrupted result.
/// </summary>
internal sealed class ValidationWorkerLifetime
{
    private readonly Task actualWorker;

    internal ValidationWorkerLifetime(Task actualWorker)
    {
        ArgumentNullException.ThrowIfNull(actualWorker);
        this.actualWorker = actualWorker;
    }

    internal bool HasPendingWork => !actualWorker.IsCompleted;

    internal async Task DrainAsync()
    {
        // No cancellation token, timeout, worker restart, cleanup or result write.
        // The original fixture already owns the actual native resources and facts.
        try { await actualWorker.ConfigureAwait(false); }
        catch (Exception) { /* Observe the actual terminal fault/cancellation only. */ }
    }
}
