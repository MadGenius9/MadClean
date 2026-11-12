using System.Security.Principal;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Fixed native repair integration candidate. There is deliberately no runtime,
/// configuration, caller or fixture override for its closed production boundary.
/// </summary>
public sealed class WindowsSystemRepairBackend : ISystemRepairWorkflowBackend
{
    private readonly SystemRepairServicingBackend backend =
        SystemToolServicingAdmission.CreateRepairBackend(new NativePlatform());

    public bool ProductionExecutionValidated => false;
    public bool HasPendingWork => backend.HasPendingWork || NativePlatform.HasPendingWork;
    public bool RequiresRetention => backend.RequiresRetention || NativePlatform.RequiresRetention;
    public bool IsHistoryBlocked => backend.IsHistoryBlocked;

    public Task<SystemRepairJournalSession> OpenJournalAsync(SystemRepairExecutionContext context,
        CancellationToken preDispatchToken)
    {
        // This boundary precedes context, cancellation, shared admission and all OS work.
        if (!ProductionExecutionValidated) return Disabled<SystemRepairJournalSession>();
        return backend.OpenJournalAsync(context, preDispatchToken);
    }

    public Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit)
    {
        if (!ProductionExecutionValidated) return Disabled<SystemRepairDispatchSession>();
        return backend.DispatchAsync(permit);
    }

    private static Task<T> Disabled<T>() => Task.FromException<T>(new InvalidOperationException(
        "Native repair execution is disabled pending separate qualification. No Windows operation was requested."));

    private sealed class NativePlatform : ISystemRepairExecutionPlatform
    {
        private static readonly object RootSync = new();
        private static Prepared? rooted;
        public bool ProductionExecutionValidated => false;
        internal static bool HasPendingWork { get { lock (RootSync) return rooted?.HasPendingWork == true; } }
        internal static bool RequiresRetention { get { lock (RootSync) return rooted?.RequiresRetention == true; } }

        public Task<SystemRepairPreparedSession> PrepareAsync(SystemRepairExecutionContext context,
            CancellationToken preDispatchToken)
        {
            // Both independent source gates precede platform/token/path/identity/worker access.
            if (!ProductionExecutionValidated || !WindowsSystemRepairDirectLaunchApi.ProductionExecutionValidated ||
                !SystemToolJournalPrivateStore.ProductionCreationValidated)
                return Disabled<SystemRepairPreparedSession>();
            if (context == null || context.Operation is not (SystemToolOperation.DismRestoreHealth or SystemToolOperation.SfcScanNow))
                return Task.FromException<SystemRepairPreparedSession>(new InvalidOperationException("The fixed repair context is unavailable."));
            if (preDispatchToken.IsCancellationRequested)
                return Task.FromCanceled<SystemRepairPreparedSession>(preDispatchToken);
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                return Task.FromException<SystemRepairPreparedSession>(new PlatformNotSupportedException("Native repair preparation requires 64-bit Windows."));
            Prepared owner;
            lock (RootSync)
            {
                if (rooted?.CanRetire == true) rooted = null;
                if (rooted != null || WindowsSystemRepairJournal.HasPendingWork || WindowsSystemRepairJournal.RequiresRetention ||
                    SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty)
                    return Task.FromException<SystemRepairPreparedSession>(new InvalidOperationException("Earlier native ownership remains pending or uncertain."));
                owner = new Prepared(context);
                rooted = owner; // Root actual mutable resources before scheduling or callbacks.
            }
            return owner.SchedulePreparation(preDispatchToken);
        }

        internal static void Settled(Prepared owner)
        {
            lock (RootSync)
                if (ReferenceEquals(rooted, owner) && owner.CanRetire) rooted = null;
        }
    }

    private sealed class Prepared(SystemRepairExecutionContext context) : SystemRepairPreparedSession(context)
    {
        private readonly object sync = new();
        private SystemToolTrustedLease? identity;
        private SystemRepairJournalSession? journal;
        private WindowsSystemRepairDirectLaunchApi? directApi;
        private readonly SystemRepairDirectLaunchDomain directDomain = new();
        private SystemRepairDirectLaunchSession? directSession;
        private Task<SystemRepairPreparedSession>? preparation;
        private Task<SystemRepairJournalSession>? journalOpening;
        private Task<SystemRepairDispatchSession>? dispatch;
        private Task<bool>? finalRelease;
        private bool retained, identityDisposeAttempted, identityDisposed, cleanupConfirmed;

        public override SystemRepairJournalSession Journal
        {
            get { lock (sync) return journal ?? throw new InvalidOperationException("The owned repair journal is unavailable."); }
        }
        internal bool HasPendingWork
        {
            get
            {
                lock (sync)
                    return preparation is { IsCompleted: false } || journalOpening is { IsCompleted: false } ||
                        dispatch is { IsCompleted: false } || finalRelease is { IsCompleted: false } ||
                        directDomain.HasPendingWork || identity != null && !identityDisposed;
            }
        }
        internal bool RequiresRetention { get { lock (sync) return retained || directDomain.RequiresRetention; } }
        internal bool CanRetire
        {
            get
            {
                lock (sync)
                    return cleanupConfirmed && !retained && !directDomain.RequiresRetention && !directDomain.HasPendingWork &&
                        finalRelease is { IsCompletedSuccessfully: true } && finalRelease.Result &&
                        preparation is { IsCompletedSuccessfully: true } && (dispatch == null || dispatch.IsCompletedSuccessfully);
            }
        }

        internal Task<SystemRepairPreparedSession> SchedulePreparation(CancellationToken token)
        {
            Task<SystemRepairPreparedSession> work;
            lock (sync)
            {
                try
                {
                    work = new Task<SystemRepairPreparedSession>(() => PrepareWorker(token), CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach);
                    preparation = work;
                    ObserveTask(work);
                }
                catch { retained = true; throw; }
            }
            StartOwned(work);
            return work;
        }

        private SystemRepairPreparedSession PrepareWorker(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!AlreadyAdministrator()) throw new InvalidOperationException("An already-effective nonimpersonated administrator is required. No elevation was requested.");
                token.ThrowIfCancellationRequested();
                var definition = FixedDefinition();
                var held = SystemToolTrustedLease.Open(definition, token);
                lock (sync) identity = held; // Retain the actual lease inside its worker before returning/awaiting.
                if (held.ToolVersion == "Unknown" || held.EmbeddedVersionObservation.State != SystemToolEmbeddedVersionState.Matched ||
                    !SystemToolActionsPolicy.ValidHash(held.Sha256))
                    throw new InvalidOperationException("The held fixed repair executable identity is incomplete.");
                token.ThrowIfCancellationRequested();
                // Journal is last. Once an actual session is returned, the Core owner handles
                // precommit cancellation and its separate journal/preparation release ordering.
                var opening = WindowsSystemRepairJournal.OpenForSelectedActionAsync(Context, token);
                lock (sync) journalOpening = opening;
                var opened = opening.GetAwaiter().GetResult();
                lock (sync) journal = opened;
                return this;
            }
            catch
            {
                // An unreturned/uncertain journal is never cleaned as a foreign owner.
                // The prepared owner and known identity stay rooted; no repair was dispatched.
                lock (sync) retained = true;
                throw;
            }
        }

        public override Task<SystemRepairDispatchSession> DispatchAsync(SystemRepairDispatchPermit permit)
        {
            // No alternate internal/caller route enables the native process boundary.
            if (!new NativePlatform().ProductionExecutionValidated || !WindowsSystemRepairDirectLaunchApi.ProductionExecutionValidated ||
                !SystemToolJournalPrivateStore.ProductionCreationValidated)
                return Disabled<SystemRepairDispatchSession>();
            Task<SystemRepairDispatchSession> work;
            lock (sync)
            {
                if (retained || dispatch != null || preparation is not { IsCompletedSuccessfully: true } ||
                    journal == null || identity == null || permit == null || !ReferenceEquals(permit.Context, Context))
                    return Task.FromException<SystemRepairDispatchSession>(new InvalidOperationException("The exact prepared repair dispatch is unavailable or already used."));
                try
                {
                    work = new Task<SystemRepairDispatchSession>(() => DispatchWorker(permit), CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach);
                    dispatch = work;
                    ObserveTask(work);
                }
                catch { retained = true; throw; }
            }
            StartOwned(work);
            return work;
        }

        private SystemRepairDispatchSession DispatchWorker(SystemRepairDispatchPermit permit)
        {
            try
            {
                if (!AlreadyAdministrator()) throw new InvalidOperationException("The caller's administrator context changed before dispatch.");
                var held = identity ?? throw new InvalidOperationException("The retained executable identity is unavailable.");
                _ = FixedDefinition();
                var api = new WindowsSystemRepairDirectLaunchApi(Context, held);
                lock (sync) directApi = api;
                // The actual API/target/domain are rooted by this prepared owner before
                // Start can schedule the kernel. It alone claims the one-shot permit.
                var session = new SystemRepairDirectLaunch(api, directDomain).Start(permit, api.HeldTarget);
                lock (sync) directSession = session;
                return SystemRepairDirectDispatchSession.Create(permit, session);
            }
            catch { lock (sync) retained = true; throw; }
        }

        public override Task<bool> ReleaseOwnershipAsync()
        {
            Task<bool> work;
            lock (sync)
            {
                if (finalRelease != null) return finalRelease;
                try
                {
                    work = new Task<bool>(() => ReleasePreparationWorker(), CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                    finalRelease = work; ObserveTask(work);
                }
                catch { retained = true; throw; }
            }
            StartOwned(work);
            return work;
        }

        private bool ReleasePreparationWorker()
        {
            try
            {
                preparation?.GetAwaiter().GetResult();
                dispatch?.GetAwaiter().GetResult();
                var actual = directSession;
                if (actual != null && !actual.ReleaseOwnershipAsync().GetAwaiter().GetResult())
                { lock (sync) retained = true; return false; }
                bool journalUnsettled = WindowsSystemRepairJournal.HasPendingWork || WindowsSystemRepairJournal.RequiresRetention;
                bool trustUnsettled = SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty;
                SystemToolTrustedLease held;
                lock (sync)
                {
                    if (retained || directDomain.HasPendingWork || directDomain.RequiresRetention ||
                        journalUnsettled || trustUnsettled || identityDisposeAttempted || identity == null)
                        return false;
                    identityDisposeAttempted = true;
                    held = identity;
                }
                held.Dispose();
                trustUnsettled = SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty;
                lock (sync)
                {
                    identityDisposed = true;
                    if (trustUnsettled)
                    { retained = true; return false; }
                    cleanupConfirmed = true;
                    return true;
                }
            }
            catch { lock (sync) retained = true; return false; }
        }

        private SystemToolActionDefinition FixedDefinition()
        {
            var definition = SystemToolActionsCatalog.Find(Context.Operation);
            if (definition == null || definition.Kind != SystemToolActionKind.Repair ||
                Context.Operation is not (SystemToolOperation.DismRestoreHealth or SystemToolOperation.SfcScanNow) ||
                definition.FileName != (Context.Operation == SystemToolOperation.DismRestoreHealth ? "Dism.exe" : "sfc.exe") ||
                !definition.Arguments.SequenceEqual(FixedArguments(Context.Operation)))
                throw new InvalidOperationException("The selected fixed repair definition is unavailable.");
            return definition;
        }

        private static IReadOnlyList<string> FixedArguments(SystemToolOperation operation) => operation switch
        {
            SystemToolOperation.DismRestoreHealth => ["/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart"],
            SystemToolOperation.SfcScanNow => ["/scannow"],
            _ => throw new InvalidOperationException("Unsupported fixed repair operation.")
        };

        private void ObserveTask(Task task)
        {
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted) { _ = completed.Exception; lock (sync) retained = true; }
                NativePlatform.Settled(this);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void StartOwned(Task task)
        {
            try { task.Start(TaskScheduler.Default); }
            catch { lock (sync) retained = true; throw; }
        }

    }

    private static bool AlreadyAdministrator()
    {
        using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true))
            if (impersonated != null) return false;
        using var current = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator);
    }

}
