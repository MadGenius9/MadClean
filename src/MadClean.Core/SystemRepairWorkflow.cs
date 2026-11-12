namespace MadClean.Core;

public enum SystemRepairConfirmation { Confirmed, MissingAcknowledgment, Invalidated, AlreadyConfirmed, Stale, SourceChanged }
public enum SystemRepairWorkflowOutcome { Blocked, Busy, CancelledBeforeDispatch, PreparationUncertain, DispatchUncertain, ClientExited, ObservationLost, NoClientCreated }
public enum SystemRepairJournalWriteOutcome { DurableAcknowledged, RefusedBeforeWrite, Uncertain }

/// <summary>One fixed repair selection. This managed protocol is not native dispatch authority.</summary>
public sealed class SystemRepairRequest
{
    private readonly object sync = new();
    private bool confirmed, reserved, invalidated, committed;
    private SystemRepairExecutionContext? reservedContext;
    internal SystemRepairRequest(SystemToolActionDefinition definition, long generation, DateTimeOffset preparedUtc)
    { Definition = definition; SourceGeneration = generation; PreparedUtc = preparedUtc; ExpiresUtc = preparedUtc.AddMinutes(2); RequestId = Guid.NewGuid(); }
    public Guid RequestId { get; }
    public SystemToolActionDefinition Definition { get; }
    public long SourceGeneration { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public bool DispatchCommitted { get { lock (sync) return committed; } }
    public bool IsInvalidated { get { lock (sync) return invalidated; } }
    public bool Invalidate()
    {
        lock (sync)
        {
            if (committed) return false;
            invalidated = true; return true;
        }
    }
    public SystemRepairConfirmation Confirm(Guid requestId, SystemToolOperation operation, long generation,
        bool operationSelected, bool effectsAcknowledged, bool elevationAcknowledged, bool observationLimitsAcknowledged,
        DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            if (invalidated || reserved || requestId != RequestId || operation != Definition.Operation)
            { invalidated = !committed; return SystemRepairConfirmation.Invalidated; }
            if (generation != SourceGeneration) { invalidated = true; return SystemRepairConfirmation.SourceChanged; }
            if (confirmed) return SystemRepairConfirmation.AlreadyConfirmed;
            if (!Fresh(nowUtc)) { invalidated = true; return SystemRepairConfirmation.Stale; }
            if (!operationSelected || !effectsAcknowledged || !elevationAcknowledged || !observationLimitsAcknowledged)
                return SystemRepairConfirmation.MissingAcknowledgment;
            confirmed = true; return SystemRepairConfirmation.Confirmed;
        }
    }
    internal bool Reserve(SystemRepairExecutionContext context, long generation, DateTimeOffset nowUtc, CancellationToken token)
    {
        lock (sync)
        {
            if (reserved) return false;
            // An admitted attempt consumes its request even when later refused.
            reserved = true;
            if (!ReferenceEquals(context.Request, this) || !confirmed || invalidated || generation != SourceGeneration || !Fresh(nowUtc) || token.IsCancellationRequested)
            { invalidated = true; return false; }
            reservedContext = context;
            return true;
        }
    }
    internal bool Commit(SystemRepairExecutionContext context, SystemRepairJournalReceipt receipt,
        DateTimeOffset nowUtc, CancellationToken token)
    {
        lock (sync)
        {
            if (!reserved || !confirmed || invalidated || committed || !ReferenceEquals(reservedContext, context) || !ReferenceEquals(context.Request, this) ||
                !receipt.BelongsTo(context, nowUtc) || !Fresh(nowUtc) || token.IsCancellationRequested) return false;
            committed = true; return true;
        }
    }
    internal bool IsCurrentReservedContext(SystemRepairExecutionContext context, DateTimeOffset nowUtc)
    {
        lock (sync) return ReferenceEquals(reservedContext, context) && reserved && confirmed && !invalidated && !committed && Fresh(nowUtc);
    }
    private bool Fresh(DateTimeOffset nowUtc) => SystemRepairWorkflow.Utc(nowUtc) && nowUtc >= PreparedUtc && nowUtc < ExpiresUtc;
}

/// <summary>Coordinator-minted identity. GUID equality alone cannot substitute another context.</summary>
public sealed class SystemRepairExecutionContext
{
    internal SystemRepairExecutionContext(SystemRepairRequest request) { Request = request; ExecutionId = Guid.NewGuid(); }
    public SystemRepairRequest Request { get; }
    public Guid ExecutionId { get; }
    public SystemToolOperation Operation => Request.Definition.Operation;
}

