namespace MadClean.Core;

public enum SystemRepairDirectPipe { StandardInput, StandardOutput, StandardError }
public enum SystemRepairDirectHandleRole
{
    StdinChildRead, StdinParentWrite, StdoutParentRead, StdoutChildWrite,
    StderrParentRead, StderrChildWrite, Process, PrimaryThread
}
public enum SystemRepairDirectAllocationRole { AttributeList, HandleListValues, CreateCallState }
public enum SystemRepairDirectSizeState { RequiredSize, Failed, Uncertain }
public enum SystemRepairDirectCreationState { NotAttempted, KnownNotCreated, Created, Uncertain }
public enum SystemRepairDirectStage { Pipes, Attributes, Drains, Create, Wait, Cleanup, Completed }

// These tokens have no public path, command, numeric native flag or raw handle.
// The trusted native adapter must validate its own concrete token types, aliases,
// pseudo handles and the actual held executable identity.
public abstract class SystemRepairDirectLaunchTarget
{
    protected SystemRepairDirectLaunchTarget(SystemRepairExecutionContext context) => Context = context ?? throw new ArgumentNullException(nameof(context));
    public SystemRepairExecutionContext Context { get; }
}
public abstract class SystemRepairDirectHandle
{
    protected SystemRepairDirectHandle(SystemRepairExecutionContext context, SystemRepairDirectHandleRole role)
    { Context = context ?? throw new ArgumentNullException(nameof(context)); Role = role; }
    public SystemRepairExecutionContext Context { get; }
    public SystemRepairDirectHandleRole Role { get; }
}
public abstract class SystemRepairDirectAllocation
{
    protected SystemRepairDirectAllocation(SystemRepairExecutionContext context, SystemRepairDirectAllocationRole role, nuint byteCount)
    { Context = context ?? throw new ArgumentNullException(nameof(context)); Role = role; ByteCount = byteCount; }
    public SystemRepairExecutionContext Context { get; }
    public SystemRepairDirectAllocationRole Role { get; }
    public nuint ByteCount { get; }
}
public sealed record SystemRepairDirectOperationResult(bool Succeeded, int? NativeError = null);
public sealed record SystemRepairDirectPipeResult(bool Succeeded, SystemRepairDirectHandle? Read,
    SystemRepairDirectHandle? Write, int? NativeError = null);
public sealed record SystemRepairDirectSizeResult(SystemRepairDirectSizeState State, nuint ByteCount, int? NativeError = null);
public sealed record SystemRepairDirectCreateResult(bool Succeeded, SystemRepairDirectHandle? Process,
    SystemRepairDirectHandle? PrimaryThread, int? ProcessId, int? NativeError = null);
public sealed record SystemRepairDirectDrainResult(bool EndOfStreamObserved, bool WorkerReleased, int? NativeError = null);
public sealed record SystemRepairDirectWaitResult(bool ExitObserved, int? ExitCode, int? NativeError = null);
public abstract class SystemRepairDirectDrain
{
    protected SystemRepairDirectDrain(SystemRepairExecutionContext context, SystemRepairDirectHandle parentRead,
        bool ready, Task<SystemRepairDirectDrainResult> completion)
    { Context = context; ParentRead = parentRead; Ready = ready; Completion = completion; }
    public SystemRepairExecutionContext Context { get; }
    public SystemRepairDirectHandle ParentRead { get; }
    public bool Ready { get; }
    public Task<SystemRepairDirectDrainResult> Completion { get; }
}

