using System.Runtime.InteropServices;
using System.Security.Principal;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>
/// Closed owned-fixture regular-file candidate. It supplies neither secure first-parent
/// creation nor production journal admission or crash durability. A separate closed
/// factory exercises one fixed synthetic journal sample inside the owned capture.
/// </summary>
internal static class WindowsSystemToolJournalRelativeFile
{
    internal static bool NativeCandidateExecutionValidated => false;
    private static readonly object RootsSync = new();
    private static readonly HashSet<NativeFrame> Roots = new(ReferenceEqualityComparer.Instance);

    internal static RelativeJournalFileFrame CreateOwnedFixtureFrame(SafeFileHandle parent, string root, string marker)
    {
        // No parent/token/root/platform access or ownership admission precedes this boundary.
        Guard();
        return CreateRooted(parent, root, marker, exerciseJournal: false);
    }
    internal static RelativeJournalFileFrame CreateOwnedFixtureJournalSampleFrame(SafeFileHandle parent, string root, string marker)
    {
        Guard();
        return CreateRooted(parent, root, marker, exerciseJournal: true);
    }
    private static RelativeJournalFileFrame CreateRooted(SafeFileHandle parent, string root, string marker, bool exerciseJournal)
    {
        lock (RootsSync)
        {
            if (Roots.Count >= 128) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.StoreBusy);
            var frame = new NativeFrame(parent, root, marker, exerciseJournal);
            Roots.Add(frame); // Root actual mutable pointer/handle slots before Prepare.
            return frame;
        }
    }
    private static void Guard()
    {
        if (!NativeCandidateExecutionValidated)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || SystemToolJournalStore.HasUncertainCleanup)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        if (Marshal.SizeOf<UnicodeName>() != 16 || Marshal.OffsetOf<UnicodeName>(nameof(UnicodeName.Buffer)).ToInt32() != 8 ||
            Marshal.SizeOf<ObjectAttributes>() != 48 || Marshal.OffsetOf<ObjectAttributes>(nameof(ObjectAttributes.Root)).ToInt32() != 8 ||
            Marshal.OffsetOf<ObjectAttributes>(nameof(ObjectAttributes.Descriptor)).ToInt32() != 32 ||
            Marshal.SizeOf<IoStatusBlock>() != 16 || Marshal.OffsetOf<IoStatusBlock>(nameof(IoStatusBlock.Information)).ToInt32() != 8)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
    }

    private sealed class NativeFrame : RelativeJournalFileFrame
    {
        private readonly SafeFileHandle parent;
        private readonly string root, marker;
        private readonly bool exerciseJournal;
        private Guid sampleExecutionId;
        private bool sampleIoActive;
        private RelativeJournalFileRequest? request;
        private string? expectedParent, expectedChild, sid;
        private bool prepareAttempted, prepared, borrowed, invokeAttempted, invokeReturned, captureAttempted, captured;
        private bool markerAttempted, markerReturned, markerOwned, markerKnownFailure;
        private bool childOwned, releaseAttempted, releaseConfirmed;
        private bool attachmentStarted, attachmentReturned;
        private bool markerStreamStarted, markerStreamReturned;
        private int? nativeStatus;
        private nint parentRaw, markerRaw, childRaw;
        private nint markerName, name, unicode, attributes, ioStatus, output, descriptor;
        private SafeFileHandle? markerWrapper, childWrapper;
        private FileStream? markerStream, childStream;
        private SystemToolJournalStore? store;
        private (ulong Volume, ulong Low, ulong High) parentIdentity;
        private readonly byte[] markerBytes = new byte[32];

        internal NativeFrame(SafeFileHandle parent, string root, string marker, bool exerciseJournal)
        { this.parent = parent; this.root = root; this.marker = marker; this.exerciseJournal = exerciseJournal; }

        internal override bool Prepare(RelativeJournalFileRequest selected, CancellationToken token)
        {
            Guard();
            if (prepareAttempted || releaseAttempted) return false;
            prepareAttempted = true;
            if (!RelativeJournalFileDomain.IsSupported(selected) || token.IsCancellationRequested) return false;
            if (exerciseJournal && (selected.Mode != RelativeJournalFileOpenMode.CreateNew || selected.ReadOnly ||
                selected.Leaf is not { Length: 38 } || !Guid.TryParseExact(selected.Leaf[..32], "N", out sampleExecutionId) ||
                sampleExecutionId == Guid.Empty || sampleExecutionId.ToString("N") + ".jsonl" != selected.Leaf)) return false;
            request = selected with { };
            string normalized = SystemToolJournalStore.ValidateRoot(root, marker);
            expectedParent = Path.Combine(normalized, "relative-regular-file-parent-v1");
            expectedChild = Path.Combine(expectedParent, selected.Leaf);
            SystemToolJournalStore.VerifyFixedLocalPath(expectedParent);
            if (parent.IsInvalid || parent.IsClosed) return false;
            // Caller keeps its original parent/tree owner alive and undisposed until
            // this independent borrow is released. We never claim to close that owner.
            parent.DangerousAddRef(ref borrowed);
            if (!borrowed) return false;
            parentRaw = parent.DangerousGetHandle();
            if (parentRaw <= 0) return false;
            using (var identity = WindowsIdentity.GetCurrent())
                sid = identity.User?.Value ?? throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.SecurityUnsupported);
            SystemToolJournalStore.ValidateObject(parent, expectedParent, true);
            SystemToolJournalStore.VerifyPersistentAclVolume(parent);
            SystemToolJournalStore.VerifyPrivateSecurity(parent, sid, true);
            parentIdentity = SystemToolJournalStore.RelativeFileIdentity(parent);
            token.ThrowIfCancellationRequested();

            string markerPath = Path.Combine(normalized, ".madclean-fixture-owner");
            Allocate(ref markerName, checked((markerPath.Length + 1) * 2));
            WriteName(markerName, markerPath);
            markerAttempted = true;
            markerRaw = Native.CreateFileW(markerName, 0x80000000, 1, 0, 3, 0x00200000 | 0x00100000, 0);
            markerReturned = true;
            if (markerRaw == Native.InvalidHandleValue) { markerKnownFailure = true; return false; }
            // Other invalid/pseudo/parent-alias shapes remain ambiguous and retained.
            if (markerRaw <= 0 || markerRaw == parentRaw) return false;
            markerOwned = true;
            markerWrapper = new SafeFileHandle(markerRaw, ownsHandle: false);
            SystemToolJournalStore.ValidateObject(markerWrapper, markerPath, false, 32);
            RequireDisk(markerWrapper);
            markerStreamStarted = true;
            markerStream = new FileStream(markerWrapper, FileAccess.Read, 32, false);
            markerStreamReturned = true;
            markerStream.ReadExactly(markerBytes);
            if (markerStream.ReadByte() != -1 || System.Text.Encoding.ASCII.GetString(markerBytes) != marker) return false;
            Array.Clear(markerBytes);
            token.ThrowIfCancellationRequested();

            descriptor = SystemToolJournalStore.CreateRelativeFileDescriptor(sid);
            Allocate(ref name, checked((selected.Leaf.Length + 1) * 2));
            WriteName(name, selected.Leaf);
            Allocate(ref unicode, 16);
            Marshal.StructureToPtr(new UnicodeName { Length = checked((ushort)(selected.Leaf.Length * 2)),
                MaximumLength = checked((ushort)((selected.Leaf.Length + 1) * 2)), Buffer = name }, unicode, false);
            Allocate(ref attributes, 48);
            Marshal.StructureToPtr(new ObjectAttributes { Length = 48, Root = parentRaw, Name = unicode,
                Attributes = Native.ObjectCaseInsensitive, Descriptor = descriptor }, attributes, false);
            Allocate(ref ioStatus, 16);
            Allocate(ref output, 8);
            token.ThrowIfCancellationRequested();
            prepared = true;
            return true;
        }

        internal override int Invoke()
        {
            Guard();
            if (!prepared || invokeAttempted || releaseAttempted || request == null || parent.IsClosed)
                throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
            uint access = (request.ReadOnly ? 0x80000000u : 0xC0000000u) | 0x20000u | Native.Synchronize;
            uint options = Native.FileNonDirectory | Native.FileOpenReparsePoint | Native.FileSynchronousNonAlert;
            if (!request.ReadOnly) options |= Native.FileWriteThrough;
            uint disposition = request.Mode == RelativeJournalFileOpenMode.CreateNew ? Native.FileCreate : Native.FileOpen;
            invokeAttempted = true;
            // All pointed-to buffers and the parent borrow are already rooted.
            // A throwing/nonzero return never grants output or cleanup ownership.
            int observed = Native.NtCreateFile(output, access, attributes, ioStatus, 0,
                0x80, 0, disposition, options, 0, 0);
            nativeStatus = observed;
            invokeReturned = true;
            return observed;
        }

        internal override bool CaptureSuccess()
        {
            Guard();
            if (captureAttempted || releaseAttempted || !invokeReturned || nativeStatus != 0 || request == null) return false;
            captureAttempted = true;
            childRaw = Marshal.ReadIntPtr(output);
            if (childRaw <= 0 || childRaw == parentRaw || childRaw == markerRaw) return false;
            childOwned = true; // Own raw success output BEFORE wrapper/store allocations.
            childWrapper = new SafeFileHandle(childRaw, ownsHandle: false);
            nuint information = unchecked((nuint)Marshal.ReadInt64(ioStatus, 8));
            if (information != ExpectedInformation(request.Mode)) return false;
            // IOSB.Status is not read: only exact immediate return zero grants this path.
            RequireDisk(childWrapper);
            SystemToolJournalStore.ValidateObject(childWrapper, expectedChild!, false, SystemToolJournalStore.MaximumIntentBytes);
            SystemToolJournalStore.VerifyPrivateSecurity(childWrapper, sid!, false);
            if (SystemToolJournalStore.RelativeFileIdentity(childWrapper).Volume != parentIdentity.Volume) return false;
            SystemToolJournalStore.ValidateObject(parent, expectedParent!, true);
            SystemToolJournalStore.VerifyPrivateSecurity(parent, sid!, true);
            if (SystemToolJournalStore.RelativeFileIdentity(parent) != parentIdentity) return false;
            attachmentStarted = true;
            store = SystemToolJournalStore.AdoptQualifiedRelativeLifecycleFile(childWrapper, parent,
                request.Mode == RelativeJournalFileOpenMode.CreateNew, request.ReadOnly, out childStream);
            attachmentReturned = true;
            if (SystemToolJournalStore.HasUncertainCleanup) return false;
            // Store stays private. The protocol's actual Open callback is still active;
            // Release refuses until this synchronous capture/sample has returned.
            if (exerciseJournal)
            {
                Volatile.Write(ref sampleIoActive, true);
                try { if (!ExerciseInertJournalSample()) return false; }
                finally { Volatile.Write(ref sampleIoActive, false); }
            }
            captured = true;
            return captured;
        }

        internal override bool ReleaseKnownOwnership()
        {
            Guard();
            if (Volatile.Read(ref sampleIoActive)) return false;
            if (releaseAttempted) return releaseConfirmed;
            releaseAttempted = true;
            if (SystemToolJournalStore.HasUncertainCleanup || markerAttempted && (!markerReturned || !markerOwned && !markerKnownFailure) ||
                invokeAttempted && (!invokeReturned || nativeStatus != 0 || !childOwned) ||
                markerStreamStarted && !markerStreamReturned ||
                attachmentStarted && !attachmentReturned && childStream == null) return false;
            try
            {
                if (store != null) SystemToolJournalStore.DisposeChecked(store);
                else SystemToolJournalStore.DisposeChecked(childStream);
                SystemToolJournalStore.DisposeChecked(childWrapper);
                SystemToolJournalStore.DisposeChecked(markerStream);
                SystemToolJournalStore.DisposeChecked(markerWrapper);
                if (SystemToolJournalStore.HasUncertainCleanup) return false;
                if (childOwned)
                {
                    if (Native.CloseHandle(childRaw) == 0) return false;
                    childOwned = false;
                }
                if (markerOwned)
                {
                    if (Native.CloseHandle(markerRaw) == 0) return false;
                    markerOwned = false;
                }
                if (descriptor != 0)
                {
                    if (!SystemToolJournalStore.ReleaseRelativeFileDescriptor(descriptor)) return false;
                    descriptor = 0;
                }
                Free(ref output); Free(ref ioStatus); Free(ref attributes); Free(ref unicode); Free(ref name); Free(ref markerName);
                Array.Clear(markerBytes);
                if (borrowed)
                {
                    if (parent.IsClosed) return false; // Caller breached the cooperating borrow contract.
                    parent.DangerousRelease();
                    borrowed = false;
                }
                releaseConfirmed = true;
                lock (RootsSync) Roots.Remove(this);
                return true;
            }
            catch { return false; } // No retry after ambiguous close/free/release.
        }

        private static void Allocate(ref nint slot, int bytes)
        {
            // Publish the raw allocation before zeroing can fail.
            slot = Marshal.AllocHGlobal(bytes);
            for (int i = 0; i < bytes; i++) Marshal.WriteByte(slot, i, 0);
        }
        private static void WriteName(nint destination, string value)
        {
            for (int i = 0; i <= value.Length; i++) Marshal.WriteInt16(destination, checked(i * 2),
                i == value.Length ? (short)0 : unchecked((short)value[i]));
        }
        private static void Free(ref nint pointer)
        {
            if (pointer == 0) return;
            Marshal.FreeHGlobal(pointer);
            pointer = 0;
        }
        private static void RequireDisk(SafeFileHandle handle)
        {
            if (Native.GetFileType(handle) != Native.FileTypeDisk)
                throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.UnsafeObject);
        }
        private static nuint ExpectedInformation(RelativeJournalFileOpenMode mode) => mode switch
        {
            RelativeJournalFileOpenMode.CreateNew => Native.FileCreated,
            RelativeJournalFileOpenMode.OpenExisting => Native.FileOpened,
            _ => throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported)
        };
        private bool ExerciseInertJournalSample()
        {
            if (store == null || request is not { Mode: RelativeJournalFileOpenMode.CreateNew, ReadOnly: false } ||
                sampleExecutionId == Guid.Empty) return false;
            // Synthetic owned-fixture bytes only. This does not record an actual repair,
            // mint a workflow receipt or invent client, exit, source or success facts.
            var now = DateTimeOffset.UtcNow;
            var expected = new SystemToolJournalEntry(sampleExecutionId, SystemToolOperation.DismRestoreHealth,
                1, now, SystemToolJournalPhase.DispatchIntent, null, null, null, null);
            var snapshot = Array.AsReadOnly(new[] { expected });
            var appended = store.AppendJournalSnapshot(snapshot, now, CancellationToken.None);
            if (!ExactSample(appended, expected)) return false;
            var readback = store.ReadJournal(DateTimeOffset.UtcNow, CancellationToken.None);
            // A failed/throwing sample leaves any actual bytes in place. Resource
            // release supplies no rollback, reset, retry or history permission.
            return ExactSample(readback, expected);
        }
        private static bool ExactSample(SystemToolJournalReview? review, SystemToolJournalEntry expected)
        {
            if (review is not { State: SystemToolJournalState.Unresolved, Entries.Count: 1 } || review.Entries[0] != expected)
                return false;
            var canonical = SystemToolJournal.Review(review.Entries, DateTimeOffset.UtcNow);
            return canonical.State == SystemToolJournalState.Unresolved && canonical.Entries.Count == 1 && canonical.Entries[0] == expected;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeName { internal ushort Length, MaximumLength; internal nint Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    { internal uint Length; internal nint Root, Name; internal uint Attributes; internal nint Descriptor, QualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { internal nint Status; internal nuint Information; }
    private static class Native
    {
        // SDK10.0.22621.0 um/handleapi.h:27: ((HANDLE)(LONG_PTR)-1).
        internal static readonly nint InvalidHandleValue = -1;
        // Verified by the coordinator against Microsoft's installed SDK10.0.22621.0.
        internal const uint ObjectCaseInsensitive = 0x40, Synchronize = 0x100000;
        internal const uint FileCreate = 2, FileOpen = 1, FileCreated = 2, FileOpened = 1;
        internal const uint FileNonDirectory = 0x40, FileOpenReparsePoint = 0x200000,
            FileSynchronousNonAlert = 0x20, FileWriteThrough = 2, FileTypeDisk = 1;
        [DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int NtCreateFile(nint outputHandle, uint access, nint attributes, nint ioStatus,
            nint allocationSize, uint fileAttributes, uint share, uint disposition, uint options, nint eaBuffer, uint eaLength);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint CreateFileW(nint name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int CloseHandle(nint handle);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern uint GetFileType(SafeFileHandle handle);
    }
}
