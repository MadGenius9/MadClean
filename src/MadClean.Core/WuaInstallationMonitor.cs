namespace MadClean.Core;

public sealed record WuaInstallationTarget(Guid UpdateId, int Revision);
public enum WuaInstallationResultCode { Unknown = -1, NotStarted = 0, InProgress = 1, Succeeded = 2, SucceededWithErrors = 3, Failed = 4, Aborted = 5 }
public sealed class WuaInstallationJobContext
{
    internal WuaInstallationJobContext() => Id = Guid.NewGuid();
    public Guid Id { get; }
}
public sealed class WuaInstallationCompletion
{
    internal WuaInstallationCompletion(WuaInstallationJobSession session) { Session = session; Context = session.Context; }
    internal WuaInstallationJobSession Session { get; }
    public WuaInstallationJobContext Context { get; }
}
public abstract class WuaInstallationResultPacket
{
    private protected WuaInstallationResultPacket(WuaInstallationJobSession session, int? code, int? hresult, bool? reboot)
    { Session = session; Context = session.Context; RawResultCode = code; HResult = hresult; RebootRequired = reboot; }
    internal WuaInstallationJobSession Session { get; }
    public WuaInstallationJobContext Context { get; }
    public int? RawResultCode { get; }
    public int? HResult { get; }
    public bool? RebootRequired { get; }
    public WuaInstallationResultCode ResultCode => RawResultCode is >= 0 and <= 5 ? (WuaInstallationResultCode)RawResultCode.Value : WuaInstallationResultCode.Unknown;
}
public sealed class WuaInstallationAggregate : WuaInstallationResultPacket
{
    internal WuaInstallationAggregate(WuaInstallationJobSession session, int? code, int? hresult, bool? reboot) : base(session, code, hresult, reboot) { }
}
public sealed class WuaInstallationItemResult : WuaInstallationResultPacket
{
    internal WuaInstallationItemResult(WuaInstallationJobSession session, int index, int? code, int? hresult, bool? reboot) : base(session, code, hresult, reboot) => Index = index;
    public int Index { get; }
}

