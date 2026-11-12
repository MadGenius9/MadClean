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
    private readonly object ownershipSync = new();
    private readonly TaskCompletionSource ownershipCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool ownershipDrainStarted;

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

    /// <summary>
    /// The exact worker may return before its native owner settles. This one-shot
    /// drain has no timeout or reset; Main must actually await the returned task.
    /// A static root alone does not keep the CLR alive or establish cleanup.
    /// </summary>
    internal Task DrainOwnershipAsync(Func<bool?> hasPending, Func<bool?> requiresRetention,
        Func<Task>? nextObservation = null)
    {
        lock (ownershipSync)
        {
            if (ownershipDrainStarted) return ownershipCompletion.Task;
            ownershipDrainStarted = true;
        }
        // Start outside the status lock: an already-completed worker can reach
        // predicates synchronously, and those predicates may reenter observation.
        _ = DrainOwnershipCoreAsync(hasPending, requiresRetention, nextObservation);
        return ownershipCompletion.Task;
    }

    private async Task DrainOwnershipCoreAsync(Func<bool?> hasPending, Func<bool?> requiresRetention,
        Func<Task>? nextObservation)
    {
        // Observe actual worker failure/cancellation first, without changing it or
        // assuming that it establishes native cleanup. No replacement worker runs.
        await DrainAsync().ConfigureAwait(false);
        try
        {
            if (hasPending == null || requiresRetention == null) return;
            while (true)
            {
                bool? pending = hasPending();
                bool? retained = requiresRetention();
                // Leaving ownershipCompletion uncompleted is the permanent hold.
                // There is no reset, second observer, cleanup claim or further poll.
                if (!pending.HasValue || retained != false) return;
                if (pending == false)
                {
                    ownershipCompletion.TrySetResult();
                    return;
                }
                Task? observation = nextObservation == null ? Task.Delay(100) : nextObservation();
                if (observation == null) return;
                await observation.ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Predicate/polling uncertainty retains the same incomplete promise.
            // Never retry, cancel pending native work or assume resources released.
        }
    }
}
