using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MadClean.Core;

namespace MadClean.Windows;

internal enum SystemToolJournalFixtureCase { Primary, WrongAcl, Reparse, Oversize, Lifecycle }
internal enum SystemToolJournalStoreReason
{
    ScopeUnsupported, PersistentAclUnavailable, SecurityUnsupported, UnsafeObject, Oversize,
    ContentMismatch, NativeUnavailable, StoreBusy, CleanupUncertain
}
internal sealed class SystemToolJournalStoreFailure(SystemToolJournalStoreReason reason, int? nativeCode = null)
    : IOException("The owned journal-store boundary could not be established.")
{
    internal SystemToolJournalStoreReason Reason { get; } = reason;
    internal int? NativeCode { get; } = nativeCode;
}

// Owned development infrastructure only. No production scope, process dispatch,
// real-client recovery or hostile-same-user guarantee exists here.
internal sealed class SystemToolJournalStore : IDisposable
{
    private const uint ReadAttributes = 0x80, ReadControl = 0x20000, ReadWrite = 0xC0000000;
    private const uint OpenReparse = 0x00200000, NoRecall = 0x00100000, Backup = 0x02000000, WriteThrough = 0x80000000;
    private const uint Unsafe = 0x400 | 0x1000 | 0x4000 | 0x40 | 0x40000 | 0x400000;
    internal const int MaximumIntentBytes = 4096;
    private static readonly byte[] FixedIntent = Encoding.ASCII.GetBytes("MadClean.InertJournalStore.v1\n");
    private static readonly object CleanupSync = new();
    private static readonly List<nint> RetainedDescriptors = [];
    private static int cleanupUncertain;
    internal static bool HasUncertainCleanup => Volatile.Read(ref cleanupUncertain) != 0;
    private readonly List<IDisposable> guards;
    private readonly FileStream intent;
    private readonly SafeFileHandle directory;
    private bool disposed, writeAttempted, journalAppendUncertain;
    private readonly SystemToolJournalFixtureCase fixtureCase;
    private bool readOnly;
    internal bool CreatedIntentFile { get; }
    internal SafeFileHandle IntentHandle => intent.SafeFileHandle;
    internal SafeFileHandle DirectoryHandle => directory;

    private SystemToolJournalStore(FileStream intent, SafeFileHandle directory, List<IDisposable> guards, bool created,
        SystemToolJournalFixtureCase fixtureCase)
    {
        this.intent = intent; this.directory = directory; this.guards = guards;
        CreatedIntentFile = created; this.fixtureCase = fixtureCase;
    }

