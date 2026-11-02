using System.Globalization;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool StartupNamespaceObservation(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "startup-transaction-namespace-observation";
        var result = StartupTransactionNamespaceProbe.ProbeAsync(fixture, marker, token).GetAwaiter().GetResult();
        static string Code(int? value) => value is int code ? $"{unchecked((uint)code):X8}" : "~";
        static char Bit(bool value) => value ? '1' : '0';
        string cases = string.Join(" | ", result.Cases.Select(item =>
            $"{item.Case}/{item.State}/{item.Stage}:n={Code(item.NativeError)},r={Code(item.RenameError)},c={Code(item.CreateError)},w={Code(item.WriteError)},o={item.FirstOutcome?.ToString(CultureInfo.InvariantCulture) ?? "~"},f={Bit(item.FirstOutcomeObservedBeforeTimeout)}{Bit(item.InterferenceSucceeded)}{Bit(item.AbortObserved)}{Bit(item.TerminalObserved)}{Bit(item.BaselinePreserved)}{Bit(item.TeardownCompleted)}"));
        string detail = $"state={result.State}; stage={result.Stage}; native={Code(result.NativeError)}; pending={result.PendingWorker}; teardown={result.TeardownCompleted}. {cases} " +
            "n/r/c/w=native/rename/create/write; o=first outcome(1 Undetermined,3 Aborted); f=early/interference/abort/terminal/baseline/teardown. " +
            "Four owned inert sequences only; no commits, key-identity/link/isolation or startup-action readiness proof.";
        // Preserve every case/status/caveat through SmokeReport's fixed export bound.
        // Future diagnostic growth must never silently turn truncated evidence into a pass.
        if (detail.Length > 1000)
        {
            const string bounded = "The fixed namespace evidence exceeded its export bound. No complete case observation or startup binding proof is published; owned cleanup status remains independently enforced.";
            if (result.State == StartupTransactionNamespaceState.Failed) report.Fail(check, bounded + " A known invariant failure remains failed.");
            else report.Unknown(check, bounded);
            return result.TeardownCompleted && !result.PendingWorker &&
                !StartupTransactionNamespaceProbe.HasPendingNativeProbe && !StartupTransactionNamespaceProbe.RequiresFixtureRetention;
        }
        bool cleaned = result.TeardownCompleted && !result.PendingWorker &&
            !StartupTransactionNamespaceProbe.HasPendingNativeProbe && !StartupTransactionNamespaceProbe.RequiresFixtureRetention;
        if (result.State == StartupTransactionNamespaceState.Failed)
            report.Fail(check, detail);
        else if (result.State == StartupTransactionNamespaceState.Recorded && cleaned && result.Cases.Count == 4 &&
            result.Cases.Select(item => item.Case).Distinct().Count() == 4 &&
            result.Cases.All(item => Enum.IsDefined(item.Case) && item.State == StartupTransactionNamespaceState.Recorded &&
                item.InterferenceSucceeded && item.FirstOutcome is 1 or 3 && item.FirstOutcomeObservedBeforeTimeout &&
                item.TerminalObserved && item.BaselinePreserved && item.TeardownCompleted))
            report.Pass(check, detail);
        else report.Unknown(check, detail);
        return cleaned;
    }
}
