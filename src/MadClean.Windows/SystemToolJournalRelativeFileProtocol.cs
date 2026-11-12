namespace MadClean.Windows;

internal enum RelativeJournalFileOpenMode { CreateNew, OpenExisting }
internal sealed record RelativeJournalFileRequest(string Leaf, RelativeJournalFileOpenMode Mode, bool ReadOnly);
internal enum RelativeJournalFileState { RefusedBeforeEntry, Held, Released, Retained }

/// <summary>
/// Trusted fixed-parent adapter. Prepare owns only its resource prefix; Invoke is the
/// file-entry boundary. No raw caller flags, arbitrary path or failure-output ownership is supplied.
/// </summary>
internal abstract class RelativeJournalFileFrame
{
    private int claimed;
    internal bool TryClaim() => Interlocked.CompareExchange(ref claimed, 1, 0) == 0;
    internal abstract bool Prepare(RelativeJournalFileRequest request, CancellationToken token);
    internal abstract int Invoke();
    internal abstract bool CaptureSuccess();
    internal abstract bool ReleaseKnownOwnership();
}

/// <summary>
/// One synchronous worker's cooperating ownership domain. The caller must root this
/// domain, its frame and the original parent-tree owner. Retention has no reset or retry.
/// </summary>
internal sealed class RelativeJournalFileDomain
{
    internal const int MaximumDependencyDepth = 8;
    private readonly object sync = new();
    private RelativeJournalFileAttempt? owner;
    private bool retained;
    internal bool HasPendingWork { get { lock (sync) return owner?.HasPendingWork == true; } }
    internal bool HasHeldOwnership { get { lock (sync) return owner?.State == RelativeJournalFileState.Held; } }
    internal bool RequiresRetention { get { lock (sync) return retained || owner?.RequiresRetention == true; } }

    internal static bool IsSupported(RelativeJournalFileRequest? request)
    {
        if (request == null || !Enum.IsDefined(request.Mode) || request.Mode == RelativeJournalFileOpenMode.CreateNew && request.ReadOnly)
            return false;
        string leaf = request.Leaf;
        return leaf == "lease.bin" || leaf is { Length: 38 } && leaf.EndsWith(".jsonl", StringComparison.Ordinal) &&
            Guid.TryParseExact(leaf[..32], "N", out var id) && id != Guid.Empty && id.ToString("N") + ".jsonl" == leaf;
    }
    internal RelativeJournalFileAttempt Open(RelativeJournalFileRequest? request, RelativeJournalFileFrame frame,
        CancellationToken token = default) => OpenCore(request, frame, null, token);

    // Ownership dependency only: this does not prove a native RootDirectory/path relation.
    // The actual native factory must still bind its exact qualified handle and request.
    internal RelativeJournalFileAttempt OpenDependent(RelativeJournalFileRequest? request, RelativeJournalFileFrame frame,
        RelativeJournalFileAttempt parent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return OpenCore(request, frame, parent, token);
    }

    private RelativeJournalFileAttempt OpenCore(RelativeJournalFileRequest? request, RelativeJournalFileFrame frame,
        RelativeJournalFileAttempt? parent, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var actual = new RelativeJournalFileAttempt(this, frame, request, parent);
        if (!IsSupported(request)) return actual; // Unclaimed preparation remains the caller's responsibility.
        lock (sync)
        {
            if (owner != null || retained) return actual;
            owner = actual; // Root the real frame before even its one-shot claim or callbacks.
        }
        if (!actual.AttachDependency()) { Complete(actual, true); return actual; }
        if (!frame.TryClaim()) { actual.RefuseUnclaimed(); return actual; }
        actual.OpenOwned(token);
        return actual;
    }
    internal void Complete(RelativeJournalFileAttempt actual, bool released)
    {
        lock (sync)
        {
            if (!ReferenceEquals(owner, actual)) return;
            if (released) owner = null;
            else retained = true;
        }
    }
}

