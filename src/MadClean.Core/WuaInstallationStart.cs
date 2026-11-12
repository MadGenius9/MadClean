namespace MadClean.Core;

/// <summary>One explicit metadata-bound request, not device applicability or native installation permission.</summary>
public sealed class WuaInstallationStartRequest
{
    private readonly object sync = new();
    private bool confirmed, reserved, invalidated, beginCommitted;
    private WuaInstallationStartSession? reservedSession;
    internal WuaInstallationStartRequest(WuaInstallationTarget target, long generation, DateTimeOffset prepared)
    { Target = target with { }; SourceGeneration = generation; PreparedUtc = prepared; ExpiresUtc = prepared.AddMinutes(2); RequestId = Guid.NewGuid(); }
    public Guid RequestId { get; }
    public WuaInstallationTarget Target { get; }
    public long SourceGeneration { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public bool BeginCommitted { get { lock (sync) return beginCommitted; } }
    public bool IsInvalidated { get { lock (sync) return invalidated; } }
    public bool Confirm(Guid requestId, WuaInstallationTarget target, long generation, bool explicitlyConfirmed, DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            if (invalidated || confirmed || reserved) return false;
            if (requestId != RequestId || target != Target || generation != SourceGeneration || !Fresh(nowUtc))
            { invalidated = true; return false; }
            if (!explicitlyConfirmed) return false;
            confirmed = true; return true;
        }
    }
    public bool Invalidate()
    {
        lock (sync) { if (beginCommitted) return false; invalidated = true; return true; }
    }
    internal bool Reserve(WuaInstallationStartSession session, long generation, DateTimeOffset nowUtc, CancellationToken token)
    {
        lock (sync)
        {
            if (reserved) return false;
            reserved = true;
            if (!confirmed || invalidated || !ReferenceEquals(session.Request, this) || generation != SourceGeneration || !Fresh(nowUtc) || token.IsCancellationRequested)
            { invalidated = true; return false; }
            reservedSession = session; return true;
        }
    }
    internal bool EnterBegin(WuaInstallationStartSession session, long generation, DateTimeOffset nowUtc, CancellationToken token)
    {
        lock (sync)
        {
            if (!reserved || !ReferenceEquals(reservedSession, session) || beginCommitted || invalidated ||
                generation != SourceGeneration || !Fresh(nowUtc) || token.IsCancellationRequested) return false;
            beginCommitted = true; return true;
        }
    }
    private bool Fresh(DateTimeOffset now) => WuaInstallationStart.Utc(now) && now >= PreparedUtc && now < ExpiresUtc;
}

/// <summary>Actual trusted-adapter observation; managed reference binding does not prove COM identity.</summary>
public sealed class WuaInstallationBeginResult
{
    internal WuaInstallationBeginResult(WuaInstallationStartSession owner, int? hresult, WuaInstallationJobSession? job)
    { Owner = owner; HResult = hresult; Job = job; Context = job?.Context; }
    internal WuaInstallationStartSession Owner { get; }
    public int? HResult { get; }
    public WuaInstallationJobSession? Job { get; }
    public WuaInstallationJobContext? Context { get; }
}

/// <summary>
/// Future trusted native adapter boundary. Construct this session and its actual job/callback
/// graph before BeginInstall; the caller still owns unentered preparation on refusal.
/// No concrete Windows adapter, source/device eligibility or production permission is supplied.
/// </summary>
public abstract class WuaInstallationStartSession
{
    protected WuaInstallationStartSession(WuaInstallationStartRequest request, WuaInstallationJobSession job)
    { Request = request ?? throw new ArgumentNullException(nameof(request)); Job = job ?? throw new ArgumentNullException(nameof(job)); }
    public WuaInstallationStartRequest Request { get; }
    public WuaInstallationJobSession Job { get; }
    protected WuaInstallationBeginResult CreateBeginResult(int? hresult, WuaInstallationJobSession? returnedJob) => new(this, hresult, returnedJob);
    protected abstract Task<WuaInstallationBeginResult?> BeginInstallAsync();
    internal Task<WuaInstallationBeginResult?> BeginOwnedAsync() => BeginInstallAsync();
}

