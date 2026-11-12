using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Bound storage adapter only. Production creation remains closed; this type does
/// not implement repair dispatch, App wiring or a new native qualification mode.
/// </summary>
internal static class WindowsSystemRepairJournal
{
    private static readonly object RootSync = new();
    private static Owner? rootedOwner;

    internal static bool HasPendingWork
    {
        get { lock (RootSync) return rootedOwner?.HasPendingWork == true; }
    }
    internal static bool RequiresRetention
    {
        get { lock (RootSync) return rootedOwner?.RequiresRetention == true; }
    }

    internal static Task<SystemRepairJournalSession> OpenForSelectedActionAsync(
        SystemRepairExecutionContext context, CancellationToken token = default)
    {
        // The existing immutable production gate is checked before platform,
        // profile paths, native state, ownership admission or worker scheduling.
        if (!SystemToolJournalPrivateStore.ProductionCreationValidated)
            return Task.FromException<SystemRepairJournalSession>(
                new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported));
        return OpenAsync(context, null, null, token);
    }

    internal static Task<SystemRepairJournalSession> OpenOwnedFixtureAsync(
        SystemRepairExecutionContext context, string root, string marker, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(marker))
            return Task.FromException<SystemRepairJournalSession>(
                new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported));
        return OpenAsync(context, root, marker, token);
    }

    private static Task<SystemRepairJournalSession> OpenAsync(SystemRepairExecutionContext context,
        string? root, string? marker, CancellationToken token)
    {
        if (context == null || context.ExecutionId == Guid.Empty || context.Request.RequestId == Guid.Empty ||
            context.Operation is not (SystemToolOperation.DismRestoreHealth or SystemToolOperation.SfcScanNow))
            return Task.FromException<SystemRepairJournalSession>(
                new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported));
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || token.IsCancellationRequested)
            return token.IsCancellationRequested
                ? Task.FromCanceled<SystemRepairJournalSession>(token)
                : Task.FromException<SystemRepairJournalSession>(
                    new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported));

        lock (RootSync)
        {
            RetireReleasedOwner();
            if (rootedOwner != null || SystemToolJournalPrivateStore.HasUncertainCleanup)
                return Task.FromException<SystemRepairJournalSession>(
                    new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.StoreBusy));

            var owner = new Owner(context);
            // Root the mutable actual resource owner before any worker can run.
            rootedOwner = owner;
            return owner.ScheduleOpen(root, marker, token);
        }
    }

    private static void RetireReleasedOwner()
    {
        if (rootedOwner?.CanRetire == true) rootedOwner = null;
    }

    private static void OnWorkerSettled(Owner owner)
    {
        lock (RootSync)
        {
            if (ReferenceEquals(rootedOwner, owner) && owner.CanRetire) rootedOwner = null;
        }
    }

    private sealed class Owner(SystemRepairExecutionContext context) : ISystemRepairSnapshotStore
    {
        private readonly object sync = new();
        private SystemToolJournalPrivateStore? store;
        private SystemRepairJournalSession? session;
        private Task<SystemRepairJournalSession>? openTask;
        private Task<SystemToolJournalReview>? writeTask;
        private Task<bool>? releaseTask;
        private IReadOnlyList<SystemToolJournalEntry> accepted = Array.Empty<SystemToolJournalEntry>();
        private bool openFailed, cleanupConfirmed, cleanupAttempted, closing, writeFailed, retained;

        public SystemRepairExecutionContext Context { get; } = context;

        internal bool HasPendingWork
        {
            get
            {
                lock (sync)
                    return openTask is { IsCompleted: false } || writeTask is { IsCompleted: false } ||
                        releaseTask is { IsCompleted: false } || store != null && !cleanupAttempted;
            }
        }
        internal bool RequiresRetention
        {
            get { lock (sync) return retained || SystemToolJournalPrivateStore.HasUncertainCleanup; }
        }
        internal bool CanRetire
        {
            get
            {
                lock (sync)
                    return !retained && !SystemToolJournalPrivateStore.HasUncertainCleanup && cleanupConfirmed &&
                        (openFailed && openTask is { IsCompleted: true } ||
                         releaseTask is { IsCompletedSuccessfully: true } && releaseTask.Result);
            }
        }

        internal Task<SystemRepairJournalSession> ScheduleOpen(string? root, string? marker, CancellationToken token)
        {
            lock (sync)
            {
                try
                {
                    var work = new Task<SystemRepairJournalSession>(() => OpenWorker(root, marker, token),
                        CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                    openTask = work;
                    ObserveSettlement(work);
                    work.Start(TaskScheduler.Default);
                    return work;
                }
                catch
                {
                    // Even an unexpected scheduling failure cannot unroot an
                    // uncertain task/resource owner or permit another attempt.
                    retained = true; openFailed = true;
                    throw;
                }
            }
        }

        private SystemRepairJournalSession OpenWorker(string? root, string? marker, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var opened = root == null
                    ? SystemToolJournalPrivateStore.OpenForSelectedAction(token)
                    : marker != null
                        ? SystemToolJournalPrivateStore.OpenOwnedFixture(root, marker, true, token)
                        : throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
                lock (sync) store = opened;
                token.ThrowIfCancellationRequested();
                var result = new SystemRepairStoredJournalSession(Context, this);
                lock (sync) session = result;
                return result;
            }
            catch
            {
                lock (sync) openFailed = true;
                CleanupOnce();
                throw;
            }
        }

        public Task<SystemToolJournalReview> PersistSnapshotAsync(IReadOnlyList<SystemToolJournalEntry> snapshot)
        {
            var now = DateTimeOffset.UtcNow;
            if (!TryFreeze(snapshot, now, out var frozen))
            {
                lock (sync) writeFailed = true;
                return Task.FromResult(Unknown());
            }
            lock (sync)
            {
                if (closing || writeFailed || retained || store == null || openTask is not { IsCompletedSuccessfully: true } ||
                    writeTask is { IsCompleted: false } || frozen.Count != accepted.Count + 1 ||
                    accepted.Where((entry, index) => entry != frozen[index]).Any())
                {
                    writeFailed = true;
                    return Task.FromResult(Unknown());
                }
                try
                {
                    var work = new Task<SystemToolJournalReview>(() => PersistWorker(frozen),
                        CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                    writeTask = work;
                    ObserveSettlement(work);
                    work.Start(TaskScheduler.Default);
                    return work;
                }
                catch { retained = true; writeFailed = true; throw; }
            }
        }

        private SystemToolJournalReview PersistWorker(IReadOnlyList<SystemToolJournalEntry> frozen)
        {
            try
            {
                SystemToolJournalPrivateStore current;
                lock (sync) current = store ?? throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
                if (SystemToolJournalPrivateStore.HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
                var now = DateTimeOffset.UtcNow;
                // Existing BeginIntent reconciles the whole bounded history before
                // CREATE_NEW. Both paths own flush and same-handle readback, and
                // never truncate/retry a malformed or uncertain tail.
                var observed = frozen.Count == 1
                    ? current.BeginIntent(frozen[0], now, CancellationToken.None)
                    : current.AppendSnapshot(frozen, now, CancellationToken.None);
                if (!ExactReadback(frozen, observed, DateTimeOffset.UtcNow))
                {
                    lock (sync) writeFailed = true;
                    return Unknown();
                }
                lock (sync)
                {
                    if (writeFailed || closing || retained || SystemToolJournalPrivateStore.HasUncertainCleanup)
                    {
                        writeFailed = true;
                        retained |= SystemToolJournalPrivateStore.HasUncertainCleanup;
                        return Unknown();
                    }
                    accepted = frozen;
                }
                return observed;
            }
            catch
            {
                lock (sync)
                {
                    writeFailed = true;
                    retained |= SystemToolJournalPrivateStore.HasUncertainCleanup;
                }
                return Unknown();
            }
        }

        public Task<bool> ReleaseOwnershipAsync()
        {
            lock (sync)
            {
                if (releaseTask != null) return releaseTask;
                closing = true;
                try
                {
                    var pendingOpen = openTask;
                    var pendingWrite = writeTask;
                    var work = new Task<bool>(() =>
                    {
                        // Only this background cleanup worker waits. No UI caller is
                        // blocked, and no native handle is closed beneath active IO.
                        try { pendingOpen?.GetAwaiter().GetResult(); } catch { }
                        try { pendingWrite?.GetAwaiter().GetResult(); } catch { }
                        return CleanupOnce();
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                    releaseTask = work;
                    ObserveSettlement(work);
                    work.Start(TaskScheduler.Default);
                    return work;
                }
                catch { retained = true; throw; }
            }
        }

        private bool CleanupOnce()
        {
            SystemToolJournalPrivateStore? current;
            lock (sync)
            {
                if (cleanupAttempted) return cleanupConfirmed;
                cleanupAttempted = true;
                current = store;
            }
            bool completed;
            try
            {
                current?.Dispose();
                completed = !SystemToolJournalPrivateStore.HasUncertainCleanup;
            }
            catch { completed = false; }
            lock (sync)
            {
                cleanupConfirmed = completed;
                retained |= !completed;
                // Keep known native participants on uncertain release. A true
                // release can drop them only after its actual worker settles.
                return completed;
            }
        }

        private void ObserveSettlement(Task work)
        {
            _ = work.ContinueWith(completed =>
            {
                _ = completed.Exception;
                OnWorkerSettled(this);
                GC.KeepAlive(session);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private bool TryFreeze(IReadOnlyList<SystemToolJournalEntry>? snapshot, DateTimeOffset now,
            out IReadOnlyList<SystemToolJournalEntry> frozen)
        {
            frozen = Array.Empty<SystemToolJournalEntry>();
            if (!SystemToolJournal.TryEncode(snapshot, now, out byte[]? bytes) || bytes == null) return false;
            try
            {
                var candidate = SystemToolJournal.Decode(bytes, now);
                if (candidate.State == SystemToolJournalState.Unknown || candidate.Entries.Any(entry =>
                    entry.ExecutionId != Context.ExecutionId || entry.Operation != Context.Operation)) return false;
                frozen = candidate.Entries;
                return true;
            }
            finally { Array.Clear(bytes); }
        }

        private static bool ExactReadback(IReadOnlyList<SystemToolJournalEntry> expected,
            SystemToolJournalReview? observed, DateTimeOffset now)
        {
            if (observed == null || observed.State == SystemToolJournalState.Unknown) return false;
            var actual = SystemToolJournal.Review(observed.Entries, now);
            return actual.State != SystemToolJournalState.Unknown && actual.State == observed.State &&
                actual.Entries.Count == expected.Count && !actual.Entries.Where((entry, index) => entry != expected[index]).Any();
        }

        private static SystemToolJournalReview Unknown() => SystemToolJournal.Review(null, DateTimeOffset.UtcNow);
    }
}