/// <summary>One dispatch claim after this exact session's durable intent receipt.</summary>
public sealed class SystemRepairDispatchPermit
{
    private int claimed;
    internal SystemRepairDispatchPermit(SystemRepairExecutionContext context) => Context = context;
    public SystemRepairExecutionContext Context { get; }
    public bool WasClaimed => Volatile.Read(ref claimed) != 0;
    public bool TryClaim(SystemRepairExecutionContext context) => ReferenceEquals(Context, context) &&
        Interlocked.CompareExchange(ref claimed, 1, 0) == 0;
}

internal sealed class SystemRepairJournalReceipt
{
    private readonly SystemRepairJournalSession session;
    private readonly SystemToolJournalEntry entry;
    internal SystemRepairJournalReceipt(SystemRepairJournalSession session, SystemToolJournalEntry entry)
    { this.session = session; this.entry = entry; }
    internal bool BelongsTo(SystemRepairExecutionContext context, DateTimeOffset nowUtc) => ReferenceEquals(session.Context, context) &&
        session.Owns(entry) && entry.ExecutionId == context.ExecutionId && entry.Operation == context.Operation &&
        entry.Sequence == 1 && entry.Phase == SystemToolJournalPhase.DispatchIntent && SystemRepairWorkflow.Utc(entry.TimestampUtc) &&
        entry.TimestampUtc >= context.Request.PreparedUtc && entry.TimestampUtc <= nowUtc;
}

/// <summary>
/// Trusted implementation boundary, not cryptographic persistence proof. Only the base wrapper
/// mints exact-entry receipts after the owner's durable write observation. No caller supplies a receipt.
/// </summary>
public abstract class SystemRepairJournalSession
{
    private readonly List<SystemToolJournalEntry> entries = [];
    private bool writeFailed;
    protected SystemRepairJournalSession(SystemRepairExecutionContext context) => Context = context ?? throw new ArgumentNullException(nameof(context));
    public SystemRepairExecutionContext Context { get; }
    protected abstract Task<SystemRepairJournalWriteOutcome> PersistAsync(SystemToolJournalEntry entry);
    protected abstract Task<bool> ReleaseOwnershipAsync();
    internal bool Owns(SystemToolJournalEntry entry) => entries.Any(actual => ReferenceEquals(actual, entry));
    internal SystemToolJournalState History => writeFailed ? SystemToolJournalState.Unknown :
        entries.Count == 0 ? SystemToolJournalState.Unknown : SystemToolJournal.Review(entries, DateTimeOffset.UtcNow).State;
    internal bool MayHaveRecord => writeFailed || entries.Count != 0;
    internal async Task<SystemRepairJournalReceipt?> AppendAsync(SystemToolJournalEntry entry)
    {
        if (writeFailed || entry.ExecutionId != Context.ExecutionId || entry.Operation != Context.Operation ||
            SystemToolJournal.Review(entries.Append(entry).ToArray(), DateTimeOffset.UtcNow).State == SystemToolJournalState.Unknown)
            return null;
        SystemRepairJournalWriteOutcome outcome;
        try { outcome = await PersistAsync(entry).ConfigureAwait(false); }
        catch { writeFailed = true; return null; }
        if (outcome == SystemRepairJournalWriteOutcome.DurableAcknowledged)
        {
            entries.Add(entry);
            return new(this, entry);
        }
        // No retry or tail salvage after an unsuccessful persistence attempt.
        // A first refusal explicitly before writes need not invent durable intent.
        writeFailed = outcome != SystemRepairJournalWriteOutcome.RefusedBeforeWrite || entries.Count != 0;
        return null;
    }
    internal async Task<bool> ReleaseAsync()
    { try { return await ReleaseOwnershipAsync().ConfigureAwait(false); } catch { return false; } }
}

/// <summary>Actual bound client owner; completion is observed without a post-commit cancellation token.</summary>
public abstract class SystemRepairDispatchSession
{
    private protected SystemRepairDispatchSession(SystemRepairExecutionContext context) =>
        Context = context ?? throw new ArgumentNullException(nameof(context));
    public SystemRepairExecutionContext Context { get; }
}

