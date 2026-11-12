namespace MadClean.Core;

/// <summary>
/// Trusted storage-owner boundary. Returned reviews describe actual persisted/read-back bytes;
/// neither this interface nor a valid journal authenticates storage or grants native permission.
/// </summary>
public interface ISystemRepairSnapshotStore
{
    SystemRepairExecutionContext Context { get; }
    Task<SystemToolJournalReview> PersistSnapshotAsync(IReadOnlyList<SystemToolJournalEntry> snapshot);
    Task<bool> ReleaseOwnershipAsync();
}

/// <summary>
/// Connects the one-shot repair protocol to an exact-context, append-only snapshot owner.
/// This class performs no file, native, profile-path, process or network access.
/// </summary>
public sealed class SystemRepairStoredJournalSession : SystemRepairJournalSession
{
    private readonly object sync = new();
    private readonly ISystemRepairSnapshotStore store;
    private readonly List<SystemToolJournalEntry> durable = [];
    private TaskCompletionSource<SystemRepairJournalWriteOutcome>? pendingWrite;
    private Task<SystemToolJournalReview>? storeWriteTask;
    private Task? writeWorkerTask;
    private TaskCompletionSource<bool>? release;
    private Task<bool>? storeReleaseTask;
    private Task? releaseWorkerTask;
    private bool stopped, uncertain, contextLost;

    public SystemRepairStoredJournalSession(SystemRepairExecutionContext context, ISystemRepairSnapshotStore store) : base(context)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        if (!ReferenceEquals(store.Context, context))
            throw new ArgumentException("The snapshot store must belong to this exact execution context.", nameof(store));
    }

    protected override Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry)
    {
        TaskCompletionSource<SystemRepairJournalWriteOutcome> completion;
        IReadOnlyList<SystemToolJournalEntry> proposed;
        byte[] canonical;
        lock (sync)
        {
            if (stopped || release != null || pendingWrite is { Task.IsCompleted: false })
            {
                // A concurrent request cannot race an acknowledgment into eligibility.
                uncertain |= pendingWrite is { Task.IsCompleted: false } || durable.Count != 0;
                stopped = true;
                return Task.FromResult(uncertain ? SystemRepairJournalWriteOutcome.Uncertain : SystemRepairJournalWriteOutcome.RefusedBeforeWrite);
            }
            var rows = durable.Select(row => row with { }).Append(entry with { }).ToArray();
            if (!OwnsStore() || entry.ExecutionId != Context.ExecutionId || entry.Operation != Context.Operation ||
                !SystemToolJournal.TryEncode(rows, DateTimeOffset.UtcNow, out var bytes) || bytes == null)
            {
                stopped = true;
                return Task.FromResult(durable.Count == 0 && !uncertain
                    ? SystemRepairJournalWriteOutcome.RefusedBeforeWrite : SystemRepairJournalWriteOutcome.Uncertain);
            }
            proposed = Array.AsReadOnly(rows);
            canonical = bytes;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingWrite = completion; // Root the owned write before invoking its collaborator.
        }
        writeWorkerTask = PersistOwnedAsync(proposed, canonical, completion);
        return completion.Task;
    }

    private async Task PersistOwnedAsync(IReadOnlyList<SystemToolJournalEntry> proposed, byte[] canonical,
        TaskCompletionSource<SystemRepairJournalWriteOutcome> completion)
    {
        var outcome = SystemRepairJournalWriteOutcome.Uncertain;
        byte[]? actualBytes = null;
        try
        {
            if (!OwnsStore()) return;
            var writing = store.PersistSnapshotAsync(proposed);
            lock (sync) storeWriteTask = writing;
            var actual = await writing.ConfigureAwait(false);
            if (!OwnsStore() || actual == null || actual.State == SystemToolJournalState.Unknown ||
                !CopyBounded(actual.Entries, out var returned) ||
                !SystemToolJournal.TryEncode(returned, DateTimeOffset.UtcNow, out actualBytes) || actualBytes == null ||
                SystemToolJournal.Review(returned, DateTimeOffset.UtcNow).State != actual.State ||
                !canonical.AsSpan().SequenceEqual(actualBytes)) return;
            lock (sync)
            {
                if (stopped || uncertain || release != null) return;
                durable.Clear(); durable.AddRange(proposed);
                outcome = SystemRepairJournalWriteOutcome.DurableAcknowledged;
            }
        }
        catch { /* Unknown write/readback/ownership is never a safe retry. */ }
        finally
        {
            Array.Clear(canonical);
            if (actualBytes != null) Array.Clear(actualBytes);
            lock (sync)
            {
                if (outcome != SystemRepairJournalWriteOutcome.DurableAcknowledged) { stopped = true; uncertain = true; }
            }
            GC.KeepAlive(storeWriteTask); GC.KeepAlive(writeWorkerTask);
            completion.TrySetResult(outcome);
        }
    }

    protected override Task<bool> ReleaseOwnershipAsync()
    {
        TaskCompletionSource<bool> completion;
        Task<SystemRepairJournalWriteOutcome>? writing;
        lock (sync)
        {
            if (release != null) return release.Task; // Exactly one actual release attempt, including failure.
            release = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopped = true;
            writing = pendingWrite?.Task;
        }
        releaseWorkerTask = ReleaseOwnedAsync(writing, completion);
        return completion.Task;
    }

    private async Task ReleaseOwnedAsync(Task<SystemRepairJournalWriteOutcome>? writing, TaskCompletionSource<bool> completion)
    {
        bool released = false;
        try
        {
            if (writing != null) await writing.ConfigureAwait(false);
            if (!OwnsStore()) return; // Never invoke another context's cleanup as our own.
            var releasing = store.ReleaseOwnershipAsync();
            lock (sync) storeReleaseTask = releasing;
            released = await releasing.ConfigureAwait(false);
            released &= OwnsStore();
        }
        catch { /* Unobserved or failed release remains sticky. */ }
        finally
        {
            GC.KeepAlive(storeReleaseTask); GC.KeepAlive(releaseWorkerTask);
            completion.TrySetResult(released);
        }
    }

    private bool OwnsStore()
    {
        lock (sync) if (contextLost) return false;
        bool matches;
        try { matches = ReferenceEquals(store.Context, Context); }
        catch { matches = false; }
        lock (sync)
        {
            if (!matches) { contextLost = true; stopped = true; uncertain = true; }
            return !contextLost;
        }
    }

    private static bool CopyBounded(IReadOnlyList<SystemToolJournalEntry>? source, out SystemToolJournalEntry[] copy)
    {
        copy = [];
        if (source == null || source.Count is < 1 or > SystemToolJournal.MaximumEntries) return false;
        int count = source.Count;
        var bounded = new SystemToolJournalEntry[count];
        for (int index = 0; index < count; index++)
        {
            if (source[index] is not { } entry) return false;
            bounded[index] = entry with { };
        }
        if (source.Count != count) return false;
        copy = bounded; return true;
    }
}