    internal static string FixtureLeafName(SystemToolJournalFixtureCase fixtureCase) => fixtureCase switch
    {
        SystemToolJournalFixtureCase.Primary => "system-tool-journal-primary",
        SystemToolJournalFixtureCase.WrongAcl => "system-tool-journal-wrong-acl",
        SystemToolJournalFixtureCase.Reparse => "system-tool-journal-reparse",
        SystemToolJournalFixtureCase.Oversize => "system-tool-journal-oversize",
        SystemToolJournalFixtureCase.Lifecycle => "system-tool-journal-lifecycle",
        _ => throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported)
    };

    internal static SystemToolJournalStore OpenOwnedFixture(string root, string ownerMarker,
        SystemToolJournalFixtureCase fixtureCase = SystemToolJournalFixtureCase.Primary, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Marshal.SizeOf<SecurityAttributes>() != 24 ||
            Marshal.SizeOf<StandardInformation>() != 24 || Marshal.SizeOf<IdentityInformation>() != 24)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        string leaf = FixtureLeafName(fixtureCase);
        string normalized = ValidateRoot(root, ownerMarker);
        string folder = Path.Combine(normalized, leaf), path = Path.Combine(folder, "intent.bin");
        if (path.Length > 258) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        List<IDisposable> owned = [];
        FileStream? stream = null;
        try
        {
            if (!WindowsShortcutNative.TryOpenAncestry(normalized, true, token, out var ancestry, out _))
                throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.UnsafeObject);
            owned.Add(ancestry!);
            using (var marker = Open(Path.Combine(normalized, ".madclean-fixture-owner"), 0x80000000, 1, false))
            {
                ValidateObject(marker, Path.Combine(normalized, ".madclean-fixture-owner"), false, 32);
                using var input = new FileStream(marker, FileAccess.Read, 32, false);
                byte[] bytes = new byte[32];
                try
                {
                    input.ReadExactly(bytes);
                    if (Encoding.ASCII.GetString(bytes) != ownerMarker)
                        throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
                }
                finally { Array.Clear(bytes); }
            }
            using var volume = Open(normalized, ReadAttributes, 3, true);
            ValidateObject(volume, normalized, true);
            if (!GetVolumeInformationByHandleW(volume, null, 0, out _, out _, out uint flags, null, 0)) throw Native();
            if ((flags & 8) == 0) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.PersistentAclUnavailable);
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.SecurityUnsupported);
            token.ThrowIfCancellationRequested();
            CreatePrivateDirectory(folder, sid);
            if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
            var directory = Open(folder, ReadAttributes | ReadControl, 3, true);
            owned.Add(directory);
            ValidateObject(directory, folder, true);
            VerifyPrivateSecurity(directory, sid, true);
            token.ThrowIfCancellationRequested();
            bool created;
            SafeFileHandle handle = CreateIntent(path, sid, out created);
            try
            {
                ValidateObject(handle, path, false, MaximumIntentBytes);
                VerifyPrivateSecurity(handle, sid, false);
                token.ThrowIfCancellationRequested();
                stream = new FileStream(handle, FileAccess.ReadWrite, 4096, false);
            }
            catch { handle.Dispose(); throw; }
            if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
            var result = new SystemToolJournalStore(stream, directory, owned, created, fixtureCase);
            stream = null; owned = [];
            return result;
        }
        finally
        {
            DisposeChecked(stream);
            for (int i = owned.Count - 1; i >= 0; i--) DisposeChecked(owned[i]);
        }
    }

    internal bool WriteAndReadFixedIntent(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (fixtureCase == SystemToolJournalFixtureCase.Lifecycle)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        token.ThrowIfCancellationRequested();
        if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
        if (CreatedIntentFile && !writeAttempted && intent.Length == 0)
        {
            writeAttempted = true;
            intent.Write(FixedIntent); intent.Flush(flushToDisk: true);
        }
        // Existing bytes are never repaired/replaced. No interpretation as real intent,
        // persisted operation status, authorship or recovery evidence is supported.
        if (intent.Length != FixedIntent.Length)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ContentMismatch);
        intent.Position = 0;
        byte[] actual = new byte[FixedIntent.Length];
        try
        {
            intent.ReadExactly(actual); token.ThrowIfCancellationRequested();
            if (!actual.AsSpan().SequenceEqual(FixedIntent))
                throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ContentMismatch);
            return true;
        }
        finally { Array.Clear(actual); }
    }

    // The private-store facade has already established fixed scope and owns the
    // directory/ancestry leases. The returned file store borrows that directory.
    internal static SystemToolJournalStore OpenLifecycleFile(string path, SafeFileHandle directory,
        string sid, bool createNew, bool readOnly, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (HasUncertainCleanup || createNew && readOnly)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        SafeFileHandle handle = createNew ? CreateIntent(path, sid, out _, requireNew: true)
            : Open(path, readOnly ? 0x80020000u : ReadWrite, 0, false, readOnly ? 0 : WriteThrough);
        try
        {
            ValidateObject(handle, path, false, MaximumIntentBytes);
            VerifyPrivateSecurity(handle, sid, false);
            token.ThrowIfCancellationRequested();
            var stream = new FileStream(handle, readOnly ? FileAccess.Read : FileAccess.ReadWrite, 4096, false);
            return new(stream, directory, [], createNew, SystemToolJournalFixtureCase.Lifecycle) { readOnly = readOnly };
        }
        catch { DisposeChecked(handle); throw; }
    }
    internal static void VerifyPersistentAclVolume(SafeFileHandle volume)
    {
        if (Marshal.SizeOf<SecurityAttributes>() != 24 || Marshal.SizeOf<StandardInformation>() != 24 || Marshal.SizeOf<IdentityInformation>() != 24)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        if (!GetVolumeInformationByHandleW(volume, null, 0, out _, out _, out uint flags, null, 0)) throw Native();
        if ((flags & 8) == 0) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.PersistentAclUnavailable);
    }
    // Used only by the closed relative-file candidate after same-handle qualification.
    // The native frame owns the raw child and publishes this stream into its own
    // slot before allocating a store. No path is opened and no handle is transferred.
    internal static SystemToolJournalStore AdoptQualifiedRelativeLifecycleFile(SafeFileHandle handle,
        SafeFileHandle parent, bool created, bool readOnly, out FileStream stream)
    {
        if (!WindowsSystemToolJournalRelativeFile.NativeCandidateExecutionValidated)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        return AdoptQualifiedLifecycleMechanics(handle, parent, created, readOnly, out stream);
    }
    // The modern fixed sample must supply its actual registered frame's one-shot
    // attachment. This does not relax the separate old Nt candidate boundary above.
    internal static SystemToolJournalStore AdoptQualifiedModernLifecycleFile(
        WindowsModernOwnedJournalSample.LifecycleAttachment attachment, out FileStream stream)
    {
        if (!WindowsModernOwnedJournalSample.ExecutionValidated)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        if (!attachment.TryClaim(out var handle, out var parent))
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        return AdoptQualifiedLifecycleMechanics(handle, parent, true, false, out stream);
    }
    private static SystemToolJournalStore AdoptQualifiedLifecycleMechanics(SafeFileHandle handle,
        SafeFileHandle parent, bool created, bool readOnly, out FileStream stream)
    {
        if (HasUncertainCleanup || handle.IsInvalid || handle.IsClosed || parent.IsInvalid || parent.IsClosed || created && readOnly)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        stream = new FileStream(handle, readOnly ? FileAccess.Read : FileAccess.ReadWrite, 4096, false);
        return new(stream, parent, [], created, SystemToolJournalFixtureCase.Lifecycle) { readOnly = readOnly };
    }
    internal static nint CreateRelativeFileDescriptor(string sid)
    {
        if (!WindowsSystemToolJournalRelativeFile.NativeCandidateExecutionValidated)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        return Descriptor(sid, false);
    }
    internal static bool ReleaseRelativeFileDescriptor(nint descriptor)
    {
        if (!WindowsSystemToolJournalRelativeFile.NativeCandidateExecutionValidated || HasUncertainCleanup) return false;
        FreeDescriptor(descriptor);
        return !HasUncertainCleanup;
    }
    internal static (ulong Volume, ulong Low, ulong High) RelativeFileIdentity(SafeFileHandle handle)
    {
        if (!WindowsSystemToolJournalRelativeFile.NativeCandidateExecutionValidated)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        if (!GetIdentity(handle, 18, out var identity, 24)) throw Native();
        if (identity.Low == 0 && identity.High == 0)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.UnsafeObject);
        return (identity.Volume, identity.Low, identity.High);
    }
    internal static void VerifyFixedLocalPath(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (root == null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\')
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
    }

    internal SystemToolJournalReview ReadJournal(DateTimeOffset nowUtc, CancellationToken token = default)
    {
        ValidateJournalLease(token);
        if (journalAppendUncertain) return UnknownJournal(nowUtc);
        byte[] bytes = ReadJournalBytes(token);
        try { return SystemToolJournal.DecodeSnapshots(bytes, nowUtc); }
        finally { Array.Clear(bytes); }
    }

    internal SystemToolJournalReview AppendJournalSnapshot(IReadOnlyList<SystemToolJournalEntry> entries,
        DateTimeOffset nowUtc, CancellationToken token = default)
    {
        if (readOnly) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        ValidateJournalLease(token);
        if (journalAppendUncertain || !SystemToolJournal.TryEncode(entries, nowUtc, out byte[]? canonical) || canonical == null)
            return UnknownJournal(nowUtc);
        byte[]? existing = null, expected = null, actual = null;
        bool appendStarted = false, confirmed = false;
        try
        {
            // Use the codec's canonical immutable snapshot rather than caller-owned
            // mutable entry collections for all subsequent comparisons and writes.
            var candidate = SystemToolJournal.Decode(canonical, nowUtc);
            if (candidate.State == SystemToolJournalState.Unknown) return UnknownJournal(nowUtc);
            existing = ReadJournalBytes(token);
            if (existing.Length == 0)
            {
                // Reopening an empty file must not manufacture first-intent provenance.
                if (!CreatedIntentFile || writeAttempted || candidate.Entries.Count != 1 ||
                    candidate.Entries[0].Phase != SystemToolJournalPhase.DispatchIntent) return UnknownJournal(nowUtc);
            }
            else
            {
                var previous = SystemToolJournal.DecodeSnapshots(existing, nowUtc);
                if (previous.State == SystemToolJournalState.Unknown || candidate.Entries.Count != previous.Entries.Count + 1)
                    return UnknownJournal(nowUtc);
                for (int i = 0; i < previous.Entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (previous.Entries[i] != candidate.Entries[i]) return UnknownJournal(nowUtc);
                }
            }
            int total = checked(existing.Length + canonical.Length + 1);
            if (total > MaximumIntentBytes || total > SystemToolJournal.MaximumUtf8Bytes || intent.Length != existing.Length)
                return UnknownJournal(nowUtc);
            expected = new byte[total];
            existing.CopyTo(expected, 0); canonical.CopyTo(expected, existing.Length); expected[^1] = (byte)'\n';
            token.ThrowIfCancellationRequested();
            if (intent.Seek(existing.Length, SeekOrigin.Begin) != existing.Length)
                return UnknownJournal(nowUtc);
            // From this point, finish synchronous flush/readback while this lease owns
            // the stream even if the caller requests cancellation. Never truncate,
            // reset or retry a partial write, and never describe cancellation as rollback.
            appendStarted = true; writeAttempted = true;
            intent.Write(expected.AsSpan(existing.Length));
            intent.Flush(flushToDisk: true);
            actual = ReadJournalBytes(CancellationToken.None);
            if (!actual.AsSpan().SequenceEqual(expected)) return UnknownJournal(nowUtc);
            var observed = SystemToolJournal.DecodeSnapshots(actual, nowUtc);
            if (observed.State == SystemToolJournalState.Unknown || observed.Entries.Count != candidate.Entries.Count)
                return UnknownJournal(nowUtc);
            for (int i = 0; i < candidate.Entries.Count; i++)
                if (observed.Entries[i] != candidate.Entries[i]) return UnknownJournal(nowUtc);
            confirmed = true;
            token.ThrowIfCancellationRequested();
            return observed;
        }
        finally
        {
            if (appendStarted && !confirmed) journalAppendUncertain = true;
            Array.Clear(canonical);
            if (existing != null) Array.Clear(existing);
            if (expected != null) Array.Clear(expected);
            if (actual != null) Array.Clear(actual);
        }
    }

    private void ValidateJournalLease(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (fixtureCase != SystemToolJournalFixtureCase.Lifecycle)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
    }
    private byte[] ReadJournalBytes(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long length = intent.Length;
        if (length < 0 || length > MaximumIntentBytes || length > SystemToolJournal.MaximumUtf8Bytes)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.Oversize);
        if (intent.Seek(0, SeekOrigin.Begin) != 0)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ContentMismatch);
        byte[] bytes = new byte[(int)length];
        try
        {
            int read = 0;
            while (read < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = intent.Read(bytes, read, bytes.Length - read);
                if (count == 0) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ContentMismatch);
                read += count;
            }
            if (intent.Length != length) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ContentMismatch);
            token.ThrowIfCancellationRequested();
            return bytes;
        }
        catch { Array.Clear(bytes); throw; }
    }
    private static SystemToolJournalReview UnknownJournal(DateTimeOffset nowUtc)
        => SystemToolJournal.DecodeSnapshots(ReadOnlySpan<byte>.Empty, nowUtc);

    internal static string ValidateRoot(string root, string marker)
    {
        if (!Guid.TryParseExact(marker, "N", out var owner) || owner == Guid.Empty || marker != owner.ToString("N") ||
            string.IsNullOrEmpty(root) || root.Length > 210 || root != Path.GetFullPath(root) || root.StartsWith("\\", StringComparison.Ordinal))
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        string name = Path.GetFileName(root); const string prefix = "windows-validation-fixtures-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty ||
            Path.GetFileName(Path.GetDirectoryName(root)) != "artifacts")
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ScopeUnsupported);
        return root;
    }

    internal static void CreatePrivateDirectory(string path, string sid)
    {
        nint descriptor = Descriptor(sid, true);
        try
        {
            var attributes = Attributes(descriptor);
            if (!CreateDirectoryW(path, ref attributes))
            {
                int code = Marshal.GetLastWin32Error();
                if (code != 183) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.NativeUnavailable, code);
            }
        }
        finally { FreeDescriptor(descriptor); }
    }
    internal static SafeFileHandle CreateIntent(string path, string sid, out bool created, bool requireNew = false)
    {
        created = false;
        nint descriptor = Descriptor(sid, false);
        SafeFileHandle? handle = null;
        try
        {
            var attributes = Attributes(descriptor);
            handle = CreateFileSecured(path, ReadWrite, 0, ref attributes, 1, WriteThrough | OpenReparse | NoRecall, 0);
            if (!handle.IsInvalid) { created = true; return handle; }
            int code = Marshal.GetLastWin32Error(); handle.Dispose(); handle = null;
            if (code is not (80 or 183)) throw new SystemToolJournalStoreFailure(code == 32 ? SystemToolJournalStoreReason.StoreBusy : SystemToolJournalStoreReason.NativeUnavailable, code);
            if (requireNew) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.ContentMismatch, code);
            // Opening an existing object does not apply the creation descriptor. Its
            // actual same-handle security must be verified before reading any bytes.
            handle = Open(path, ReadWrite, 0, false, WriteThrough);
            return handle;
        }
        finally
        {
            FreeDescriptor(descriptor);
            if (HasUncertainCleanup) DisposeChecked(handle);
        }
    }
    private static nint Descriptor(string sid, bool directory)
    {
        if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
        string inheritance = directory ? "OICI" : "";
        string sddl = $"O:{sid}D:P(A;{inheritance};FA;;;{sid})(A;{inheritance};FA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out nint descriptor, out _)) throw Native();
        return descriptor;
    }
    private static SecurityAttributes Attributes(nint descriptor) => new() { Length = 24, Descriptor = descriptor, Inherit = 0 };

    // Namespace exclusion snapshot only. It neither grants creation rights nor replaces
    // the stricter protected user+SYSTEM descriptor required for private store objects.
    internal static void VerifyCreationParentSecurity(SafeFileHandle handle, string sid)
    {
        nint descriptor = ReadOwnedSecurityDescriptor(handle);
        try
        {
            if (descriptor == 0 || !IsValidSecurityDescriptor(descriptor)) throw Security();
            uint length = GetSecurityDescriptorLength(descriptor);
            if (length is < 20 or > 65536) throw Security();
            byte[] bytes = new byte[length];
            try
            {
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var security = new RawSecurityDescriptor(bytes, 0);
                var acl = security.DiscretionaryAcl;
                if (acl == null || acl.Count is < 1 or > JournalCreationParentSecurity.MaximumAces) throw Security();
                var entries = new JournalParentAce[acl.Count];
                for (int i = 0; i < entries.Length; i++)
                {
                    if (acl[i] is CommonAce ace && !ace.IsCallback && ace.AceQualifier == AceQualifier.AccessAllowed)
                        entries[i] = new(JournalParentAceKind.OrdinaryAllow, ace.SecurityIdentifier.Value, ace.AccessMask, (int)ace.AceFlags);
                    else throw Security();
                }
                if (!JournalCreationParentSecurity.IsSupported(security.Owner?.Value, sid,
                    (security.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0, entries)) throw Security();
            }
            finally { Array.Clear(bytes); }
        }
        finally { FreeDescriptor(descriptor); }
        if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
    }

    internal static void VerifyPrivateSecurity(SafeFileHandle handle, string sid, bool directory)
    {
        nint descriptor = ReadOwnedSecurityDescriptor(handle);
        try
        {
            if (descriptor == 0 || !IsValidSecurityDescriptor(descriptor)) throw Security();
            uint length = GetSecurityDescriptorLength(descriptor);
            // API-owned allocation is validated after return; this is not a native allocation cap.
            if (length is < 20 or > 65536) throw Security();
            byte[] bytes = new byte[length];
            try
            {
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var security = new RawSecurityDescriptor(bytes, 0);
                if (security.Owner?.Value != sid || security.DiscretionaryAcl is not { Count: 2 } acl ||
                    (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0) throw Security();
                bool user = false, system = false;
                foreach (var entry in acl)
                {
                    if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed ||
                        ace.AccessMask != 0x1F01FF || ace.AceFlags != (directory ? AceFlags.ObjectInherit | AceFlags.ContainerInherit : AceFlags.None)) throw Security();
                    if (ace.SecurityIdentifier.Value == sid && !user) user = true;
                    else if (ace.SecurityIdentifier.Value == "S-1-5-18" && !system) system = true;
                    else throw Security();
                }
                if (!user || !system) throw Security();
            }
            finally { Array.Clear(bytes); }
        }
        finally { FreeDescriptor(descriptor); }
        if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
    }

    private static nint ReadOwnedSecurityDescriptor(SafeFileHandle handle)
    {
        if (HasUncertainCleanup) throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.CleanupUncertain);
        uint code = GetSecurityInfo(handle, 1, 5, 0, 0, 0, 0, out nint descriptor);
        if (code == 0) return descriptor;
        // Treat only a successful call as a known allocation we may inspect/free.
        // A nonzero failed output is opaque: keep it without dereference or LocalFree.
        if (descriptor != 0)
        {
            lock (CleanupSync) { Volatile.Write(ref cleanupUncertain, 1); RetainedDescriptors.Add(descriptor); }
        }
        throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.NativeUnavailable, unchecked((int)code));
    }

    internal static SafeFileHandle Open(string path, uint access, uint share, bool directory, uint extra = 0)
    {
        var handle = CreateFilePlain(path, access, share, 0, 3, OpenReparse | NoRecall | extra | (directory ? Backup : 0), 0);
        if (!handle.IsInvalid) return handle;
        int code = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new SystemToolJournalStoreFailure(code == 32 ? SystemToolJournalStoreReason.StoreBusy : SystemToolJournalStoreReason.NativeUnavailable, code);
    }
    internal static void ValidateObject(SafeFileHandle handle, string expected, bool directory, long maximum = 0)
    {
        if (!GetTag(handle, 9, out var tag, 8) || !GetStandard(handle, 1, out var standard, 24) || !GetIdentity(handle, 18, out var id, 24)) throw Native();
        if ((tag.Attributes & Unsafe) != 0 || tag.ReparseTag != 0 || ((tag.Attributes & 0x10) != 0) != directory ||
            standard.Directory != directory || standard.DeletePending || (id.Low == 0 && id.High == 0) || !directory && standard.Links != 1)
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.UnsafeObject);
        if (!directory && (standard.Bytes < 0 || standard.Bytes > maximum))
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.Oversize);
        var final = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (count == 0 || count >= final.Capacity) throw Native();
        string path = final.ToString();
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
            throw new SystemToolJournalStoreFailure(SystemToolJournalStoreReason.UnsafeObject);
    }
    private static SystemToolJournalStoreFailure Security() => new(SystemToolJournalStoreReason.SecurityUnsupported);
    private static SystemToolJournalStoreFailure Native() => new(SystemToolJournalStoreReason.NativeUnavailable, Marshal.GetLastWin32Error());
    private static void FreeDescriptor(nint descriptor)
    {
        if (descriptor == 0) return;
        if (LocalFree(descriptor) == 0) return;
        // Latch before the managed list may allocate, including under memory pressure.
        lock (CleanupSync) { Volatile.Write(ref cleanupUncertain, 1); RetainedDescriptors.Add(descriptor); }
    }
    internal static void DisposeChecked(IDisposable? value)
    {
        try { value?.Dispose(); }
        catch (Exception) { Volatile.Write(ref cleanupUncertain, 1); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DisposeChecked(intent);
        for (int i = guards.Count - 1; i >= 0; i--) DisposeChecked(guards[i]);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal uint Length; internal nint Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct TagInformation { internal uint Attributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct IdentityInformation { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInformation
    { internal long Allocation, Bytes; internal uint Links; [MarshalAs(UnmanagedType.U1)] internal bool DeletePending; [MarshalAs(UnmanagedType.U1)] internal bool Directory; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeFileHandle CreateFilePlain(string path, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeFileHandle CreateFileSecured(string path, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint bytes);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint information, nint owner, nint group, nint dacl, nint sacl, out nint descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsValidSecurityDescriptor(nint descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? name, uint nameCapacity, out uint serial, out uint maximumComponent, out uint flags, StringBuilder? fileSystem, uint fileSystemCapacity);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTag(SafeFileHandle handle, int informationClass, out TagInformation information, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(SafeFileHandle handle, int informationClass, out StandardInformation information, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdentity(SafeFileHandle handle, int informationClass, out IdentityInformation information, uint bytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
