namespace MadClean.Core;

/// <summary>
/// Admission of observations from the owned registered-product request. Consistency
/// is not authentication, uninstall completion, registration absence or permission.
/// </summary>
public static class MsiUninstallResultPolicy
{
    public static bool IsConsistent(MsiUninstallRequestIntent? intent, MsiUninstallRequestResult? result,
        DateTimeOffset nowUtc)
    {
        if (intent == null || result == null || !Utc(nowUtc) || intent.RequestId == Guid.Empty ||
            result.RequestId != intent.RequestId || !Enum.IsDefined(result.State) || !Utc(result.RequestedUtc) ||
            result.RequestedUtc < intent.PreparedUtc || result.RequestedUtc > nowUtc ||
            result.ExitDisposition is { } disposition && !Enum.IsDefined(disposition) ||
            result.ClientProcessId is <= 0) return false;

        bool hasClientFacts = result.StartedUtc.HasValue || result.ExitedUtc.HasValue ||
            result.ClientProcessId.HasValue || result.ExitCode.HasValue || result.ExitDisposition.HasValue;
        bool clientState = result.State is MsiUninstallRequestState.ClientStarted or MsiUninstallRequestState.ClientExited;
        bool preClientState = result.State is MsiUninstallRequestState.NotStarted or
            MsiUninstallRequestState.Blocked or MsiUninstallRequestState.Preparing;
        if (preClientState && (hasClientFacts || result.ClientStartObserved == true)) return false;
        if ((hasClientFacts || clientState || result.ClientStartObserved == true) &&
            (result.ClientStartObserved != true || !intent.IsDispatchReserved)) return false;

        if (result.StartedUtc is { } start &&
            (!Utc(start) || start < result.RequestedUtc || start > nowUtc)) return false;
        if (result.ExitedUtc is { } exit)
        {
            if (!Utc(exit) || exit < result.RequestedUtc || exit > nowUtc ||
                result.StartedUtc is { } actualStart && exit < actualStart ||
                result.State is not (MsiUninstallRequestState.ClientExited or MsiUninstallRequestState.MonitoringUnknown)) return false;
        }
        else if (result.State == MsiUninstallRequestState.ClientExited || result.ExitCode.HasValue || result.ExitDisposition.HasValue)
            return false;

        // Native optional property reads may fail after an actual observed exit.
        // Missing PID/start/code remains missing, while a supplied code must keep
        // its exact classifier. Preview expiry cannot erase an already-run client.
        return result.ExitCode is { } code
            ? result.ExitDisposition == MsiUninstallRequestPolicy.ClassifyExit(code)
            : result.ExitDisposition == null;
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
