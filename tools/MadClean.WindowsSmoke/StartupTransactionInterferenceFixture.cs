using System.ComponentModel;
using System.IO;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool StartupTransactionInterference(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "startup-transaction-interference-probe";
        try
        {
            // The worker owns its transaction and registry cleanup. No additional caller
            // deadline, force-close, or ordinary active-transaction read is introduced here.
            var result = StartupTransactionProbe.ProbeInterferenceAsync(fixture, token).GetAwaiter().GetResult();
            bool pending = StartupTransactionProbe.HasPendingNativeProbe;
            string stage = result.Stage is { Length: >= 1 and <= 96 } && result.Stage.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? result.Stage : "UnrecognizedFixedStage";
            string code = result.NativeError.HasValue ? "0x" + unchecked((uint)result.NativeError.Value).ToString("X8") : "none";
            string detail = $"Isolated interference state={result.State}; ordinaryWriteSucceeded={result.NonTransactedWriteSucceeded}; abortObserved={result.AbortObserved}; baselinePreserved={result.BaselinePreserved}; observedBeforeTimeout={result.ObservedBeforeTimeout}; teardownCompleted={result.TeardownCompleted}; pendingWorker={pending}; stage={stage}; nativeCode={code}. ";
            if (!Enum.IsDefined(result.State))
            {
                report.Fail(check, "Unrecognized interference outcome; fixture ownership retained and no production safety claim.");
                return false;
            }
            if (result.State == StartupTransactionProbeState.Passed)
            {
                if (!result.NonTransactedWriteSucceeded || !result.AbortObserved || !result.BaselinePreserved ||
                    !result.ObservedBeforeTimeout || !result.TeardownCompleted || pending)
                {
                    report.Fail(check, detail + "A claimed pass lacks required observed sequence, timing or checked teardown evidence; no interference proof is claimed.");
                    return false;
                }
                report.Pass(check, detail + "An abort was observed after one ordinary same-anchor inert DWORD write, before cleanup rollback and before the configured timeout boundary. This is a narrow owned sequence, not proof the write caused the abort, ABA prevention, rename/link/concurrency coverage, journal recovery, durability or safe production startup actions. No real startup registration or production gate changed.");
            }
            else if (result.State == StartupTransactionProbeState.Failed)
                report.Fail(check, detail + "Owned observed behavior contradicted the probe invariant; no production startup safety claim.");
            else
                report.Unknown(check, detail + "No qualifying pre-cleanup abort sequence was established. Unsupported, pending, non-aborted or delayed outcomes stay unverified; a cleanup rollback is not interference evidence or a production safety proof.");
            return result.TeardownCompleted && result.State != StartupTransactionProbeState.Pending && !pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or System.Security.SecurityException or OperationCanceledException or TimeoutException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            report.Unknown(check, $"The isolated interference wrapper did not establish a terminal result (code 0x{ex.HResult:X8}); all owned filesystem fixtures are retained. No forced abort or production startup safety is inferred.");
            return false;
        }
    }
}
