using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal enum ShortcutRenameProbeBackend { ManagedWin32, CppWin32, NativeNt }
internal enum ShortcutRenameProbeFileSystem { Unknown, Ntfs, Refs }
internal enum ShortcutRenameGuardProfile { Baseline, SourceParentShareRead, DestinationParentShareRead, FixtureAncestorShareRead }
internal sealed record ShortcutRenameProbeResult(CheckState State, bool MoveObserved, bool RestoreObserved,
    bool CollisionRefused, bool IdentityPreserved, bool BytesPreserved, bool TargetUnchanged,
    bool AttributeAnchorHandoffObserved, bool? ExclusiveWithLiveMapping, bool? MappedWriteObserved,
    bool TeardownCompleted, int? NativeErrorCode, string Reason)
{
    internal ShortcutRenameProbeBackend Backend { get; init; }
    internal ShortcutRenameGuardProfile GuardProfile { get; init; }
    internal int ControlApiCalls { get; init; }
    internal int? ScalarDiagBufferBytes { get; init; }
    internal int? ScalarDiagNameBytes { get; init; }
    internal bool? ScalarDiagRootDirValid { get; init; }
    internal bool? ScalarDiagSameVolume { get; init; }
    internal ShortcutRenameProbeFileSystem FileSystem { get; init; }
    internal bool MappingReadOnlyOpenRequested { get; init; }
    internal int? MappedNativeErrorCode { get; init; }
    internal int NtApiCalls { get; init; }
    internal int NtSuccessfulRenameCalls { get; init; }
    // Status/observed flags describe the last returned NT operation, not an aggregate
    // assertion that none of the earlier operations was pending.
    internal int? ImmediateNtStatus { get; init; }
    internal int? CompletionNtStatus { get; init; }
    internal bool CompletionNtStatusObserved { get; init; }
    internal bool PendingNtObserved { get; init; }
    internal bool TerminalNtObserved { get; init; }
    internal int? NtSetupWin32Error { get; init; }
    internal int? NtCollisionStatus { get; init; }
}

// Development-only object-rename proof. It is not a quarantine implementation, action
// token, inactive/immutable-file test, ACL/privacy guarantee or durable recovery journal.
internal static class ShortcutRenameProbe
{
    private static int active;
    private static Task<ShortcutRenameProbeResult>? pending;
    internal static bool HasPendingNativeProbe => Volatile.Read(ref active) != 0;
    internal static Task<ShortcutRenameProbeResult>? PendingCompletion => Volatile.Read(ref pending);

    internal static Task<ShortcutRenameProbeResult> RunAsync(string ownedFixtureRoot, string ownerMarker,
        Action<string, string> createOwnedShortcut, CancellationToken token)
        => StartAsync(ownedFixtureRoot, ownerMarker, createOwnedShortcut, ShortcutRenameProbeBackend.ManagedWin32, ShortcutRenameGuardProfile.Baseline, token);

    internal static Task<ShortcutRenameProbeResult> RunCppControlAsync(string ownedFixtureRoot, string ownerMarker,
        Action<string, string> createOwnedShortcut, CancellationToken token)
        => StartAsync(ownedFixtureRoot, ownerMarker, createOwnedShortcut, ShortcutRenameProbeBackend.CppWin32, ShortcutRenameGuardProfile.Baseline, token);

    internal static Task<ShortcutRenameProbeResult> RunNtControlAsync(string ownedFixtureRoot, string ownerMarker,
        Action<string, string> createOwnedShortcut, CancellationToken token)
        => StartAsync(ownedFixtureRoot, ownerMarker, createOwnedShortcut, ShortcutRenameProbeBackend.NativeNt, ShortcutRenameGuardProfile.Baseline, token);

    internal static Task<ShortcutRenameProbeResult> RunNtGuardControlAsync(string ownedFixtureRoot, string ownerMarker,
        Action<string, string> createOwnedShortcut, ShortcutRenameGuardProfile profile, CancellationToken token)
        => StartAsync(ownedFixtureRoot, ownerMarker, createOwnedShortcut, ShortcutRenameProbeBackend.NativeNt, profile, token);