/// <summary>
/// Held successful output or retained uncertainty. EntryAttempted means Invoke was called,
/// not that a throwing adapter established native entry. Release concerns this child/prefix
/// and parent borrow only; it never releases the caller's original parent tree or rolls back a file.
/// </summary>
internal sealed class RelativeJournalFileAttempt
{
    private readonly object sync = new();
    private readonly RelativeJournalFileDomain domain;
    private readonly RelativeJournalFileFrame frame;
    private readonly RelativeJournalFileRequest? request;
    private readonly RelativeJournalFileAttempt? dependency;
    private RelativeJournalFileAttempt? dependentChild;
    private int dependencyAttached;
    private bool opened, openActive, releaseActive, releaseAttempted, ownershipReleased, captured;
    private int? nativeStatus;
    private bool entryAttempted;
    private RelativeJournalFileState state = RelativeJournalFileState.RefusedBeforeEntry;
    internal RelativeJournalFileAttempt(RelativeJournalFileDomain domain, RelativeJournalFileFrame frame,
        RelativeJournalFileRequest? request, RelativeJournalFileAttempt? dependency = null)
    {
        this.domain = domain; this.frame = frame; this.request = request; this.dependency = dependency;
        DependencyDepth = dependency == null ? 0 : dependency.DependencyDepth + 1;
    }
    internal int DependencyDepth { get; }
    internal RelativeJournalFileState State { get { lock (sync) return state; } }
    internal int? NativeStatus { get { lock (sync) return nativeStatus; } }
    internal bool EntryAttempted { get { lock (sync) return entryAttempted; } }
    internal bool Captured { get { lock (sync) return captured; } }
    internal bool OwnershipReleased { get { lock (sync) return ownershipReleased; } }
    internal bool HasPendingWork { get { lock (sync) return openActive || releaseActive || dependentChild?.HasPendingWork == true; } }
    internal bool RequiresRetention { get { lock (sync) return state == RelativeJournalFileState.Retained || dependentChild?.RequiresRetention == true; } }

    internal bool AttachDependency()
    {
        if (dependency == null) return true;
        if (DependencyDepth > RelativeJournalFileDomain.MaximumDependencyDepth || !dependency.AttachChild(this)) return false;
        Volatile.Write(ref dependencyAttached, 1);
        return true;
    }
    private bool AttachChild(RelativeJournalFileAttempt child)
    {
        lock (sync)
        {
            if (state != RelativeJournalFileState.Held || !captured || openActive || releaseActive || releaseAttempted || dependentChild != null)
                return false;
            // Retain the actual child/frame/domain, not just a counter or a caller flag.
            dependentChild = child;
            return true;
        }
    }
    private bool DetachDependency()
    {
        if (dependency == null || Interlocked.Exchange(ref dependencyAttached, 0) == 0) return true;
        lock (dependency.sync)
        {
            if (!ReferenceEquals(dependency.dependentChild, this)) return false;
            dependency.dependentChild = null;
            return true;
        }
    }
    internal void RefuseUnclaimed()
    {
        // No frame prefix belongs to this failed claim. Only release its managed borrow.
        if (DetachDependency()) domain.Complete(this, true);
        else Retain();
    }

    internal void OpenOwned(CancellationToken token)
    {
        lock (sync)
        {
            if (opened) return;
            opened = openActive = true;
        }
        try
        {
            if (token.IsCancellationRequested) { ReleaseKnown(); return; }
            bool prepared;
            try { prepared = frame.Prepare(request!, token); }
            catch { ReleaseKnown(); return; }
            if (!prepared || token.IsCancellationRequested) { ReleaseKnown(); return; }
            lock (sync) entryAttempted = true;
            int observed;
            try { observed = frame.Invoke(); }
            catch { Retain(); return; }
            lock (sync) nativeStatus = observed;
            // Only exact zero has a documented known-success output contract here.
            // In particular, STATUS_PENDING or another NT-success value is not adopted.
            if (observed != 0) { Retain(); return; }
            bool qualified;
            try { qualified = frame.CaptureSuccess(); }
            catch { qualified = false; }
            if (!qualified) { ReleaseKnown(); return; }
            lock (sync) { captured = true; state = RelativeJournalFileState.Held; }
        }
        finally { lock (sync) openActive = false; }
    }
    internal bool Release()
    {
        lock (sync)
        {
            // Refusal during a callback is not a cleanup attempt and does not consume
            // a later legitimate held release. No callback runs under this state lock.
            if (openActive || releaseActive || dependentChild != null) return false;
            if (releaseAttempted) return ownershipReleased;
            if (!opened || state != RelativeJournalFileState.Held) return false;
        }
        return ReleaseKnown();
    }
    private bool ReleaseKnown()
    {
        lock (sync)
        {
            // Recheck at the release commitment, atomically with child admission.
            // A refused early parent release must not consume its legitimate later one.
            if (releaseActive || dependentChild != null) return false;
            if (releaseAttempted) return ownershipReleased;
            releaseAttempted = releaseActive = true;
        }
        bool released;
        try { released = frame.ReleaseKnownOwnership(); }
        catch { released = false; }
        if (released) released = DetachDependency();
        lock (sync)
        {
            ownershipReleased = released;
            releaseActive = false;
            state = released ? entryAttempted ? RelativeJournalFileState.Released : RelativeJournalFileState.RefusedBeforeEntry
                : RelativeJournalFileState.Retained;
        }
        domain.Complete(this, released);
        return released;
    }
    private void Retain()
    {
        lock (sync) state = RelativeJournalFileState.Retained;
        domain.Complete(this, false);
    }
}
