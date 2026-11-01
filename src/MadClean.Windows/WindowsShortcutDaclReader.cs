using System.Runtime.InteropServices;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>
/// Aggregate DACL facts copied from one already-held file. No owner/group/SACL query,
/// ACE/SID export, access evaluation, ACL changes or effective privacy conclusion.
/// </summary>
internal static class WindowsShortcutDaclReader
{
    private const uint DaclSecurityInformation = 4, FileObject = 1;
    private const ushort DaclPresent = 4, DaclDefaulted = 8, DaclProtected = 0x1000, SelfRelative = 0x8000;
    private const uint MaximumDescriptorBytes = 131_072;
    private static readonly SemaphoreSlim QueryGate = new(1, 1);
    // An allocation whose release could not be confirmed remains owned here. There
    // are no retries or further allocations after that event, and no worker is parked.
    private static nint retainedDescriptor;

    internal static ShortcutDaclObservation Read(SafeFileHandle heldHandle, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Empty(CheckState.Unsupported, ShortcutDaclReason.Unsupported);
        if (heldHandle == null || heldHandle.IsClosed || heldHandle.IsInvalid)
            return Empty(CheckState.Unavailable, ShortcutDaclReason.NativeQueryFailed);
        if (!QueryGate.Wait(0)) return Empty(CheckState.Unavailable, ShortcutDaclReason.NativeQueryFailed);
        nint descriptor = 0;
        bool handleReference = false;
        var started = DateTimeOffset.UtcNow;
        ShortcutDaclObservation observation = Empty(CheckState.Unknown, ShortcutDaclReason.MetadataInvalid);
        try
        {
            if (retainedDescriptor != 0)
                // A prior allocation is retained, but this call attempted no new query
                // or release. Never attribute a previous file's native error to this file.
                observation = Empty(CheckState.Unavailable, ShortcutDaclReason.DescriptorReleaseUnconfirmed);
            else
            {
                heldHandle.DangerousAddRef(ref handleReference);
                token.ThrowIfCancellationRequested();
                // GENERIC_READ maps READ_CONTROL. Do not reopen with extra rights, use
                // a path lookup, request owner/group/SACL, or enable any privilege.
                uint code = GetSecurityInfo(heldHandle, FileObject, DaclSecurityInformation, 0, 0, 0, 0, out descriptor);
                observation = Empty(code == 0 ? CheckState.Unknown : CheckState.Unavailable,
                    code == 0 ? ShortcutDaclReason.MetadataInvalid : ShortcutDaclReason.NativeQueryFailed) with { NativeResult = code };
                if (code == 0) observation = Inspect(descriptor, token);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { observation = Empty(CheckState.Unsupported, ShortcutDaclReason.Unsupported) with { NativeResult = observation.NativeResult }; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException or
            System.Security.SecurityException)
        { observation = Empty(CheckState.Unknown, ShortcutDaclReason.MetadataInvalid) with { NativeResult = observation.NativeResult }; }
        finally
        {
            try
            {
                if (descriptor != 0)
                {
                    uint? releaseError = null;
                    bool released;
                    try
                    {
                        released = LocalFree(descriptor) == 0;
                        if (!released) releaseError = unchecked((uint)Marshal.GetLastWin32Error());
                    }
                    catch (Exception)
                    {
                        // No native status is invented if cleanup itself is unavailable.
                        released = false;
                    }
                    if (released)
                        observation = observation with { DescriptorRelease = ShortcutDescriptorReleaseState.Released };
                    else
                    {
                        retainedDescriptor = descriptor;
                        observation = Empty(CheckState.Unknown, ShortcutDaclReason.DescriptorReleaseUnconfirmed) with
                        {
                            NativeResult = observation.NativeResult, DescriptorRelease = ShortcutDescriptorReleaseState.Unconfirmed,
                            DescriptorReleaseError = releaseError
                        };
                    }
                    descriptor = 0; // Freed or transferred to the single retained owner.
                }
            }
            finally
            {
                try { if (handleReference) heldHandle.DangerousRelease(); }
                finally { QueryGate.Release(); }
            }
        }
        token.ThrowIfCancellationRequested();
        // These are the synchronous query-attempt boundaries, not proof that access
        // permissions remained fixed throughout the surrounding file inspection.
        if (observation.NativeResult.HasValue)
            observation = observation with { QueryStartedUtc = started, QueryCompletedUtc = DateTimeOffset.UtcNow };
        return observation;
    }

    private static ShortcutDaclObservation Inspect(nint descriptor, CancellationToken token)
    {
        var invalid = Empty(CheckState.Unknown, ShortcutDaclReason.MetadataInvalid) with { NativeResult = 0 };
        token.ThrowIfCancellationRequested();
        // Both validation functions explicitly have no extended error information.
        if (descriptor == 0 || !IsValidSecurityDescriptor(descriptor)) return invalid;
        if (!GetSecurityDescriptorControl(descriptor, out ushort control, out uint descriptorRevision)) return MetadataFailure();
        if (descriptorRevision != 1 || (control & SelfRelative) == 0) return invalid;
        uint bytes = GetSecurityDescriptorLength(descriptor);
        // This is a post-allocation inspection cap. GetSecurityInfo owns native allocation
        // sizing and provides no caller-supplied preallocation limit.
        if (bytes is < 20 or > MaximumDescriptorBytes) return invalid;
        token.ThrowIfCancellationRequested();
        if (!GetSecurityDescriptorDacl(descriptor, out bool present, out nint acl, out bool defaulted)) return MetadataFailure();
        if (present != ((control & DaclPresent) != 0)) return invalid;
        var observed = Empty(CheckState.Available, ShortcutDaclReason.Observed) with
        { NativeResult = 0, DaclPresent = present, DaclProtected = (control & DaclProtected) != 0 };
        // The other GetSecurityDescriptorDacl outputs are undefined when absent.
        if (!present) return observed;
        if (defaulted != ((control & DaclDefaulted) != 0)) return invalid;
        observed = observed with { DaclDefaulted = defaulted, NullDacl = acl == 0 };
        if (acl == 0) return observed; // Never pass NULL to either ACL function.
        nuint start = unchecked((nuint)descriptor), end = checked(start + bytes), pointer = unchecked((nuint)acl);
        if ((pointer & 3) != 0 || pointer < start || pointer > end - 8) return invalid;
        if (!IsValidAcl(acl)) return invalid;
        token.ThrowIfCancellationRequested();
        if (!GetAclInformation(acl, out AclSizeInformation size, 12, 2)) return MetadataFailure();
        if (!GetAclInformation(acl, out AclRevisionInformation revision, 4, 1)) return MetadataFailure();
        ulong allocated = (ulong)size.BytesInUse + size.BytesFree;
        if (size.AceCount > ShortcutDaclObservationPolicy.MaximumAceCount ||
            allocated is < ShortcutDaclObservationPolicy.MinimumAclBytes or > ShortcutDaclObservationPolicy.MaximumAclBytes ||
            size.BytesInUse < 8UL + 4UL * size.AceCount || allocated > (ulong)(end - pointer) || revision.Revision is not (2 or 4)) return invalid;
        token.ThrowIfCancellationRequested();
        return observed with { AceCount = (int)size.AceCount, AclBytes = (int)allocated, Revision = (int)revision.Revision };
    }

    // Inspect runs only after GetSecurityInfo returned ERROR_SUCCESS. Do not replace
    // that API's status with a different helper's BOOL/GetLastError status domain.
    private static ShortcutDaclObservation MetadataFailure() => Empty(CheckState.Unknown, ShortcutDaclReason.MetadataInvalid) with
    { NativeResult = 0 };
    private static ShortcutDaclObservation Empty(CheckState state, ShortcutDaclReason reason) =>
        new(state, reason, null, null, null, null, null, null, null, null, null, null, ShortcutDescriptorReleaseState.NotAttempted, null);

    [StructLayout(LayoutKind.Sequential)] private struct AclSizeInformation { internal uint AceCount, BytesInUse, BytesFree; }
    [StructLayout(LayoutKind.Sequential)] private struct AclRevisionInformation { internal uint Revision; }
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint information, nint owner, nint group,
        nint dacl, nint sacl, out nint descriptor);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(nint descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out nint dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSecurityDescriptor(nint descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsValidAcl(nint acl);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAclInformation(nint acl, out AclSizeInformation information, uint length, uint kind);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAclInformation(nint acl, out AclRevisionInformation information, uint length, uint kind);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint LocalFree(nint allocation);
}
