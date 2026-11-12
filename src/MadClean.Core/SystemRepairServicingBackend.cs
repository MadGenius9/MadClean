namespace MadClean.Core;

/// <summary>Trusted fixed-operation platform boundary, never a caller-supplied executable or permission flag.</summary>
public interface ISystemRepairExecutionPlatform
{
    bool ProductionExecutionValidated { get; }
    Task<SystemRepairPreparedSession> PrepareAsync(SystemRepairExecutionContext context, CancellationToken preDispatchToken);
}

/// <summary>
/// Owns qualified preparation resources. Journal and client ownership are released separately;
/// final release must not close them again or infer servicing cancellation.
/// </summary>
public abstract class SystemRepairPreparedSession
{
    protected SystemRepairPreparedSession(SystemRepairExecutionContext context) =>
        Context = context ?? throw new ArgumentNullException(nameof(context));
    public SystemRepairExecutionContext Context { get; }
    public abstract SystemRepairJournalSession Journal { get; }
    public abstract Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit);
    public abstract Task<bool> ReleaseOwnershipAsync();
}

/// <summary>
/// Composes repair preparation/journal/client ownership with the same cooperating admission
/// instance used by non-repair owners. No production qualification or native permission is supplied.
/// </summary>
public sealed class SystemRepairServicingBackend : ISystemRepairWorkflowBackend
{
    private readonly ISystemRepairExecutionPlatform platform;
    private readonly SystemToolAdmissionGate sharedGate;
    private readonly object sync = new();
    private readonly Dictionary<SystemRepairExecutionContext, Owner> owners = new(ReferenceEqualityComparer.Instance);

