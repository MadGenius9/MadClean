namespace MadClean.Core;

public enum ClientProcessObservationState { Exited, ObservationStopped, Unavailable }
public enum ClientProcessObservationReason
{
    ClientExited, StopRequested, ProcessUnavailable, ExitNotObserved, InvalidObservation
}

/// <summary>Observation of an already-started process, never an integrity, health or repair assessment.</summary>
public sealed record ClientProcessObservation(ClientProcessObservationState State, ClientProcessObservationReason Reason,
    DateTimeOffset? ObservationStartedUtc, DateTimeOffset? ObservationCompletedUtc, int? ObservedProcessId,
    DateTimeOffset? ClientStartUtc, int? ExitCode, bool StopRequested);

public static class ClientProcessObservationProjection
{
    public static ClientProcessObservation Normalize(ClientProcessObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var now = DateTimeOffset.UtcNow;
        bool valid = Enum.IsDefined(observation.State) && Enum.IsDefined(observation.Reason) &&
            observation.ObservationStartedUtc is { } start && observation.ObservationCompletedUtc is { } end &&
            Utc(start) && Utc(end) && start <= end && end <= now &&
            (observation.ObservedProcessId == null || observation.ObservedProcessId > 0);
        if (valid)
        {
            valid = observation.State switch
            {
                ClientProcessObservationState.Exited => observation.Reason == ClientProcessObservationReason.ClientExited &&
                    observation.ObservedProcessId.HasValue && observation.ExitCode.HasValue,
                ClientProcessObservationState.ObservationStopped => observation.Reason == ClientProcessObservationReason.StopRequested &&
                    observation.StopRequested && !observation.ExitCode.HasValue,
                ClientProcessObservationState.Unavailable => (observation.Reason is ClientProcessObservationReason.ProcessUnavailable or
                    ClientProcessObservationReason.ExitNotObserved or ClientProcessObservationReason.InvalidObservation) && !observation.ExitCode.HasValue,
                _ => false
            };
        }
        // Stopping observation never implies killing/cancelling a process. An actual observed exit
        // may coexist with StopRequested=true; neither a zero nor any other exit code is health.
        // Optional start-time retrieval is independent of an actual observed PID/exit.
        // Invalid optional metadata is omitted, never substituted with observation time.
        var clientStart = observation.ClientStartUtc is { } actualStart && Utc(actualStart) &&
            observation.ObservationCompletedUtc is { } completed && actualStart <= completed && actualStart <= now
            ? observation.ClientStartUtc : null;
        return valid ? observation with { ClientStartUtc = clientStart } : new(ClientProcessObservationState.Unavailable,
            ClientProcessObservationReason.InvalidObservation, null, null, null, null, null, observation.StopRequested);
    }
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
