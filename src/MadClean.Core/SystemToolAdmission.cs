namespace MadClean.Core;

/// <summary>
/// Non-queuing admission for cooperating tool owners sharing this gate instance.
/// Admission is not permission, native ownership proof or a cross-process lock.
/// </summary>
public sealed class SystemToolAdmissionGate
{
    private readonly object sync = new();
    private SystemToolAdmissionLease? owner;
    private bool pending, retained;

    public bool HasPendingWork { get { lock (sync) return pending; } }
    public bool RequiresRetention { get { lock (sync) return retained; } }

    public SystemToolAdmissionLease? TryAcquire(SystemToolOperation operation, Guid requestId)
    {
        if (requestId == Guid.Empty || operation is not (SystemToolOperation.DismCheckHealth or
            SystemToolOperation.DismScanHealth or SystemToolOperation.SfcVerifyOnly)) return null;
        lock (sync)
        {
            if (owner != null) return null;
            var acquired = new SystemToolAdmissionLease(this, operation, requestId);
            owner = acquired;
            pending = true;
            return acquired;
        }
    }

    internal void Complete(SystemToolAdmissionLease lease, bool ownershipReleased)
    {
        lock (sync)
        {
            // Exact lease identity, not a reusable request GUID, identifies this slot.
            // Only the first completion is accepted; a stale callback cannot alter
            // a successor or rehabilitate a terminal retained owner.
            if (!ReferenceEquals(owner, lease) || !pending) return;
            pending = false;
            if (ownershipReleased) owner = null;
            else retained = true;
        }
    }
}

/// <summary>
/// An admission capability held by its actual tool owner. The owner must explicitly
/// complete with its real release observation; disposal, cancellation and GC never
/// release admission. A false first completion permanently retains this gate.
/// </summary>
public sealed class SystemToolAdmissionLease
{
    private readonly SystemToolAdmissionGate gate;
    internal SystemToolAdmissionLease(SystemToolAdmissionGate gate, SystemToolOperation operation, Guid requestId)
    {
        this.gate = gate;
        Operation = operation;
        RequestId = requestId;
    }

    public SystemToolOperation Operation { get; }
    public Guid RequestId { get; }
    public void Complete(bool ownershipReleased) => gate.Complete(this, ownershipReleased);
}