    private static async Task<ShortcutRenameProbeResult> StartAsync(string ownedFixtureRoot, string ownerMarker,
        Action<string, string> createOwnedShortcut, ShortcutRenameProbeBackend backend, ShortcutRenameGuardProfile profile, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Empty(CheckState.Cancelled, "cancelled-before-start", backend, profile);
        if (!Enum.IsDefined(profile)) return Empty(CheckState.Unsupported, "guard-profile-unsupported", backend, profile);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return Empty(CheckState.Unsupported, "windows-x64-required", backend, profile);
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return Empty(CheckState.Unavailable, "previous-native-worker-pending", backend, profile);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<ShortcutRenameProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref pending, completion.Task);
        var thread = new Thread(() =>
        {
            ShortcutRenameProbeResult result;
            try { result = new ProbeWorker(ownedFixtureRoot, ownerMarker, createOwnedShortcut, backend, profile, stop.Token).Run(); }
            catch { result = Empty(CheckState.Failed, "worker-failed-owned-tree-retained", backend, profile); }
            finally { stop.Dispose(); Volatile.Write(ref active, 0); }
            completion.TrySetResult(result);
        }) { IsBackground = true, Name = "MadClean owned shortcut rename proof" };
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch
        {
            stop.Dispose(); Volatile.Write(ref active, 0);
            completion.TrySetResult(Empty(CheckState.Unavailable, "native-worker-start-unavailable", backend, profile));
        }
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false); }
        catch (TimeoutException)
        { try { stop.Cancel(); } catch (ObjectDisposedException) { } return Empty(CheckState.Unavailable, "caller-wait-expired-worker-owns-tree", backend, profile); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { try { stop.Cancel(); } catch (ObjectDisposedException) { } return Empty(CheckState.Cancelled, "caller-cancelled-worker-owns-tree", backend, profile); }
    }

    private static ShortcutRenameProbeResult Empty(CheckState state, string reason, ShortcutRenameProbeBackend backend, ShortcutRenameGuardProfile profile) =>
        new(state, false, false, false, false, false, false, false, null, null, false, null, reason) { Backend = backend, GuardProfile = profile };

    private sealed class ProbeWorker(string fixture, string marker, Action<string, string> createShortcut,
        ShortcutRenameProbeBackend backend, ShortcutRenameGuardProfile profile, CancellationToken token)
    {
        private const uint ReadAttributes = 0x80, Traverse = 0x20, GenericRead = 0x80000000, GenericWrite = 0x40000000, Delete = 0x10000;
        private const uint BackupSemantics = 0x02000000, OpenReparse = 0x00200000, NoRecall = 0x00100000;
        private const uint UnsafeAttributes = 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;
        private readonly List<(string Path, SafeFileHandle Handle)> parents = [];
        private readonly HashSet<string> ownedFiles = new(StringComparer.OrdinalIgnoreCase);
        private string root = "", child = "", vault = "", source = "", collision = "", target = "", mapped = "";
        private string reason = "initializing";
        private bool created, uncertain, teardown, com;
        private bool moved, restored, collisionRefused, identity, bytes, targetUnchanged, anchorHandoff;
        private bool? exclusiveMapping, mappedWrite;
        private int? nativeError;
        private CheckState state = CheckState.Unavailable;
        private nint controlModule;
        private SafeFileHandle? controlFile;
        private RelativeRenameControl? controlRename;
        private RelativeRenameNtControl? ntRename;
        private int controlCalls;
        private int? diagnosticBufferBytes, diagnosticNameBytes;
        private bool? diagnosticRoot, diagnosticVolume;
        private ShortcutRenameProbeFileSystem fileSystem;
        private bool mappingReadOnlyOpenRequested;
        private int? mappedNativeError;
        private int ntCalls, ntSuccessfulRenames;
        private int? immediateNtStatus, completionNtStatus, ntSetupError, ntCollisionStatus;
        private bool completionObserved, pendingObserved, terminalObserved;

        internal ShortcutRenameProbeResult Run()
        {
            try
            {
                Check(); ValidateAbi(); ValidateOwner();
                using var ownership = Open(Path.Combine(root, ".madclean-fixture-owner"), GenericRead, 1);
                if (ReadBytes(ownership, 32).Length != 32 || Encoding.ASCII.GetString(ReadBytes(ownership, 32)) != marker)
                    throw new InvalidDataException();
                int initialized = CoInitializeEx(0, 2);
                if (initialized < 0) { reason = "com-initialization-unavailable"; return Result(); }
                com = true; Check();
                if (backend != ShortcutRenameProbeBackend.ManagedWin32) LoadControl();
                child = Path.Combine(root, "shortcut-rename-probe-" + Guid.NewGuid().ToString("N"));
                reason = "create-owned-child";
                if (!CreateDirectoryW(child, 0)) throw Native();
                created = true;
                vault = Path.Combine(child, "owned-destination");
                if (!CreateDirectoryW(vault, 0)) throw Native();
                OpenParent(child); OpenParent(vault);
                target = Path.Combine(child, "target.bin"); mapped = Path.Combine(child, "mapped.bin");
                source = Path.Combine(child, "source-" + Guid.NewGuid().ToString("N") + ".lnk");
                collision = Path.Combine(child, "collision-" + Guid.NewGuid().ToString("N") + ".lnk");
                ownedFiles.UnionWith([target, mapped, source, collision]);
                WriteNew(target, [17, 34, 51, 68]);
                Check(); reason = "mapped-view-witness";
                MappedWitness();
                if (uncertain) throw new InvalidOperationException();
                Check(); reason = "create-owned-shortcuts";
                createShortcut(source, target); Check(); createShortcut(collision, target); Check();
                using var targetHandle = Open(target, GenericRead, 1);
                var targetBefore = Identity(targetHandle); byte[] targetBytes = ReadBytes(targetHandle, 64);
                using var collisionHandle = Open(collision, GenericRead, 7);
                var collisionBefore = Identity(collisionHandle); byte[] collisionBytes = ReadBytes(collisionHandle, 1_048_576);
                // READ_ATTRIBUTES is metadata-only: it is not a data-read share reservation.
                using var anchor = Open(source, ReadAttributes, 7);
                var prior = Identity(anchor);
                using (var action = Open(source, GenericRead | Delete, 0))
                {
                    var actionIdentity = Identity(action);
                    anchorHandoff = Same(prior, actionIdentity) && prior.Links == 1;
                    if (!anchorHandoff) throw new InvalidDataException();
                    byte[] original = ReadBytes(action, 1_048_576);
                    if (original.Length == 0) throw new InvalidDataException();
                    Check(); reason = "strengthen-owned-directory-guard";
                    StrengthenSelectedParent();
                    var destinationDirectory = parents.Single(item => item.Path == vault).Handle;
                    var sourceDirectory = parents.Single(item => item.Path == child).Handle;
                    string destinationLeaf = "moved-" + Guid.NewGuid().ToString("N") + ".lnk";
                    string destination = Path.Combine(vault, destinationLeaf); ownedFiles.Add(destination);
                    Check(); reason = "move-owned-link";
                    MoveAndVerify(action, destinationDirectory, destinationLeaf, destination, prior, original); moved = true;
                    Check(); reason = "restore-owned-link";
                    MoveAndVerify(action, sourceDirectory, Path.GetFileName(source), source, prior, original); restored = true;
                    Check(); reason = "collision-owned-link";
                    RevalidateParents(); uncertain = true;
                    bool replaced = Rename(action, sourceDirectory, Path.GetFileName(collision), out int collisionError);
                    if (replaced) throw new InvalidDataException();
                    bool originalPreserved = FinalPath(action) == source && Same(prior, Identity(action)) && original.AsSpan().SequenceEqual(ReadBytes(action, 1_048_576));
                    bool destinationPreserved = Same(collisionBefore, Identity(collisionHandle)) && collisionBytes.AsSpan().SequenceEqual(ReadBytes(collisionHandle, 1_048_576));
                    if (!originalPreserved || !destinationPreserved) throw new InvalidDataException();
                    uncertain = false;
                    if (backend == ShortcutRenameProbeBackend.NativeNt)
                    {
                        int? collisionStatus = completionObserved ? completionNtStatus : immediateNtStatus;
                        if (collisionStatus is not < 0) throw new NtRenameFailure();
                        ntCollisionStatus = collisionStatus;
                    }
                    else nativeError = collisionError;
                    collisionRefused = true;
                    identity = Same(prior, Identity(action)); bytes = originalPreserved;
                }
                targetUnchanged = Same(targetBefore, Identity(targetHandle)) && targetBytes.AsSpan().SequenceEqual(ReadBytes(targetHandle, 64));
                if (!targetUnchanged) throw new InvalidDataException();
                RevalidateParents(); Check();
                state = exclusiveMapping == true && mappedWrite == true ? CheckState.Available : CheckState.Partial;
                reason = state == CheckState.Available ? "owned-primitives-and-mapped-witness-observed" : "owned-primitives-observed-mapping-unverified";
            }
            catch (OperationCanceledException) { state = CheckState.Cancelled; reason = "worker-cancelled-before-or-after-observed-operation"; }
            catch (NtRenameFailure) { state = CheckState.Unavailable; }
            catch (NtControlSetupFailure) { state = CheckState.Unavailable; }
            catch (Win32Exception exception) { nativeError = exception.NativeErrorCode; state = CheckState.Unavailable; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or NotSupportedException or System.Security.SecurityException or COMException)
            { state = CheckState.Failed; }
            finally
            {
                // Release only the child-directory guards first: keeping their no-DELETE
                // leases would prevent fixture deletion. Root ancestry stays held below.
                foreach (var parent in parents.Where(item => item.Path == child || item.Path == vault).Reverse()) parent.Handle.Dispose();
                parents.RemoveAll(item => item.Path == child || item.Path == vault);
                controlRename = null; ntRename = null;
                if (controlModule != 0 && !FreeLibrary(controlModule)) uncertain = true;
                controlModule = 0; controlFile?.Dispose(); controlFile = null;
                if (com) CoUninitialize();
                // This deletes only this worker's disposable fixtures, after all operation
                // handles/views/COM objects close. A user-facing action never calls here.
                if (created && !uncertain)
                {
                    try
                    {
                        RevalidateParents();
                        if (!OwnerStillMatches() || !SafeOwnedTree()) throw new InvalidDataException();
                        Directory.Delete(child, true); teardown = !Directory.Exists(child);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { teardown = false; }
                }
                foreach (var parent in parents.AsEnumerable().Reverse()) parent.Handle.Dispose();
                parents.Clear();
            }
            return Result();
        }
        private ShortcutRenameProbeResult Result() => new(
            (state is CheckState.Available or CheckState.Partial) && !teardown ? CheckState.Unknown : state,
            moved, restored, collisionRefused, identity, bytes, targetUnchanged, anchorHandoff,
            exclusiveMapping, mappedWrite, teardown, nativeError,
            (state is CheckState.Available or CheckState.Partial) && !teardown ? "owned-tree-teardown-unverified" : reason)
        {
            Backend = backend, GuardProfile = profile, ControlApiCalls = controlCalls, ScalarDiagBufferBytes = diagnosticBufferBytes,
            ScalarDiagNameBytes = diagnosticNameBytes, ScalarDiagRootDirValid = diagnosticRoot,
            ScalarDiagSameVolume = diagnosticVolume, FileSystem = fileSystem,
            MappingReadOnlyOpenRequested = mappingReadOnlyOpenRequested, MappedNativeErrorCode = mappedNativeError,
            NtApiCalls = ntCalls, NtSuccessfulRenameCalls = ntSuccessfulRenames, ImmediateNtStatus = immediateNtStatus,
            CompletionNtStatus = completionNtStatus, CompletionNtStatusObserved = completionObserved,
            PendingNtObserved = pendingObserved, TerminalNtObserved = terminalObserved,
            NtSetupWin32Error = ntSetupError, NtCollisionStatus = ntCollisionStatus
        };
        private void Check() => token.ThrowIfCancellationRequested();
        private void ValidateOwner()
        {
            reason = "validate-owned-root";
            if (!Guid.TryParseExact(marker, "N", out var owner) || owner == Guid.Empty || marker != owner.ToString("N")) throw new InvalidDataException();
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture));
            string name = Path.GetFileName(root); const string prefix = "windows-validation-fixtures-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty ||
                Path.GetFileName(Path.GetDirectoryName(root)) != "artifacts" || root.Length > 240 || root.StartsWith(@"\\", StringComparison.Ordinal) ||
                root.Length < 4 || root[1] != ':' || root[2] != '\\' || GetDriveTypeW(Path.GetPathRoot(root)!) != 3) throw new InvalidDataException();
            var chain = new Stack<string>();
            for (string? current = root; current != null; current = Path.GetDirectoryName(current))
            { if (chain.Count >= 64) throw new InvalidDataException(); chain.Push(current); }
            while (chain.TryPop(out string? ancestor)) { Check(); OpenParent(ancestor); }
            using var guard = Open(Path.Combine(root, ".madclean-fixture-owner"), GenericRead, 1);
            if (Encoding.ASCII.GetString(ReadBytes(guard, 32)) != marker) throw new InvalidDataException();
        }
        private void OpenParent(string path)
        {
            // Owned-fixture-only sharing: permit ADD_FILE target opens, deny DELETE.
            var handle = Open(path, ReadAttributes | Traverse, 3, directory: true);
            parents.Add((path, handle));
        }
        private void StrengthenSelectedParent()
        {
            if (profile == ShortcutRenameGuardProfile.Baseline) return;
            string selected = profile switch
            {
                ShortcutRenameGuardProfile.SourceParentShareRead => child,
                ShortcutRenameGuardProfile.DestinationParentShareRead => vault,
                ShortcutRenameGuardProfile.FixtureAncestorShareRead => root,
                _ => throw new InvalidDataException()
            };
            RevalidateParents(); Check();
            int index = parents.FindIndex(item => item.Path == selected);
            if (index < 0) throw new InvalidDataException();
            var previous = parents[index];
            var before = DirectoryIdentity(previous.Handle);
            SafeFileHandle? replacement = Open(selected, ReadAttributes | Traverse, 1, directory: true);
            try
            {
                // Both leases overlap. Compare the complete native directory identity,
                // volume, tag and final path before replacing the prior RW-share lease.
                if (before != DirectoryIdentity(replacement) || before != DirectoryIdentity(previous.Handle) ||
                    !string.Equals(FinalPath(previous.Handle), selected, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(FinalPath(replacement), selected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                Check();
                parents[index] = (selected, replacement);
                replacement = null;
                previous.Handle.Dispose();
            }
            finally { replacement?.Dispose(); }
        }
        private static (ulong Volume, Guid Id, uint Attributes, uint ReparseTag) DirectoryIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(handle, 18, out IdInfo id, 24) ||
                !GetFileInformationByHandleEx(handle, 1, out StandardInfo standard, 24) ||
                !GetFileInformationByHandleEx(handle, 9, out TagInfo tag, 8)) throw Native();
            if (id.Id == Guid.Empty || !standard.Directory || standard.DeletePending ||
                (tag.Attributes & 0x10) == 0 || (tag.Attributes & UnsafeAttributes) != 0 || tag.ReparseTag != 0)
                throw new InvalidDataException();
            return (id.Volume, id.Id, tag.Attributes, tag.ReparseTag);
        }
        private void RevalidateParents()
        {
            foreach (var parent in parents)
                if (!string.Equals(FinalPath(parent.Handle), Path.TrimEndingDirectorySeparator(parent.Path), StringComparison.OrdinalIgnoreCase) ||
                    !GetFileInformationByHandleEx(parent.Handle, 9, out TagInfo tag, 8) || (tag.Attributes & UnsafeAttributes) != 0)
                    throw new InvalidDataException();
        }
        private SafeFileHandle Open(string path, uint access, uint share, bool directory = false)
        {
            var handle = CreateFileW(path, access, share, 0, 3, OpenReparse | NoRecall | (directory ? BackupSemantics : 0), 0);
            if (handle.IsInvalid) { var error = Native(); handle.Dispose(); throw error; }
            try
            {
                if (!GetFileInformationByHandleEx(handle, 9, out TagInfo tag, 8)) throw Native();
                if ((tag.Attributes & UnsafeAttributes) != 0 || ((tag.Attributes & 0x10) != 0) != directory ||
                    !string.Equals(FinalPath(handle), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        private static void WriteNew(string path, byte[] bytes)
        { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); }
        private static byte[] ReadBytes(SafeFileHandle handle, int maximum)
        {
            if (!GetFileInformationByHandleEx(handle, 1, out StandardInfo info, 24)) throw Native();
            if (info.EndOfFile < 0 || info.EndOfFile > maximum || info.Directory || info.DeletePending) throw new InvalidDataException();
            if (!SetFilePointerEx(handle, 0, out long position, 0) || position != 0) throw Native();
            byte[] data = new byte[(int)info.EndOfFile]; int total = 0;
            byte[] part = new byte[Math.Min(65_536, data.Length)];
            while (total < data.Length)
            {
                int requested = Math.Min(part.Length, data.Length - total);
                if (!ReadFile(handle, part, (uint)requested, out uint read, 0)) throw Native();
                if (read == 0 || read > requested) throw new InvalidDataException();
                part.AsSpan(0, (int)read).CopyTo(data.AsSpan(total)); total += (int)read;
            }
            return data;
        }
        private static (ulong Volume, Guid Id, uint Links) Identity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(handle, 18, out IdInfo id, 24) ||
                !GetFileInformationByHandleEx(handle, 1, out StandardInfo standard, 24)) throw Native();
            if (id.Id == Guid.Empty || standard.Directory || standard.DeletePending || standard.NumberOfLinks != 1) throw new InvalidDataException();
            return (id.Volume, id.Id, standard.NumberOfLinks);
        }
        private static bool Same((ulong Volume, Guid Id, uint Links) first, (ulong Volume, Guid Id, uint Links) second) => first == second;
        private void MoveAndVerify(SafeFileHandle action, SafeFileHandle directory, string leaf, string expected,
            (ulong Volume, Guid Id, uint Links) prior, byte[] original)
        {
            RevalidateParents(); Check(); uncertain = true;
            if (!Rename(action, directory, leaf, out int renameError))
            {
                if (Same(prior, Identity(action)) && original.AsSpan().SequenceEqual(ReadBytes(action, 1_048_576))) uncertain = false;
                if (backend == ShortcutRenameProbeBackend.NativeNt) throw new NtRenameFailure();
                throw new Win32Exception(renameError);
            }
            // Once dispatched, verify the actual outcome before honoring cancellation.
            if (FinalPath(action) != expected || !Same(prior, Identity(action)) || !original.AsSpan().SequenceEqual(ReadBytes(action, 1_048_576))) throw new InvalidDataException();
            RevalidateParents(); uncertain = false;
            if (backend == ShortcutRenameProbeBackend.NativeNt) ntSuccessfulRenames++;
        }
        private bool Rename(SafeFileHandle sourceHandle, SafeFileHandle directory, string leaf, out int nativeError)
        {
            if (string.IsNullOrWhiteSpace(leaf) || leaf.Length > 100 || !leaf.EndsWith(".lnk", StringComparison.Ordinal) ||
                leaf.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.'))) throw new InvalidDataException();
            byte[] name = Encoding.Unicode.GetBytes(leaf);
            diagnosticNameBytes = name.Length; diagnosticBufferBytes = 24 + name.Length;
            if (!GetFileInformationByHandleEx(directory, 1, out StandardInfo directoryInfo, 24) ||
                !GetFileInformationByHandleEx(directory, 18, out IdInfo directoryId, 24)) throw Native();
            diagnosticRoot = directoryInfo.Directory && !directoryInfo.DeletePending;
            diagnosticVolume = Identity(sourceHandle).Volume == directoryId.Volume;
            if (diagnosticRoot != true || diagnosticVolume != true) throw new InvalidDataException();
            var fs = new StringBuilder(32);
            if (GetVolumeInformationByHandleW(directory, null, 0, out _, out _, out _, fs, 32))
                fileSystem = fs.ToString() switch { "NTFS" => ShortcutRenameProbeFileSystem.Ntfs, "ReFS" => ShortcutRenameProbeFileSystem.Refs, _ => ShortcutRenameProbeFileSystem.Unknown };
            if (backend == ShortcutRenameProbeBackend.NativeNt)
            {
                nativeError = 0; // Not an NTSTATUS-to-Win32 conversion; this output is unused by this backend.
                if (ntRename == null) throw new InvalidOperationException();
                bool result = ntRename(sourceHandle, directory, leaf, (uint)leaf.Length, out bool called,
                    out int immediate, out int completion, out bool observedCompletion, out bool observedPending,
                    out bool observedTerminal, out uint setupError);
                ntSetupError = setupError == 0 ? null : unchecked((int)setupError);
                if (!called)
                {
                    reason = "nt-control-setup-unavailable"; uncertain = false;
                    throw new NtControlSetupFailure();
                }
                ntCalls++; immediateNtStatus = immediate;
                completionObserved = observedCompletion; completionNtStatus = observedCompletion ? completion : null;
                pendingObserved = observedPending; terminalObserved = observedTerminal;
                if (!observedTerminal || immediate == 0x103 && !observedCompletion || observedCompletion && completion == 0x103)
                {
                    // Defensive ABI boundary: never release handles if the helper returns
                    // without confirming completion. The outer caller still expires at45s.
                    uncertain = true;
                    for (;;) Thread.Sleep(Timeout.Infinite);
                }
                int terminal = observedCompletion ? completion : immediate;
                if (result != (terminal == 0) || setupError != 0) throw new NtControlSetupFailure();
                return result;
            }
            if (backend == ShortcutRenameProbeBackend.CppWin32)
            {
                if (controlRename == null) throw new InvalidOperationException();
                bool result = controlRename(sourceHandle, directory, leaf, (uint)leaf.Length, out uint code, out bool called);
                if (!called) throw new InvalidDataException();
                controlCalls++;
                nativeError = unchecked((int)code);
                return result;
            }
            nint buffer = Marshal.AllocHGlobal(24 + name.Length);
            bool reference = false;
            try
            {
                Marshal.Copy(new byte[24 + name.Length], 0, buffer, 24 + name.Length);
                directory.DangerousAddRef(ref reference);
                Marshal.WriteIntPtr(buffer, 8, directory.DangerousGetHandle()); Marshal.WriteInt32(buffer, 16, name.Length);
                Marshal.Copy(name, 0, buffer + 20, name.Length);
                bool result = SetFileInformationByHandle(sourceHandle, 3, buffer, (uint)(24 + name.Length));
                nativeError = result ? 0 : Marshal.GetLastWin32Error();
                return result;
            }
            finally { if (reference) directory.DangerousRelease(); Marshal.FreeHGlobal(buffer); }
        }
        private void LoadControl()
        {
            reason = "cpp-control-load"; Check();
            string repository = Path.GetDirectoryName(Path.GetDirectoryName(root))!;
            string controlDirectory = Path.Combine(repository, "artifacts", "native-controls");
            string path = Path.Combine(controlDirectory, "ShortcutRenameControl.dll");
            var chain = new Stack<string>();
            for (string? current = controlDirectory; current != null; current = Path.GetDirectoryName(current))
            { if (chain.Count >= 64) throw new InvalidDataException(); chain.Push(current); }
            while (chain.TryPop(out string? ancestor)) { Check(); OpenParent(ancestor); }
            controlFile = Open(path, GenericRead, 1);
            if (ReadBytes(controlFile, 4_194_304).Length == 0) throw new InvalidDataException();
            RevalidateParents(); Check();
            // Absolute development artifact only; all dependency search is restricted to System32.
            controlModule = LoadLibraryExW(path, 0, 0x800);
            if (controlModule == 0) throw Native();
            nint versionPointer = GetProcAddress(controlModule, "ShortcutRenameControlAbiVersion");
            if (versionPointer == 0) throw Native();
            var version = Marshal.GetDelegateForFunctionPointer<ControlAbiVersion>(versionPointer);
            if (version() != 2) throw new InvalidDataException();
            string export = backend == ShortcutRenameProbeBackend.NativeNt ? "RelativeRenameNt" : "RelativeRename";
            nint renamePointer = GetProcAddress(controlModule, export);
            if (renamePointer == 0) throw Native();
            if (backend == ShortcutRenameProbeBackend.NativeNt)
                ntRename = Marshal.GetDelegateForFunctionPointer<RelativeRenameNtControl>(renamePointer);
            else controlRename = Marshal.GetDelegateForFunctionPointer<RelativeRenameControl>(renamePointer);
            Check();
        }
        private void MappedWitness()
        {
            Check();
            nint view = 0; SafeFileHandle? mapping = null;
            try
            {
                WriteNew(mapped, [17, 0, 0, 0, 0, 0, 0, 0]);
                using (var original = Open(mapped, GenericRead | GenericWrite, 7))
                {
                    mapping = CreateFileMappingW(original, 0, 4, 0, 0, null);
                    if (mapping.IsInvalid) { mappedNativeError = Marshal.GetLastWin32Error(); return; }
                    view = MapViewOfFile(mapping, 6, 0, 0, 8);
                    if (view == 0) { mappedNativeError = Marshal.GetLastWin32Error(); return; }
                }
                mapping.Dispose(); mapping = null; Check();
                // This is the cleanup reader's read-only sharing pattern, not DELETE
                // access and not a test of whether a rename would be eligible.
                mappingReadOnlyOpenRequested = true;
                using var exclusive = CreateFileW(mapped, GenericRead, 0, 0, 3, OpenReparse | NoRecall, 0);
                if (exclusive.IsInvalid) { exclusiveMapping = false; mappedNativeError = Marshal.GetLastWin32Error(); return; }
                exclusiveMapping = true;
                Marshal.WriteByte(view, 42);
                if (!FlushViewOfFile(view, 8)) { mappedNativeError = Marshal.GetLastWin32Error(); return; }
                mappedWrite = ReadBytes(exclusive, 64)[0] == 42 && Marshal.ReadByte(view) == 42;
            }
            catch (Win32Exception exception) { mappedNativeError = exception.NativeErrorCode; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            { /* Unsupported/unverified witness remains nullable; no native code is invented. */ }
            finally
            {
                if (view != 0 && !UnmapViewOfFile(view))
                { mappedNativeError = Marshal.GetLastWin32Error(); uncertain = true; }
                mapping?.Dispose();
            }
        }
        private bool OwnerStillMatches()
        {
            using var guard = Open(Path.Combine(root, ".madclean-fixture-owner"), GenericRead, 1);
            return Encoding.ASCII.GetString(ReadBytes(guard, 32)) == marker;
        }
        private bool SafeOwnedTree()
        {
            if (Directory.GetParent(child)?.FullName != root || !Path.GetFileName(child).StartsWith("shortcut-rename-probe-", StringComparison.Ordinal)) return false;
            var remaining = new Stack<string>(); remaining.Push(child); int count = 0, queued = 1;
            while (remaining.TryPop(out string? path))
            {
                if (++count > 32 || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return false;
                if (Directory.Exists(path))
                {
                    if (path != child && path != vault) return false;
                    foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                    { if (++queued > 32) return false; remaining.Push(entry); }
                }
                else if (!ownedFiles.Contains(path)) return false;
            }
            return true;
        }
        private static string FinalPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(1024); uint count = GetFinalPathNameByHandleW(handle, buffer, 1024, 0);
            if (count == 0) throw Native();
            if (count >= 1024) throw new InvalidDataException();
            string result = buffer.ToString();
            if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
            return Path.TrimEndingDirectorySeparator(result);
        }
        private static Win32Exception Native() => new(Marshal.GetLastWin32Error());
        private static void ValidateAbi()
        {
            if (Marshal.SizeOf<IdInfo>() != 24 || Marshal.OffsetOf<IdInfo>(nameof(IdInfo.Id)).ToInt32() != 8 ||
                Marshal.SizeOf<StandardInfo>() != 24 || Marshal.OffsetOf<StandardInfo>(nameof(StandardInfo.NumberOfLinks)).ToInt32() != 16 ||
                Marshal.OffsetOf<StandardInfo>(nameof(StandardInfo.DeletePending)).ToInt32() != 20 ||
                Marshal.OffsetOf<StandardInfo>(nameof(StandardInfo.Directory)).ToInt32() != 21 ||
                Marshal.SizeOf<RenameInfo>() != 24 || Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.Root)).ToInt32() != 8 ||
                Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.NameLength)).ToInt32() != 16 ||
                Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.Name)).ToInt32() != 20 || Marshal.SizeOf<TagInfo>() != 8) throw new PlatformNotSupportedException();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct TagInfo { internal uint Attributes, ReparseTag; }
    private sealed class NtRenameFailure : Exception { }
    private sealed class NtControlSetupFailure : Exception { }
    [StructLayout(LayoutKind.Sequential)] private struct RenameInfo { internal uint Flags; internal nint Root; internal uint NameLength; internal ushort Name; }
    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { internal ulong Volume; internal Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    { internal long AllocationSize, EndOfFile; internal uint NumberOfLinks; [MarshalAs(UnmanagedType.U1)] internal bool DeletePending; [MarshalAs(UnmanagedType.U1)] internal bool Directory; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ControlAbiVersion();
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private delegate bool RelativeRenameControl(SafeFileHandle source, SafeFileHandle directory,
        [MarshalAs(UnmanagedType.LPWStr)] string leaf, uint characters, out uint error, [MarshalAs(UnmanagedType.Bool)] out bool apiCalled);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private delegate bool RelativeRenameNtControl(SafeFileHandle source, SafeFileHandle directory,
        [MarshalAs(UnmanagedType.LPWStr)] string leaf, uint characters, [MarshalAs(UnmanagedType.Bool)] out bool apiCalled,
        out int immediate, out int completion, [MarshalAs(UnmanagedType.Bool)] out bool completionObserved,
        [MarshalAs(UnmanagedType.Bool)] out bool pendingObserved, [MarshalAs(UnmanagedType.Bool)] out bool terminalObserved, out uint setupWin32Error);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file,
        StringBuilder? volumeName, uint volumeNameSize, out uint serial, out uint maximumComponent, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out TagInfo data, uint size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out StandardInfo data, uint size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out IdInfo data, uint size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, nint data, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(SafeFileHandle file, [Out] byte[] buffer, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeFileHandle CreateFileMappingW(SafeFileHandle file, nint security, uint protect, uint high, uint low, string? name);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint MapViewOfFile(SafeFileHandle mapping, uint access, uint high, uint low, nuint size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnmapViewOfFile(nint view);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushViewOfFile(nint view, nuint size);
    [DllImport("ole32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void CoUninitialize();
}