/// <summary>
/// Must be constructed before a future native BeginInstall call. The future adapter independently
/// roots the actual job/callback owner before Begin and retains it if monitor adoption is refused.
/// This abstraction starts no operation and exposes no abort or installation permission.
/// </summary>
public abstract class WuaInstallationJobSession
{
    private readonly object lifecycle = new();
    private bool adopted;
    private WuaInstallationStartSession? startOwner;
    protected WuaInstallationJobSession(IReadOnlyList<WuaInstallationTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        int count = targets.Count;
        if (count is < 1 or > 128) throw new ArgumentException("Select one to 128 distinct update identities.", nameof(targets));
        var copy = new WuaInstallationTarget[count]; var ids = new HashSet<Guid>();
        for (int i = 0; i < count; i++)
        {
            var item = targets[i];
            if (item == null || item.UpdateId == Guid.Empty || item.Revision < 0 || !ids.Add(item.UpdateId))
                throw new ArgumentException("Update identities must be nonempty and unique, with nonnegative revisions.", nameof(targets));
            copy[i] = item with { };
        }
        if (targets.Count != count) throw new ArgumentException("Target metadata changed during capture.", nameof(targets));
        Targets = Array.AsReadOnly(copy); Context = new();
    }
    public WuaInstallationJobContext Context { get; }
    public IReadOnlyList<WuaInstallationTarget> Targets { get; }
    protected WuaInstallationCompletion CreateCompletion() => new(this);
    protected WuaInstallationAggregate CreateAggregate(int? resultCode, int? hResult, bool? rebootRequired) => new(this, resultCode, hResult, rebootRequired);
    protected WuaInstallationItemResult CreateItemResult(int index, int? resultCode, int? hResult, bool? rebootRequired)
    {
        if (index < 0 || index >= Targets.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new(this, index, resultCode, hResult, rebootRequired);
    }
    protected abstract Task<WuaInstallationCompletion> ObserveCompletionAsync();
    protected abstract Task<WuaInstallationAggregate> EndInstallAsync();
    protected abstract Task<WuaInstallationItemResult> ReadUpdateResultAsync(int index);
    protected abstract Task CleanUpAsync();
    protected abstract Task<bool> ReleaseOwnershipAsync();
    // Both claims share this lock. A public monitor cannot steal a reserved
    // future start, and the same job cannot be begun through another wrapper.
    internal bool TryReserveStart(WuaInstallationStartSession owner)
    {
        lock (lifecycle)
        {
            if (!ReferenceEquals(owner.Job, this) || adopted || startOwner != null) return false;
            startOwner = owner; return true;
        }
    }
    internal bool TryAdopt(WuaInstallationStartSession? startProof = null)
    {
        lock (lifecycle)
        {
            if (adopted || !ReferenceEquals(startOwner, startProof) ||
                startProof != null && !ReferenceEquals(startProof.Job, this)) return false;
            adopted = true; return true;
        }
    }
    internal Task<WuaInstallationCompletion> ObserveOwnedAsync() => ObserveCompletionAsync();
    internal Task<WuaInstallationAggregate> EndOwnedAsync() => EndInstallAsync();
    internal Task<WuaInstallationItemResult> ReadOwnedAsync(int index) => ReadUpdateResultAsync(index);
    internal Task CleanupOwnedAsync() => CleanUpAsync();
    internal Task<bool> ReleaseOwnedAsync() => ReleaseOwnershipAsync();
}

public sealed record WuaInstallationItemObservation(int Index, WuaInstallationTarget Target, WuaInstallationItemResult? Raw)
{
    public WuaInstallationResultCode ResultCode => Raw?.ResultCode ?? WuaInstallationResultCode.Unknown;
    public CheckState InstalledState => CheckState.Unknown;
}
public sealed record WuaInstallationMonitorResult(bool Adopted, bool CompletionObserved, bool OwnershipReleased,
    bool StopRequested, WuaInstallationAggregate? Aggregate, IReadOnlyList<WuaInstallationItemObservation> Items, string Message)
{
    public CheckState InstalledState => CheckState.Unknown;
}

/// <summary>Caller must keep this managed cooperating owner domain process-rooted. It is not cross-process exclusion or native permission.</summary>
public sealed class WuaInstallationMonitorDomain
{
    private readonly object sync = new(); private WuaInstallationMonitorOwner? owner;
    public bool HasPendingWork { get { lock (sync) return owner?.HasPendingWork == true; } }
    public bool RequiresRetention { get { lock (sync) return owner?.RequiresRetention == true; } }
    internal bool Enter(WuaInstallationMonitorOwner candidate, WuaInstallationJobSession session, WuaInstallationStartSession? startProof)
    {
        lock (sync)
        {
            if (owner != null) return false;
            owner = candidate; // Root the actual owner before claiming the session.
            if (session.TryAdopt(startProof)) return true;
            owner = null; return false; // Known failed claim made no callback.
        }
    }
    internal void Complete(WuaInstallationMonitorOwner candidate, bool released)
    { lock (sync) { if (ReferenceEquals(owner, candidate) && released) owner = null; } }
}
public sealed class WuaInstallationMonitor(WuaInstallationMonitorDomain domain)
{
    public Task<WuaInstallationMonitorResult> ObserveAsync(WuaInstallationJobSession session, CancellationToken token = default) =>
        ObserveCore(session, null, token);
    internal Task<WuaInstallationMonitorResult> ObserveStartedAsync(WuaInstallationJobSession session,
        WuaInstallationStartSession startProof, CancellationToken token = default) => ObserveCore(session, startProof, token);
    private Task<WuaInstallationMonitorResult> ObserveCore(WuaInstallationJobSession session,
        WuaInstallationStartSession? startProof, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(domain); ArgumentNullException.ThrowIfNull(session);
        var owner = new WuaInstallationMonitorOwner(domain, session, token);
        if (!domain.Enter(owner, session, startProof)) return Task.FromResult(new WuaInstallationMonitorResult(false, false, false,
            token.IsCancellationRequested, null, Array.AsReadOnly(Array.Empty<WuaInstallationItemObservation>()),
            "Monitor adoption was refused. The original native owner remains responsible; no absence or release is established."));
        owner.Start(); return owner.Completion.Task;
    }
}
internal sealed class WuaInstallationMonitorOwner(WuaInstallationMonitorDomain domain, WuaInstallationJobSession session, CancellationToken token)
{
    private readonly object sync = new(); private readonly List<Task> tasks = [];
    private bool retained;
    private Task? forwarding;
    internal readonly TaskCompletionSource<WuaInstallationMonitorResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HasPendingWork { get { lock (sync) return !Completion.Task.IsCompleted || tasks.Any(task => !task.IsCompleted) || forwarding is { IsCompleted: false } && !Completion.Task.IsCompleted; } }
    internal bool RequiresRetention { get { lock (sync) return retained; } }
    internal void Start()
    {
        try
        {
            var cold = new Task<Task<WuaInstallationMonitorResult>>(RunAsync, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
            var actual = cold.Unwrap(); lock (sync) tasks.Add(actual);
            var forward = actual.ContinueWith(done =>
            {
                if (done.IsCompletedSuccessfully)
                {
                    var result = done.Result;
                    if (RequiresRetention && result.OwnershipReleased) result = result with { OwnershipReleased = false,
                        Message = "Monitor task ownership became uncertain; the exact job owner remains retained without retry." };
                    domain.Complete(this, result.OwnershipReleased && !RequiresRetention); Completion.TrySetResult(result);
                }
                else { Retain(); Completion.TrySetResult(Result(false, false, null, [])); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            lock (sync) forwarding = forward;
            cold.Start(TaskScheduler.Default);
        }
        catch { Retain(); Completion.TrySetResult(Result(false, false, null, [])); }
    }
    private async Task<WuaInstallationMonitorResult> RunAsync()
    {
        bool observed = false, released = false; WuaInstallationAggregate? aggregate = null;
        var items = session.Targets.Select((target, index) => new WuaInstallationItemObservation(index, target, null)).ToArray();
        try
        {
            var completion = await Schedule(session.ObserveOwnedAsync).ConfigureAwait(false);
            if (completion == null || !ReferenceEquals(completion.Session, session) || !ReferenceEquals(completion.Context, session.Context)) return Retained();
            observed = true;
            var ended = await Schedule(session.EndOwnedAsync).ConfigureAwait(false);
            if (!Bound(ended)) return Retained();
            aggregate = ended;
            for (int index = 0; index < items.Length; index++)
            {
                int selected = index;
                try
                {
                    var result = await Schedule(() => session.ReadOwnedAsync(selected)).ConfigureAwait(false);
                    if (Bound(result) && result.Index == selected) items[selected] = items[selected] with { Raw = result };
                }
                catch
                {
                    if (RequiresRetention) return Retained(); // Scheduling uncertainty may still own work.
                    // A completed failed getter affects this index only; continue every other selected index.
                }
            }
            await Schedule(async () => { await session.CleanupOwnedAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false);
            released = await Schedule(session.ReleaseOwnedAsync).ConfigureAwait(false);
            if (!released) Retain();
        }
        catch { Retain(); }
        return Result(observed, released, aggregate, items);
        bool Bound(WuaInstallationResultPacket? packet) => packet != null && ReferenceEquals(packet.Session, session) && ReferenceEquals(packet.Context, session.Context);
        WuaInstallationMonitorResult Retained() { Retain(); return Result(observed, false, aggregate, items); }
    }
    // Every call, including End/per-index/CleanUp/release, gets its own rooted Default task.
    // ConfigureAwait(false) alone would permit invocation inside synchronous native callbacks.
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

    private void Retain() { lock (sync) retained = true; }
    private WuaInstallationMonitorResult Result(bool observed, bool released, WuaInstallationAggregate? aggregate, IReadOnlyList<WuaInstallationItemObservation> items) =>
        new(true, observed, released, token.IsCancellationRequested, aggregate, Array.AsReadOnly(items.ToArray()), released
            ? "Actual job completion and cleanup were observed. Unavailable per-update properties, installed state and update eligibility remain unknown."
            : "Actual job/result/callback ownership is uncertain or pending and remains retained; no retry, abort, rollback or installed-state claim is supplied.");
}
