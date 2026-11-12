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
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var actual = new RelativeJournalFileAttempt(this, frame, request);
        if (!IsSupported(request)) return actual; // Unclaimed preparation remains the caller's responsibility.
        lock (sync)
        {
            if (owner != null || retained) return actual;
            owner = actual; // Root the real frame before even its one-shot claim or callbacks.
        }
        if (!frame.TryClaim()) { Complete(actual, true); return actual; }
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
    private bool opened, openActive, releaseActive, releaseAttempted, ownershipReleased, captured;
    private int? nativeStatus;
    private bool entryAttempted;
    private RelativeJournalFileState state = RelativeJournalFileState.RefusedBeforeEntry;
    internal RelativeJournalFileAttempt(RelativeJournalFileDomain domain, RelativeJournalFileFrame frame, RelativeJournalFileRequest? request)
    { this.domain = domain; this.frame = frame; this.request = request; }
    internal RelativeJournalFileState State { get { lock (sync) return state; } }
    internal int? NativeStatus { get { lock (sync) return nativeStatus; } }
    internal bool EntryAttempted { get { lock (sync) return entryAttempted; } }
    internal bool Captured { get { lock (sync) return captured; } }
    internal bool OwnershipReleased { get { lock (sync) return ownershipReleased; } }
    internal bool HasPendingWork { get { lock (sync) return openActive || releaseActive; } }
    internal bool RequiresRetention { get { lock (sync) return state == RelativeJournalFileState.Retained; } }

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
            if (openActive || releaseActive) return false;
            if (releaseAttempted) return ownershipReleased;
            if (!opened || state != RelativeJournalFileState.Held) return false;
        }
        return ReleaseKnown();
    }
    private bool ReleaseKnown()
    {
        lock (sync)
        {
            if (releaseActive) return false;
            if (releaseAttempted) return ownershipReleased;
            releaseAttempted = releaseActive = true;
        }
        bool released;
        try { released = frame.ReleaseKnownOwnership(); }
        catch { released = false; }
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
