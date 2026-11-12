using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// One cooperating in-process admission slot for the selected component-store
/// routes and dormant SFC candidate. Not a cross-process lock or native proof.
/// The disabled legacy executor does not participate and must not be enabled
/// without its own admission/ownership integration and qualification.
/// </summary>
internal static class SystemToolServicingAdmission
{
    private static readonly SystemToolAdmissionGate Gate = new();
    internal static bool HasPendingWork => Gate.HasPendingWork;
    internal static bool RequiresRetention => Gate.RequiresRetention;
    internal static bool IsHistoryBlocked => Gate.IsHistoryBlocked;
    internal static SystemToolAdmissionLease? TryAcquire(SystemToolOperation operation, Guid requestId)
        => Gate.TryAcquire(operation, requestId);

    // Candidate repairs use this exact cooperating gate through a separate
    // opaque-context route. Existing non-repair acquisition remains unchanged.
    // The platform's immutable production guard is checked before acquisition.
    internal static SystemRepairServicingBackend CreateRepairBackend(ISystemRepairExecutionPlatform platform)
        => new(platform, Gate);

    // Existing-history review participates in the same slot. Construction does
    // not start a read or bypass the reader's independent qualification guard.
    internal static SystemRepairRecovery CreateRepairRecovery(ISystemRepairRecoveryReader reader)
        => new(reader, Gate);
}
