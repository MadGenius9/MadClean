using System.Runtime.CompilerServices;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemRepairRecoveryTests
{
    [Theory]
    [InlineData("unresolved", SystemToolJournalHistoryState.UnresolvedPresent, true)]
    [InlineData("malformed", SystemToolJournalHistoryState.UnknownEvidence, true)]
    [InlineData("terminal", SystemToolJournalHistoryState.RecordedTerminalOnly, false)]
    [InlineData("empty", SystemToolJournalHistoryState.NoRecordsObserved, false)]
    public async Task RecreatedManagedDomainsRereadWholeOwnedFilesWithoutTreatingHistoryAsPermission(string content, SystemToolJournalHistoryState expected, bool blocked)
    {
        using var fixture = new Files(content);
        // This recreates managed admission/readers, not a process restart or crash-durability test.
        for (int i = 0; i < 2; i++)
        {
            var gate = new SystemToolAdmissionGate(); var reader = new Reader(fixture);
            var recovery = new SystemRepairRecovery(reader, gate);
            var result = await recovery.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, result.History!.State); Assert.Equal(blocked, result.HistoryBlocked);
            Assert.True(result.OwnershipReleased); Assert.False(recovery.RequiresRetention); Assert.False(recovery.HasPendingWork);
            Assert.Equal(1, reader.Opens); Assert.Equal(1, reader.Session!.Reads); Assert.Equal(1, reader.Session.Releases);
            Assert.Equal(blocked, gate.IsHistoryBlocked); Assert.False(gate.RequiresRetention); Assert.False(gate.HasPendingWork);
            Assert.Same(recovery.RunAsync(), recovery.RunAsync()); Assert.Equal(1, reader.Opens);
            Assert.Equal(fixture.Original, await File.ReadAllBytesAsync(fixture.Path));
            var ordinary = gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid());
            if (blocked) Assert.Null(ordinary); else { Assert.NotNull(ordinary); ordinary.Complete(true); }
        }
    }

    [Fact]
    public async Task ClosedGuardAndPreEntryStopAndBusyGateMakeNoReaderCalls()
    {
        using var files = new Files("empty"); var reader = new Reader(files) { Enabled = false };
        var gate = new SystemToolAdmissionGate();
        var result = await new SystemRepairRecovery(reader, gate).RunAsync();
        Assert.Equal(SystemRepairRecoveryOutcome.Blocked, result.Outcome); Assert.Null(result.HistoryBlocked); Assert.Equal(0, reader.Opens);
        reader.Enabled = true; using var stop = new CancellationTokenSource(); stop.Cancel();
        result = await new SystemRepairRecovery(reader, gate).RunAsync(stop.Token);
        Assert.Equal(SystemRepairRecoveryOutcome.CancelledBeforeEntry, result.Outcome); Assert.Null(result.HistoryBlocked); Assert.Equal(0, reader.Opens);
        var lease = gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid()); Assert.NotNull(lease);
        result = await new SystemRepairRecovery(reader, gate).RunAsync();
        Assert.Equal(SystemRepairRecoveryOutcome.Busy, result.Outcome); Assert.Null(result.HistoryBlocked); Assert.Equal(0, reader.Opens);
        lease.Complete(true);
    }

    [Fact]
    public async Task ClockFailureBeforeAdmissionIsKnownNoIoAndDoesNotDisturbAnExistingOwner()
    {
        using var files = new Files("empty"); var gate = new SystemToolAdmissionGate(); var reader = new Reader(files);
        var ordinary = gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid()); Assert.NotNull(ordinary);
        var recovery = new SystemRepairRecovery(reader, gate, new FailingClock()); var result = await recovery.RunAsync();
        Assert.Equal(SystemRepairRecoveryOutcome.Blocked, result.Outcome); Assert.True(result.OwnershipReleased);
        Assert.Null(result.HistoryBlocked); Assert.False(recovery.RequiresRetention); Assert.Equal(0, reader.Opens);
        Assert.True(gate.HasPendingWork); Assert.False(gate.RequiresRetention); ordinary.Complete(true);
    }
    private sealed class FailingClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Controlled clock failure."); }

    [Fact]
    public async Task ExistingHistoryBlockCannotBeClearedByAnotherRecoveryOnTheSameGate()
    {
        using var files = new Files("unresolved"); var gate = new SystemToolAdmissionGate();
        await new SystemRepairRecovery(new Reader(files), gate).RunAsync();
        var reader = new Reader(files); var result = await new SystemRepairRecovery(reader, gate).RunAsync();
        Assert.Equal(SystemRepairRecoveryOutcome.Busy, result.Outcome); Assert.Equal(0, reader.Opens); Assert.True(gate.IsHistoryBlocked);
    }

    [Fact]
    public async Task LateCallerStopAwaitsActualReadAndReleaseButDoesNotQualifyEvenEmptyHistory()
    {
        using var files = new Files("empty"); var reader = new Reader(files) { HoldRead = true, HoldRelease = true };
        var gate = new SystemToolAdmissionGate();
        var stale = gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid()); Assert.NotNull(stale); stale.Complete(true);
        var recovery = new SystemRepairRecovery(reader, gate);
        using var stop = new CancellationTokenSource(); var run = recovery.RunAsync(stop.Token);
        await reader.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        stale.Complete(true); // A finished ordinary lease cannot clear the new recovery owner.
        Assert.False(run.IsCompleted); Assert.True(recovery.HasPendingWork); Assert.True(gate.HasPendingWork);
        Assert.Null(gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid())); Assert.False(reader.EntryToken.CanBeCanceled);
        reader.ReadContinue.TrySetResult(); await reader.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted); Assert.True(reader.Session!.Stream!.CanRead);
        reader.ReleaseContinue.TrySetResult(); var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.StopRequested); Assert.True(result.HistoryBlocked); Assert.True(result.OwnershipReleased);
        Assert.Equal(SystemToolJournalHistoryState.NoRecordsObserved, result.History!.State);
        Assert.True(gate.IsHistoryBlocked); Assert.False(gate.RequiresRetention); Assert.False(recovery.RequiresRetention);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateReadFailureAndCleanupFailureHaveDistinctHistoryAndResourceRetention(bool cleanupFails)
    {
        using var files = new Files("terminal"); var reader = new Reader(files) { HoldRead = true, ReadFails = !cleanupFails, ReleaseFails = cleanupFails };
        var gate = new SystemToolAdmissionGate(); var recovery = new SystemRepairRecovery(reader, gate); var run = recovery.RunAsync();
        await reader.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(run.IsCompleted);
        reader.ReadContinue.TrySetResult(); var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.HistoryBlocked); Assert.Equal(!cleanupFails, result.OwnershipReleased);
        Assert.Equal(cleanupFails, recovery.RequiresRetention); Assert.Equal(cleanupFails, gate.RequiresRetention);
        Assert.Equal(1, reader.Session!.Releases); Assert.Same(run, recovery.RunAsync());
        if (cleanupFails)
        {
            var replacement = new Reader(files);
            Assert.Equal(SystemRepairRecoveryOutcome.Busy, (await new SystemRepairRecovery(replacement, gate).RunAsync()).Outcome);
            Assert.Equal(0, replacement.Opens);
            reader.Session.Stream!.Dispose(); // Explicit owned fixture teardown, not recovery retry.
        }
    }

    [Fact]
    public async Task KnownUnavailableBoundSessionBlocksOnlyHistoryAfterItsActualCleanup()
    {
        using var files = new Files("empty"); var reader = new Reader(files) { KnownUnavailable = true };
        var gate = new SystemToolAdmissionGate(); var recovery = new SystemRepairRecovery(reader, gate);
        var result = await recovery.RunAsync();
        Assert.Equal(SystemRepairRecoveryOutcome.Uncertain, result.Outcome);
        Assert.Equal(SystemToolJournalHistoryState.UnknownEvidence, result.History!.State);
        Assert.True(result.OwnershipReleased); Assert.True(result.HistoryBlocked); Assert.True(gate.IsHistoryBlocked);
        Assert.False(gate.RequiresRetention); Assert.False(recovery.RequiresRetention); Assert.False(recovery.HasPendingWork);
        Assert.Equal(1, reader.Session!.Reads); Assert.Equal(1, reader.Session.Releases); Assert.Null(reader.Session.Stream);
    }

    [Fact]
    public async Task ForeignSessionIsNeitherReadNorReleasedAndOpenFailureRetainsAdmission()
    {
        using var files = new Files("empty"); var firstReader = new Reader(files) { HoldRead = true };
        var first = new SystemRepairRecovery(firstReader, new()).RunAsync(); await firstReader.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var gate = new SystemToolAdmissionGate(); var foreign = new Reader(files) { Foreign = firstReader.Session };
        var result = await new SystemRepairRecovery(foreign, gate).RunAsync();
        Assert.False(result.OwnershipReleased); Assert.True(gate.RequiresRetention); Assert.Equal(1, firstReader.Session!.Reads); Assert.Equal(0, firstReader.Session.Releases);
        firstReader.ReadContinue.TrySetResult(); await first;
        var failingGate = new SystemToolAdmissionGate(); var failing = new Reader(files) { OpenFails = true };
        result = await new SystemRepairRecovery(failing, failingGate).RunAsync(); Assert.False(result.OwnershipReleased); Assert.True(failingGate.RequiresRetention);
    }

    [Fact]
    public async Task SharedGateRootsDepartedReaderAndActualTaskUntilItsOwnedStreamSettles()
    {
        using var files = new Files("terminal"); var gate = new SystemToolAdmissionGate();
        var references = await Depart(files, gate);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(references.Reader.TryGetTarget(out var reader)); Assert.True(references.Run.TryGetTarget(out var run));
        Assert.True(gate.HasPendingWork); Assert.Equal(0, reader!.Session!.Releases);
        reader.ReadContinue.TrySetResult(); var result = await run!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.OwnershipReleased); Assert.False(gate.HasPendingWork); Assert.False(gate.RequiresRetention);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<Reader> Reader, WeakReference<Task<SystemRepairRecoveryResult>> Run)> Depart(Files files, SystemToolAdmissionGate gate)
    {
        var reader = new Reader(files) { HoldRead = true }; var run = new SystemRepairRecovery(reader, gate).RunAsync();
        await reader.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); return (new(reader), new(run));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Reader(Files files) : ISystemRepairRecoveryReader
    {
        public bool Enabled = true, HoldRead, HoldRelease, ReadFails, ReleaseFails, OpenFails, KnownUnavailable;
        public int Opens; public ReadSession? Session, Foreign; public CancellationToken EntryToken;
        public readonly TaskCompletionSource ReadEntered = Signal(), ReadContinue = Signal(), ReleaseEntered = Signal(), ReleaseContinue = Signal();
        public bool ProductionReviewValidated => Enabled;
        public Task<SystemRepairRecoveryReadSession> OpenAsync(SystemRepairRecoveryContext context, CancellationToken token)
        {
            Opens++; EntryToken = token;
            if (OpenFails) throw new IOException("Controlled open ownership uncertainty.");
            if (Foreign != null) return Task.FromResult<SystemRepairRecoveryReadSession>(Foreign);
            Session = new(this, files, context); return Task.FromResult<SystemRepairRecoveryReadSession>(Session);
        }
        public sealed class ReadSession(Reader reader, Files files, SystemRepairRecoveryContext context) : SystemRepairRecoveryReadSession(context)
        {
            public FileStream? Stream; public int Reads, Releases;
            protected override async Task<SystemToolJournalHistory> ReadHistoryAsync()
            {
                Reads++;
                if (reader.KnownUnavailable) return SystemToolJournalReconciliation.Review(null, false, DateTimeOffset.UtcNow);
                Stream = new(files.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                reader.ReadEntered.TrySetResult(); if (reader.HoldRead) await reader.ReadContinue.Task;
                if (reader.ReadFails) throw new IOException("Controlled late read failure.");
                using var bytes = new MemoryStream(); await Stream.CopyToAsync(bytes);
                return SystemToolJournalReconciliation.Review(bytes.Length == 0 ? [] : [SystemToolJournal.DecodeSnapshots(bytes.ToArray(), DateTimeOffset.UtcNow)], true, DateTimeOffset.UtcNow);
            }
            protected override async Task<bool> ReleaseOwnershipAsync()
            {
                Releases++; reader.ReleaseEntered.TrySetResult(); if (reader.HoldRelease) await reader.ReleaseContinue.Task;
                if (reader.ReleaseFails) return false;
                Stream?.Dispose(); return true;
            }
        }
    }
    private sealed class Files : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "madclean-recovery-" + Guid.NewGuid().ToString("N"));
        public string Path { get; } public byte[] Original { get; }
        public Files(string content)
        {
            Directory.CreateDirectory(root); Path = System.IO.Path.Combine(root, "owned.journal");
            var now = DateTimeOffset.UtcNow.AddSeconds(-1); var id = Guid.NewGuid();
            var first = new SystemToolJournalEntry(id, SystemToolOperation.DismRestoreHealth, 1, now, SystemToolJournalPhase.DispatchIntent, null, null, null, null);
            var entries = new List<SystemToolJournalEntry> { first };
            using var stream = new MemoryStream();
            if (content is "unresolved" or "terminal")
            {
                Append();
                if (content == "terminal") { entries.Add(first with { Sequence = 2, Phase = SystemToolJournalPhase.LaunchDenied, NativeError = 1223 }); Append(); }
                void Append() { Assert.True(SystemToolJournal.TryEncode(entries, DateTimeOffset.UtcNow, out var bytes)); stream.Write(bytes!); stream.WriteByte((byte)'\n'); }
            }
            else if (content == "malformed") stream.Write("partial-tail"u8);
            Original = stream.ToArray(); File.WriteAllBytes(Path, Original);
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