public abstract class SystemRepairClientSession : SystemRepairDispatchSession
{
    protected SystemRepairClientSession(SystemRepairExecutionContext context, int processId, DateTimeOffset? clientStartUtc) : base(context)
    { ProcessId = processId; ClientStartUtc = clientStartUtc; }
    public int ProcessId { get; }
    public DateTimeOffset? ClientStartUtc { get; }
    protected abstract Task<ClientProcessObservation> ObserveAsync();
    protected abstract Task<bool> ReleaseOwnershipAsync();
    internal async Task<ClientProcessObservation?> ObserveOwnedAsync()
    { try { return await ObserveAsync().ConfigureAwait(false); } catch { return null; } }
    internal async Task<bool> ReleaseAsync()
    { try { return await ReleaseOwnershipAsync().ConfigureAwait(false); } catch { return false; } }
}

/// <summary>
/// Trusted adapter boundary. A disabled Windows candidate supplies no App permission or qualification.
/// Controlled test collaborators do not enable SystemToolActionsProvider's closed production gate.
/// </summary>
public interface ISystemRepairWorkflowBackend
{
    bool ProductionExecutionValidated { get; }
    Task<SystemRepairJournalSession> OpenJournalAsync(SystemRepairExecutionContext context, CancellationToken preDispatchToken);
    Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit);
}

internal sealed class SystemRepairOwnedState(ISystemRepairWorkflowBackend backend, SystemRepairExecutionContext context)
{
    internal readonly ISystemRepairWorkflowBackend Backend = backend;
    internal readonly SystemRepairExecutionContext Context = context;
    internal SystemRepairJournalSession? Journal;
    internal SystemRepairClientSession? Client;
    internal SystemRepairDirectDispatchSession? Direct;
    internal Task<SystemRepairDirectDispatchEvidence>? DirectObservationTask;
    internal Task? OpenTask, DispatchTask, JournalTask, ReleaseTask;
    internal Task<ClientProcessObservation?>? ObservationTask;
}

/// <summary>Separate managed cooperating domain, not the qualified non-repair gate or a cross-process lock.</summary>
public sealed class SystemRepairAdmission
{
    private readonly object sync = new();
    private SystemRepairExecutionContext? owner;
    private object? retainedOwner;
    private bool pending, blocked, requiresRetention;
    public bool HasPendingWork { get { lock (sync) return pending; } }
    public bool IsBlocked { get { lock (sync) return blocked; } }
    public bool RequiresRetention { get { lock (sync) return requiresRetention && retainedOwner != null; } }
    internal bool Enter(SystemRepairExecutionContext context, SystemRepairOwnedState ownership)
    {
        lock (sync)
        {
            if (owner != null || blocked) return false;
            owner = context; retainedOwner = ownership; pending = true; return true;
        }
    }
    internal void Complete(SystemRepairExecutionContext context, bool released, bool unresolvedHistory)
    {
        lock (sync)
        {
            if (!ReferenceEquals(owner, context) || !pending) return;
            pending = false;
            blocked = !released || unresolvedHistory;
            requiresRetention = !released;
            if (!blocked) { owner = null; retainedOwner = null; }
        }
    }
}

public sealed record SystemRepairWorkflowResult(Guid RequestId, Guid? ExecutionId, SystemRepairWorkflowOutcome Outcome,
    bool DispatchAttempted, bool StopRequested, bool OwnershipReleased, SystemToolJournalState JournalState,
    ClientProcessObservation? ClientObservation, string Message)
{
    public bool? DirectEvidenceConsistent { get; init; }
    public SystemRepairDirectCreation? DirectCreation { get; init; }
    public SystemRepairDirectLaunchResult? DirectCompletion { get; init; }
    public CheckState RepairSucceeded => CheckState.Unknown;
    public CheckState Integrity => CheckState.Unknown;
}

