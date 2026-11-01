using System.ComponentModel;
using System.IO;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool ShortcutRenameHandles(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string rename = "shortcut-rename-handle-probe", mapped = "shortcut-mapped-write-witness";
        try
        {
            // RunAsync owns its caller deadline and background STA/native lifetime.
            // No detached wrapper timeout or caller disposal may bypass that ownership.
            var result = ShortcutRenameProbe.RunAsync(fixture, marker, CreateShortcut, token).GetAwaiter().GetResult();
            bool pending = ShortcutRenameProbe.HasPendingNativeProbe;
            bool schema = Enum.IsDefined(result.State) && result.Reason is { Length: >= 1 and <= 80 } &&
                result.Reason.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
            if (!schema)
            {
                report.Fail(rename, "Owned shortcut probe returned an invalid fixed state/reason contract; ownership tree retained.");
                report.Fail(mapped, "Owned mapped-write witness schema was invalid; no exclusivity/activity inference.");
                return false;
            }
            string code = result.NativeErrorCode is int error ? "0x" + unchecked((uint)error).ToString("X8") : "none";
            string NullableFlag(bool? value) => value.HasValue ? value.Value ? "true" : "false" : "unknown";
            string detail = $"state={result.State}; move={result.MoveObserved}; restore={result.RestoreObserved}; collisionRefused={result.CollisionRefused}; identityPreserved={result.IdentityPreserved}; bytesPreserved={result.BytesPreserved}; targetUnchanged={result.TargetUnchanged}; attributeAnchorHandoff={result.AttributeAnchorHandoffObserved}; teardown={result.TeardownCompleted}; pendingWorker={pending}; reason={result.Reason}; nativeCode={code}. ";
            bool renameProof = result.MoveObserved && result.RestoreObserved && result.CollisionRefused && result.IdentityPreserved &&
                result.BytesPreserved && result.TargetUnchanged && result.AttributeAnchorHandoffObserved && result.TeardownCompleted && !pending;
            if (result.State == CheckState.Available && !renameProof)
            {
                report.Fail(rename, detail + "A claimed positive result lacked actual move/restore/identity/bytes/target/teardown evidence.");
                report.Unknown(mapped, "Rename result contradicted its schema; no mapped exclusivity conclusion promoted.");
                return false;
            }
            if (result.State == CheckState.Failed) report.Fail(rename, detail + "Owned fixture observations contradicted a native invariant; no production shortcut-action proof.");
            else if ((result.State is CheckState.Available or CheckState.Partial) && renameProof)
                report.Pass(rename, detail + "Only the marked development shortcut was moved and restored by handle without overwrite. No personal shortcut, application target or production action gate changed. This primitive is not deletion, durable recovery or race-safe action authorization.");
            else report.Unknown(rename, detail + "The narrow owned handle-rename sequence was not fully established; no fallback or production readiness inferred.");

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
            report.Unknown(mapped, "Owned mapped-write witness did not establish completion; no exclusivity/inactivity conclusion.");
            return false;
        }
    }
}
