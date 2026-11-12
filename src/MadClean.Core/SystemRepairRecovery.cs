namespace MadClean.Core;

public enum SystemRepairRecoveryOutcome { Blocked, Busy, CancelledBeforeEntry, Reviewed, Uncertain }

/// <summary>One exact existing-history review identity, never repair permission.</summary>
public sealed class SystemRepairRecoveryContext
{
    internal SystemRepairRecoveryContext(DateTimeOffset startedUtc) { Id = Guid.NewGuid(); StartedUtc = startedUtc; }
    public Guid Id { get; }
    public DateTimeOffset StartedUtc { get; }
}

public interface ISystemRepairRecoveryReader
{
    bool ProductionReviewValidated { get; }
    Task<SystemRepairRecoveryReadSession> OpenAsync(SystemRepairRecoveryContext context, CancellationToken preEntryToken);
}

public abstract class SystemRepairRecoveryReadSession
{
    protected SystemRepairRecoveryReadSession(SystemRepairRecoveryContext context) => Context = context ?? throw new ArgumentNullException(nameof(context));
    public SystemRepairRecoveryContext Context { get; }
    protected abstract Task<SystemToolJournalHistory> ReadHistoryAsync();
    protected abstract Task<bool> ReleaseOwnershipAsync();
    internal async Task<SystemToolJournalHistory?> ReadOwnedAsync()
    { try { return await ReadHistoryAsync().ConfigureAwait(false); } catch { return null; } }
    internal async Task<bool> ReleaseOwnedAsync()
    { try { return await ReleaseOwnershipAsync().ConfigureAwait(false); } catch { return false; } }
}

public sealed record SystemRepairRecoveryResult(Guid? ReviewId, SystemRepairRecoveryOutcome Outcome,
    SystemToolJournalHistory? History, bool StopRequested, bool OwnershipReleased, bool? HistoryBlocked, string Message);

