using System.Globalization;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool CleanupMappedDispositionObservation(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "cleanup-mapped-disposition-owned-probe";
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = OwnedCleanupMappedDispositionProbe.RunAsync(fixture, marker, cancelled.Token).GetAwaiter().GetResult();
        bool preCancel = stopped.State == OwnedCleanupMappedDispositionState.CancelledBeforeStart &&
            !OwnedCleanupMappedDispositionProbe.HasPendingNativeProbe && !OwnedCleanupMappedDispositionProbe.RequiresRetention;
        if (!preCancel)
        {
            report.Fail(check, "Owned mapped-disposition pre-cancel did not establish no-start. No production action is supported.");
            return false;
        }
        var data = OwnedCleanupMappedDispositionProbe.RunAsync(fixture, marker, token).GetAwaiter().GetResult();
        static string Code(int? value) => value is int code ? unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture) : "~";
        static char Bit(bool? value) => value == true ? '1' : value == false ? '0' : '?';
        string detail = $"state={data.State};stage={data.Stage};native={Code(data.NativeError)};preCancel={preCancel}; " +
            $"sourceClose={Bit(data.SourceCloseConfirmed)};viewBefore={Bit(data.ViewWriteRestoreBeforeObserved)};open={Bit(data.TargetOpenSucceeded)}/{Code(data.TargetOpenNativeError)}; " +
            $"disposition={Bit(data.DispositionAccepted)}/{Code(data.DispositionNativeError)};pending={Bit(data.DeletePendingObserved)};targetClose={Bit(data.TargetCloseConfirmed)}; " +
            $"absentWithView={Bit(data.NameAbsentWithViewObserved)}/{Code(data.NameWithViewNativeError)};viewAfter={Bit(data.ViewWriteRestoreAfterObserved)}; " +
            $"unmap={Bit(data.ViewUnmapConfirmed)}/{Code(data.UnmapNativeError)};mappingClose={Bit(data.MappingCloseConfirmed)};absentAfterUnmap={Bit(data.NameAbsentAfterUnmapObserved)}/{Code(data.NameAfterUnmapNativeError)}; " +
            $"controlAudit={data.ControlUnchanged};rootAudit={data.RootIdentitiesUnchanged};cleanup={data.CleanupConfirmed}. " +
            "All memory access is one byte in this process's owned non-executable view; ?/~=unobserved. A live-view deletion is a counterexample, never inactivity or safety proof. No file/view coherence, physical persistence, reclaimed bytes or production permission is established.";
        bool cleanup = data.CleanupConfirmed && !OwnedCleanupMappedDispositionProbe.HasPendingNativeProbe && !OwnedCleanupMappedDispositionProbe.RequiresRetention;
        bool live = data.SourceCloseConfirmed == true && data.ViewWriteRestoreBeforeObserved == true && data.ViewWriteRestoreAfterObserved == true;
        bool baseProof = preCancel && cleanup && live && data.ViewUnmapConfirmed == true && data.MappingCloseConfirmed == true &&
            data.ControlUnchanged && data.RootIdentitiesUnchanged && !data.ExecutionSupported;
        bool counterexample = data.State == OwnedCleanupMappedDispositionState.CounterexampleObserved && baseProof &&
            data.TargetOpenSucceeded == true && data.DispositionAccepted == true && data.TargetCloseConfirmed == true &&
            data.NameAbsentWithViewObserved == true && data.NameWithViewNativeError == 2;
        bool refusedOpen = data.State == OwnedCleanupMappedDispositionState.ControlledOpenRefusalObserved && baseProof &&
            data.TargetOpenSucceeded == false && data.TargetOpenNativeError is 32 or 33 && data.DispositionAccepted == null;
        bool observed = data.State == OwnedCleanupMappedDispositionState.Observed && baseProof &&
            data.TargetOpenSucceeded == true && data.DispositionAccepted == true && data.TargetCloseConfirmed == true;
        if (detail.Length > 1000)
        {
            const string bounded = "Complete owned mapping evidence exceeded its export bound. No mapped-file deletion or inactivity conclusion is published; cleanup is independently gated.";
            if (data.State == OwnedCleanupMappedDispositionState.Failed) report.Fail(check, bounded + " A known invariant failure remains failed.");
            else report.Unknown(check, bounded);
        }
        else if (data.State == OwnedCleanupMappedDispositionState.Failed) report.Fail(check, detail);
        else if (counterexample || refusedOpen || observed) report.Pass(check, detail);
        else report.Unknown(check, detail);
        return cleanup;
    }
}