public enum WuaInstallationStartOutcome { RefusedBeforeEntry, BeginUncertain, MonitoringUncertain, Monitored }
public sealed record WuaInstallationStartResult(Guid RequestId, WuaInstallationStartOutcome Outcome,
    bool? BeginAttempted, int? BeginHResult, bool OwnershipReleased, bool StopRequested,
    WuaInstallationMonitorResult? Monitor, string Message)
{
    public CheckState InstalledState => CheckState.Unknown;
}

/// <summary>
/// Cooperating in-process start owner with a private monitor domain. Keep this domain process-rooted;
/// a new instance is not recovery, cross-process exclusion or permission to retry an uncertain job.
/// </summary>
public sealed class WuaInstallationStartDomain
{
    private readonly object sync = new();
    private readonly WuaInstallationMonitorDomain monitorDomain = new();
    private WuaInstallationStartOwner? owner;
    public bool HasPendingWork { get { lock (sync) return owner?.HasPendingWork == true; } }
    public bool RequiresRetention { get { lock (sync) return owner?.RequiresRetention == true; } }
    internal bool Enter(WuaInstallationStartOwner candidate)
    { lock (sync) { if (owner != null) return false; owner = candidate; return true; } }
    internal void Complete(WuaInstallationStartOwner actual, bool releasedOrNeverEntered)
    { lock (sync) { if (ReferenceEquals(owner, actual) && releasedOrNeverEntered) owner = null; } }
    internal Task<WuaInstallationMonitorResult> MonitorAsync(WuaInstallationStartSession session, CancellationToken token) =>
        new WuaInstallationMonitor(monitorDomain).ObserveStartedAsync(session.Job, session, token);
}

/// <summary>Portable one-shot BeginInstall handoff only; there is no native installer or App entry point.</summary>
public sealed class WuaInstallationStart(WuaInstallationStartDomain domain, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    public static WuaInstallationStartRequest? CreateRequest(WuaInstallationTarget? target, long generation, DateTimeOffset nowUtc) =>
        target is { UpdateId: var id, Revision: >= 0 } && id != Guid.Empty && generation >= 0 && Utc(nowUtc) && nowUtc <= DateTimeOffset.MaxValue.AddMinutes(-2)
            ? new(target, generation, nowUtc) : null;
    internal static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    public Task<WuaInstallationStartResult> StartAsync(WuaInstallationStartRequest request, WuaInstallationStartSession session,
        long currentGeneration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(domain); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.Request, request) || session.Job.Targets.Count != 1 || session.Job.Targets[0] != request.Target)
            return Task.FromResult(Refused(request, token));
        var owner = new WuaInstallationStartOwner(domain, session, currentGeneration, clock, token);
        if (!domain.Enter(owner)) return Task.FromResult(Refused(request, token));
        try
        {
            if (!request.Reserve(session, currentGeneration, clock.GetUtcNow(), token) || !session.Job.TryReserveStart(session))
            { domain.Complete(owner, true); return Task.FromResult(Refused(request, token)); }
        }
        catch
        {
            // Reservation/scheduling uncertainty cannot free an actual precreated graph.
            owner.Retain(); owner.RefuseUncertain(); return owner.Completion.Task;
        }
        owner.Start(); return owner.Completion.Task;
    }
    internal static WuaInstallationStartResult Refused(WuaInstallationStartRequest request, CancellationToken token) => new(request.RequestId,
        WuaInstallationStartOutcome.RefusedBeforeEntry, false, null, false, token.IsCancellationRequested, null,
        "This call did not invoke BeginInstall. The original adapter still owns any unentered preparation or prior job; no native resource release or installation outcome is established.");
}