/// <summary>
/// Executable managed orchestration for fixed future repairs. Native provenance, durable storage,
/// shared servicing integration, elevation and actual repair qualification are not supplied here.
/// </summary>
public sealed class SystemRepairWorkflow
{
    private readonly ISystemRepairWorkflowBackend backend;
    private readonly SystemRepairAdmission admission;
    private readonly TimeProvider clock;
    public SystemRepairWorkflow(ISystemRepairWorkflowBackend backend, SystemRepairAdmission admission, TimeProvider? clock = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        this.clock = clock ?? TimeProvider.System;
    }
    public static SystemRepairRequest? Prepare(SystemToolOperation operation, long sourceGeneration, DateTimeOffset nowUtc)
    {
        if (operation is not (SystemToolOperation.DismRestoreHealth or SystemToolOperation.SfcScanNow) || sourceGeneration < 0 ||
            !Utc(nowUtc) || nowUtc > DateTimeOffset.MaxValue.AddMinutes(-2)) return null;
        return new(SystemToolActionsCatalog.Find(operation)!, sourceGeneration, nowUtc);
    }
    internal static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    public async Task<SystemRepairWorkflowResult> ExecuteAsync(SystemRepairRequest request, long currentGeneration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // This check precedes admission, identity, journal and dispatch collaborator calls.
        bool enabled;
        try { enabled = backend.ProductionExecutionValidated; }
        catch { enabled = false; }
        if (!enabled) return Result(SystemRepairWorkflowOutcome.Blocked, "Repair execution is not qualified; no journal or dispatch was requested.");
        var context = new SystemRepairExecutionContext(request);
        var owned = new SystemRepairOwnedState(backend, context);
        if (!admission.Enter(context, owned)) return Result(SystemRepairWorkflowOutcome.Busy, "Another cooperating repair is pending or blocked by unresolved ownership/history.");
        SystemRepairJournalSession? journal = null;
        SystemRepairClientSession? client = null;
        ClientProcessObservation? observation = null;
        SystemRepairDirectDispatchEvidence? directEvidence = null;
        bool directReleaseQualified = false;
        bool attempted = false, released = true, historyBlocked = false;
        var outcome = SystemRepairWorkflowOutcome.Blocked;
        string message = "The exact confirmed repair request was refused before dispatch.";
        try { await RunOwnedAsync().ConfigureAwait(false); }
        catch
        {
            historyBlocked = true;
            outcome = attempted ? SystemRepairWorkflowOutcome.DispatchUncertain : SystemRepairWorkflowOutcome.PreparationUncertain;
            message = "The owned protocol could not establish a complete result. No dispatch is retried and no repair or rollback is inferred.";
        }
        finally
        {
            // This finally cannot be bypassed by caller cancellation. Release observations
            // are independent from historical client observations or journal validity.
            if (client != null)
            {
                owned.ObservationTask ??= client.ObserveOwnedAsync();
                await owned.ObservationTask.ConfigureAwait(false);
                // Stopping/failing observation is not actual client settlement. Keep
                // that owner rooted instead of asking it to close potentially live resources.
                if (observation?.State == ClientProcessObservationState.Exited)
                {
                    var release = client.ReleaseAsync(); owned.ReleaseTask = release;
                    released &= await release.ConfigureAwait(false);
                }
                else { released = false; historyBlocked = true; }
            }
            if (owned.Direct != null)
            {
                if (directReleaseQualified)
                {
                    var release = owned.Direct.Session.ReleaseOwnershipAsync(); owned.ReleaseTask = release;
                    released &= await release.ConfigureAwait(false);
                }
                else { released = false; historyBlocked = true; }
            }
            if (journal != null)
            {
                var release = journal.ReleaseAsync(); owned.ReleaseTask = release;
                released &= await release.ConfigureAwait(false);
                historyBlocked |= journal.MayHaveRecord && journal.History != SystemToolJournalState.RecordedClientExit;
            }
            admission.Complete(context, released, historyBlocked);
        }

        return Finish();

        async Task RunOwnedAsync()
        {
            if (!request.Reserve(context, currentGeneration, clock.GetUtcNow(), token))
            {
                if (token.IsCancellationRequested) outcome = SystemRepairWorkflowOutcome.CancelledBeforeDispatch;
                return;
            }
            SystemRepairJournalSession returned;
            try
            {
                var opening = backend.OpenJournalAsync(context, token); owned.OpenTask = opening;
                returned = await opening.ConfigureAwait(false);
            }
            catch
            {
                released = false; historyBlocked = true; outcome = SystemRepairWorkflowOutcome.PreparationUncertain;
                message = "Journal preparation did not establish settled ownership. No dispatch was attempted.";
                return;
            }
            if (returned == null || !ReferenceEquals(returned.Context, context))
            {
                // Never close or invoke another session owner's resources.
                released = false; historyBlocked = true; outcome = SystemRepairWorkflowOutcome.PreparationUncertain;
                message = "The returned journal did not belong to this exact request owner. No dispatch was attempted.";
                return;
            }
            journal = returned;
            owned.Journal = journal;
            if (token.IsCancellationRequested)
            { outcome = SystemRepairWorkflowOutcome.CancelledBeforeDispatch; return; }
            var intent = Entry(SystemToolJournalPhase.DispatchIntent, 1);
            var receipt = await Append(intent).ConfigureAwait(false);
            if (receipt == null || !request.Commit(context, receipt, clock.GetUtcNow(), token))
            {
                if (journal.MayHaveRecord) await Append(Entry(SystemToolJournalPhase.ObservationLost, 2)).ConfigureAwait(false);
                historyBlocked = journal.MayHaveRecord;
                outcome = token.IsCancellationRequested ? SystemRepairWorkflowOutcome.CancelledBeforeDispatch : SystemRepairWorkflowOutcome.Blocked;
                message = "Dispatch was not admitted. Any durable intent remains unresolved history; no cancellation or rollback of Windows is claimed.";
                return;
            }
            var permit = new SystemRepairDispatchPermit(context);
            var dispatchUtc = clock.GetUtcNow();
            attempted = true;
            SystemRepairDispatchSession returnedClient;
            try
            {
                var dispatching = backend.DispatchAsync(permit); owned.DispatchTask = dispatching;
                returnedClient = await dispatching.ConfigureAwait(false);
            }
            catch
            {
                released = false; historyBlocked = true; outcome = SystemRepairWorkflowOutcome.DispatchUncertain;
                message = "Dispatch outcome is uncertain. It is never retried; an operation may continue independently.";
                await Append(Entry(SystemToolJournalPhase.ObservationLost, 2)).ConfigureAwait(false);
                return;
            }
            if (returnedClient == null || !ReferenceEquals(returnedClient.Context, context))
            {
                released = false; historyBlocked = true; outcome = SystemRepairWorkflowOutcome.DispatchUncertain;
                message = "No owned client session was established. Foreign sessions are not observed or released and dispatch is not retried.";
                await Append(Entry(SystemToolJournalPhase.ObservationLost, 2)).ConfigureAwait(false);
                return;
            }
            if (returnedClient is SystemRepairDirectDispatchSession direct)
            {
                if (!direct.BelongsTo(permit))
                {
                    released = false; historyBlocked = true; outcome = SystemRepairWorkflowOutcome.DispatchUncertain;
                    message = "The direct launch did not belong to this exact permit; foreign resources remain untouched.";
                    await Append(Entry(SystemToolJournalPhase.ObservationLost, 2)).ConfigureAwait(false);
                    return;
                }
                owned.Direct = direct;
                var observing = direct.ObserveOwnedAsync(); owned.DirectObservationTask = observing;
                directEvidence = await observing.ConfigureAwait(false);
                await ObserveDirectAsync(dispatchUtc).ConfigureAwait(false);
                return;
            }
            if (returnedClient is not SystemRepairClientSession returnedOwnedClient)
                throw new InvalidOperationException("Unknown dispatch session type.");
            client = returnedOwnedClient;
            owned.Client = client;
            owned.ObservationTask = client.ObserveOwnedAsync();
            var start = client.ClientStartUtc is { } reported && Utc(reported) && reported >= dispatchUtc && reported <= DateTimeOffset.UtcNow
                ? client.ClientStartUtc : null;
            bool headerValid = permit.WasClaimed && client.ProcessId > 0;
            SystemRepairJournalReceipt? observedReceipt = null;
            if (headerValid) observedReceipt = await Append(Entry(SystemToolJournalPhase.ClientObserved, 2, client.ProcessId, start)).ConfigureAwait(false);
            // Cancellation after commit is an observation request only. Always await actual owner completion.
            var raw = await owned.ObservationTask.ConfigureAwait(false);
            if (raw != null)
            {
                var normalized = ClientProcessObservationProjection.Normalize(raw);
                if (headerValid && normalized.ObservedProcessId == client.ProcessId &&
                    normalized.ObservationStartedUtc >= dispatchUtc &&
                    (start == null || normalized.ClientStartUtc == null || start == normalized.ClientStartUtc)) observation = normalized;
            }
            if (observation?.State == ClientProcessObservationState.Exited)
            {
                outcome = SystemRepairWorkflowOutcome.ClientExited;
                message = "The actual client exit/code was observed. This does not establish successful repair, Windows integrity or rollback.";
                if (observedReceipt != null)
                    await Append(Entry(SystemToolJournalPhase.ClientExited, 3, client.ProcessId, start, observation.ExitCode)).ConfigureAwait(false);
            }
            else
            {
                outcome = SystemRepairWorkflowOutcome.ObservationLost;
                message = "No qualified client exit was observed. Windows work may continue; cancellation, rollback and repair success remain unknown.";
                await Append(Entry(SystemToolJournalPhase.ObservationLost, observedReceipt == null ? 2 : 3,
                    observedReceipt == null ? null : client.ProcessId, observedReceipt == null ? null : start)).ConfigureAwait(false);
            }
            historyBlocked = journal.History != SystemToolJournalState.RecordedClientExit;
            return;
        }

        async Task ObserveDirectAsync(DateTimeOffset dispatchUtc)
        {
            var evidence = directEvidence!;
            var completion = evidence.Completion;
            if (!evidence.IsConsistent || completion == null)
            {
                released = false; historyBlocked = true; outcome = SystemRepairWorkflowOutcome.DispatchUncertain;
                message = "Direct launch evidence is uncertain or inconsistent; no dispatch is retried and ownership is retained.";
                await Append(Entry(SystemToolJournalPhase.ObservationLost, 2)).ConfigureAwait(false);
                return;
            }
            directReleaseQualified = evidence.CleanupQualified;
            if (evidence.KnownNoClient)
            {
                outcome = SystemRepairWorkflowOutcome.NoClientCreated;
                message = "The admitted direct launch established that no client was created. Durable dispatch intent remains unresolved history.";
                await Append(Entry(SystemToolJournalPhase.ObservationLost, 2)).ConfigureAwait(false);
            }
            else if (evidence.ClientWasCreated && completion.ExitObserved == true && completion.ProcessId is > 0 &&
                completion.ExitCode is { } code && completion.ExitObservedUtc is { } exitUtc && exitUtc >= dispatchUtc)
            {
                var pid = completion.ProcessId.Value;
                var observedReceipt = await Append(Entry(SystemToolJournalPhase.ClientObserved, 2, pid)).ConfigureAwait(false);
                observation = new(ClientProcessObservationState.Exited, ClientProcessObservationReason.ClientExited,
                    dispatchUtc, exitUtc, pid, null, code, false);
                outcome = SystemRepairWorkflowOutcome.ClientExited;
                message = "The directly owned client exit/code was observed; repair success and Windows integrity remain unknown.";
                if (observedReceipt != null) await Append(Entry(SystemToolJournalPhase.ClientExited, 3, pid, null, code)).ConfigureAwait(false);
            }
            else
            {
                outcome = SystemRepairWorkflowOutcome.ObservationLost;
                message = "Direct client creation facts are preserved, but a qualified PID and exit/code pair was not established.";
                await Append(Entry(SystemToolJournalPhase.ObservationLost, 2)).ConfigureAwait(false);
            }
            historyBlocked = journal!.History != SystemToolJournalState.RecordedClientExit;
        }

        SystemToolJournalEntry Entry(SystemToolJournalPhase phase, int sequence, int? pid = null, DateTimeOffset? start = null, int? exit = null) =>
            new(context.ExecutionId, context.Operation, sequence, clock.GetUtcNow(), phase, pid, start, exit, null);
        Task<SystemRepairJournalReceipt?> Append(SystemToolJournalEntry entry)
        {
            var writing = journal!.AppendAsync(entry); owned.JournalTask = writing; return writing;
        }
        SystemRepairWorkflowResult Result(SystemRepairWorkflowOutcome state, string text) =>
            new(request.RequestId, null, state, false, token.IsCancellationRequested, true, SystemToolJournalState.Unknown, null, text);
        SystemRepairWorkflowResult Finish() => new(request.RequestId, context.ExecutionId, outcome, attempted,
            token.IsCancellationRequested || observation?.StopRequested == true, released,
            journal?.History ?? SystemToolJournalState.Unknown, observation, message +
            (!released ? " Ownership release was not confirmed; the owner is retained and further cooperating requests are blocked."
                : historyBlocked ? " Journal history remains unresolved; further cooperating requests are blocked." : ""))
            { DirectEvidenceConsistent = directEvidence?.IsConsistent, DirectCreation = directEvidence?.Creation, DirectCompletion = directEvidence?.Completion };
    }
}