    public SystemRepairServicingBackend(ISystemRepairExecutionPlatform platform, SystemToolAdmissionGate sharedGate)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.sharedGate = sharedGate ?? throw new ArgumentNullException(nameof(sharedGate));
    }
    public bool ProductionExecutionValidated => platform.ProductionExecutionValidated;
    public bool HasPendingWork => Owners().Any(owner => owner.HasPendingWork);
    public bool RequiresRetention => Owners().Any(owner => owner.RequiresRetention);
    public bool IsHistoryBlocked => sharedGate.IsHistoryBlocked;
    private Owner[] Owners() { lock (sync) return owners.Values.ToArray(); }

    public Task<SystemRepairJournalSession> OpenJournalAsync(SystemRepairExecutionContext context, CancellationToken preDispatchToken)
    {
        // This precedes shared admission and every platform/preparation callback.
        if (!Enabled()) return Refuse<SystemRepairJournalSession>();
        ArgumentNullException.ThrowIfNull(context);
        if (preDispatchToken.IsCancellationRequested) return Task.FromResult<SystemRepairJournalSession>(new RefusedJournal(context));
        var owner = new Owner(this, context);
        lock (sync)
        {
            var lease = sharedGate.TryAcquireRepair(context, owner);
            if (lease == null) return Task.FromResult<SystemRepairJournalSession>(new RefusedJournal(context));
            owner.Attach(lease); // The shared gate already roots this exact owner.
            try { owners.Add(context, owner); }
            catch { owner.Fail(); throw; }
        }
        return owner.BeginPrepare(preDispatchToken);
    }

    public Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit)
    {
        if (!Enabled()) return Refuse<SystemRepairDispatchSession>();
        ArgumentNullException.ThrowIfNull(permit);
        Owner? owner;
        lock (sync) owners.TryGetValue(permit.Context, out owner);
        return owner == null ? Refuse<SystemRepairDispatchSession>() : owner.BeginDispatch(permit);
    }
    private bool Enabled()
    { try { return platform.ProductionExecutionValidated; } catch { return false; } }
    private static Task<T> Refuse<T>() => Task.FromException<T>(new InvalidOperationException(
        "The fixed repair backend is disabled, busy or not bound to this actual reserved request."));
    private void Forget(Owner owner)
    {
        lock (sync)
            if (owners.TryGetValue(owner.Context, out var actual) && ReferenceEquals(owner, actual)) owners.Remove(owner.Context);
    }
    private sealed class RefusedJournal(SystemRepairExecutionContext context) : SystemRepairJournalSession(context)
    {
        protected override Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry) =>
            Task.FromResult(SystemRepairJournalWriteOutcome.RefusedBeforeWrite);
        protected override Task<bool> ReleaseOwnershipAsync() => Task.FromResult(true);
    }

    private sealed class Owner(SystemRepairServicingBackend backend, SystemRepairExecutionContext context)
    {
        private readonly object sync = new();
        private SystemToolAdmissionLease? lease;
        private SystemRepairPreparedSession? prepared;
        private SystemRepairJournalSession? journal;
        private SystemRepairClientSession? client;
        private SystemRepairDirectDispatchSession? direct;
        private Task<SystemRepairDirectDispatchEvidence>? directEvidenceTask;
        private Task<bool>? directReleaseTask;
        private Task<SystemRepairPreparedSession>? preparingTask;
        private Task<SystemRepairJournalSession>? openingTask;
        private Task<SystemRepairDispatchSession>? dispatchingTask;
        private Task<SystemRepairDispatchSession>? dispatchedTask;
        private Task? dispatchWorkerTask, dispatchForwardTask, observationWorkerTask, observationForwardTask, clientReleaseWorkerTask, clientReleaseForwardTask;
        private Task<SystemRepairJournalReceipt?>? writeTask;
        private Task<ClientProcessObservation?>? observingTask;
        private Task<bool>? clientReleaseTask, journalReleaseTask, preparationReleaseTask;
        private TaskCompletionSource<bool>? journalRelease;
        private Task? finalReleaseContinuation;
        private bool dispatchAttempted, uncertain, clientExitQualified, clientReleased, finalized, resourcesReleased;
        private DateTimeOffset dispatchUtc;

        internal SystemRepairExecutionContext Context { get; } = context;
        internal bool HasPendingWork
        {
            get
            {
                lock (sync) return !finalized && !uncertain ||
                    new Task?[] { preparingTask, openingTask, dispatchingTask, dispatchedTask, writeTask, observingTask,
                        clientReleaseTask, journalReleaseTask, preparationReleaseTask, directEvidenceTask, directReleaseTask }.Any(task => task is { IsCompleted: false }) ||
                    direct?.Session.HasPendingWork == true;
            }
        }
        internal bool RequiresRetention { get { lock (sync) return uncertain || finalized && !resourcesReleased || direct?.Session.RequiresRetention == true; } }
        internal void Attach(SystemToolAdmissionLease value) => lease = value;
        internal void Fail()
        {
            lock (sync) uncertain = true;
            lease?.CompleteRepair(Context, this, false, true);
        }
        internal Task<SystemRepairJournalSession> BeginPrepare(CancellationToken token)
        {
            var task = PrepareOwnedAsync(token);
            lock (sync) openingTask = task;
            return task;
        }
        private async Task<SystemRepairJournalSession> PrepareOwnedAsync(CancellationToken token)
        {
            try
            {
                var work = backend.platform.PrepareAsync(Context, token);
                lock (sync) preparingTask = work;
                var actual = await work.ConfigureAwait(false);
                if (actual == null || !ReferenceEquals(actual.Context, Context)) throw new InvalidOperationException();
                lock (sync) prepared = actual;
                var actualJournal = actual.Journal;
                if (actualJournal == null || !ReferenceEquals(actualJournal.Context, Context)) throw new InvalidOperationException();
                lock (sync) journal = actualJournal;
                return new BoundJournal(this);
            }
            catch { Fail(); throw; } // Never release foreign or unreturned resources as our own.
        }

        internal Task<SystemRepairDispatchSession> BeginDispatch(SystemRepairDispatchPermit permit)
        {
            TaskCompletionSource<SystemRepairDispatchSession> completion;
            lock (sync)
            {
                if (uncertain || dispatchAttempted || journalRelease != null || prepared == null || journal == null ||
                    !ReferenceEquals(permit.Context, Context) || !Context.Request.DispatchCommitted || permit.WasClaimed)
                    return Refuse<SystemRepairDispatchSession>();
                dispatchAttempted = true;
                dispatchUtc = DateTimeOffset.UtcNow;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                dispatchedTask = completion.Task;
            }
            // Never call a platform collaborator under the admission/state lock.
            var task = DispatchOwnedAsync(permit);
            lock (sync) dispatchWorkerTask = task;
            var forwarding = Forward(task, completion);
            lock (sync) dispatchForwardTask = forwarding;
            return completion.Task;
        }
        private async Task<SystemRepairDispatchSession> DispatchOwnedAsync(SystemRepairDispatchPermit permit)
        {
            try
            {
                var work = prepared!.DispatchAsync(permit);
                lock (sync) dispatchingTask = work;
                var actual = await work.ConfigureAwait(false);
                if (actual == null || !ReferenceEquals(actual.Context, Context)) throw new InvalidOperationException();
                if (actual is SystemRepairClientSession actualClient)
                {
                    lock (sync) client = actualClient;
                    return new BoundClient(this, actualClient);
                }
                if (actual is SystemRepairDirectDispatchSession actualDirect && actualDirect.BelongsTo(permit))
                {
                    lock (sync) direct = actualDirect;
                    return actualDirect;
                }
                throw new InvalidOperationException();
            }
            catch { Fail(); throw; } // An attempted dispatch is never retried.
        }

        private async Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry)
        {
            var work = journal!.AppendAsync(entry);
            lock (sync) writeTask = work;
            var receipt = await work.ConfigureAwait(false);
            return receipt != null ? SystemRepairJournalWriteOutcome.DurableAcknowledged
                : journal.MayHaveRecord ? SystemRepairJournalWriteOutcome.Uncertain : SystemRepairJournalWriteOutcome.RefusedBeforeWrite;
        }
        private Task<ClientProcessObservation?> ObserveAsync()
        {
            TaskCompletionSource<ClientProcessObservation?> completion;
            lock (sync)
            {
                if (observingTask != null) return observingTask;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                observingTask = completion.Task;
            }
            var task = ObserveOwnedAsync();
            lock (sync) observationWorkerTask = task;
            var forwarding = Forward(task, completion);
            lock (sync) observationForwardTask = forwarding;
            return completion.Task;
        }
        private async Task<ClientProcessObservation?> ObserveOwnedAsync()
        {
            var raw = await client!.ObserveOwnedAsync().ConfigureAwait(false);
            if (raw != null)
            {
                var normalized = ClientProcessObservationProjection.Normalize(raw);
                bool qualified = normalized.State == ClientProcessObservationState.Exited && client.ProcessId > 0 &&
                    normalized.ObservedProcessId == client.ProcessId && normalized.ObservationStartedUtc >= dispatchUtc &&
                    (client.ClientStartUtc == null || normalized.ClientStartUtc == null || client.ClientStartUtc == normalized.ClientStartUtc);
                lock (sync) clientExitQualified = qualified;
            }
            return raw;
        }
        private Task<bool> ReleaseClientAsync()
        {
            TaskCompletionSource<bool> completion;
            lock (sync)
            {
                if (clientReleaseTask != null) return clientReleaseTask;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                clientReleaseTask = completion.Task;
            }
            var task = ReleaseClientOwnedAsync();
            lock (sync) clientReleaseWorkerTask = task;
            var forwarding = Forward(task, completion);
            lock (sync) clientReleaseForwardTask = forwarding;
            return completion.Task;
        }
        private async Task<bool> ReleaseClientOwnedAsync()
        {
            await ObserveAsync().ConfigureAwait(false);
            bool qualified;
            lock (sync) qualified = clientExitQualified;
            if (!qualified) { Fail(); return false; }
            var released = await client!.ReleaseAsync().ConfigureAwait(false);
            lock (sync) clientReleased = released;
            if (!released) Fail();
            return released;
        }

        private Task<bool> ReleaseJournalAsync()
        {
            TaskCompletionSource<bool> completion;
            lock (sync)
            {
                if (journalRelease != null) return journalRelease.Task;
                journalRelease = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                var work = ReleaseAllOwnedAsync();
                lock (sync) journalReleaseTask = work;
                var continuation = work.ContinueWith(done =>
                {
                    bool released = done.Status == TaskStatus.RanToCompletion && done.Result;
                    bool history = journal!.MayHaveRecord && journal.History != SystemToolJournalState.RecordedClientExit;
                    lock (sync) { finalized = true; resourcesReleased = released; uncertain |= !released; }
                    lease!.CompleteRepair(Context, this, released, history);
                    if (released && !history) backend.Forget(this);
                    completion.TrySetResult(released);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                lock (sync) finalReleaseContinuation = continuation;
            }
            catch { Fail(); completion.TrySetResult(false); }
            return completion.Task;
        }

        private async Task Forward<T>(Task<T> task, TaskCompletionSource<T> completion)
        {
            try { completion.TrySetResult(await task.ConfigureAwait(false)); }
            catch (Exception exception) { Fail(); completion.TrySetException(exception); }
        }
        private async Task<bool> ReleaseAllOwnedAsync()
        {
            try
            {
                Task<SystemRepairJournalReceipt?>? writing;
                lock (sync) writing = writeTask;
                if (writing != null) await writing.ConfigureAwait(false);
                if (direct != null)
                {
                    var evidenceWork = direct.ObserveOwnedAsync();
                    lock (sync) directEvidenceTask = evidenceWork;
                    var evidence = await evidenceWork.ConfigureAwait(false);
                    if (evidence.CleanupQualified)
                    {
                        var releaseWork = direct.Session.ReleaseOwnershipAsync();
                        lock (sync) directReleaseTask = releaseWork;
                        var actualReleased = await releaseWork.ConfigureAwait(false);
                        lock (sync) clientReleased = actualReleased;
                        if (!actualReleased) { Fail(); return false; }
                    }
                    else { Fail(); return false; }
                }
                var released = await journal!.ReleaseAsync().ConfigureAwait(false);
                bool canRelease;
                lock (sync) canRelease = !uncertain && (!dispatchAttempted || clientReleased);
                if (!released || !canRelease) return false;
                var work = prepared!.ReleaseOwnershipAsync();
                lock (sync) preparationReleaseTask = work;
                return await work.ConfigureAwait(false);
            }
            catch { return false; }
        }

        private sealed class BoundJournal(Owner owner) : SystemRepairJournalSession(owner.Context)
        {
            protected override Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry) => owner.PersistAsync(entry);
            protected override Task<bool> ReleaseOwnershipAsync() => owner.ReleaseJournalAsync();
        }
        private sealed class BoundClient(Owner owner, SystemRepairClientSession actual)
            : SystemRepairClientSession(owner.Context, actual.ProcessId, actual.ClientStartUtc)
        {
            protected override async Task<ClientProcessObservation> ObserveAsync() =>
                await owner.ObserveAsync().ConfigureAwait(false) ?? throw new InvalidOperationException();
            protected override Task<bool> ReleaseOwnershipAsync() => owner.ReleaseClientAsync();
        }
    }
}