internal sealed class WuaInstallationStartOwner(WuaInstallationStartDomain domain, WuaInstallationStartSession session,
    long generation, TimeProvider clock, CancellationToken token)
{
    private readonly object sync = new();
    private readonly List<Task> tasks = [];
    private bool retained;
    internal readonly TaskCompletionSource<WuaInstallationStartResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HasPendingWork { get { lock (sync) return !Completion.Task.IsCompleted || tasks.Any(task => !task.IsCompleted); } }
    internal bool RequiresRetention { get { lock (sync) return retained; } }
    internal void Retain() { lock (sync) retained = true; }
    internal void RefuseUncertain() => Completion.TrySetResult(Uncertain(null));
    internal void Start()
    {
        try
        {
            var cold = new Task<Task<WuaInstallationStartResult>>(RunAsync, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
            var actual = cold.Unwrap(); lock (sync) tasks.Add(actual);
            var forwarding = actual.ContinueWith(done =>
            {
                if (!done.IsCompletedSuccessfully) { Retain(); Completion.TrySetResult(Uncertain(null)); return; }
                var result = done.Result;
                if (RequiresRetention && result.OwnershipReleased)
                    result = result with { OwnershipReleased = false, Outcome = WuaInstallationStartOutcome.MonitoringUncertain,
                        Message = "Start task ownership became uncertain. The exact precreated owner remains retained without retry, cleanup or an installed-state claim." };
                domain.Complete(this, !RequiresRetention && (result.OwnershipReleased || result.Outcome == WuaInstallationStartOutcome.RefusedBeforeEntry));
                Completion.TrySetResult(result);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            lock (sync) tasks.Add(forwarding);
            cold.Start(TaskScheduler.Default);
        }
        catch { Retain(); Completion.TrySetResult(Uncertain(null)); }
    }
    private async Task<WuaInstallationStartResult> RunAsync()
    {
        bool attempted = false;
        int? hresult = null;
        try
        {
            if (RequiresRetention) return Uncertain(null);
            var entry = await Schedule(async () =>
            {
                if (RequiresRetention) return new BeginEntry(null, null);
                if (!session.Request.EnterBegin(session, generation, clock.GetUtcNow(), token)) return new BeginEntry(false, null);
                // This is inside the already-rooted cold worker. No await or scheduling
                // separates the final reservation/stop check from the collaborator call.
                attempted = true;
                var actual = session.BeginOwnedAsync();
                return new BeginEntry(true, await actual.ConfigureAwait(false));
            }).ConfigureAwait(false);
            if (entry.Entered == false) return WuaInstallationStart.Refused(session.Request, token);
            if (entry.Entered != true || RequiresRetention) { Retain(); return Uncertain(entry.Entered); }
            var started = entry.Packet;
            if (started == null || !ReferenceEquals(started.Owner, session) || !ReferenceEquals(started.Job, session.Job) ||
                !ReferenceEquals(started.Context, session.Job.Context))
            { Retain(); return Uncertain(true); } // Never attribute a foreign packet's raw HRESULT.
            hresult = started.HResult;
            if (hresult != 0) { Retain(); return Uncertain(true, hresult); }
            // Even an inline completion only queues this handoff; monitor adoption and
            // later End/CleanUp work never execute on that native callback stack.
            var monitored = await Schedule(async () =>
            {
                if (RequiresRetention) return null;
                return await domain.MonitorAsync(session, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (monitored == null) { Retain(); return Uncertain(true, hresult); }
            if (!monitored.Adopted || !monitored.OwnershipReleased) Retain();
            return new(session.Request.RequestId, RequiresRetention ? WuaInstallationStartOutcome.MonitoringUncertain : WuaInstallationStartOutcome.Monitored,
                true, hresult, monitored.OwnershipReleased && !RequiresRetention, token.IsCancellationRequested, monitored,
                "BeginInstall returned the exact owned job with S_OK. This means asynchronous work started, not installation success. Actual monitor results and ownership remain separate; installed state is Unknown.");
        }
        catch { Retain(); return Uncertain(attempted ? true : null, hresult); }
    }
    private sealed record BeginEntry(bool? Entered, WuaInstallationBeginResult? Packet);
    private Task<T> Schedule<T>(Func<Task<T>> callback)
    {
        try
        {
            var cold = new Task<Task<T>>(callback, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
            var actual = cold.Unwrap(); lock (sync) tasks.Add(actual);
            cold.Start(TaskScheduler.Default); return actual;
        }
        catch { Retain(); throw; }
    }
    private WuaInstallationStartResult Uncertain(bool? attempted, int? hresult = null) => new(session.Request.RequestId,
        WuaInstallationStartOutcome.BeginUncertain, attempted, hresult, false, token.IsCancellationRequested, null,
        "Begin or job ownership was not established. Failed HRESULT, missing/foreign output or a thrown call does not prove no operation occurred. The actual precreated owner remains retained; no retry, EndInstall, cleanup, abort or installed-state claim is supplied.");
}
