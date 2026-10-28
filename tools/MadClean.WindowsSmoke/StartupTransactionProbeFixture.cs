using System.ComponentModel;
using System.IO;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    // Returns permission to remove only the harness-owned filesystem fixture tree, never a
    // registry/action authorization. The probe worker independently owns all registry teardown.
    private static bool StartupTransactions(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "startup-transaction-handle-probe";
        try
        {
            // ProbeAsync itself has a bounded caller wait and worker-owned resource lifetime.
            // Do not add another Query deadline that could detach its ownership bookkeeping.
            var result = StartupTransactionProbe.ProbeAsync(fixture, token).GetAwaiter().GetResult();
            bool pending = StartupTransactionProbe.HasPendingNativeProbe;
            string stage = result.Stage is { Length: >= 1 and <= 96 } && result.Stage.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? result.Stage : "UnrecognizedFixedStage";
            string code = result.NativeError.HasValue ? "0x" + unchecked((uint)result.NativeError.Value).ToString("X8") : "none";
            string detail = $"Isolated transaction state={result.State}; rollbackObserved={result.RollbackObserved}; commitObserved={result.CommitObserved}; teardownCompleted={result.TeardownCompleted}; pendingWorker={pending}; stage={stage}; nativeCode={code}. ";
            if (!Enum.IsDefined(result.State))
            {
                report.Fail(check, "The isolated transaction probe returned an unrecognized outcome; fixture ownership retained and no production readiness inferred.");
                return false;
            }
            if (result.State == StartupTransactionProbeState.Passed)
            {
                if (!result.RollbackObserved || !result.CommitObserved || !result.TeardownCompleted || pending)
                {
                    report.Fail(check, detail + "A claimed pass lacks terminal-outcome or cleanup evidence; no handle-enlistment proof is claimed.");
                    return false;
                }
                report.Pass(check, detail + "Two sequential inert DWORD transactions verified handle-based enlistment rollback and commit only inside an ownership-marked development HKCU subtree. No actual startup registrations, SIDs, links, rename/concurrency cases, DPAPI recovery or production action gate exercised; durability and safe startup mutations remain unverified.");
            }
            else if (result.State == StartupTransactionProbeState.Failed)
                report.Fail(check, detail + "Observed owned fixture behavior contradicted the probe invariant; no production startup-action proof.");
            else
                report.Unknown(check, detail + "Native support, terminal outcome or completion remains unverified. Pending worker ownership is retained; no forced abort, ordinary active-transaction read or production readiness claim.");
            return result.TeardownCompleted && result.State != StartupTransactionProbeState.Pending && !pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or System.Security.SecurityException or OperationCanceledException or TimeoutException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            report.Unknown(check, $"The isolated transaction probe wrapper did not establish a terminal result (code 0x{ex.HResult:X8}); the entire owned filesystem tree is retained. No registry teardown, forced abort or production action readiness is inferred.");
            return false;
        }
    }
}
