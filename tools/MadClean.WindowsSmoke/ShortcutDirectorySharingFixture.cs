using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool ShortcutDirectorySharingCleanupConfirmed { get; set; } = true;

    private static void ShortcutDirectorySharingFixture(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "shortcut-directory-sharing-observation";
        var state = new DirectorySharingObservation();
        var directories = new List<DirectorySharingCase>();
        string root = Path.Combine(fixture, "shortcut-directory-sharing");
        string sentinel = Path.Combine(root, "sentinel.bin");
        byte[] sentinelBytes = Encoding.ASCII.GetBytes("Owned directory-sharing sentinel; no source data or real shortcut is changed.");
        bool initialized = false;
        bool complete = false;
        string stage = "initializing";
        int? error = null;
        ShortcutDirectorySharingCleanupConfirmed = true;
        try
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(root)) throw new DirectorySharingUnknown("owned-child-already-existed");
            Directory.CreateDirectory(root);
            using (var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(sentinelBytes);
            foreach (string name in new[] { "control-rename", "control-write", "control-attributes", "guard-rename", "guard-write", "preopened-write", "preopened-attributes", "new-attributes" })
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                using var anchor = DirectorySharingOpen(path, 0xA0, 7);
                if (anchor.IsInvalid) throw DirectorySharingError("baseline-metadata-open");
                var stamp = DirectorySharingStamp(anchor, path);
                // Only these ordinary directory attributes are eligible for this narrow Hidden test.
                if ((stamp.Attributes & ~0x2032u) != 0 || (stamp.Attributes & 0x10) == 0)
                    throw new DirectorySharingUnknown("ordinary-directory-attributes-unavailable");
                directories.Add(new(path, path + "-renamed", stamp));
            }
            initialized = true;

            // Matching independent capabilities must succeed before guarded results are interpreted.
            state.ControlRename = DirectorySharingRename(directories[0], false, state, token, out _);
            if (!state.ControlRename) throw new DirectorySharingUnknown("unguarded-rename-capability-unavailable", state.ControlRenameCode);
            using (var writer = DirectorySharingOpen(directories[1].Path, 0x40000000, 7))
            {
                int code = writer.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                token.ThrowIfCancellationRequested();
                if (writer.IsInvalid) throw new DirectorySharingUnknown("generic-write-capability-unavailable", code);
                state.ControlWrite = true;
            }
            DirectorySharingAttributes(directories[2], 0, state, token);
            state.ControlAttributes = true;

            bool renamed = DirectorySharingRename(directories[3], true, state, token, out int renameCode);
            state.GuardedRenameRefused = !renamed; state.GuardedRenameCode = renameCode;
            using (var guard = DirectorySharingOpen(directories[4].Path, 0xA0, 1))
            {
                if (guard.IsInvalid) throw DirectorySharingError("new-writer-guard-unavailable");
                token.ThrowIfCancellationRequested();
                using var writer = DirectorySharingOpen(directories[4].Path, 0x40000000, 7);
                int code = writer.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                state.GuardedWriteRefused = writer.IsInvalid; state.GuardedWriteCode = code;
                token.ThrowIfCancellationRequested();
            }
            using (var writer = DirectorySharingOpen(directories[5].Path, 0x40000000, 7))
            {
                if (writer.IsInvalid) throw DirectorySharingError("preopened-writer-capability-unavailable");
                token.ThrowIfCancellationRequested();
                using var guard = DirectorySharingOpen(directories[5].Path, 0xA0, 1);
                int code = guard.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                state.PreopenedWriterGuardRefused = guard.IsInvalid; state.PreopenedWriterGuardCode = code;
                token.ThrowIfCancellationRequested();
            }
            DirectorySharingAttributes(directories[6], 1, state, token);
            state.PreopenedMetadataCoexisted = true;
            DirectorySharingAttributes(directories[7], 2, state, token);
            state.NewMetadataCoexisted = true;
            complete = true;
            stage = "observations-completed";
        }
        catch (DirectorySharingUnknown ex) { stage = ex.Stage; error = ex.NativeError; }
        catch (OperationCanceledException) { stage = "cancelled-between-owned-operations"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { stage = "owned-observation-unavailable"; error = ex.HResult; }
        finally
        {
            // Every operation helper has already returned and disposed its handles. No caller
            // closes a pending native handle; the fixture is synchronous on its owning thread.
            if (initialized)
            {
                bool audited = !state.RestorationUncertain;
                try
                {
                    foreach (var directory in directories)
                    {
                        using var anchor = DirectorySharingOpen(directory.Path, 0xA0, 7);
                        if (anchor.IsInvalid || DirectorySharingStamp(anchor, directory.Path) != directory.Original ||
                            Directory.Exists(directory.Alternate) || Directory.EnumerateFileSystemEntries(directory.Path).Any())
                            audited = false;
                    }
                    if (!File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(sentinelBytes)) audited = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
                { audited = false; }
                ShortcutDirectorySharingCleanupConfirmed = audited;
            }
            else if (state.MutationAttempted) ShortcutDirectorySharingCleanupConfirmed = false;
            Array.Clear(sentinelBytes);
        }
        string observations = $"controlRename={state.ControlRename}; controlWrite={state.ControlWrite}; controlAttributes={state.ControlAttributes}; " +
            $"guardedRenameRefused={state.GuardedRenameRefused}; guardedRenameCode={state.GuardedRenameCode}; " +
            $"guardedWriteRefused={state.GuardedWriteRefused}; guardedWriteCode={state.GuardedWriteCode}; " +
            $"preopenedWriterGuardRefused={state.PreopenedWriterGuardRefused}; preopenedWriterGuardCode={state.PreopenedWriterGuardCode}; " +
            $"preopenedMetadataCoexisted={state.PreopenedMetadataCoexisted}; newMetadataCoexisted={state.NewMetadataCoexisted}; " +
            $"ownedRestorationConfirmed={ShortcutDirectorySharingCleanupConfirmed}.";
        bool relations = state.GuardedRenameRefused && state.GuardedWriteRefused && state.PreopenedWriterGuardRefused &&
            state.PreopenedMetadataCoexisted && state.NewMetadataCoexisted;
        if (complete && relations && ShortcutDirectorySharingCleanupConfirmed)
            report.Pass(check, observations + " Metadata-only Hidden modification remained possible with the share-read guard. This is an owned sharing observation, not namespace/reparse protection, inactivity, privacy, recovery or action readiness proof.");
        else
            report.Unknown(check, $"stage={stage}; nativeOrErrorCode={error}; " + observations +
                " Capabilities or restoration were incomplete/unexpected; no sharing protection is inferred. Retain the marked parent tree if restoration is unconfirmed.");
    }

    private sealed record DirectorySharingIdentity(ulong Volume, Guid Id, uint Attributes);
    private sealed record DirectorySharingCase(string Path, string Alternate, DirectorySharingIdentity Original);
    private sealed class DirectorySharingObservation
    {
        internal bool MutationAttempted, RestorationUncertain;
        internal bool ControlRename, ControlWrite, ControlAttributes, GuardedRenameRefused, GuardedWriteRefused, PreopenedWriterGuardRefused;
        internal bool PreopenedMetadataCoexisted, NewMetadataCoexisted;
        internal int? ControlRenameCode, GuardedRenameCode, GuardedWriteCode, PreopenedWriterGuardCode;
    }
    private sealed class DirectorySharingUnknown(string stage, int? error = null) : Exception("Owned directory-sharing observation is incomplete.")
    { internal string Stage { get; } = stage; internal int? NativeError { get; } = error; }
    private static DirectorySharingUnknown DirectorySharingError(string stage) => new(stage, Marshal.GetLastWin32Error());
    private static SafeFileHandle DirectorySharingOpen(string path, uint access, uint share) =>
        DirectorySharingCreateFileW(path, access, share, IntPtr.Zero, 3, 0x02000000 | 0x00200000 | 0x00100000, IntPtr.Zero);

    private static DirectorySharingIdentity DirectorySharingStamp(SafeFileHandle handle, string expected)
    {
        if (!ShortcutEvidenceGetIdentity(handle, 18, out var identity, (uint)Marshal.SizeOf<ShortcutEvidenceId>()) ||
            !DirectorySharingGetAttributes(handle, 9, out var attributes, 8)) throw DirectorySharingError("owned-directory-metadata-unavailable");
        if (identity.Id == Guid.Empty || (attributes.Attributes & 0x10) == 0 ||
            (attributes.Attributes & (0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000)) != 0 || attributes.Tag != 0)
            throw new DirectorySharingUnknown("owned-directory-metadata-unsupported");
        string actual = DirectorySharingFinalPath(handle);
        if (!string.Equals(actual, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new DirectorySharingUnknown("owned-directory-final-path-unexpected");
        return new(identity.Volume, identity.Id, attributes.Attributes);
    }
    private static string DirectorySharingFinalPath(SafeFileHandle handle)
    {
        var text = new StringBuilder(4096);
        uint length = DirectorySharingGetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 0);
        if (length == 0) throw DirectorySharingError("owned-final-path-query-failed");
        if (length >= text.Capacity) throw new DirectorySharingUnknown("owned-final-path-bound");
        string value = text.ToString();
        if (!value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new DirectorySharingUnknown("owned-final-path-not-ordinary-local");
        return value[4..];
    }
    private static void DirectorySharingMutation(DirectorySharingObservation state)
    { state.MutationAttempted = true; ShortcutDirectorySharingCleanupConfirmed = false; }

    private static bool DirectorySharingRename(DirectorySharingCase directory, bool guarded,
        DirectorySharingObservation state, CancellationToken token, out int resultCode)
    {
        using var anchor = DirectorySharingOpen(directory.Path, 0xA0, 7);
        if (anchor.IsInvalid) throw DirectorySharingError("rename-anchor-unavailable");
        SafeFileHandle? guard = null;
        bool moved = false;
        resultCode = 0;
        try
        {
            if (DirectorySharingStamp(anchor, directory.Path) != directory.Original || Directory.Exists(directory.Alternate))
                throw new DirectorySharingUnknown("rename-baseline-changed");
            if (guarded)
            {
                guard = DirectorySharingOpen(directory.Path, 0xA0, 1);
                if (guard.IsInvalid) throw DirectorySharingError("rename-guard-unavailable");
            }
            token.ThrowIfCancellationRequested(); DirectorySharingMutation(state);
            moved = DirectorySharingMoveFileExW(directory.Path, directory.Alternate, 0);
            resultCode = moved ? 0 : Marshal.GetLastWin32Error();
            if (!guarded) state.ControlRenameCode = resultCode;
            token.ThrowIfCancellationRequested();
            if (moved)
            {
                if (DirectorySharingStamp(anchor, directory.Alternate) != directory.Original)
                    throw new DirectorySharingUnknown("renamed-directory-identity-changed");
            }
            else if (DirectorySharingStamp(anchor, directory.Path) != directory.Original || Directory.Exists(directory.Alternate))
                throw new DirectorySharingUnknown("refused-rename-did-not-establish-no-change", resultCode);
            return moved;
        }
        finally
        {
            // The measurement is over. This owning thread releases its guard before restoring
            // an unexpectedly moved owned directory; no protection result is inferred from restore.
            guard?.Dispose();
            try
            {
                string actual = DirectorySharingFinalPath(anchor);
                if (string.Equals(actual, Path.GetFullPath(directory.Alternate), StringComparison.OrdinalIgnoreCase))
                {
                    bool restored = DirectorySharingMoveFileExW(directory.Alternate, directory.Path, 0);
                    int code = restored ? 0 : Marshal.GetLastWin32Error();
                    if (!restored) { state.RestorationUncertain = true; throw new DirectorySharingUnknown("owned-rename-restoration-unconfirmed", code); }
                }
                else if (!string.Equals(actual, Path.GetFullPath(directory.Path), StringComparison.OrdinalIgnoreCase))
                    throw new DirectorySharingUnknown("owned-rename-location-unconfirmed");
                if (DirectorySharingStamp(anchor, directory.Path) != directory.Original || Directory.Exists(directory.Alternate))
                    throw new DirectorySharingUnknown("owned-rename-final-audit-unconfirmed");
            }
            catch { state.RestorationUncertain = true; throw; }
        }
    }

    // mode 0: matching unguarded capability; 1: attributes writer before guard; 2: writer after guard.
    private static void DirectorySharingAttributes(DirectorySharingCase directory, int mode,
        DirectorySharingObservation state, CancellationToken token)
    {
        using var anchor = DirectorySharingOpen(directory.Path, 0xA0, 7);
        if (anchor.IsInvalid) throw DirectorySharingError("attribute-anchor-unavailable");
        SafeFileHandle? writer = null, guard = null;
        bool restorationAttempted = false;
        try
        {
            if (DirectorySharingStamp(anchor, directory.Path) != directory.Original) throw new DirectorySharingUnknown("attribute-baseline-changed");
            if (mode != 2)
            {
                writer = DirectorySharingOpen(directory.Path, 0x100, 7);
                if (writer.IsInvalid) throw DirectorySharingError("metadata-writer-capability-unavailable");
            }
            if (mode != 0)
            {
                guard = DirectorySharingOpen(directory.Path, 0xA0, 1);
                if (guard.IsInvalid) throw DirectorySharingError("metadata-guard-coexistence-unavailable");
            }
            if (mode == 2)
            {
                writer = DirectorySharingOpen(directory.Path, 0x100, 7);
                if (writer.IsInvalid) throw DirectorySharingError("new-metadata-writer-unavailable");
            }
            token.ThrowIfCancellationRequested(); DirectorySharingMutation(state);
            uint changed = directory.Original.Attributes ^ 2u;
            if (!DirectorySharingSetAttributes(directory.Path, changed, out int toggleCode))
                throw new DirectorySharingUnknown("metadata-hidden-toggle-unavailable", toggleCode);
            if (DirectorySharingStamp(anchor, directory.Path) != (directory.Original with { Attributes = changed }))
                throw new DirectorySharingUnknown("metadata-hidden-toggle-readback-unavailable");
            token.ThrowIfCancellationRequested();
            restorationAttempted = true;
            if (!DirectorySharingSetAttributes(directory.Path, directory.Original.Attributes, out int restoreCode))
            { state.RestorationUncertain = true; throw new DirectorySharingUnknown("metadata-hidden-restoration-unconfirmed", restoreCode); }
            if (DirectorySharingStamp(anchor, directory.Path) != directory.Original)
            { state.RestorationUncertain = true; throw new DirectorySharingUnknown("metadata-restoration-readback-unconfirmed"); }
        }
        finally
        {
            guard?.Dispose(); writer?.Dispose();
            try
            {
                var current = DirectorySharingStamp(anchor, directory.Path);
                if (current.Volume != directory.Original.Volume || current.Id != directory.Original.Id)
                    throw new DirectorySharingUnknown("owned-attribute-object-identity-unconfirmed");
                if (current != directory.Original)
                {
                    if (!restorationAttempted) restorationAttempted = true;
                    if (!DirectorySharingSetAttributes(directory.Path, directory.Original.Attributes, out int code) ||
                        DirectorySharingStamp(anchor, directory.Path) != directory.Original)
                    { state.RestorationUncertain = true; throw new DirectorySharingUnknown("owned-attribute-cleanup-unconfirmed", code); }
                }
            }
            catch { state.RestorationUncertain = true; throw; }
        }
    }
    private static bool DirectorySharingSetAttributes(string path, uint attributes, out int code)
    {
        uint requested = attributes & ~0x10u; // DIRECTORY is an observed type, not an attribute-setting operation.
        if (requested == 0) requested = 0x80; // NORMAL clears the supported optional flags.
        bool result = DirectorySharingSetFileAttributesW(path, requested);
        code = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct DirectorySharingAttributeTag { public uint Attributes, Tag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle DirectorySharingCreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DirectorySharingGetAttributes(SafeFileHandle handle, int info, out DirectorySharingAttributeTag attributes, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint DirectorySharingGetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DirectorySharingMoveFileExW(string source, string target, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "SetFileAttributesW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DirectorySharingSetFileAttributesW(string path, uint attributes);
}
