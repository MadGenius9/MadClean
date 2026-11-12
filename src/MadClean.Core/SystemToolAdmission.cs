namespace MadClean.Core;

/// <summary>
/// Non-queuing admission for cooperating tool owners sharing this gate instance.
/// Admission is not permission, native ownership proof or a cross-process lock.
/// </summary>
public sealed class SystemToolAdmissionGate
{
    private readonly object sync = new();
    private object? owner;
    private bool pending, retained, historyBlocked;

    public bool HasPendingWork { get { lock (sync) return pending; } }
    public bool RequiresRetention { get { lock (sync) return retained; } }
    public bool IsHistoryBlocked { get { lock (sync) return historyBlocked; } }

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

    // Candidate repair coordination is intentionally separate from the public
    // non-repair whitelist. Only the actual reserved opaque context can reach it.
    // This is not native permission or production activation.
    internal SystemToolAdmissionLease? TryAcquireRepair(SystemRepairExecutionContext context, object resourceOwner)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(resourceOwner);
        if (context.ExecutionId == Guid.Empty || context.Request.RequestId == Guid.Empty ||
            context.Operation is not (SystemToolOperation.DismRestoreHealth or SystemToolOperation.SfcScanNow) ||
            !context.Request.IsCurrentReservedContext(context, DateTimeOffset.UtcNow)) return null;
        lock (sync)
        {
            if (owner != null) return null;
            var acquired = new SystemToolAdmissionLease(this, context.Operation, context.Request.RequestId, context, resourceOwner);
            owner = acquired; pending = true; return acquired;
        }
    }

    // Fresh-domain recovery reads take their own identity, never a fabricated tool
    // operation. Existing pending, retained AND history-only owners cannot be reset.
    internal SystemToolRecoveryAdmissionLease? TryAcquireRecovery(object resourceOwner)
    {
        ArgumentNullException.ThrowIfNull(resourceOwner);
        lock (sync)
        {
            if (owner != null) return null;
            var acquired = new SystemToolRecoveryAdmissionLease(this, resourceOwner);
            owner = acquired;
            pending = true;
            return acquired;
        }
    }

    internal void CompleteRecovery(SystemToolRecoveryAdmissionLease lease, object resourceOwner,
        bool ownershipReleased, bool unresolvedHistory)
    {
        lock (sync)
        {
            if (!ReferenceEquals(owner, lease) || !pending || !ReferenceEquals(lease.ResourceOwner, resourceOwner)) return;
            pending = false;
            retained = !ownershipReleased;
            historyBlocked = unresolvedHistory;
            if (ownershipReleased && !unresolvedHistory) owner = null;
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
            if (lease.RepairContext != null)
            {
                // Ordinary completion must never clear an opaque repair owner.
                pending = false; retained = true; return;
            }
            pending = false;
            if (ownershipReleased) owner = null;
            else retained = true;
        }
    }

    internal void CompleteRepair(SystemToolAdmissionLease lease, SystemRepairExecutionContext context,
        object resourceOwner, bool ownershipReleased, bool unresolvedHistory)
    {
        lock (sync)
        {
            if (!ReferenceEquals(owner, lease) || !pending || !ReferenceEquals(lease.RepairContext, context) ||
                !ReferenceEquals(lease.RepairOwner, resourceOwner)) return;
            pending = false;
            retained = !ownershipReleased;
            historyBlocked = unresolvedHistory;
            if (ownershipReleased && !unresolvedHistory) owner = null;
        }
    }
}

internal sealed class SystemToolRecoveryAdmissionLease(SystemToolAdmissionGate gate, object resourceOwner)
{
    internal object ResourceOwner { get; } = resourceOwner;
    internal void Complete(object actualOwner, bool ownershipReleased, bool unresolvedHistory) =>
        gate.CompleteRecovery(this, actualOwner, ownershipReleased, unresolvedHistory);
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
    internal SystemToolAdmissionLease(SystemToolAdmissionGate gate, SystemToolOperation operation, Guid requestId,
        SystemRepairExecutionContext context, object resourceOwner) : this(gate, operation, requestId)
    { RepairContext = context; RepairOwner = resourceOwner; }

    internal SystemRepairExecutionContext? RepairContext { get; }
    internal object? RepairOwner { get; }

    public SystemToolOperation Operation { get; }
    public Guid RequestId { get; }
    public void Complete(bool ownershipReleased) => gate.Complete(this, ownershipReleased);
    internal void CompleteRepair(SystemRepairExecutionContext context, object resourceOwner,
        bool ownershipReleased, bool unresolvedHistory) => gate.CompleteRepair(this, context, resourceOwner, ownershipReleased, unresolvedHistory);
}
