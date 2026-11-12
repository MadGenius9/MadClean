using System.Runtime.CompilerServices;
using System.Reflection;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemRepairServicingBackendTests
{
    private const long Generation = 4;
    private static SystemRepairRequest Request()
    {
        var request = Assert.IsType<SystemRepairRequest>(SystemRepairWorkflow.Prepare(
            SystemToolOperation.DismRestoreHealth, Generation, DateTimeOffset.UtcNow));
        Assert.Equal(SystemRepairConfirmation.Confirmed, request.Confirm(request.RequestId, request.Definition.Operation,
            Generation, true, true, true, true, DateTimeOffset.UtcNow));
        return request;
    }
    private static Task<SystemRepairWorkflowResult> Run(SystemRepairServicingBackend backend, CancellationToken token = default) =>
        new SystemRepairWorkflow(backend, new()).ExecuteAsync(Request(), Generation, token);

    [Fact]
    public async Task ClosedPlatformDoesNotTouchAdmissionOrPreparation()
    {
        var platform = new Platform { Enabled = false }; var gate = new SystemToolAdmissionGate();
        var backend = new SystemRepairServicingBackend(platform, gate);
        var result = await Run(backend);
        Assert.Equal(SystemRepairWorkflowOutcome.Blocked, result.Outcome);
        Assert.Equal(0, platform.PrepareCalls); Assert.False(gate.HasPendingWork);
        Assert.False(backend.HasPendingWork); Assert.False(backend.RequiresRetention);
    }

    [Fact]
    public async Task CancellationBetweenReservationAndBackendEntryDoesNotInventResourceUncertainty()
    {
        using var cancel = new CancellationTokenSource(); int reads = 0;
        var platform = new Platform { Gate = () => { if (++reads == 2) cancel.Cancel(); return true; } };
        var gate = new SystemToolAdmissionGate(); var own = new SystemRepairAdmission();
        var backend = new SystemRepairServicingBackend(platform, gate);
        var result = await new SystemRepairWorkflow(backend, own).ExecuteAsync(Request(), Generation, cancel.Token);
        Assert.Equal(SystemRepairWorkflowOutcome.CancelledBeforeDispatch, result.Outcome);
        Assert.True(result.OwnershipReleased); Assert.False(own.IsBlocked);
        Assert.Equal(0, platform.PrepareCalls); Assert.False(gate.HasPendingWork); Assert.False(backend.RequiresRetention);
    }

    [Fact]
    public async Task ExistingNonRepairOwnerRefusesRepairWithoutClaimingRepairResourcesOrPoisoningItsCaller()
    {
        var gate = new SystemToolAdmissionGate(); var platform = new Platform();
        Assert.Null(gate.TryAcquire(SystemToolOperation.DismRestoreHealth, Guid.NewGuid()));
        Assert.Null(gate.TryAcquire(SystemToolOperation.SfcScanNow, Guid.NewGuid()));
        var normal = Assert.IsType<SystemToolAdmissionLease>(gate.TryAcquire(SystemToolOperation.DismScanHealth, Guid.NewGuid()));
        var backend = new SystemRepairServicingBackend(platform, gate); var own = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, own).ExecuteAsync(Request(), Generation);
        Assert.False(result.DispatchAttempted); Assert.True(result.OwnershipReleased);
        Assert.False(own.IsBlocked); Assert.Equal(0, platform.PrepareCalls);
        Assert.True(gate.HasPendingWork); Assert.False(backend.HasPendingWork); Assert.False(backend.RequiresRetention);
        normal.Complete(true);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, (await Run(backend)).Outcome);
        Assert.Equal(1, platform.PrepareCalls);
    }

    [Fact]
    public async Task SharedLeaseCoversPreparationDispatchAndEveryCleanupBeforeNormalOwnersCanEnter()
    {
        var gate = new SystemToolAdmissionGate(); var preparation = Source<SystemRepairPreparedSession>();
        var platform = new Platform { Prepare = (_, _) => preparation.Task }; var backend = new SystemRepairServicingBackend(platform, gate);
        var run = Run(backend);
        Assert.True(gate.HasPendingWork); Assert.True(backend.HasPendingWork); RefuseNormal(gate);
        var prepared = new Prepared(platform, Assert.IsType<SystemRepairExecutionContext>(platform.Context));
        platform.Prepared = prepared; preparation.SetResult(prepared);
        var result = await run;
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome);
        Assert.True(result.OwnershipReleased); Assert.Equal(CheckState.Unknown, result.RepairSucceeded);
        Assert.Equal(new[] { "client-release", "journal-release", "preparation-release" }, platform.Releases);
        Assert.False(gate.HasPendingWork); Assert.False(gate.RequiresRetention); Assert.False(gate.IsHistoryBlocked);
        var next = Assert.IsType<SystemToolAdmissionLease>(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, Guid.NewGuid())); next.Complete(true);
    }

    [Fact]
    public async Task CancellationDuringPreparationWaitsForActualOwnerAndDoesNotWriteOrDispatch()
    {
        using var cancel = new CancellationTokenSource(); var pending = Source<SystemRepairPreparedSession>();
        var platform = new Platform { Prepare = (_, _) => pending.Task }; var gate = new SystemToolAdmissionGate();
        var backend = new SystemRepairServicingBackend(platform, gate); var run = Run(backend, cancel.Token);
        cancel.Cancel(); Assert.False(run.IsCompleted); RefuseNormal(gate);
        var prepared = new Prepared(platform, platform.Context!); platform.Prepared = prepared; pending.SetResult(prepared);
        var result = await run;
        Assert.Equal(SystemRepairWorkflowOutcome.CancelledBeforeDispatch, result.Outcome);
        Assert.Equal(0, prepared.Store.WriteCalls); Assert.Equal(0, platform.DispatchCalls);
        Assert.Equal(1, prepared.ReleaseCalls); Assert.True(result.OwnershipReleased); Assert.False(gate.IsHistoryBlocked);
    }

    [Fact]
    public async Task DurableIntentCancellationLeavesHistoryBlockedWithoutClaimingLiveResources()
    {
        using var cancel = new CancellationTokenSource(); var pending = Source<SystemToolJournalReview>();
        var platform = new Platform { Persist = (_, snapshot) => snapshot.Count == 1 ? pending.Task : Task.FromResult(Review(snapshot)) };
        var gate = new SystemToolAdmissionGate(); var backend = new SystemRepairServicingBackend(platform, gate);
        var run = Run(backend, cancel.Token);
        var intent = Assert.Single(platform.Prepared!.Store.Proposals);
        cancel.Cancel(); Assert.False(run.IsCompleted); pending.SetResult(Review(intent));
        var result = await run;
        Assert.False(result.DispatchAttempted); Assert.Equal(0, platform.DispatchCalls);
        Assert.Equal(SystemToolJournalState.Unresolved, result.JournalState); Assert.True(result.OwnershipReleased);
        Assert.True(gate.IsHistoryBlocked); Assert.False(gate.RequiresRetention); Assert.False(gate.HasPendingWork);
        Assert.False(backend.RequiresRetention); Assert.False(backend.HasPendingWork); RefuseNormal(gate);
    }

    [Fact]
    public async Task PostCommitCallerStopWaitsForLateExitAndDoesNotCancelOrRewriteTheLiteralResult()
    {
        using var cancel = new CancellationTokenSource(); var platform = new Platform { PendingClient = true };
        var gate = new SystemToolAdmissionGate(); var backend = new SystemRepairServicingBackend(platform, gate);
        var run = Run(backend, cancel.Token); var client = platform.Client!;
        cancel.Cancel(); Assert.False(run.IsCompleted); Assert.True(backend.HasPendingWork); RefuseNormal(gate);
        client.Completion.SetResult(Exit(client, 0)); var result = await run;
        Assert.True(result.StopRequested); Assert.Equal(0, result.ClientObservation!.ExitCode);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome);
        Assert.Equal(CheckState.Unknown, result.RepairSucceeded); Assert.Equal(CheckState.Unknown, result.Integrity);
        Assert.True(result.OwnershipReleased); Assert.False(gate.IsHistoryBlocked); Assert.False(gate.RequiresRetention);
    }

    [Fact]
    public async Task SameRequestAndPermitCannotReachPlatformDispatchTwice()
    {
        var platform = new Platform { ReplayDispatch = true }; var gate = new SystemToolAdmissionGate();
        var backend = new SystemRepairServicingBackend(platform, gate); platform.Backend = backend;
        var request = Request(); var workflow = new SystemRepairWorkflow(backend, new());
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, (await workflow.ExecuteAsync(request, Generation)).Outcome);
        Assert.NotNull(platform.Replay); await Assert.ThrowsAsync<InvalidOperationException>(() => platform.Replay!);
        Assert.False((await workflow.ExecuteAsync(request, Generation)).DispatchAttempted);
        Assert.Equal(1, platform.PrepareCalls); Assert.Equal(1, platform.DispatchCalls);
    }

    [Fact]
    public async Task DispatchAndCleanupCallbacksDoNotHoldStateLocksAcrossExternalWork()
    {
        var platform = new Platform(); var backend = new SystemRepairServicingBackend(platform, new());
        platform.Probe = _ =>
        {
            var inspect = Task.Run(() => backend.HasPendingWork);
            Assert.True(inspect.Wait(TimeSpan.FromSeconds(3)), "A platform callback was invoked under the state lock.");
        };
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, (await Run(backend)).Outcome);
        Assert.Equal(4, platform.ProbeCalls);
    }

    [Fact]
    public async Task ContextWithTheSameReservedRequestCannotSubstituteForItsActualReservationOwner()
    {
        var pending = Source<SystemRepairPreparedSession>();
        var platform = new Platform { Prepare = (_, _) => pending.Task }; var original = new SystemRepairServicingBackend(platform, new());
        var run = Run(original); var actual = platform.Context!;
        var foreign = Assert.IsType<SystemRepairExecutionContext>(Activator.CreateInstance(typeof(SystemRepairExecutionContext),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [actual.Request], null));
        var untouched = new Platform(); var second = new SystemRepairServicingBackend(untouched, new());
        await second.OpenJournalAsync(foreign, CancellationToken.None);
        Assert.Equal(0, untouched.PrepareCalls); Assert.False(second.HasPendingWork);
        var prepared = new Prepared(platform, actual); platform.Prepared = prepared; pending.SetResult(prepared);
        Assert.True((await run).OwnershipReleased);
    }

    [Fact]
    public async Task OrdinaryCompletionCannotClearACandidateRepairLeaseOrRehabilitateItsRetention()
    {
        var platform = new Platform { PendingClient = true }; var gate = new SystemToolAdmissionGate();
        var backend = new SystemRepairServicingBackend(platform, gate); var run = Run(backend);
        var lease = Assert.IsType<SystemToolAdmissionLease>(typeof(SystemToolAdmissionGate)
            .GetField("owner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate));
        lease.Complete(true); Assert.True(gate.RequiresRetention); RefuseNormal(gate);
        platform.Client!.Completion.SetResult(Exit(platform.Client, 0)); await run;
        Assert.True(gate.RequiresRetention); RefuseNormal(gate);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AmbiguousDispatchOrUnknownClientRetainsPreparationAndNeverRetries(bool unknownClient)
    {
        var platform = new Platform { DispatchFailure = !unknownClient, UnknownClient = unknownClient };
        var gate = new SystemToolAdmissionGate(); var backend = new SystemRepairServicingBackend(platform, gate);
        var result = await Run(backend);
        Assert.True(result.DispatchAttempted); Assert.False(result.OwnershipReleased);
        Assert.True(gate.RequiresRetention); Assert.True(backend.RequiresRetention); RefuseNormal(gate);
        Assert.Equal(0, platform.Prepared!.ReleaseCalls); Assert.Equal(1, platform.Prepared.Store.ReleaseCalls);
        Assert.Equal(0, platform.Client?.ReleaseCalls ?? 0);
        await Run(backend); Assert.Equal(1, platform.PrepareCalls); Assert.Equal(1, platform.DispatchCalls);
    }

    [Fact]
    public async Task TerminalJournalUncertaintyKeepsLiteralExitAndBlocksAllRoutesWithoutInventingRetention()
    {
        var platform = new Platform { Persist = (_, snapshot) => Task.FromResult(snapshot.Count == 3 ? Unknown() : Review(snapshot)) };
        var gate = new SystemToolAdmissionGate(); var backend = new SystemRepairServicingBackend(platform, gate);
        var result = await Run(backend);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome); Assert.Equal(0, result.ClientObservation!.ExitCode);
        Assert.True(result.OwnershipReleased); Assert.True(gate.IsHistoryBlocked);
        Assert.False(gate.RequiresRetention); Assert.False(backend.RequiresRetention); RefuseNormal(gate);
        Assert.Equal(1, platform.Prepared!.ReleaseCalls);
    }

    [Fact]
    public async Task FinalPreparationReleaseMustSettleAndFalseCannotBeRehabilitated()
    {
        var release = Source<bool>(); var platform = new Platform { ReleasePreparation = () => release.Task };
        var gate = new SystemToolAdmissionGate(); var backend = new SystemRepairServicingBackend(platform, gate);
        var run = Run(backend);
        Assert.Equal(1, platform.Prepared!.ReleaseCalls); Assert.False(run.IsCompleted); RefuseNormal(gate);
        release.SetResult(false); var result = await run;
        Assert.False(result.OwnershipReleased); Assert.True(backend.RequiresRetention); Assert.True(gate.RequiresRetention);
        await Run(backend); Assert.Equal(1, platform.Prepared.ReleaseCalls); Assert.Equal(1, platform.PrepareCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ForeignPreparedOrClientSessionIsNeverReleasedAsAnOwnedResource(bool foreignClient)
    {
        var warm = new Platform(); await Run(new(warm, new()));
        var foreign = warm.Context!;
        var platform = new Platform();
        var returnedPrepared = new Prepared(platform, foreign); var returnedClient = new Client(platform, foreign);
        if (foreignClient) platform.Dispatch = _ => Task.FromResult<SystemRepairDispatchSession>(returnedClient);
        else platform.Prepare = (_, _) => Task.FromResult<SystemRepairPreparedSession>(returnedPrepared);
        var gate = new SystemToolAdmissionGate(); var backend = new SystemRepairServicingBackend(platform, gate);
        var result = await Run(backend);
        Assert.False(result.OwnershipReleased); Assert.True(gate.RequiresRetention);
        Assert.Equal(0, returnedPrepared.ReleaseCalls); Assert.Equal(0, returnedPrepared.Store.ReleaseCalls);
        Assert.Equal(0, returnedClient.ReleaseCalls); Assert.Equal(default, returnedClient.ObservationStarted);
        Assert.Equal(0, platform.Prepared?.ReleaseCalls ?? 0);
        RefuseNormal(gate);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecreatedRepairAdmissionCannotIgnoreUnresolvedOrMalformedWholeFileHistory(bool malformed)
    {
        string folder = Path.Combine(Path.GetTempPath(), "MadCleanRepairHistory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var cancel = new CancellationTokenSource();
            var first = new Platform { StoreFactory = context => new FileStore(context, folder, () => cancel.Cancel()) };
            var prior = await Run(new(first, new()), cancel.Token);
            Assert.False(prior.DispatchAttempted); Assert.Equal(SystemToolJournalState.Unresolved, prior.JournalState);
            var file = Assert.Single(Directory.GetFiles(folder));
            if (malformed) File.AppendAllText(file, "{unterminated-tail");
            byte[] before = File.ReadAllBytes(file);
            // A new managed domain re-reads the fixture; this does not create a
            // process restart, crash or power-loss durability observation.
            var restarted = new Platform { StoreFactory = context => new FileStore(context, folder, null) };
            var newGate = new SystemToolAdmissionGate(); var second = await Run(new(restarted, newGate));
            Assert.False(second.DispatchAttempted); Assert.Equal(0, restarted.DispatchCalls);
            Assert.True(newGate.IsHistoryBlocked); RefuseNormal(newGate);
            Assert.Equal(before, File.ReadAllBytes(file)); Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ProcessRootedSharedGateKeepsAbandonedRepairOwnerAndLateCleanupAlive()
    {
        var gate = new SystemToolAdmissionGate(); var refs = StartAbandoned(gate);
        for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.True(refs.Client.TryGetTarget(out var client)); Assert.True(refs.Run.TryGetTarget(out var run));
        RefuseNormal(gate); client!.Completion.SetResult(Exit(client, 0));
        Assert.True((await run!).OwnershipReleased); Assert.False(gate.HasPendingWork);
        GC.KeepAlive(gate);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<Client> Client, WeakReference<Task<SystemRepairWorkflowResult>> Run) StartAbandoned(SystemToolAdmissionGate gate)
    {
        var platform = new Platform { PendingClient = true }; var backend = new SystemRepairServicingBackend(platform, gate);
        var run = Run(backend); return (new(platform.Client!), new(run));
    }

    private static void RefuseNormal(SystemToolAdmissionGate gate)
    {
        foreach (var operation in new[] { SystemToolOperation.DismCheckHealth, SystemToolOperation.DismScanHealth, SystemToolOperation.SfcVerifyOnly })
            Assert.Null(gate.TryAcquire(operation, Guid.NewGuid()));
    }
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SystemToolJournalReview Review(IReadOnlyList<SystemToolJournalEntry> entries) => SystemToolJournal.Review(entries, DateTimeOffset.UtcNow);
    private static SystemToolJournalReview Unknown() => SystemToolJournal.Review(null, DateTimeOffset.UtcNow);
    private static ClientProcessObservation Exit(Client client, int code) => new(ClientProcessObservationState.Exited,
        ClientProcessObservationReason.ClientExited, client.ObservationStarted, DateTimeOffset.UtcNow, client.ProcessId, client.ClientStartUtc, code, false);

    private sealed class Platform : ISystemRepairExecutionPlatform
    {
        public bool Enabled = true, PendingClient, UnknownClient, DispatchFailure, ReplayDispatch;
        public int PrepareCalls, DispatchCalls;
        public SystemRepairExecutionContext? Context;
        public Prepared? Prepared; public Client? Client;
        public SystemRepairServicingBackend? Backend; public Task<SystemRepairDispatchSession>? Replay;
        public readonly List<string> Releases = [];
        public Func<SystemRepairExecutionContext, CancellationToken, Task<SystemRepairPreparedSession>>? Prepare;
        public Func<SystemRepairDispatchPermit, Task<SystemRepairDispatchSession>>? Dispatch;
        public Func<SystemRepairExecutionContext, MemoryStore>? StoreFactory;
        public Func<MemoryStore, IReadOnlyList<SystemToolJournalEntry>, Task<SystemToolJournalReview>>? Persist;
        public Func<Task<bool>>? ReleasePreparation;
        public Func<bool>? Gate; public Action<string>? Probe; public int ProbeCalls;
        public bool ProductionExecutionValidated => Gate?.Invoke() ?? Enabled;
        public void Inspect(string stage) { if (Probe != null) { ProbeCalls++; Probe(stage); } }
        public Task<SystemRepairPreparedSession> PrepareAsync(SystemRepairExecutionContext context, CancellationToken token)
        {
            PrepareCalls++; Context = context;
            if (Prepare != null) return Prepare(context, token);
            Prepared = new(this, context); return Task.FromResult<SystemRepairPreparedSession>(Prepared);
        }
    }
    private sealed class Prepared : SystemRepairPreparedSession
    {
        private readonly Platform platform;
        public readonly MemoryStore Store;
        private readonly SystemRepairJournalSession journal;
        public int ReleaseCalls;
        public Prepared(Platform platform, SystemRepairExecutionContext context) : base(context)
        {
            this.platform = platform; Store = platform.StoreFactory?.Invoke(context) ?? new MemoryStore(context, platform);
            journal = new SystemRepairStoredJournalSession(context, Store);
        }
        public override SystemRepairJournalSession Journal => journal;
        public override Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit)
        {
            platform.DispatchCalls++; Assert.Single(Store.Durable); Assert.True(permit.TryClaim(Context));
            platform.Inspect("dispatch");
            if (platform.ReplayDispatch) platform.Replay = platform.Backend!.DispatchAsync(permit);
            if (platform.DispatchFailure) throw new InvalidOperationException("Controlled dispatch uncertainty");
            if (platform.Dispatch != null) return platform.Dispatch(permit);
            platform.Client = new(platform, Context); return Task.FromResult<SystemRepairDispatchSession>(platform.Client);
        }
        public override async Task<bool> ReleaseOwnershipAsync()
        {
            ReleaseCalls++; platform.Releases.Add("preparation-release");
            platform.Inspect("preparation-release");
            return platform.ReleasePreparation == null || await platform.ReleasePreparation();
        }
    }
    private sealed class Client(Platform platform, SystemRepairExecutionContext context)
        : SystemRepairClientSession(context, 482, DateTimeOffset.UtcNow)
    {
        public readonly TaskCompletionSource<ClientProcessObservation> Completion = Source<ClientProcessObservation>();
        public DateTimeOffset ObservationStarted;
        public int ReleaseCalls;
        protected override Task<ClientProcessObservation> ObserveAsync()
        {
            ObservationStarted = DateTimeOffset.UtcNow;
            if (platform.PendingClient) return Completion.Task;
            return Task.FromResult(platform.UnknownClient
                ? new(ClientProcessObservationState.Unavailable, ClientProcessObservationReason.ExitNotObserved,
                    ObservationStarted, DateTimeOffset.UtcNow, ProcessId, ClientStartUtc, null, false)
                : Exit(this, 0));
        }
        protected override Task<bool> ReleaseOwnershipAsync()
        { ReleaseCalls++; platform.Releases.Add("client-release"); platform.Inspect("client-release"); return Task.FromResult(true); }
    }
    private class MemoryStore(SystemRepairExecutionContext context, Platform? platform = null) : ISystemRepairSnapshotStore
    {
        public SystemRepairExecutionContext Context { get; } = context;
        public IReadOnlyList<SystemToolJournalEntry> Durable = Array.Empty<SystemToolJournalEntry>();
        public readonly List<IReadOnlyList<SystemToolJournalEntry>> Proposals = [];
        public int WriteCalls, ReleaseCalls;
        public virtual async Task<SystemToolJournalReview> PersistSnapshotAsync(IReadOnlyList<SystemToolJournalEntry> snapshot)
        {
            WriteCalls++; Proposals.Add(snapshot);
            var observed = platform?.Persist == null ? Review(snapshot) : await platform.Persist(this, snapshot);
            if (observed.State != SystemToolJournalState.Unknown) Durable = observed.Entries;
            return observed;
        }
        public virtual Task<bool> ReleaseOwnershipAsync()
        { ReleaseCalls++; platform?.Releases.Add("journal-release"); platform?.Inspect("journal-release"); return Task.FromResult(true); }
    }
    private sealed class FileStore(SystemRepairExecutionContext context, string directory, Action? afterFirst) : MemoryStore(context)
    {
        private FileStream? stream;
        public override Task<SystemToolJournalReview> PersistSnapshotAsync(IReadOnlyList<SystemToolJournalEntry> snapshot)
        {
            WriteCalls++;
            // NEW recovery replacement: original helper suffix truncated in evidence.
            Proposals.Add(snapshot);
            string path = Path.Combine(directory, "repair-history.jsonl");
            if (stream == null)
            {
                if (File.Exists(path))
                {
                    // Recreated owners inspect the entire existing file without extending or repairing it.
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    var existing = ReadBack();
                    Durable = existing.Entries;
                    return Task.FromResult(existing);
                }
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            Assert.True(stream.CanWrite, "An existing history file must never be rewritten by a new owner.");
            Assert.True(SystemToolJournal.TryEncode(snapshot, DateTimeOffset.UtcNow, out var encoded));
            Assert.NotNull(encoded);
            stream.Position = stream.Length;
            stream.Write(encoded!);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
            var observed = ReadBack();
            Durable = observed.Entries;
            if (WriteCalls == 1) afterFirst?.Invoke();
            return Task.FromResult(observed);
        }

        // NEW recovery replacement: bounded whole-file readback without tail salvage.
        private SystemToolJournalReview ReadBack()
        {
            if (stream == null || stream.Length is <= 0 or > SystemToolJournal.MaximumUtf8Bytes) return Unknown();
            var bytes = new byte[checked((int)stream.Length)];
            stream.Position = 0;
            stream.ReadExactly(bytes);
            return SystemToolJournal.DecodeSnapshots(bytes, DateTimeOffset.UtcNow);
        }

        // NEW recovery replacement: release only the stream acquired by this temporary fixture owner.
        public override Task<bool> ReleaseOwnershipAsync()
        {
            ReleaseCalls++;
            stream?.Dispose();
            stream = null;
            return Task.FromResult(true);
        }
    }
}
