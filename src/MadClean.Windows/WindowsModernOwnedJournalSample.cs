using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

// Fixed inert sample only. There is no App, command-line, harness or production hook.
internal static class WindowsModernOwnedJournalSample
{
    internal static bool ExecutionValidated => false;
    private static readonly ModernOwnedJournalSampleDomain Domain = new();

    internal static ModernOwnedJournalSampleAttempt RunOwnedFixture(string root, string marker, CancellationToken token = default)
    {
        Guard(); // Before argument, token, profile, scheduling or Windows access.
        var frame = new Frame(root, marker, SampleMode.FreshWrite); // Managed fields only; Domain roots before Prepare.
        return Domain.Run(frame, token);
    }

    internal static ModernOwnedJournalSampleAttempt RunExistingOwnedFixture(string root, string marker, CancellationToken token = default)
    {
        Guard(); // This is not a production recovery hook or caller-selected open mode.
        var frame = new Frame(root, marker, SampleMode.ExistingReadOnly);
        return Domain.Run(frame, token);
    }

    private enum SampleMode { FreshWrite, ExistingReadOnly }

    private static void Guard()
    {
        if (!ExecutionValidated) throw Refused();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || !Environment.Is64BitProcess ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 || RuntimeInformation.OSArchitecture != Architecture.X64 ||
            SystemToolJournalStore.HasUncertainCleanup || Marshal.SizeOf<SecurityAttributes>() != 24 ||
            Marshal.OffsetOf<SecurityAttributes>(nameof(SecurityAttributes.Descriptor)).ToInt32() != 8 ||
            Marshal.OffsetOf<SecurityAttributes>(nameof(SecurityAttributes.Inherit)).ToInt32() != 16 ||
            Marshal.SizeOf<ExtendedParameters>() != 32 ||
            Marshal.OffsetOf<ExtendedParameters>(nameof(ExtendedParameters.Security)).ToInt32() != 16 ||
            Marshal.OffsetOf<ExtendedParameters>(nameof(ExtendedParameters.Template)).ToInt32() != 24 ||
            Marshal.SizeOf<IdentityInformation>() != 24)
            throw Refused();
    }
    private static SystemToolJournalStoreFailure Refused() => new(SystemToolJournalStoreReason.ScopeUnsupported);

    // A token is useful only during this exact registered frame's one attachment call.
    // Supplying another managed object or a copied wrapper cannot mint an attachment.
    internal sealed class LifecycleAttachment
    {
        private readonly object owner;
        private int claimed;
        internal LifecycleAttachment(object owner) { this.owner = owner; }
        internal bool TryClaim(out SafeFileHandle handle, out SafeFileHandle parent, out bool created, out bool readOnly)
        {
            Guard();
            handle = parent = null!;
            created = readOnly = false;
            if (owner is not Frame frame || !Domain.IsActive(frame, ModernJournalSamplePhase.Exercise) || !ReferenceEquals(frame.Attachment, this) ||
                frame.mode is not (SampleMode.FreshWrite or SampleMode.ExistingReadOnly) ||
                !frame.AttachmentReady || frame.ReleaseStarted || frame.JournalWrapper == null || frame.DirectoryWrapper == null ||
                Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return false;
            frame.QualifyAttachment();
            handle = frame.JournalWrapper; parent = frame.DirectoryWrapper;
            // These facts come only from the claimed immutable private frame.
            readOnly = frame.mode == SampleMode.ExistingReadOnly;
            created = !readOnly;
            return true;
        }
    }

    private sealed class Frame : ModernOwnedJournalSampleFrame
    {
        private readonly string rootInput, markerInput;
        internal readonly SampleMode mode;
        // Pointer slots exist before any allocation/native call; they are never finalizer-owned.
        private readonly nint[] allocations = new nint[64];
        private readonly nint[] ancestorRaws = new nint[16];
        private readonly bool[] ancestorOwned = new bool[16];
        private readonly SafeFileHandle?[] ancestorWrappers = new SafeFileHandle?[16];
        private int ancestorCount;
        private int allocationCount;
        private string? root, directory, leasePath, journalPath, sid;
        private Guid executionId;
        private nint rootName, markerName, directoryName, leaseName, journalName;
        private nint rootParameters, markerParameters, fileParameters, directoryAttributes;
        private nint directoryDescriptor, fileDescriptor;
        private nint kernelModule, filesystemBuffer;
        private bool kernelModuleOwned;
        private nint rootRaw, markerRaw, directoryRaw, leaseRaw, journalRaw;
        private bool rootOwned, markerOwned, directoryOwned, leaseOwned, journalOwned;
        private bool directoryDescriptorOwned, fileDescriptorOwned, descriptorUncertain;
        private bool prepareAttempted, prepared, directoryAttempted, leaseAttempted, journalAttempted, exerciseAttempted, attachmentStarted, attachmentReturned;
        private bool markerStreamStarted, markerStreamReturned, released;
        private SafeFileHandle? rootWrapper, markerWrapper, leaseWrapper;
        private FileStream? markerStream, childStream;
        private SystemToolJournalStore? store;
        private IdentityInformation rootIdentity, directoryIdentity, leaseIdentity, journalIdentity;
        internal readonly LifecycleAttachment Attachment;
        internal SafeFileHandle? DirectoryWrapper, JournalWrapper;
        internal bool AttachmentReady, ReleaseStarted;

        internal Frame(string root, string marker, SampleMode mode)
        { rootInput = root; markerInput = marker; this.mode = mode; Attachment = new(this); }

        internal override bool Prepare(CancellationToken token)
        {
            RequirePhase(ModernJournalSamplePhase.Prepare);
            if (prepareAttempted || ReleaseStarted || mode is not (SampleMode.FreshWrite or SampleMode.ExistingReadOnly)) return false;
            prepareAttempted = true;
            token.ThrowIfCancellationRequested();
            root = SystemToolJournalStore.ValidateRoot(rootInput, markerInput);
            string repository = Path.GetFullPath(Environment.CurrentDirectory);
            if (root.Length > 160 || !File.Exists(Path.Combine(repository, "MadClean.sln")) ||
                root != Path.Combine(repository, "artifacts", "windows-validation-fixtures-" + markerInput) ||
                !Guid.TryParseExact(markerInput, "N", out executionId) || executionId == Guid.Empty)
                return false;
            directory = Path.Combine(root, executionId.ToString("N"));
            leasePath = Path.Combine(directory, "lease.bin");
            journalPath = Path.Combine(directory, executionId.ToString("N") + ".jsonl");
            if (journalPath.Length > 258) return false;
            var components = root[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length > ancestorRaws.Length) return false;
            SystemToolJournalStore.VerifyFixedLocalPath(root);
            // Resolve required modern exports before any directory/file mutation.
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!Path.IsPathFullyQualified(system)) return false;
            nint moduleName = Name(Path.Combine(system, "kernel32.dll"));
            kernelModule = Native.LoadLibraryExW(moduleName, 0, 0x800);
            if (kernelModule == 0) return false;
            kernelModuleOwned = true;
            if (kernelModule < 0) throw Refused();
            string[] requiredExports = mode == SampleMode.FreshWrite
                ? ["CreateDirectory2W", "CreateFile3", "GetFileInformationByHandleEx", "GetFileType", "GetVolumeInformationByHandleW", "CloseHandle", "LocalFree", "FreeLibrary"]
                : ["CreateFile3", "GetFileInformationByHandleEx", "GetFileType", "GetVolumeInformationByHandleW", "CloseHandle", "LocalFree", "FreeLibrary"];
            foreach (string export in requiredExports)
            {
                nint exportName = Allocate(export.Length + 1);
                for (int i = 0; i < export.Length; i++) Marshal.WriteByte(exportName, i, checked((byte)export[i]));
                if (Native.GetProcAddress(kernelModule, exportName) == 0) return false;
            }
            using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User?.Value ?? throw Refused();
            filesystemBuffer = Allocate(64);
            rootName = Name(root); markerName = Name(Path.Combine(root, ".madclean-fixture-owner"));
            directoryName = Name(directory); leaseName = Name(leasePath); journalName = Name(journalPath);
            rootParameters = Parameters(0x02010000, 0); // BACKUP_SEMANTICS + DISALLOW_PATH_REDIRECTS.
            markerParameters = Parameters(0x00010000, 0);
            string prefix = root[..3];
            for (int i = 0; i < components.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i > 0) prefix = Path.Combine(prefix, components[i - 1]);
                nint prefixName = Name(prefix);
                int slot = ancestorCount++;
                ancestorRaws[slot] = Native.CreateFile3(prefixName, 0x80, 3, 3, rootParameters);
                if (ancestorRaws[slot] == -1) { ancestorRaws[slot] = 0; return false; }
                ancestorOwned[slot] = true;
                if (ancestorRaws[slot] <= 0) throw Refused();
                for (int prior = 0; prior < slot; prior++)
                    if (ancestorRaws[prior] == ancestorRaws[slot]) throw Refused();
                ancestorWrappers[slot] = new(ancestorRaws[slot], ownsHandle: false);
                RequireDisk(ancestorWrappers[slot]!);
                SystemToolJournalStore.ValidateObject(ancestorWrappers[slot]!, prefix, true);
            }
            rootRaw = Native.CreateFile3(rootName, 0x20080, 3, 3, rootParameters);
            if (rootRaw == -1) { rootRaw = 0; return false; }
            Adopt(ref rootOwned, rootRaw);
            rootWrapper = new(rootRaw, ownsHandle: false);
            QualifyDirectory(rootWrapper, root);
            SystemToolJournalStore.VerifyCreationParentSecurity(rootWrapper, sid);
            rootIdentity = Identity(rootWrapper);
            markerRaw = Native.CreateFile3(markerName, 0x80000000, 1, 3, markerParameters);
            if (markerRaw == -1) { markerRaw = 0; return false; }
            Adopt(ref markerOwned, markerRaw);
            markerWrapper = new(markerRaw, ownsHandle: false);
            RequireDisk(markerWrapper);
            SystemToolJournalStore.ValidateObject(markerWrapper, Path.Combine(root, ".madclean-fixture-owner"), false, 32);
            markerStreamStarted = true;
            markerStream = new(markerWrapper, FileAccess.Read, 32, false);
            markerStreamReturned = true;
            byte[] bytes = new byte[32];
            try
            {
                markerStream.ReadExactly(bytes);
                if (markerStream.ReadByte() != -1 || Encoding.ASCII.GetString(bytes) != markerInput) return false;
            }
            finally { Array.Clear(bytes); }
            token.ThrowIfCancellationRequested();
            if (mode == SampleMode.FreshWrite)
            {
                directoryDescriptor = Descriptor($"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)", ref directoryDescriptorOwned);
                fileDescriptor = Descriptor($"O:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;SY)", ref fileDescriptorOwned);
                directoryAttributes = Security(directoryDescriptor);
                fileParameters = Parameters(0x80010000, Security(fileDescriptor)); // WRITE_THROUGH + DISALLOW.
            }
            else fileParameters = Parameters(0x00010000, 0); // No creation descriptor or write-through flag.
            prepared = true;
            return true;
        }

        internal override ModernJournalAcquisition AcquireDirectory()
        {
            RequirePhase(ModernJournalSamplePhase.Directory);
            if (!prepared || directoryAttempted || ReleaseStarted) throw Refused();
            directoryAttempted = true;
            if (mode == SampleMode.FreshWrite)
            {
                directoryRaw = Native.CreateDirectory2W(directoryName, 0x200A0, 3, 1, directoryAttributes);
                // The current official prose/sample disagree on the failure sentinel.
                // Neither zero nor -1 authorizes cleanup, retry, adoption or absence.
                if (directoryRaw <= 0) return ModernJournalAcquisition.Uncertain;
            }
            else
            {
                directoryRaw = Native.CreateFile3(directoryName, 0x200A0, 3, 3, rootParameters);
                if (directoryRaw == -1) { directoryRaw = 0; return ModernJournalAcquisition.NotAcquired; }
            }
            Adopt(ref directoryOwned, directoryRaw);
            DirectoryWrapper = new(directoryRaw, ownsHandle: false);
            QualifyDirectory(DirectoryWrapper, directory!);
            directoryIdentity = Identity(DirectoryWrapper);
            if (directoryIdentity.Volume != rootIdentity.Volume) throw Refused();
            RevalidateParents();
            return ModernJournalAcquisition.Acquired;
        }

        internal override ModernJournalAcquisition AcquireLease()
        {
            RequirePhase(ModernJournalSamplePhase.Lease);
            if (!directoryOwned || DirectoryWrapper == null || leaseAttempted || ReleaseStarted) throw Refused();
            leaseAttempted = true;
            RevalidateParents();
            leaseRaw = mode == SampleMode.FreshWrite
                ? Native.CreateFile3(leaseName, 0xC0020000, 0, 1, fileParameters)
                : Native.CreateFile3(leaseName, 0x80020000, 0, 3, fileParameters);
            if (leaseRaw == -1) { leaseRaw = 0; return ModernJournalAcquisition.NotAcquired; }
            Adopt(ref leaseOwned, leaseRaw);
            leaseWrapper = new(leaseRaw, ownsHandle: false);
            QualifyFile(leaseWrapper, leasePath!, 0);
            leaseIdentity = Identity(leaseWrapper);
            RevalidateParents();
            return ModernJournalAcquisition.Acquired;
        }

        internal override ModernJournalAcquisition AcquireJournal()
        {
            RequirePhase(ModernJournalSamplePhase.Journal);
            if (!leaseOwned || journalAttempted || ReleaseStarted) throw Refused();
            journalAttempted = true;
            RevalidateParents();
            journalRaw = mode == SampleMode.FreshWrite
                ? Native.CreateFile3(journalName, 0xC0020000, 0, 1, fileParameters)
                : Native.CreateFile3(journalName, 0x80020000, 0, 3, fileParameters);
            if (journalRaw == -1) { journalRaw = 0; return ModernJournalAcquisition.NotAcquired; }
            Adopt(ref journalOwned, journalRaw);
            JournalWrapper = new(journalRaw, ownsHandle: false);
            QualifyFile(JournalWrapper, journalPath!, mode == SampleMode.FreshWrite ? 0 : SystemToolJournalStore.MaximumIntentBytes);
            journalIdentity = Identity(JournalWrapper);
            RevalidateParents();
            return ModernJournalAcquisition.Acquired;
        }

        internal override bool ObserveSyntheticIntent()
        {
            RequirePhase(ModernJournalSamplePhase.Exercise);
            if (!journalOwned || exerciseAttempted || attachmentStarted || ReleaseStarted) throw Refused();
            exerciseAttempted = true;
            RevalidateParents();
            attachmentStarted = AttachmentReady = true;
            store = SystemToolJournalStore.AdoptQualifiedModernLifecycleFile(Attachment, out childStream);
            attachmentReturned = true; AttachmentReady = false;
            if (mode == SampleMode.ExistingReadOnly)
            {
                // Only this exact known synthetic journal is read. No enumeration,
                // empty-history admission, write, repair or first-intent provenance.
                var first = store.ReadJournal(DateTimeOffset.UtcNow, CancellationToken.None);
                var second = store.ReadJournal(DateTimeOffset.UtcNow, CancellationToken.None);
                QualifyAttachment(); // Recheck held object/parent identity and security after the reads.
                var observedUtc = DateTimeOffset.UtcNow;
                return ModernJournalSyntheticSeed.IsExpected(first, executionId, observedUtc) &&
                    ModernJournalSyntheticSeed.IsExpected(second, executionId, observedUtc) &&
                    first.Entries[0] == second.Entries[0];
            }
            var now = DateTimeOffset.UtcNow;
            var intent = new SystemToolJournalEntry(executionId, SystemToolOperation.DismCheckHealth, 1, now,
                SystemToolJournalPhase.DispatchIntent, null, null, null, null);
            var written = store.AppendJournalSnapshot(Array.AsReadOnly(new[] { intent }), now, CancellationToken.None);
            var readback = store.ReadJournal(DateTimeOffset.UtcNow, CancellationToken.None);
            return Exact(written, intent) && Exact(readback, intent);
        }
        private static bool Exact(SystemToolJournalReview review, SystemToolJournalEntry intent) =>
            ModernJournalSyntheticSeed.IsExpected(review, intent.ExecutionId, DateTimeOffset.UtcNow) && review.Entries[0] == intent;

        internal override bool ReleaseKnownOwnership()
        {
            RequirePhase(ModernJournalSamplePhase.Release);
            if (ReleaseStarted) return released;
            ReleaseStarted = true; AttachmentReady = false;
            if (descriptorUncertain || attachmentStarted && !attachmentReturned || markerStreamStarted && !markerStreamReturned)
                return false;
            try
            {
                if (store != null)
                {
                    store.Dispose();
                    if (SystemToolJournalStore.HasUncertainCleanup) return false;
                    store = null; childStream = null;
                }
                else if (childStream != null) { childStream.Dispose(); childStream = null; }
                if (markerStream != null) { markerStream.Dispose(); markerStream = null; }
                JournalWrapper?.Dispose(); leaseWrapper?.Dispose(); DirectoryWrapper?.Dispose();
                markerWrapper?.Dispose(); rootWrapper?.Dispose(); // All wrappers are non-owning.
                if (!Close(ref journalRaw, ref journalOwned) || !Close(ref leaseRaw, ref leaseOwned) ||
                    !Close(ref directoryRaw, ref directoryOwned) || !Close(ref markerRaw, ref markerOwned) ||
                    !Close(ref rootRaw, ref rootOwned)) return false;
                for (int i = ancestorCount - 1; i >= 0; i--)
                {
                    ancestorWrappers[i]?.Dispose();
                    if (!Close(ref ancestorRaws[i], ref ancestorOwned[i])) return false;
                }
                if (!FreeDescriptor(ref fileDescriptor, ref fileDescriptorOwned) ||
                    !FreeDescriptor(ref directoryDescriptor, ref directoryDescriptorOwned)) return false;
                for (int i = allocationCount - 1; i >= 0; i--)
                {
                    if (allocations[i] == 0) continue;
                    Marshal.FreeHGlobal(allocations[i]); allocations[i] = 0;
                }
                if (kernelModuleOwned)
                {
                    if (Native.FreeLibrary(kernelModule) == 0) return false;
                    kernelModuleOwned = false; kernelModule = 0;
                }
                released = true;
                return true;
            }
            catch { return false; }
        }

        private void RevalidateParents()
        {
            if (rootWrapper == null || DirectoryWrapper == null) throw Refused();
            QualifyDirectory(rootWrapper, root!); QualifyDirectory(DirectoryWrapper, directory!);
            if (!Same(Identity(rootWrapper), rootIdentity) || !Same(Identity(DirectoryWrapper), directoryIdentity)) throw Refused();
        }
        private void QualifyDirectory(SafeFileHandle handle, string expected)
        {
            RequireDisk(handle); SystemToolJournalStore.ValidateObject(handle, expected, true);
            SystemToolJournalStore.VerifyPersistentAclVolume(handle);
            for (int i = 0; i < 64; i++) Marshal.WriteByte(filesystemBuffer, i, 0);
            if (Native.GetVolumeInformation(handle, 0, 0, out _, out _, out uint flags, filesystemBuffer, 32) == 0 ||
                (flags & 8) == 0 || Marshal.PtrToStringUni(filesystemBuffer, 5) != "NTFS\0") throw Refused();
            SystemToolJournalStore.VerifyPrivateSecurity(handle, sid!, true);
        }
        internal void QualifyAttachment()
        {
            RequirePhase(ModernJournalSamplePhase.Exercise);
            if (!leaseOwned || !journalOwned || leaseWrapper == null || JournalWrapper == null || DirectoryWrapper == null ||
                leaseWrapper.IsClosed || leaseWrapper.IsInvalid || JournalWrapper.IsClosed || JournalWrapper.IsInvalid ||
                DirectoryWrapper.IsClosed || DirectoryWrapper.IsInvalid ||
                leaseWrapper.DangerousGetHandle() != leaseRaw || JournalWrapper.DangerousGetHandle() != journalRaw ||
                DirectoryWrapper.DangerousGetHandle() != directoryRaw) throw Refused();
            RevalidateParents();
            QualifyFile(leaseWrapper, leasePath!, 0);
            QualifyFile(JournalWrapper, journalPath!, mode == SampleMode.FreshWrite ? 0 : SystemToolJournalStore.MaximumIntentBytes);
            if (!Same(Identity(leaseWrapper), leaseIdentity) || !Same(Identity(JournalWrapper), journalIdentity)) throw Refused();
            RevalidateParents();
        }
        private void RequirePhase(ModernJournalSamplePhase phase)
        {
            Guard();
            if (!Domain.IsActive(this, phase)) throw Refused();
        }
        private void QualifyFile(SafeFileHandle handle, string expected, long maximum)
        {
            RequireDisk(handle); SystemToolJournalStore.ValidateObject(handle, expected, false, maximum);
            SystemToolJournalStore.VerifyPrivateSecurity(handle, sid!, false);
            if (Identity(handle).Volume != directoryIdentity.Volume) throw Refused();
        }
        private void Adopt(ref bool owned, nint raw)
        {
            // Store success output before wrappers/validation. Suspicious aliases retain all slots.
            owned = true;
            if (raw <= 0 || new[] { rootRaw, markerRaw, directoryRaw, leaseRaw, journalRaw }.Count(value => value == raw) != 1 ||
                ancestorRaws.Any(value => value == raw))
                throw Refused();
        }
        private nint Allocate(int bytes)
        {
            if (allocationCount >= allocations.Length) throw Refused();
            int slot = allocationCount++;
            allocations[slot] = Marshal.AllocHGlobal(bytes);
            for (int i = 0; i < bytes; i++) Marshal.WriteByte(allocations[slot], i, 0);
            return allocations[slot];
        }
        private nint Name(string value)
        {
            nint pointer = Allocate(checked((value.Length + 1) * 2));
            for (int i = 0; i < value.Length; i++) Marshal.WriteInt16(pointer, i * 2, unchecked((short)value[i]));
            return pointer;
        }
        private nint Security(nint descriptor)
        {
            nint pointer = Allocate(24);
            Marshal.StructureToPtr(new SecurityAttributes { Length = 24, Descriptor = descriptor }, pointer, false);
            return pointer;
        }
        private nint Parameters(uint flags, nint security)
        {
            nint pointer = Allocate(32);
            Marshal.StructureToPtr(new ExtendedParameters { Size = 32, Attributes = 0x80, Flags = flags, Security = security }, pointer, false);
            return pointer;
        }
        private nint Descriptor(string sddl, ref bool owned)
        {
            nint text = Name(sddl), output = Allocate(IntPtr.Size), size = Allocate(4);
            descriptorUncertain = true;
            if (Native.ConvertDescriptor(text, 1, output, size) == 0) throw Refused();
            nint descriptor = Marshal.ReadIntPtr(output);
            owned = true;
            // Unknown output shape remains rooted in output; no inferred failed-call free.
            if (descriptor <= 0 || Marshal.ReadInt32(size) is < 20 or > 65536) throw Refused();
            descriptorUncertain = false;
            return descriptor;
        }
        private static bool Close(ref nint raw, ref bool owned)
        {
            if (!owned) return true;
            if (Native.CloseHandle(raw) == 0) return false;
            owned = false; raw = 0; return true;
        }
        private static bool FreeDescriptor(ref nint raw, ref bool owned)
        {
            if (!owned) return true;
            if (raw == 0 || Native.LocalFree(raw) != 0) return false;
            owned = false; raw = 0; return true;
        }
        private static void RequireDisk(SafeFileHandle handle)
        { if (Native.GetFileType(handle) != 1) throw Refused(); }
        private static IdentityInformation Identity(SafeFileHandle handle)
        {
            if (Native.GetIdentity(handle, 18, out var result, 24) == 0 || result.Low == 0 && result.High == 0) throw Refused();
            return result;
        }
        private static bool Same(IdentityInformation left, IdentityInformation right) =>
            left.Volume == right.Volume && left.Low == right.Low && left.High == right.High;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { internal uint Length; internal nint Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedParameters
    { internal uint Size, Attributes, Flags, Qos; internal nint Security, Template; }
    [StructLayout(LayoutKind.Sequential)] private struct IdentityInformation
    { internal ulong Volume, Low, High; }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateDirectory2W", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint CreateDirectory2W(nint path, uint access, uint share, uint flags, nint security);
        [DllImport("kernel32.dll", EntryPoint = "CreateFile3", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint CreateFile3(nint path, uint access, uint share, uint disposition, nint parameters);
        [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int ConvertDescriptor(nint text, uint revision, nint descriptorOutput, nint sizeOutput);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetIdentity(SafeFileHandle handle, int information, out IdentityInformation identity, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileType", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetVolumeInformation(SafeFileHandle handle, nint volumeName, uint volumeNameSize,
            out uint serial, out uint maximumComponent, out uint flags, nint filesystemName, uint filesystemNameSize);
        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int CloseHandle(nint handle);
        [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint LocalFree(nint memory);
        [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int FreeLibrary(nint module);
        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint LoadLibraryExW(nint path, nint file, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GetProcAddress(nint module, nint name);
    }
}