/// <summary>Trusted fixed CreateProcess/pipe owner boundary. No production implementation is enabled by this interface.</summary>
public interface ISystemRepairDirectLaunchApi
{
    SystemRepairDirectPipeResult CreatePipe(SystemRepairExecutionContext context, SystemRepairDirectPipe pipe);
    SystemRepairDirectOperationResult MakeParentNonInheritable(SystemRepairDirectHandle handle);
    SystemRepairDirectOperationResult MakeChildInheritable(SystemRepairDirectHandle handle);
    SystemRepairDirectSizeResult QueryAttributeSize(SystemRepairExecutionContext context);
    SystemRepairDirectAllocation? AllocateAttributes(SystemRepairExecutionContext context, nuint byteCount);
    SystemRepairDirectAllocation? AllocateHandleValues(SystemRepairExecutionContext context, IReadOnlyList<SystemRepairDirectHandle> childHandles);
    SystemRepairDirectOperationResult InitializeAttributes(SystemRepairDirectAllocation attributes);
    SystemRepairDirectOperationResult UpdateHandleList(SystemRepairDirectAllocation attributes,
        SystemRepairDirectAllocation values, IReadOnlyList<SystemRepairDirectHandle> childHandles);
    SystemRepairDirectAllocation? AllocateCreateState(SystemRepairExecutionContext context, SystemRepairDirectLaunchTarget target);
    SystemRepairDirectDrain StartDiscardDrain(SystemRepairDirectHandle parentRead);
    SystemRepairDirectCreateResult CreateProcess(SystemRepairDirectLaunchTarget target,
        SystemRepairDirectAllocation attributes, SystemRepairDirectAllocation createState, IReadOnlyList<SystemRepairDirectHandle> childHandles);
    Task<SystemRepairDirectWaitResult> WaitForExitAsync(SystemRepairDirectHandle process);
    SystemRepairDirectOperationResult Close(SystemRepairDirectHandle handle);
    void DeleteAttributes(SystemRepairDirectAllocation attributes);
    SystemRepairDirectOperationResult Free(SystemRepairDirectAllocation allocation);
}

public sealed record SystemRepairDirectCreation(SystemRepairExecutionContext Context, SystemRepairDirectCreationState State,
    bool CreateAttempted, int? ProcessId, DateTimeOffset ObservedUtc, int? NativeError);
public sealed record SystemRepairDirectLaunchResult(SystemRepairExecutionContext Context,
    SystemRepairDirectCreationState CreationState, SystemRepairDirectStage Stage, bool CreateAttempted, int? ProcessId,
    bool? ExitObserved, int? ExitCode, DateTimeOffset? ExitObservedUtc,
    SystemRepairDirectDrainResult? Stdout, SystemRepairDirectDrainResult? Stderr,
    bool CleanupConfirmed, bool RequiresRetention, int? NativeError, string Message)
{
    public CheckState RepairSucceeded => CheckState.Unknown;
    public CheckState Integrity => CheckState.Unknown;
}

/// <summary>Caller must keep this cooperating domain process-rooted. It grants no native permission or cross-process exclusion.</summary>
public sealed class SystemRepairDirectLaunchDomain
{
    private readonly object sync = new();
    private readonly HashSet<SystemRepairDispatchPermit> attempts = new(ReferenceEqualityComparer.Instance);
    private SystemRepairDirectOwner? owner;
    public bool HasPendingWork { get { lock (sync) return owner?.HasPendingWork == true; } }
    public bool RequiresRetention { get { lock (sync) return owner?.RequiresRetention == true; } }
    internal bool Enter(SystemRepairDirectOwner candidate, SystemRepairDispatchPermit permit)
    {
        lock (sync)
        {
            // Bounded lifetime admission history, never an unbounded retry cache.
            if (owner != null || attempts.Count >= 128 || !attempts.Add(permit)) return false;
            owner = candidate; return true;
        }
    }
    internal void Complete(SystemRepairDirectOwner candidate, bool released)
    { lock (sync) { if (ReferenceEquals(owner, candidate) && released) owner = null; } }
}

public sealed class SystemRepairDirectLaunchSession
{
    private readonly SystemRepairDirectOwner owner;
    internal SystemRepairDirectLaunchSession(SystemRepairDirectOwner owner) => this.owner = owner;
    public SystemRepairExecutionContext Context => owner.Context;
    internal bool WasAdmittedFor(SystemRepairDispatchPermit permit) => owner.WasAdmittedFor(permit);
    internal bool StartedStdout => owner.StartedStdout;
    internal bool StartedStderr => owner.StartedStderr;
    public Task<SystemRepairDirectCreation> Creation => owner.Creation.Task;
    public Task<SystemRepairDirectLaunchResult> Completion => owner.Completion.Task;
    public bool HasPendingWork => owner.HasPendingWork;
    public bool RequiresRetention => owner.RequiresRetention;
    public async Task<bool> ReleaseOwnershipAsync() => (await Completion.ConfigureAwait(false)).CleanupConfirmed &&
        !RequiresRetention && !HasPendingWork;
}

