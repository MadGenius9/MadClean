using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void ShortcutDaclFixture(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "shortcut-dacl-fixture";
        string root = Path.Combine(fixture, "shortcut-dacl");
        string links = Path.Combine(root, "links"), targets = Path.Combine(root, "targets");
        string link = Path.Combine(links, "read-only-dacl.lnk"), absent = Path.Combine(targets, "absent.bin");
        string sentinel = Path.Combine(targets, "sentinel.bin");
        byte[]? original = null;
        byte[] sentinelBytes = Encoding.ASCII.GetBytes("Owned DACL evidence sentinel; no access-control mutation is requested.");
        ShortcutDaclAudit? before = null, after = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (WindowsShortcutInventory.HasPendingMetadataRead)
            { report.Unknown(check, "A previous shortcut metadata worker is pending; no DACL fixture work started."); return; }
            Directory.CreateDirectory(links); Directory.CreateDirectory(targets);
            using (var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(sentinelBytes);
            if (File.Exists(absent)) throw new SmokeAssertionException("shortcut-dacl-owned-target-already-existed");
            CreateShortcut(link, absent);
            original = File.ReadAllBytes(link);
            before = ShortcutDaclReadAudit(link, token);

            var provider = new WindowsShortcutInventory();
            var snapshot = Query(ct => provider.ScanAsync(links, ct), token);
            if (ShortcutDaclPending(report)) return;
            var scope = ShortcutEvidenceSelection(links, link, snapshot, token);
            if (scope == null)
            { report.Unknown(check, "The actual owned shortcut did not establish a genuine missing-target selection; no DACL projection proof is claimed."); return; }
            IShortcutActionPreview reader = provider;
            var result = Query(ct => reader.InspectAsync(scope, ct), token);
            if (ShortcutDaclPending(report)) return;
            if (result.Items.Count != 1 || result.Items[0].Protection != ShortcutProtectionState.OutsideKnownProtectedFolders ||
                result.Items[0].Evidence == null)
            { report.Unknown(check, "Known-folder or exact selected-file evidence was unavailable; no DACL or protection facts were manufactured."); return; }
            var preview = result.Items[0];
            var evidence = preview.Evidence!;
            var dacl = evidence.Dacl;
            if (dacl == null || dacl.State != CheckState.Available)
            { report.Unknown(check, $"Actual optional DACL state={dacl?.State.ToString() ?? "NotChecked"}; native descriptor facts and release remain unverified here."); return; }
            after = ShortcutDaclReadAudit(link, token);
            if (!before.MeaningfullyEquals(after) || before.AclBytes != after.AclBytes)
            {
                report.Unknown(check, "The independent before/after meaningful DACL observations differed or were not comparable. No ACL-change cause or unchanged-descriptor proof is inferred.");
                return;
            }
            if (preview.CanMove || preview.ReviewState != ShortcutActionReviewState.Preview ||
                preview.Current?.State != ShortcutTargetState.LocalTargetMissing || preview.SourceCoverageState != scope.SourceCoverageState ||
                evidence.VolumeSerialNumber != before.Volume || evidence.FileId != before.FileId ||
                dacl.Reason != ShortcutDaclReason.Observed || dacl.NativeResult != 0 ||
                dacl.DescriptorRelease != ShortcutDescriptorReleaseState.Released || dacl.DescriptorReleaseError != null ||
                dacl.QueryStartedUtc is not { } started || dacl.QueryCompletedUtc is not { } completed ||
                started < evidence.ReadStartedUtc || started > completed || completed > evidence.ReadCompletedUtc ||
                started.Offset != TimeSpan.Zero || completed.Offset != TimeSpan.Zero ||
                dacl.DaclPresent != before.Present || dacl.NullDacl != before.Null || dacl.DaclProtected != before.Protected ||
                dacl.DaclDefaulted != before.Defaulted || dacl.AceCount != before.AceCount ||
                dacl.AclBytes != before.AclBytes || dacl.Revision != before.Revision)
                throw new SmokeAssertionException("shortcut-dacl-native-projection-contract");
            if (!File.ReadAllBytes(link).AsSpan().SequenceEqual(original) ||
                !File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(sentinelBytes) || File.Exists(absent))
                throw new SmokeAssertionException("shortcut-dacl-fixture-content-boundary");
            report.Pass(check,
                $"providerState={result.State}; daclState={dacl.State}; present={dacl.DaclPresent}; nullDacl={dacl.NullDacl}; protected={dacl.DaclProtected}; defaulted={dacl.DaclDefaulted}; aceCount={dacl.AceCount}; aclBytes={dacl.AclBytes}; revision={dacl.Revision}; descriptorReleased=true; nativeFactsMatched=true; meaningfulDaclBytesUnchanged=true; linkAndSentinelBytesUnchanged=true; CanMove=false. Only one genuine owned file's current facts were observed; no ACL was set and no effective-access, ownership, privacy or recovery conclusion is made. Synthetic malformed/null/empty cases are not certified by this check.");
        }
        catch (SmokeAssertionException ex)
        { report.Fail(check, "Owned DACL evidence invariant " + ex.Code + " failed; no action or privacy proof is claimed."); }
        catch (ShortcutDaclAuditUnavailable)
        { report.Unknown(check, "The independent bounded DACL query, meaningful-byte audit or LocalFree release could not be established; no unchanged-ACL or privacy claim is made."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or
            System.ComponentModel.Win32Exception or InvalidOperationException or COMException or System.Security.SecurityException or OverflowException)
        { report.Unknown(check, $"The owned read-only DACL fixture could not complete (0x{ex.HResult:X8}); no descriptor, readiness or action proof is inferred. Pending workers retain their resources and require parent-tree retention."); }
        finally
        {
            before?.Dispose(); after?.Dispose();
            if (original != null) Array.Clear(original);
            Array.Clear(sentinelBytes);
        }
        // No local filesystem teardown or ACL mutation; parent owns the complete marked fixture tree.
    }

    private static bool ShortcutDaclPending(SmokeReport report)
    {
        if (!WindowsShortcutInventory.HasPendingMetadataRead) return false;
        report.Unknown("shortcut-dacl-fixture", "A shortcut metadata worker is pending; no further audit reads, fixture changes or local teardown were attempted. Retain the entire marked parent tree.");
        return true;
    }
    private sealed class ShortcutDaclAuditUnavailable : Exception { }
    private sealed class ShortcutDaclAudit : IDisposable
    {
        internal ulong Volume;
        internal Guid FileId;
        internal ushort MeaningfulControl;
        internal bool Present, Protected;
        internal bool? Null, Defaulted;
        internal int? AceCount, AclBytes, Revision;
        internal List<byte[]> AceRecords { get; } = [];
        internal bool MeaningfullyEquals(ShortcutDaclAudit other)
        {
            if (Volume != other.Volume || FileId != other.FileId || MeaningfulControl != other.MeaningfulControl ||
                Present != other.Present || Null != other.Null || Protected != other.Protected || Defaulted != other.Defaulted ||
                AceCount != other.AceCount || Revision != other.Revision || AceRecords.Count != other.AceRecords.Count) return false;
            // AclBytes is allocated ACL capacity and may include unused space. Compare actual
            // ordered ACE records, not SD addresses, reserved header bytes or unused ACL padding.
            for (int i = 0; i < AceRecords.Count; i++)
                if (!AceRecords[i].AsSpan().SequenceEqual(other.AceRecords[i])) return false;
            return true;
        }
        public void Dispose() { foreach (byte[] bytes in AceRecords) Array.Clear(bytes); AceRecords.Clear(); }
    }
    private static ShortcutDaclAudit ShortcutDaclReadAudit(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var handle = ShortcutDaclCreateFileW(path, 0x00020080, 1, IntPtr.Zero, 3, 0x00200000 | 0x00100000, IntPtr.Zero);
        if (handle.IsInvalid || !ShortcutEvidenceGetIdentity(handle, 18, out var identity, (uint)Marshal.SizeOf<ShortcutEvidenceId>()))
            throw new ShortcutDaclAuditUnavailable();
        var audit = new ShortcutDaclAudit { Volume = identity.Volume, FileId = identity.Id };
        IntPtr descriptor = IntPtr.Zero;
        bool complete = false, released = false;
        try
        {
            // DACL_SECURITY_INFORMATION only: never request owner, group or SACL information.
            uint code = ShortcutDaclGetSecurityInfo(handle, 1, 4, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out descriptor);
            token.ThrowIfCancellationRequested();
            if (code != 0 || descriptor == IntPtr.Zero || !ShortcutDaclIsValidSecurityDescriptor(descriptor) ||
                !ShortcutDaclGetControl(descriptor, out ushort control, out uint descriptorRevision) || descriptorRevision != 1 ||
                (control & 0x8000) == 0) throw new ShortcutDaclAuditUnavailable();
            uint descriptorBytes = ShortcutDaclDescriptorLength(descriptor);
            if (descriptorBytes is < 20 or > 65536) throw new ShortcutDaclAuditUnavailable();
            audit.Protected = (control & 0x1000) != 0;
            if (!ShortcutDaclGetDacl(descriptor, out bool present, out var acl, out bool defaulted) || present != ((control & 4) != 0))
                throw new ShortcutDaclAuditUnavailable();
            audit.Present = present;
            audit.MeaningfulControl = (ushort)(control & 0x1004); // Present and protected remain independently reported.
            if (present)
            {
                audit.MeaningfulControl |= (ushort)(control & 0x0508); // Defaulted/auto-inheritance only for a present DACL.
                audit.Defaulted = defaulted; audit.Null = acl == IntPtr.Zero;
                if (defaulted != ((control & 8) != 0)) throw new ShortcutDaclAuditUnavailable();
                if (acl != IntPtr.Zero)
                {
                    long begin = descriptor.ToInt64(), end = checked(begin + descriptorBytes), pointer = acl.ToInt64();
                    if (pointer < begin || pointer > end - 8 || (pointer & 3) != 0 || !ShortcutDaclIsValidAcl(acl))
                        throw new ShortcutDaclAuditUnavailable();
                    // Independently inspect the documented ACL header, rather than reuse the provider's summary reader.
                    int revision = Marshal.ReadByte(acl), bytes = unchecked((ushort)Marshal.ReadInt16(acl, 2));
                    int count = unchecked((ushort)Marshal.ReadInt16(acl, 4));
                    if (revision is not (2 or 4) || bytes < 8 || bytes > end - pointer || count > 4096)
                        throw new ShortcutDaclAuditUnavailable();
                    audit.Revision = revision; audit.AclBytes = bytes; audit.AceCount = count;
                    long previousEnd = pointer + 8;
                    for (uint i = 0; i < count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!ShortcutDaclGetAce(acl, i, out var ace)) throw new ShortcutDaclAuditUnavailable();
                        long address = ace.ToInt64();
                        if (address < previousEnd || address > pointer + bytes - 4) throw new ShortcutDaclAuditUnavailable();
                        int size = unchecked((ushort)Marshal.ReadInt16(ace, 2));
                        if (size < 4 || size > pointer + bytes - address) throw new ShortcutDaclAuditUnavailable();
                        byte[] raw = new byte[size]; Marshal.Copy(ace, raw, 0, raw.Length); audit.AceRecords.Add(raw);
                        previousEnd = address + size;
                    }
                }
            }
            complete = true;
        }
        finally
        {
            try { if (descriptor != IntPtr.Zero) released = ShortcutDaclLocalFree(descriptor) == IntPtr.Zero; }
            finally { if (!complete || !released) audit.Dispose(); }
        }
        if (!released) throw new ShortcutDaclAuditUnavailable();
        return audit;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle ShortcutDaclCreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ShortcutDaclGetSecurityInfo(SafeFileHandle handle, uint objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", EntryPoint = "IsValidSecurityDescriptor", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShortcutDaclIsValidSecurityDescriptor(IntPtr descriptor);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorControl", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShortcutDaclGetControl(IntPtr descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ShortcutDaclDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorDacl", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShortcutDaclGetDacl(IntPtr descriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr acl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", EntryPoint = "IsValidAcl", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShortcutDaclIsValidAcl(IntPtr acl);
    [DllImport("advapi32.dll", EntryPoint = "GetAce", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShortcutDaclGetAce(IntPtr acl, uint index, out IntPtr ace);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr ShortcutDaclLocalFree(IntPtr memory);
}
