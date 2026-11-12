using System.Reflection;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemRepairStoredJournalTests
{
    private const long Generation = 3;
    private static SystemRepairRequest Request()
    {
        var request = Assert.IsType<SystemRepairRequest>(SystemRepairWorkflow.Prepare(
            SystemToolOperation.DismRestoreHealth, Generation, DateTimeOffset.UtcNow));
        Assert.Equal(SystemRepairConfirmation.Confirmed, request.Confirm(request.RequestId,
            request.Definition.Operation, Generation, true, true, true, true, DateTimeOffset.UtcNow));
        return request;
    }

    [Fact]
    public async Task FullProtocolRequiresExactReadbackBeforeDispatchAndKeepsImmutablePrefixes()
    {
        var backend = new Backend(); var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        var store = Assert.IsType<MemoryStore>(backend.Store);
        Assert.True(backend.IntentWasReadBack); Assert.Equal(1, backend.DispatchCalls);
        Assert.Equal(3, store.Snapshots.Count); Assert.Equal(1, store.ReleaseCalls);
        for (int index = 0; index < store.Snapshots.Count; index++)
        {
            var snapshot = store.Snapshots[index];
            Assert.Equal(index + 1, snapshot.Count);
            Assert.Throws<NotSupportedException>(() => ((IList<SystemToolJournalEntry>)snapshot).Clear());
            for (int previous = 0; previous < index; previous++) Assert.Equal(store.Snapshots[index - 1][previous], snapshot[previous]);
        }
        Assert.Equal(SystemToolJournalState.RecordedClientExit, result.JournalState);
        Assert.True(result.OwnershipReleased); Assert.False(admission.IsBlocked);
        Assert.Equal(CheckState.Unknown, result.RepairSucceeded); Assert.Equal(CheckState.Unknown, result.Integrity);
    }

    [Theory]
    [InlineData("operation")] [InlineData("execution")] [InlineData("time")]
    [InlineData("extra")] [InlineData("state")] [InlineData("empty")]
    public async Task CodecValidOrContradictoryForeignIntentReadbackCannotAdmitDispatch(string mutation)
    {
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        {
            Persist = (_, snapshot) => Task.FromResult(MutateIntent(snapshot, mutation))
        } };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        var store = Assert.IsType<MemoryStore>(backend.Store);
        Assert.False(result.DispatchAttempted); Assert.Equal(0, backend.DispatchCalls);
        Assert.Equal(1, store.WriteCalls); Assert.Equal(1, store.ReleaseCalls);
        Assert.Equal(SystemToolJournalState.Unknown, result.JournalState); Assert.True(admission.IsBlocked);
    }

    [Theory]
    [InlineData("pid")] [InlineData("start")] [InlineData("exit")]
    public async Task EveryKnownClientFieldMustMatchExactTerminalReadback(string mutation)
    {
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        {
            Persist = (_, snapshot) =>
            {
                if (snapshot.Count != 3) return Task.FromResult(Readback(snapshot));
                var altered = snapshot.Select(entry => entry with { }).ToArray();
                if (mutation == "pid")
                {
                    altered[1] = altered[1] with { ProcessId = 902 };
                    altered[2] = altered[2] with { ProcessId = 902 };
                }
                else if (mutation == "start")
                {
                    altered[1] = altered[1] with { ClientStartUtc = altered[1].ClientStartUtc!.Value.AddTicks(-1) };
                    altered[2] = altered[2] with { ClientStartUtc = altered[2].ClientStartUtc!.Value.AddTicks(-1) };
                }
                else altered[2] = altered[2] with { ExitCode = 19 };
                var actual = Readback(altered);
                Assert.Equal(SystemToolJournalState.RecordedClientExit, actual.State);
                return Task.FromResult(actual);
            }
        } };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome);
        Assert.Equal(0, result.ClientObservation!.ExitCode);
        Assert.Equal(SystemToolJournalState.Unknown, result.JournalState);
        Assert.True(admission.IsBlocked); Assert.True(result.OwnershipReleased);
        Assert.Equal(3, Assert.IsType<MemoryStore>(backend.Store).WriteCalls);
    }

    [Fact]
    public async Task EarlierValidPrefixCannotStandInForNewObservedRecordAndIsNeverRetried()
    {
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        {
            Persist = (_, snapshot) => Task.FromResult(Readback(snapshot.Take(1).ToArray()))
        } };
        var admission = new SystemRepairAdmission(); var workflow = new SystemRepairWorkflow(backend, admission);
        var result = await workflow.ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome);
        Assert.Equal(0, result.ClientObservation!.ExitCode); Assert.Equal(SystemToolJournalState.Unknown, result.JournalState);
        Assert.Equal(2, Assert.IsType<MemoryStore>(backend.Store).WriteCalls);
        Assert.Equal(SystemRepairWorkflowOutcome.Busy, (await workflow.ExecuteAsync(Request(), Generation)).Outcome);
        Assert.Equal(1, backend.DispatchCalls);
    }

    [Fact]
    public async Task PrivateStoreMessageIsNotParsedOrPropagatedAsEvidence()
    {
        const string secret = "Private fixture source text must not become protocol output";
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        {
            Persist = (_, snapshot) => Task.FromResult(Readback(snapshot) with { Message = secret })
        } };
        var result = await new SystemRepairWorkflow(backend, new()).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemToolJournalState.RecordedClientExit, result.JournalState);
        Assert.DoesNotContain(secret, result.Message);
    }

    [Fact]
    public async Task PendingIntentReadbackRetainsOwnershipAndCancellationPreventsDispatch()
    {
        var entered = Signal(); var response = Source<SystemToolJournalReview>();
        IReadOnlyList<SystemToolJournalEntry>? intent = null;
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        {
            Persist = (_, snapshot) =>
            {
                if (snapshot.Count != 1) return Task.FromResult(Readback(snapshot));
                intent = snapshot; entered.SetResult(); return response.Task;
            }
        } };
        var admission = new SystemRepairAdmission(); using var stop = new CancellationTokenSource();
        var run = new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        Assert.False(run.IsCompleted); Assert.True(admission.HasPendingWork);
        Assert.Equal(0, Assert.IsType<MemoryStore>(backend.Store).ReleaseCalls); Assert.Equal(0, backend.DispatchCalls);
        response.SetResult(Readback(intent!));
        var result = await run;
        Assert.False(result.DispatchAttempted); Assert.True(result.StopRequested);
        Assert.Equal(SystemToolJournalState.Unresolved, result.JournalState);
        Assert.True(result.OwnershipReleased); Assert.True(admission.IsBlocked); Assert.False(admission.RequiresRetention);
    }

    [Fact]
    public async Task PendingReleaseMustSettleAndFailureCannotBeRetriedByAnotherRequest()
    {
        var entered = Signal(); var release = Source<bool>();
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        { Release = () => { entered.SetResult(); return release.Task; } } };
        var admission = new SystemRepairAdmission(); var workflow = new SystemRepairWorkflow(backend, admission);
        var run = workflow.ExecuteAsync(Request(), Generation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted); Assert.True(admission.HasPendingWork);
        Assert.Equal(SystemRepairWorkflowOutcome.Busy, (await workflow.ExecuteAsync(Request(), Generation)).Outcome);
        release.SetResult(false);
        var result = await run;
        Assert.False(result.OwnershipReleased); Assert.True(admission.RequiresRetention);
        Assert.Equal(SystemRepairWorkflowOutcome.Busy, (await workflow.ExecuteAsync(Request(), Generation)).Outcome);
        Assert.Equal(1, Assert.IsType<MemoryStore>(backend.Store).ReleaseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedOrThrowingReleaseRetainsActualExitWithoutASecondCleanupAttempt(bool throws)
    {
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        { Release = () => throws ? Task.FromException<bool>(new IOException("Owned release fixture")) : Task.FromResult(false) } };
        var admission = new SystemRepairAdmission(); var workflow = new SystemRepairWorkflow(backend, admission);
        var result = await workflow.ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.ClientExited, result.Outcome);
        Assert.Equal(0, result.ClientObservation!.ExitCode);
        Assert.False(result.OwnershipReleased); Assert.True(admission.RequiresRetention);
        Assert.Equal(SystemRepairWorkflowOutcome.Busy, (await workflow.ExecuteAsync(Request(), Generation)).Outcome);
        Assert.Equal(1, Assert.IsType<MemoryStore>(backend.Store).ReleaseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WriteFaultOrUnknownReadbackLatchesBeforeAnyRetry(bool throws)
    {
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        { Persist = (_, _) => throws ? Task.FromException<SystemToolJournalReview>(new IOException("Owned fixture failure")) : Task.FromResult(Unknown()) } };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.Equal(0, backend.DispatchCalls); Assert.True(admission.IsBlocked);
        Assert.Equal(1, Assert.IsType<MemoryStore>(backend.Store).WriteCalls);
        Assert.Equal(SystemToolJournalState.Unknown, result.JournalState);
    }

    [Fact]
    public async Task ForeignContextWithEqualExecutionGuidIsRefusedBeforeStoreCallbacks()
    {
        var earlier = new Backend(); await new SystemRepairWorkflow(earlier, new()).ExecuteAsync(Request(), Generation);
        var foreignContext = earlier.Store!.Context;
        MemoryStore? foreign = null;
        var backend = new Backend { CreateStore = context =>
        {
            // Deliberately forge only owned fixture metadata to prove that GUID equality
            // cannot replace the opaque context-reference boundary.
            typeof(SystemRepairExecutionContext).GetField("<ExecutionId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(foreignContext, context.ExecutionId);
            foreign = new(foreignContext); return foreign;
        } };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        Assert.Equal(SystemRepairWorkflowOutcome.PreparationUncertain, result.Outcome);
        Assert.Equal(0, foreign!.WriteCalls); Assert.Equal(0, foreign.ReleaseCalls); Assert.Equal(0, backend.DispatchCalls);
        Assert.False(result.OwnershipReleased); Assert.True(admission.RequiresRetention);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TransientContextLossOrGetterFailureCannotRecoverForCleanup(bool throws)
    {
        var earlier = new Backend(); await new SystemRepairWorkflow(earlier, new()).ExecuteAsync(Request(), Generation);
        var foreignContext = earlier.Store!.Context;
        var backend = new Backend { CreateStore = context => new MemoryStore(context)
        {
            Persist = (store, snapshot) =>
            {
                store.ContextReader = () =>
                {
                    store.ContextReader = null; // It would return the original context afterward.
                    if (throws) throw new InvalidOperationException("Owned fixture context failure");
                    return foreignContext;
                };
                return Task.FromResult(Readback(snapshot));
            }
        } };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        var actual = Assert.IsType<MemoryStore>(backend.Store);
        Assert.Same(actual.OwnedContext, actual.Context); // The transient mismatch is already over.
        Assert.Equal(0, actual.ReleaseCalls); Assert.Equal(1, actual.WriteCalls); Assert.Equal(0, backend.DispatchCalls);
        Assert.False(result.OwnershipReleased); Assert.True(admission.RequiresRetention);
    }

    [Fact]
    public async Task OwnedFileAppendFlushAndSameHandleReadbackWorksThroughActualProtocol()
    {
        using var scope = new OwnedFileScope();
        var backend = new Backend { CreateStore = context => new FileStore(context, scope.File) };
        var result = await new SystemRepairWorkflow(backend, new()).ExecuteAsync(Request(), Generation);
        var bytes = File.ReadAllBytes(scope.File);
        var actual = SystemToolJournal.DecodeSnapshots(bytes, DateTimeOffset.UtcNow);
        Assert.Equal(SystemToolJournalState.RecordedClientExit, actual.State); Assert.Equal(3, actual.Entries.Count);
        Assert.Equal(SystemToolJournalState.RecordedClientExit, result.JournalState);
        Assert.True(backend.IntentWasReadBack); Assert.Equal(0, result.ClientObservation!.ExitCode);
        Assert.Equal(CheckState.Unknown, result.RepairSucceeded);
        Assert.InRange(bytes.Length, 1, SystemToolJournal.MaximumUtf8Bytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OwnedFilePartialWriteOrMalformedTailNeverSalvagesTheIntent(bool partialWrite)
    {
        using var scope = new OwnedFileScope();
        var backend = new Backend { CreateStore = context => new FileStore(context, scope.File, partialWrite ? FileFailure.Partial : FileFailure.MalformedTail) };
        var admission = new SystemRepairAdmission();
        var result = await new SystemRepairWorkflow(backend, admission).ExecuteAsync(Request(), Generation);
        var bytes = File.ReadAllBytes(scope.File);
        Assert.NotEmpty(bytes); Assert.Equal(SystemToolJournalState.Unknown, SystemToolJournal.DecodeSnapshots(bytes, DateTimeOffset.UtcNow).State);
        Assert.False(result.DispatchAttempted); Assert.Equal(0, backend.DispatchCalls);
        Assert.Equal(1, Assert.IsType<FileStore>(backend.Store).WriteCalls); Assert.True(admission.IsBlocked);
    }

    private static SystemToolJournalReview MutateIntent(IReadOnlyList<SystemToolJournalEntry> snapshot, string mutation)
    {
        var first = snapshot[0];
        if (mutation == "empty") return new(SystemToolJournalState.Unresolved, [], "Untrusted fixture reason");
        if (mutation == "state") return Readback(snapshot) with { State = SystemToolJournalState.RecordedClientExit };
        if (mutation == "extra") return Readback([first, first with { Sequence = 2, Phase = SystemToolJournalPhase.ObservationLost }]);
        var altered = mutation switch
        {
            "operation" => first with { Operation = SystemToolOperation.SfcScanNow },
            "execution" => first with { ExecutionId = Guid.NewGuid() },
            _ => first with { TimestampUtc = first.TimestampUtc.AddTicks(-1) }
        };
        var actual = Readback([altered]); Assert.Equal(SystemToolJournalState.Unresolved, actual.State); return actual;
    }
    private static SystemToolJournalReview Readback(IReadOnlyList<SystemToolJournalEntry> snapshot)
    {
        Assert.True(SystemToolJournal.TryEncode(snapshot, DateTimeOffset.UtcNow, out var bytes));
        return SystemToolJournal.Decode(bytes!, DateTimeOffset.UtcNow);
    }
    private static SystemToolJournalReview Unknown() => SystemToolJournal.Review(null, DateTimeOffset.UtcNow);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Backend : ISystemRepairWorkflowBackend
    {
        public Func<SystemRepairExecutionContext, ISystemRepairSnapshotStore>? CreateStore;
        public ISystemRepairSnapshotStore? Store;
        public int DispatchCalls;
        public bool IntentWasReadBack;
        public bool ProductionExecutionValidated => true; // Controlled protocol fixture, never a Windows provider.
        public Task<SystemRepairJournalSession> OpenJournalAsync(SystemRepairExecutionContext context, CancellationToken token)
        {
            Store = CreateStore?.Invoke(context) ?? new MemoryStore(context);
            return Task.FromResult<SystemRepairJournalSession>(new SystemRepairStoredJournalSession(context, Store));
        }
        public Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit)
        {
            DispatchCalls++;
            Assert.True(permit.TryClaim(permit.Context));
            IntentWasReadBack = Store is MemoryStore memory ? memory.ReadBackCount == 1 : ((FileStore)Store!).ReadBackCount == 1;
            Assert.True(IntentWasReadBack);
            return Task.FromResult<SystemRepairDispatchSession>(new Client(permit.Context));
        }
    }
    private sealed class Client(SystemRepairExecutionContext context) : SystemRepairClientSession(context, 901, DateTimeOffset.UtcNow)
    {
        protected override Task<ClientProcessObservation> ObserveAsync() => Task.FromResult(new ClientProcessObservation(
            ClientProcessObservationState.Exited, ClientProcessObservationReason.ClientExited,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProcessId, ClientStartUtc, 0, false));
        protected override Task<bool> ReleaseOwnershipAsync() => Task.FromResult(true);
    }
    private sealed class MemoryStore(SystemRepairExecutionContext context) : ISystemRepairSnapshotStore
    {
        public readonly SystemRepairExecutionContext OwnedContext = context;
        public Func<SystemRepairExecutionContext>? ContextReader;
        public SystemRepairExecutionContext Context => ContextReader?.Invoke() ?? OwnedContext;
        public Func<MemoryStore, IReadOnlyList<SystemToolJournalEntry>, Task<SystemToolJournalReview>>? Persist;
        public Func<Task<bool>>? Release;
        public readonly List<IReadOnlyList<SystemToolJournalEntry>> Snapshots = [];
        public int WriteCalls, ReleaseCalls, ReadBackCount;
        public async Task<SystemToolJournalReview> PersistSnapshotAsync(IReadOnlyList<SystemToolJournalEntry> snapshot)
        {
            WriteCalls++; Snapshots.Add(snapshot);
            var actual = Persist == null ? Readback(snapshot) : await Persist(this, snapshot);
            ReadBackCount++; return actual;
        }
        public Task<bool> ReleaseOwnershipAsync() { ReleaseCalls++; return Release?.Invoke() ?? Task.FromResult(true); }
    }
    private enum FileFailure { None, Partial, MalformedTail }
    private sealed class FileStore : ISystemRepairSnapshotStore
    {
        private readonly FileStream stream;
        private readonly FileFailure failure;
        public SystemRepairExecutionContext Context { get; }
        public int WriteCalls, ReadBackCount;
        public FileStore(SystemRepairExecutionContext context, string file, FileFailure failure = FileFailure.None)
        {
            Context = context; this.failure = failure;
            stream = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        }
        public Task<SystemToolJournalReview> PersistSnapshotAsync(IReadOnlyList<SystemToolJournalEntry> snapshot)
        {
            WriteCalls++;
            Assert.True(SystemToolJournal.TryEncode(snapshot, DateTimeOffset.UtcNow, out var bytes));
            stream.Position = stream.Length;
            if (failure == FileFailure.Partial)
            {
                stream.Write(bytes!, 0, bytes!.Length / 2); stream.Flush(true);
                throw new IOException("Owned partial-write fixture");
            }
            stream.Write(bytes!);
            // NEW replacement helper suffix: original source after the preceding write
            // was truncated in recovery evidence. This is fixture development, not recovered code.
            stream.WriteByte((byte)'\n');
            if (failure == FileFailure.MalformedTail) stream.WriteByte((byte)'{');
            stream.Flush(true);
            Assert.InRange(stream.Length, 1L, (long)SystemToolJournal.MaximumUtf8Bytes);
            var persisted = new byte[checked((int)stream.Length)];
            stream.Position = 0;
            stream.ReadExactly(persisted);
            var actual = SystemToolJournal.DecodeSnapshots(persisted, DateTimeOffset.UtcNow);
            ReadBackCount++;
            return Task.FromResult(actual);
        }
        public Task<bool> ReleaseOwnershipAsync()
        {
            stream.Dispose();
            return Task.FromResult(true);
        }
    }

    // NEW replacement fixture scope; only this freshly created directory and exact file are owned.
    private sealed class OwnedFileScope : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "MadClean-stored-journal-" + Guid.NewGuid().ToString("N"));
        public string File { get; }
        public OwnedFileScope()
        {
            Directory.CreateDirectory(directory);
            File = Path.Combine(directory, "journal.jsonl");
        }
        public void Dispose()
        {
            if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
            Directory.Delete(directory, recursive: false);
        }
    }
}
