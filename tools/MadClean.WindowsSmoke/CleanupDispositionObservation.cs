using System.Globalization;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool CleanupDispositionObservation(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "cleanup-disposition-owned-probe";
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = OwnedCleanupDispositionProbe.RunAsync(fixture, marker, cancelled.Token).GetAwaiter().GetResult();
        bool preCancel = stopped.State == OwnedCleanupDispositionState.CancelledBeforeStart && stopped.Cases.Count == 0 &&
            !OwnedCleanupDispositionProbe.HasPendingNativeProbe && !OwnedCleanupDispositionProbe.RequiresRetention;
        if (!preCancel)
        {
            report.Fail(check, "The pre-cancelled owned disposition probe did not establish its no-start contract. No production deletion permission is supplied.");
            return false;
        }
        var result = OwnedCleanupDispositionProbe.RunAsync(fixture, marker, token).GetAwaiter().GetResult();
        static string Code(int? value) => value is int code ? unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture) : "~";
        static char Bit(bool? value) => value == true ? '1' : value == false ? '0' : '?';
        string observations = string.Join(" | ", result.Cases.Select(item =>
            $"{item.Kind}:o={Code(item.TargetOpenNativeError)},a={Bit(item.DispositionAttempted)},r={Bit(item.DispositionAccepted)},e={Code(item.DispositionNativeError)},p={Bit(item.DeletePendingObserved)},c={Bit(item.TargetCloseConfirmed)},n={Bit(item.NameAbsentObserved)},ne={Code(item.NameProbeNativeError)},f={Bit(item.PredicateRefused)}"));
        string detail = $"state={result.State};stage={result.Stage};native={Code(result.NativeError)};preCancel={preCancel};siblingAudit={result.SiblingUntouched};rootIdAudit={result.RootIdentitiesUnchanged};cleanup={result.CleanupConfirmed}. {observations} " +
            "o=target-open error;a/r/e=disposition attempt/accepted/error;p=pending;c=checked target close;n/ne=name absent/probe error;f=predicate refused;?/~=unobserved. Owned files only; no fallback, recycling, inactivity, reclaimed bytes or production execution permission.";
        bool cleanup = result.CleanupConfirmed && !OwnedCleanupDispositionProbe.HasPendingNativeProbe && !OwnedCleanupDispositionProbe.RequiresRetention;
        if (detail.Length > 1000)
        {
            const string bounded = "The owned disposition evidence exceeded its export bound; complete primitive proof is not published. Production execution remains unsupported.";
            if (result.State == OwnedCleanupDispositionState.Failed) report.Fail(check, bounded + " A known invariant failure remains failed.");
            else report.Unknown(check, bounded);
            return cleanup;
        }
        var normal = result.Cases.SingleOrDefault(item => item.Kind == OwnedCleanupDispositionCaseKind.Normal);
        var writer = result.Cases.SingleOrDefault(item => item.Kind == OwnedCleanupDispositionCaseKind.ConflictingWriter);
        bool observed = result.State == OwnedCleanupDispositionState.Observed && cleanup && !result.ExecutionSupported &&
            result.SiblingUntouched && result.RootIdentitiesUnchanged && result.Cases.Count == 7 &&
            result.Cases.Select(item => item.Kind).Distinct().Count() == 7 && result.Cases.All(item => Enum.IsDefined(item.Kind)) &&
            normal is { DispositionAttempted: true, DispositionAccepted: true, TargetCloseConfirmed: true, NameAbsentObserved: true, NameProbeNativeError: 2, PredicateRefused: false } &&
            normal.DispositionNativeError == null && writer?.TargetOpenNativeError is 32 or 33 && writer.TargetCloseConfirmed == null &&
            result.Cases.Where(item => item.Kind != OwnedCleanupDispositionCaseKind.Normal).All(item =>
                item.PredicateRefused && !item.DispositionAttempted && item.DispositionAccepted == null && item.DispositionNativeError == null);
        if (result.State == OwnedCleanupDispositionState.Failed) report.Fail(check, detail);
        else if (observed) report.Pass(check, detail);
        else report.Unknown(check, detail);
        return cleanup;
    }
}
