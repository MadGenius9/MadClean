using System.ComponentModel;
using System.IO;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool ShortcutRenameHandles(string fixture, string marker, SmokeReport report, CancellationToken token,
        bool cppControl = false)
    {
        string rename = cppControl ? "shortcut-rename-cpp-control" : "shortcut-rename-handle-probe";
        const string mapped = "shortcut-mapped-write-witness";
        try
        {
            // RunAsync owns its caller deadline and background STA/native lifetime.
            // No detached wrapper timeout or caller disposal may bypass that ownership.
            var result = (cppControl ? ShortcutRenameProbe.RunCppControlAsync(fixture, marker, CreateShortcut, token) :
                ShortcutRenameProbe.RunAsync(fixture, marker, CreateShortcut, token)).GetAwaiter().GetResult();
            bool pending = ShortcutRenameProbe.HasPendingNativeProbe;
            bool schema = Enum.IsDefined(result.State) && Enum.IsDefined(result.Backend) && Enum.IsDefined(result.FileSystem) &&
                result.Backend == (cppControl ? ShortcutRenameProbeBackend.CppWin32 : ShortcutRenameProbeBackend.ManagedWin32) &&
                result.ControlApiCalls is >= 0 and <= 3 && (cppControl || result.ControlApiCalls == 0) &&
                result.ScalarDiagBufferBytes is not (< 0 or > 65536) && result.ScalarDiagNameBytes is not (< 0 or > 65536) &&
                result.Reason is { Length: >= 1 and <= 80 } &&
                result.Reason.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
            if (!schema)
            {
                report.Fail(rename, "Owned shortcut probe returned an invalid fixed state/reason contract; ownership tree retained.");
                if (!cppControl) report.Fail(mapped, "Owned mapped-write witness schema was invalid; no exclusivity/activity inference.");
                return false;
            }
            string code = result.NativeErrorCode is int error ? "0x" + unchecked((uint)error).ToString("X8") : "none";
            string NullableFlag(bool? value) => value.HasValue ? value.Value ? "true" : "false" : "unknown";
            string Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            string detail = $"state={result.State}; backend={result.Backend}; controlApiCalls={result.ControlApiCalls}; move={result.MoveObserved}; restore={result.RestoreObserved}; collisionRefused={result.CollisionRefused}; identityPreserved={result.IdentityPreserved}; bytesPreserved={result.BytesPreserved}; targetUnchanged={result.TargetUnchanged}; attributeAnchorHandoff={result.AttributeAnchorHandoffObserved}; teardown={result.TeardownCompleted}; pendingWorker={pending}; reason={result.Reason}; nativeCode={code}; bufferBytes={Number(result.ScalarDiagBufferBytes)}; nameBytes={Number(result.ScalarDiagNameBytes)}; rootValid={NullableFlag(result.ScalarDiagRootDirValid)}; sameVolume={NullableFlag(result.ScalarDiagSameVolume)}; fileSystem={result.FileSystem}. ";
            bool renameProof = result.MoveObserved && result.RestoreObserved && result.CollisionRefused && result.IdentityPreserved &&
                result.BytesPreserved && result.TargetUnchanged && result.AttributeAnchorHandoffObserved && result.TeardownCompleted && !pending &&
                (!cppControl || result.ControlApiCalls == 3);
            if (result.State == CheckState.Available && !renameProof)
            {
                report.Fail(rename, detail + "A claimed positive result lacked actual move/restore/identity/bytes/target/teardown evidence.");
                if (!cppControl) report.Unknown(mapped, "Rename result contradicted its schema; no mapped exclusivity conclusion promoted.");
                return false;
            }
            if (result.State == CheckState.Failed) report.Fail(rename, detail + "Owned fixture observations contradicted a native invariant; no production shortcut-action proof.");
            else if ((result.State is CheckState.Available or CheckState.Partial) && renameProof)
                report.Pass(rename, detail + "Only this separate marked development shortcut moved/restored without overwrite. Explicit independent backend; no retry/fallback or promotion of the other backend. No personal shortcut/target or production gate changed; not deletion, recovery or action authorization.");
            else report.Unknown(rename, detail + "The narrow owned handle-rename sequence was not fully established; no fallback or production readiness inferred.");

            if (cppControl) return result.TeardownCompleted && !pending;
            string witness = $"exclusiveWithLiveMapping={NullableFlag(result.ExclusiveWithLiveMapping)}; mappedWriteObserved={NullableFlag(result.MappedWriteObserved)}; teardown={result.TeardownCompleted}; pendingWorker={pending}; nativeCode={code}; reason={result.Reason}. ";
            if (result.MappedWriteObserved == true && result.ExclusiveWithLiveMapping != true)
                report.Fail(mapped, witness + "A mapped write claim contradicted its exclusive-open precondition.");
            else if (result.State == CheckState.Failed)
                report.Unknown(mapped, witness + "Other owned fixture invariants failed; no positive mapped witness is promoted.");
            else if (result.ExclusiveWithLiveMapping == true && result.MappedWriteObserved == true && result.TeardownCompleted && !pending)
                report.Pass(mapped, witness + "A live owned mapped view could write while an exclusive handle was held: exclusivity is not inactivity. This is a negative safety witness, never permission to mutate personal files or infer active use absent.");
            else report.Unknown(mapped, witness + "The owned live-mapping witness was not established; no inactivity or safe-mutation inference.");
            return result.TeardownCompleted && !pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or
            System.Security.SecurityException or OperationCanceledException or TimeoutException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            report.Unknown(rename, $"Owned shortcut probe did not establish terminal teardown (code 0x{ex.HResult:X8}); entire fixture marker/tree retained, no caller cleanup or production mutation.");
            if (!cppControl) report.Unknown(mapped, "Owned mapped-write witness did not establish completion; no exclusivity/inactivity conclusion.");
            return false;
        }
    }
}
