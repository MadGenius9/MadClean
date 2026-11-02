using System.Diagnostics;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Observes a caller-owned, already-started local process. Stopping observation never
/// terminates the process. The caller retains ownership until this task completes.
/// </summary>
internal static class SystemToolProcessObserver
{
    internal static async Task<ClientProcessObservation> ObserveAsync(Process process,
        CancellationToken observationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        var started = DateTimeOffset.UtcNow;
        int? pid = null;
        DateTimeOffset? clientStarted = null;
        bool processUnavailable = false;
        // Metadata failures are independent: an unavailable start time must not replace
        // an established exit/code observation or cause a process-ID lookup/reopen.
        try { int observed = process.Id; if (observed > 0) pid = observed; }
        catch (Exception) { processUnavailable = true; }
        try { clientStarted = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); }
        catch (Exception) { }

        bool TryExit(out int exitCode)
        {
            exitCode = default;
            try
            {
                if (!process.HasExited) return false;
                exitCode = process.ExitCode;
                return true;
            }
            catch (Exception) { processUnavailable = true; return false; }
        }
        ClientProcessObservation Result(ClientProcessObservationState state, ClientProcessObservationReason reason, int? code = null)
            => ClientProcessObservationProjection.Normalize(new(state, reason, started, DateTimeOffset.UtcNow,
                pid, clientStarted, code, observationToken.IsCancellationRequested));
        ClientProcessObservation Exited(int code) => pid.HasValue
            ? Result(ClientProcessObservationState.Exited, ClientProcessObservationReason.ClientExited, code)
            : Result(ClientProcessObservationState.Unavailable, ClientProcessObservationReason.ProcessUnavailable);

        // The actual exit observation has priority even with a pre-cancelled token.
        if (TryExit(out int exit)) return Exited(exit);
        if (!observationToken.IsCancellationRequested)
        {
            try { await process.WaitForExitAsync(observationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (observationToken.IsCancellationRequested) { }
            catch (Exception) { processUnavailable = true; }
        }
        // Wait cancellation can race with exit or cancel output-EOF waiting after exit.
        // Read actual process state again; a cancelled task does not establish liveness.
        if (TryExit(out exit)) return Exited(exit);
        if (observationToken.IsCancellationRequested)
            return Result(ClientProcessObservationState.ObservationStopped, ClientProcessObservationReason.StopRequested);
        return Result(ClientProcessObservationState.Unavailable, processUnavailable
            ? ClientProcessObservationReason.ProcessUnavailable : ClientProcessObservationReason.ExitNotObserved);
    }
}
