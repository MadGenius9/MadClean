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
    internal static SystemToolAdmissionLease? TryAcquire(SystemToolOperation operation, Guid requestId)
        => Gate.TryAcquire(operation, requestId);
}
