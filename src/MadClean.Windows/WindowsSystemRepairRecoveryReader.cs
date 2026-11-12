using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Explicit existing-store recovery review candidate. It neither creates nor
/// changes history, and cannot resume an operation or establish process state.
/// </summary>
public sealed class WindowsSystemRepairRecoveryReader : ISystemRepairRecoveryReader
{
    private static readonly object RootSync = new();
    private static Owner? rooted;

    public bool ProductionReviewValidated => false;
    public bool HasPendingWork { get { lock (RootSync) return rooted?.HasPendingWork == true; } }
    public bool RequiresRetention { get { lock (RootSync) return rooted?.RequiresRetention == true; } }

    public Task<SystemRepairRecoveryReadSession> OpenAsync(SystemRepairRecoveryContext context,
        CancellationToken preEntryToken)
    {
        // This immutable boundary precedes context/token/profile/platform/worker access.
        if (!ProductionReviewValidated)
            return Task.FromException<SystemRepairRecoveryReadSession>(new InvalidOperationException(
                "Existing repair-history review is disabled pending separate qualification. No store was opened."));
        if (context == null || context.Id == Guid.Empty || context.StartedUtc == default ||
            context.StartedUtc.Offset != TimeSpan.Zero || context.StartedUtc > DateTimeOffset.UtcNow)
            return Task.FromException<SystemRepairRecoveryReadSession>(new InvalidOperationException("The exact review context is unavailable."));
        if (preEntryToken.IsCancellationRequested)
            return Task.FromCanceled<SystemRepairRecoveryReadSession>(preEntryToken);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return Task.FromException<SystemRepairRecoveryReadSession>(new PlatformNotSupportedException("Existing private-store review requires 64-bit Windows."));

        Owner owner;
        lock (RootSync)
        {
            if (rooted?.CanRetire == true) rooted = null;
            if (rooted != null || SystemToolJournalPrivateStore.HasUncertainCleanup)
                return Task.FromException<SystemRepairRecoveryReadSession>(new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.StoreBusy));
            owner = new Owner(context);
            rooted = owner; // Root actual mutable store/session/task slots before scheduling.
        }
        // Entry is now owned. A caller stop cannot abandon opening, reading or disposal.
        return owner.ScheduleOpen();
    }

    private static void Settled(Owner owner)
    {
        lock (RootSync)
            if (ReferenceEquals(rooted, owner) && owner.CanRetire) rooted = null;
    }

    private sealed class Owner
    {
        private readonly object sync = new();
        private readonly Session session;
        private SystemToolJournalPrivateStore? store;
        private Task<SystemRepairRecoveryReadSession>? opening;
        private Task<SystemToolJournalHistory>? reading;
        private Task<bool>? releasing;
        private bool knownUnavailable, closing, retained, cleanupAttempted, cleanupConfirmed;

        internal Owner(SystemRepairRecoveryContext context) => session = new Session(context, this);
        internal bool HasPendingWork
        {
            get
            {
                lock (sync)
                    return opening is { IsCompleted: false } || reading is { IsCompleted: false } ||
                        releasing is { IsCompleted: false } || store != null && !cleanupConfirmed;
            }
        }
        internal bool RequiresRetention { get { lock (sync) return retained; } }
        internal bool CanRetire
        {
            get
            {
                lock (sync)
                    return cleanupConfirmed && !retained && opening is { IsCompletedSuccessfully: true } &&
                        (reading == null || reading.IsCompletedSuccessfully) &&
                        releasing is { IsCompletedSuccessfully: true } && releasing.Result;
            }
        }

        internal Task<SystemRepairRecoveryReadSession> ScheduleOpen()
        {
            Task<SystemRepairRecoveryReadSession> work;
            lock (sync)
            {
                try
                {
                    work = new Task<SystemRepairRecoveryReadSession>(OpenWorker, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                    opening = work;
                    Observe(work);
                }
                catch { retained = true; throw; }
            }
            Start(work);
            return work;
        }

        private SystemRepairRecoveryReadSession OpenWorker()
        {
            try
            {
                var opened = SystemToolJournalPrivateStore.OpenExistingForReview(CancellationToken.None);
                lock (sync) store = opened; // Actual returned lease is owned before any result is published.
                if (SystemToolJournalPrivateStore.HasUncertainCleanup)
                { lock (sync) retained = true; }
                return session;
            }
            catch (SystemToolJournalStoreFailure failure)
            {
                bool cleanupUnknown = SystemToolJournalPrivateStore.HasUncertainCleanup;
                lock (sync)
                {
                    // Typed missing/access/busy/unsafe refusals carry no absence claim.
                    // Only the no-returned-store, checked-cleanup case can later release.
                    if (store == null && failure.Reason != SystemToolJournalStoreReason.CleanupUncertain && !cleanupUnknown)
                        knownUnavailable = true;
                    else retained = true;
                }
                return session;
            }
            catch
            {
                lock (sync) retained = true;
                throw; // Unknown native participants stay rooted; no retry or guessed cleanup.
            }
        }

        internal Task<SystemToolJournalHistory> Read()
        {
            Task<SystemToolJournalHistory> work;
            lock (sync)
            {
                if (reading != null) return reading;
                if (closing || opening is not { IsCompletedSuccessfully: true })
                    return Task.FromResult(Unknown());
                try
                {
                    work = new Task<SystemToolJournalHistory>(ReadWorker, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                    reading = work;
                    Observe(work);
                }
                catch { retained = true; throw; }
            }
            Start(work);
            return work;
        }

        private SystemToolJournalHistory ReadWorker()
        {
            SystemToolJournalPrivateStore? current;
            lock (sync)
            {
                if (knownUnavailable || retained) return Unknown();
                current = store;
                if (current == null) { retained = true; return Unknown(); }
            }
            try
            {
                var history = current.ReadHistory(DateTimeOffset.UtcNow, CancellationToken.None);
                if (SystemToolJournalPrivateStore.HasUncertainCleanup)
                { lock (sync) retained = true; return Unknown(); }
                return history;
            }
            catch
            {
                lock (sync) retained = true;
                return Unknown();
            }
        }

        internal Task<bool> Release()
        {
            Task<bool> work;
            Task<SystemRepairRecoveryReadSession>? actualOpen;
            Task<SystemToolJournalHistory>? actualRead;
            lock (sync)
            {
                if (releasing != null) return releasing;
                closing = true;
                actualOpen = opening;
                actualRead = reading;
                try
                {
                    work = new Task<bool>(() => ReleaseWorker(actualOpen, actualRead), CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                    releasing = work;
                    Observe(work);
                }
                catch { retained = true; throw; }
            }
            Start(work);
            return work;
        }

        private bool ReleaseWorker(Task<SystemRepairRecoveryReadSession>? actualOpen, Task<SystemToolJournalHistory>? actualRead)
        {
            try
            {
                // Wait actual workers before disposing the exclusive cooperating lease.
                actualOpen?.GetAwaiter().GetResult();
                actualRead?.GetAwaiter().GetResult();
                SystemToolJournalPrivateStore? current;
                lock (sync)
                {
                    if (retained || cleanupAttempted || actualOpen == null || store == null && !knownUnavailable)
                        return false;
                    cleanupAttempted = true;
                    current = store;
                }
                if (SystemToolJournalPrivateStore.HasUncertainCleanup)
                { lock (sync) retained = true; return false; }
                current?.Dispose();
                bool checkedRelease = !SystemToolJournalPrivateStore.HasUncertainCleanup;
                lock (sync)
                {
                    cleanupConfirmed = checkedRelease;
                    retained |= !checkedRelease;
                }
                return checkedRelease;
            }
            catch { lock (sync) retained = true; return false; }
        }

        private void Observe(Task task)
        {
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted) { _ = completed.Exception; lock (sync) retained = true; }
                Settled(this);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private void Start(Task task)
        {
            try { task.Start(TaskScheduler.Default); }
            catch { lock (sync) retained = true; throw; }
        }
        private static SystemToolJournalHistory Unknown() =>
            SystemToolJournalReconciliation.Review(null, false, DateTimeOffset.UtcNow);

        private sealed class Session(SystemRepairRecoveryContext context, Owner owner) : SystemRepairRecoveryReadSession(context)
        {
            protected override Task<SystemToolJournalHistory> ReadHistoryAsync() => owner.Read();
            protected override Task<bool> ReleaseOwnershipAsync() => owner.Release();
        }
    }
}