/// <summary>Fixed lower-level ownership kernel. No platform gate, arbitrary executable, Process.Start fallback or cancellation API.</summary>
public sealed class SystemRepairDirectLaunch(ISystemRepairDirectLaunchApi api, SystemRepairDirectLaunchDomain domain)
{
    public SystemRepairDirectLaunchSession Start(SystemRepairDispatchPermit permit, SystemRepairDirectLaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(api); ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(permit); ArgumentNullException.ThrowIfNull(target);
        var owner = new SystemRepairDirectOwner(api, domain, permit, target);
        var session = new SystemRepairDirectLaunchSession(owner);
        if (!ReferenceEquals(permit.Context, target.Context) || !permit.Context.Request.DispatchCommitted || permit.WasClaimed ||
            permit.Context.Operation is not (SystemToolOperation.DismRestoreHealth or SystemToolOperation.SfcScanNow) ||
            !domain.Enter(owner, permit)) owner.Refuse();
        else { owner.MarkAdmitted(); owner.Schedule(); }
        return session;
    }
}

internal sealed class SystemRepairDirectOwner(ISystemRepairDirectLaunchApi api, SystemRepairDirectLaunchDomain domain,
    SystemRepairDispatchPermit permit, SystemRepairDirectLaunchTarget target)
{
    private readonly object sync = new();
    private readonly List<SystemRepairDirectHandle> handles = [];
    private readonly HashSet<SystemRepairDirectHandle> closeAttempted = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<SystemRepairDirectHandle> closed = new(ReferenceEqualityComparer.Instance);
    private readonly List<SystemRepairDirectAllocation> allocations = [];
    private readonly HashSet<SystemRepairDirectAllocation> freeAttempted = new(ReferenceEqualityComparer.Instance);
    private SystemRepairDirectAllocation? attributes, values, createState;
    private IReadOnlyList<SystemRepairDirectHandle>? childHandles;
    private SystemRepairDirectHandle? process, primaryThread;
    private SystemRepairDirectDrain? stdout, stderr;
    private Task<SystemRepairDirectWaitResult>? waitTask;
    private Task<SystemRepairDirectLaunchResult>? workTask;
    private Task? finishing;
    private bool initialized, attempted, retained, exitObserved, cleanup;
    private int? pid, exitCode, error;
    private DateTimeOffset? exitUtc;
    private SystemRepairDirectCreationState creation = SystemRepairDirectCreationState.NotAttempted;
    private SystemRepairDirectStage stage = SystemRepairDirectStage.Pipes;
    private SystemRepairDirectDrainResult? stdoutResult, stderrResult;
    internal SystemRepairExecutionContext Context { get; } = permit.Context;
    internal readonly TaskCompletionSource<SystemRepairDirectCreation> Creation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<SystemRepairDirectLaunchResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool StartedStdout { get { lock (sync) return stdout != null; } }
    internal bool StartedStderr { get { lock (sync) return stderr != null; } }
    private bool admitted;
    internal void MarkAdmitted() { lock (sync) admitted = true; }
    internal bool WasAdmittedFor(SystemRepairDispatchPermit actual) { lock (sync) return admitted && ReferenceEquals(permit, actual); }
    internal bool RequiresRetention { get { lock (sync) return retained; } }
    internal bool HasPendingWork
    {
        get
        {
            lock (sync) return workTask is { IsCompleted: false } || waitTask is { IsCompleted: false } ||
                stdout?.Completion is { IsCompleted: false } || stderr?.Completion is { IsCompleted: false };
        }
    }
    internal void Refuse()
    {
        Creation.TrySetResult(new(Context, creation, false, null, DateTimeOffset.UtcNow, null));
        cleanup = true;
        Completion.TrySetResult(Result("This exact committed launch was not admitted; no native callback was made."));
    }
    internal void Schedule()
    {
        try
        {
            var scheduled = new Task<Task<SystemRepairDirectLaunchResult>>(RunOwnedAsync, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
            lock (sync) workTask = scheduled.Unwrap();
            finishing = workTask.ContinueWith(done =>
            {
                SystemRepairDirectLaunchResult result;
                if (done.IsCompletedSuccessfully) result = done.Result;
                else { Latch(); result = Result("The native launch owner did not establish a complete result; resources remain retained."); }
                if (RequiresRetention && !result.RequiresRetention) result = result with { CleanupConfirmed = false, RequiresRetention = true };
                Creation.TrySetResult(new(Context, creation, attempted, pid, DateTimeOffset.UtcNow, error));
                domain.Complete(this, result.CleanupConfirmed);
                Completion.TrySetResult(result);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            scheduled.Start(TaskScheduler.Default);
        }
        catch
        {
            Latch(); creation = SystemRepairDirectCreationState.Uncertain;
            Creation.TrySetResult(new(Context, creation, false, null, DateTimeOffset.UtcNow, null));
            Completion.TrySetResult(Result("Launch scheduling is uncertain; the exact owner remains retained."));
        }
    }

    private async Task<SystemRepairDirectLaunchResult> RunOwnedAsync()
    {
        try
        {
            if (!PreparePipes()) return await CleanKnownNoProcess().ConfigureAwait(false);
            childHandles = Array.AsReadOnly(new[] { Handle(SystemRepairDirectHandleRole.StdinChildRead),
                Handle(SystemRepairDirectHandleRole.StdoutChildWrite), Handle(SystemRepairDirectHandleRole.StderrChildWrite) });
            if (!PrepareAttributes() || !PrepareCreateState()) return await CleanKnownNoProcess().ConfigureAwait(false);
            stage = SystemRepairDirectStage.Drains;
            var outputReader = AdoptDrain(api.StartDiscardDrain(Handle(SystemRepairDirectHandleRole.StdoutParentRead)), SystemRepairDirectHandleRole.StdoutParentRead);
            lock (sync) stdout = outputReader;
            var errorReader = AdoptDrain(api.StartDiscardDrain(Handle(SystemRepairDirectHandleRole.StderrParentRead)), SystemRepairDirectHandleRole.StderrParentRead);
            lock (sync) stderr = errorReader;
            if (!stdout.Ready || !stderr.Ready) return await CleanKnownNoProcess().ConfigureAwait(false);
            // No input is ever written or inherited from a host console.
            if (!Close(Handle(SystemRepairDirectHandleRole.StdinParentWrite))) return RetainedResult();
            stage = SystemRepairDirectStage.Create;
            var actualApi = api;
            var actualTarget = target;
            var actualAttributes = attributes!;
            var actualCreateState = createState!;
            var actualChildren = ChildHandles();
            // All child writers are still owned here. A terminal reader before
            // creation is an unexpected readiness loss, not permission to launch
            // an output-producing client without active drains.
            if (stdout.Completion.IsCompleted || stderr.Completion.IsCompleted)
            {
                if (stdout.Completion.IsCompletedSuccessfully) stdoutResult = stdout.Completion.Result;
                if (stderr.Completion.IsCompletedSuccessfully) stderrResult = stderr.Completion.Result;
                return RetainedResult();
            }
            if (!permit.TryClaim(Context)) return await CleanKnownNoProcess().ConfigureAwait(false);
            attempted = true;
            var created = actualApi.CreateProcess(actualTarget, actualAttributes, actualCreateState, actualChildren);
            if (!created.Succeeded)
            {
                // Failed PROCESS_INFORMATION values are not ownership evidence.
                error = created.NativeError; creation = SystemRepairDirectCreationState.KnownNotCreated;
                Creation.TrySetResult(new(Context, creation, true, null, DateTimeOffset.UtcNow, error));
                return await CleanKnownNoProcess().ConfigureAwait(false);
            }
            creation = SystemRepairDirectCreationState.Created;
            process = Adopt(created.Process, SystemRepairDirectHandleRole.Process);
            primaryThread = Adopt(created.PrimaryThread, SystemRepairDirectHandleRole.PrimaryThread);
            pid = created.ProcessId is > 0 ? created.ProcessId : null;
            Creation.TrySetResult(new(Context, creation, true, pid, DateTimeOffset.UtcNow, null));
            stage = SystemRepairDirectStage.Wait;
            var waiting = api.WaitForExitAsync(process);
            lock (sync) waitTask = waiting;
            bool childCopiesClosed = CloseChildCopies();
            Close(primaryThread);
            if (!childCopiesClosed) return RetainedResult();
            var observed = await waiting.ConfigureAwait(false);
            error ??= observed.NativeError;
            if (!observed.ExitObserved) return RetainedResult();
            exitObserved = true; exitCode = observed.ExitCode; exitUtc = DateTimeOffset.UtcNow;
            if (!await DrainWorkers().ConfigureAwait(false)) return RetainedResult();
            CleanupAfterWorkers();
            return Result("The created client was observed separately from its literal exit code and cleanup. Repair and integrity remain Unknown.");
        }
        catch
        {
            if (attempted && creation == SystemRepairDirectCreationState.NotAttempted) creation = SystemRepairDirectCreationState.Uncertain;
            Latch();
            Creation.TrySetResult(new(Context, creation, attempted, pid, DateTimeOffset.UtcNow, error));
            return Result("A native call or ownership transition was uncertain. It is never retried; no process cancellation or repair success is inferred.");
        }
    }

    private bool PreparePipes()
    {
        foreach (var channel in Enum.GetValues<SystemRepairDirectPipe>())
        {
            var made = api.CreatePipe(Context, channel);
            if (!made.Succeeded) { error = made.NativeError; return false; } // Outputs indeterminate: never adopt or close.
            var read = Adopt(made.Read, channel switch
            {
                SystemRepairDirectPipe.StandardInput => SystemRepairDirectHandleRole.StdinChildRead,
                SystemRepairDirectPipe.StandardOutput => SystemRepairDirectHandleRole.StdoutParentRead,
                _ => SystemRepairDirectHandleRole.StderrParentRead
            });
            var write = Adopt(made.Write, channel switch
            {
                SystemRepairDirectPipe.StandardInput => SystemRepairDirectHandleRole.StdinParentWrite,
                SystemRepairDirectPipe.StandardOutput => SystemRepairDirectHandleRole.StdoutChildWrite,
                _ => SystemRepairDirectHandleRole.StderrChildWrite
            });
            var parent = channel == SystemRepairDirectPipe.StandardInput ? write : read;
            var child = channel == SystemRepairDirectPipe.StandardInput ? read : write;
            if (!Okay(api.MakeParentNonInheritable(parent)) || !Okay(api.MakeChildInheritable(child))) return false;
        }
        return true;
    }
    private bool PrepareAttributes()
    {
        stage = SystemRepairDirectStage.Attributes;
        var size = api.QueryAttributeSize(Context);
        if (size.State == SystemRepairDirectSizeState.Uncertain) throw new InvalidOperationException();
        if (size.State != SystemRepairDirectSizeState.RequiredSize || size.ByteCount is 0 or > 65_536)
        { error = size.NativeError; return false; } // MadClean bound, not an ABI size.
        var allocated = api.AllocateAttributes(Context, size.ByteCount);
        if (allocated == null) return false;
        attributes = Adopt(allocated, SystemRepairDirectAllocationRole.AttributeList);
        if (attributes.ByteCount < size.ByteCount) throw new InvalidOperationException();
        if (!Okay(api.InitializeAttributes(attributes))) return false;
        initialized = true;
        var allocatedValues = api.AllocateHandleValues(Context, ChildHandles());
        if (allocatedValues == null) return false;
        values = Adopt(allocatedValues, SystemRepairDirectAllocationRole.HandleListValues);
        return Okay(api.UpdateHandleList(attributes, values, ChildHandles()));
    }
    private async Task<SystemRepairDirectLaunchResult> CleanKnownNoProcess()
    {
        Creation.TrySetResult(new(Context, creation, attempted, null, DateTimeOffset.UtcNow, error));
        if (!CloseChildCopies()) return RetainedResult();
        var inputParent = handles.SingleOrDefault(handle => handle.Role == SystemRepairDirectHandleRole.StdinParentWrite);
        if (inputParent != null && !closeAttempted.Contains(inputParent) && !Close(inputParent)) return RetainedResult();
        if (!await DrainWorkers().ConfigureAwait(false)) return RetainedResult();
        CleanupAfterWorkers();
        return Result("No successful process creation was observed for this attempt. Only definitely owned setup resources were cleaned; failed native outputs were ignored.");
    }
    private bool PrepareCreateState()
    {
        var state = api.AllocateCreateState(Context, target);
        if (state == null) return false;
        createState = Adopt(state, SystemRepairDirectAllocationRole.CreateCallState);
        return true;
    }
    private async Task<bool> DrainWorkers()
    {
        bool released = true;
        if (stdout != null)
        {
            stdoutResult = await stdout.Completion.ConfigureAwait(false);
            released &= stdoutResult.WorkerReleased && stdoutResult.EndOfStreamObserved;
            if (!stdoutResult.EndOfStreamObserved) Latch();
        }
        if (stderr != null)
        {
            stderrResult = await stderr.Completion.ConfigureAwait(false);
            released &= stderrResult.WorkerReleased && stderrResult.EndOfStreamObserved;
            if (!stderrResult.EndOfStreamObserved) Latch();
        }
        if (!released) Latch();
        return released;
    }
    private void CleanupAfterWorkers()
    {
        stage = SystemRepairDirectStage.Cleanup;
        foreach (var handle in handles)
            if (!closeAttempted.Contains(handle)) Close(handle);
        if (initialized)
        {
            api.DeleteAttributes(attributes!); // Void deletion is not allocation freeing.
            initialized = false;
        }
        foreach (var allocation in allocations.AsEnumerable().Reverse())
        {
            if (!freeAttempted.Add(allocation)) continue;
            if (!Okay(api.Free(allocation))) Latch();
        }
        cleanup = !retained && handles.All(closed.Contains) && allocations.All(freeAttempted.Contains);
        if (cleanup) stage = SystemRepairDirectStage.Completed;
    }
    private bool CloseChildCopies()
    {
        bool okay = true;
        foreach (var handle in handles.Where(handle => handle.Role is SystemRepairDirectHandleRole.StdinChildRead or
            SystemRepairDirectHandleRole.StdoutChildWrite or SystemRepairDirectHandleRole.StderrChildWrite))
            if (!closeAttempted.Contains(handle)) okay &= Close(handle);
        return okay;
    }
    private bool Close(SystemRepairDirectHandle handle)
    {
        if (!closeAttempted.Add(handle)) return closed.Contains(handle);
        if (Okay(api.Close(handle))) { closed.Add(handle); return true; }
        Latch(); return false;
    }
    private bool Okay(SystemRepairDirectOperationResult result)
    { if (result.Succeeded) return true; error ??= result.NativeError; return false; }
    private SystemRepairDirectHandle Handle(SystemRepairDirectHandleRole role) => handles.Single(handle => handle.Role == role);
    private IReadOnlyList<SystemRepairDirectHandle> ChildHandles() => childHandles ?? throw new InvalidOperationException();
    private SystemRepairDirectHandle Adopt(SystemRepairDirectHandle? handle, SystemRepairDirectHandleRole role)
    {
        if (handle == null || !ReferenceEquals(handle.Context, Context) || handle.Role != role ||
            handles.Any(actual => ReferenceEquals(actual, handle))) throw new InvalidOperationException();
        handles.Add(handle); return handle;
    }
    private SystemRepairDirectAllocation Adopt(SystemRepairDirectAllocation allocation, SystemRepairDirectAllocationRole role)
    {
        if (!ReferenceEquals(allocation.Context, Context) || allocation.Role != role || allocation.ByteCount is 0 or > 65_536 ||
            allocations.Any(actual => ReferenceEquals(actual, allocation))) throw new InvalidOperationException();
        allocations.Add(allocation); return allocation;
    }
    private SystemRepairDirectDrain AdoptDrain(SystemRepairDirectDrain drain, SystemRepairDirectHandleRole role)
    {
        if (drain == null || !ReferenceEquals(drain.Context, Context) || !ReferenceEquals(drain.ParentRead, Handle(role)) || drain.Completion == null)
            throw new InvalidOperationException();
        return drain;
    }
    private void Latch() { lock (sync) retained = true; }
    private SystemRepairDirectLaunchResult RetainedResult() { Latch(); return Result("Actual resource or worker settlement is unconfirmed; the exact owner and pending tasks remain retained without retry."); }
    private SystemRepairDirectLaunchResult Result(string message) => new(Context, creation, stage, attempted, pid,
        creation == SystemRepairDirectCreationState.Created ? exitObserved : null, exitCode, exitUtc,
        stdoutResult, stderrResult, cleanup, retained, error, message);
}