/// <summary>
/// One startup review in a fresh cooperating admission domain. It never resets an existing owner,
/// edits records, reopens a PID, resumes work or grants action permission. A late caller stop is
/// observational: actual IO settles, but its history cannot qualify the domain for new work.
/// </summary>
public sealed class SystemRepairRecovery
{
    private readonly ISystemRepairRecoveryReader reader;
    private readonly SystemToolAdmissionGate gate;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private Owner? owner;
    public SystemRepairRecovery(ISystemRepairRecoveryReader reader, SystemToolAdmissionGate gate, TimeProvider? clock = null)
    { this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); this.gate = gate ?? throw new ArgumentNullException(nameof(gate)); this.clock = clock ?? TimeProvider.System; }
    public bool HasPendingWork { get { lock (sync) return owner?.HasPendingWork == true; } }
    public bool RequiresRetention { get { lock (sync) return owner?.RequiresRetention == true; } }
    public Task<SystemRepairRecoveryResult> RunAsync(CancellationToken token = default)
    {
        Owner actual;
        lock (sync)
        {
            if (owner != null) return owner.Completion.Task;
            owner = actual = new(reader, gate, clock, token);
        }
        actual.Start(); // Actual mutable owner is rooted before any collaborator or task runs.
        return actual.Completion.Task;
    }
    private sealed class Owner(ISystemRepairRecoveryReader reader, SystemToolAdmissionGate gate, TimeProvider clock, CancellationToken token)
    {
        private readonly object sync = new();
        private Task? worker, opening, reading, releasing;
        private SystemRepairRecoveryReadSession? session;
        private SystemToolRecoveryAdmissionLease? lease;
        private bool retained;
        private bool? completionStop;
        internal readonly TaskCompletionSource<SystemRepairRecoveryResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HasPendingWork { get { lock (sync) return !Completion.Task.IsCompleted || worker is { IsCompleted: false } ||
            opening is { IsCompleted: false } || reading is { IsCompleted: false } || releasing is { IsCompleted: false }; } }
        internal bool RequiresRetention { get { lock (sync) return retained; } }
        internal void Start()
        {
            try
            {
                var scheduled = new Task<Task<SystemRepairRecoveryResult>>(RunOwnedAsync, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                var actual = scheduled.Unwrap(); lock (sync) worker = actual;
                _ = actual.ContinueWith(done =>
                {
                    if (done.IsCompletedSuccessfully) Completion.TrySetResult(done.Result);
                    else { Retain(); lease?.Complete(this, false, true); Completion.TrySetResult(new(null, SystemRepairRecoveryOutcome.Uncertain,
                        null, token.IsCancellationRequested, false, true, "Recovery ownership is uncertain; no retry or action is authorized.")); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                scheduled.Start(TaskScheduler.Default);
            }
            catch { Retain(); Completion.TrySetResult(new(null, SystemRepairRecoveryOutcome.Uncertain, null, token.IsCancellationRequested,
                false, null, "Recovery scheduling did not establish settled ownership; no retry is supplied.")); }
        }
        private async Task<SystemRepairRecoveryResult> RunOwnedAsync()
        {
            bool enabled; try { enabled = reader.ProductionReviewValidated; } catch { enabled = false; }
            if (!enabled) return Result(null, SystemRepairRecoveryOutcome.Blocked, null, true, null, "Existing-history review is not qualified; no admission or IO was requested.");
            if (token.IsCancellationRequested) return Result(null, SystemRepairRecoveryOutcome.CancelledBeforeEntry, null, true, null, "Stopped before entry; no IO was requested.");
            DateTimeOffset now;
            try { now = clock.GetUtcNow(); }
            catch { return Result(null, SystemRepairRecoveryOutcome.Blocked, null, true, null, "A UTC review time could not be read; no admission or IO was requested."); }
            if (!SystemRepairWorkflow.Utc(now)) return Result(null, SystemRepairRecoveryOutcome.Blocked, null, true, null, "A valid UTC review time was not available.");
            var context = new SystemRepairRecoveryContext(now);
            lease = gate.TryAcquireRecovery(this);
            if (lease == null) return Result(null, SystemRepairRecoveryOutcome.Busy, null, true, null, "The shared domain already has pending, retained or history-blocked ownership. Recovery cannot reset it.");
            bool released = true, blocked = true;
            SystemToolJournalHistory? history = null;
            var outcome = SystemRepairRecoveryOutcome.Uncertain;
            try { await ReviewOwnedAsync().ConfigureAwait(false); }
            catch { blocked = true; }
            finally
            {
                if (session != null)
                {
                    var release = session.ReleaseOwnedAsync(); lock (sync) releasing = release;
                    released &= await release.ConfigureAwait(false);
                }
                if (!released) { blocked = true; Retain(); }
                // This is the completion decision boundary. Later cancellation cannot
                // retroactively alter its result or turn a settled review into running work.
                completionStop = token.IsCancellationRequested;
                blocked |= completionStop.Value && outcome != SystemRepairRecoveryOutcome.CancelledBeforeEntry;
                lease.Complete(this, released, blocked);
            }
            return Result(context.Id, outcome, history, released, blocked, outcome == SystemRepairRecoveryOutcome.CancelledBeforeEntry
                ? "Stopped before reader entry; no IO or history review was requested."
                : !released
                ? "Actual reader cleanup is uncertain; ownership remains retained and further work is blocked."
                : blocked ? "History review did not qualify new work. Records and actual ownership were preserved; no operation was resumed."
                : "Complete existing history was reviewed and released. This removes one historical prerequisite only; it grants no action permission.");
            async Task ReviewOwnedAsync()
            {
                if (token.IsCancellationRequested)
                { blocked = false; outcome = SystemRepairRecoveryOutcome.CancelledBeforeEntry; return; }
                // No caller token reaches the actual admitted IO. A later stop does not abandon it.
                Task<SystemRepairRecoveryReadSession> pending;
                try
                {
                    pending = reader.OpenAsync(context, CancellationToken.None); lock (sync) opening = pending;
                    var returned = await pending.ConfigureAwait(false);
                    if (returned == null || !ReferenceEquals(returned.Context, context)) { released = false; return; }
                    session = returned;
                }
                catch { released = false; return; }
                var read = session.ReadOwnedAsync(); lock (sync) reading = read;
                var source = await read.ConfigureAwait(false);
                var reviewed = SystemToolJournalReconciliation.Review(source?.Journals, source?.EnumerationComplete == true, clock.GetUtcNow());
                history = source != null && source.State == reviewed.State ? reviewed : SystemToolJournalReconciliation.Review(null, false, clock.GetUtcNow());
                blocked = token.IsCancellationRequested || history.State is SystemToolJournalHistoryState.UnknownEvidence or SystemToolJournalHistoryState.UnresolvedPresent;
                outcome = history.State == SystemToolJournalHistoryState.UnknownEvidence ? SystemRepairRecoveryOutcome.Uncertain : SystemRepairRecoveryOutcome.Reviewed;
            }

        }
        private void Retain() { lock (sync) retained = true; }
        private SystemRepairRecoveryResult Result(Guid? id, SystemRepairRecoveryOutcome outcome, SystemToolJournalHistory? history,
            bool released, bool? blocked, string message) => new(id, outcome, history, completionStop ?? token.IsCancellationRequested, released, blocked, message);
    }
}
