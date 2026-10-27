using System.Runtime.InteropServices;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

internal static class WindowsShortcutNative
{
    private const uint ReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x1;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OpenNoRecall = 0x00100000;
    private const uint UnsafeAttributes = 0x400 | 0x1000 | 0x4000 | 0x40 | 0x40000 | 0x400000;
    internal const uint DirectoryAttribute = 0x10;

    internal sealed class AncestryLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles;
        internal BasicInformation LeafInformation { get; }
        internal AncestryLease(List<SafeFileHandle> handles, BasicInformation information)
        { this.handles = handles; LeafInformation = information; }
        public void Dispose()
        {
            for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        }
    }

    internal static bool TryOpenAncestry(string path, bool requireDirectory, CancellationToken token,
        out AncestryLease? lease, out ShortcutTargetProbe failure)
    {
        lease = null;
        string? local = ShortcutPathPolicy.NormalizeLocalPath(path);
        failure = new(ShortcutProbeState.Unknown, "The path is not a supported unambiguous local Windows path.");
        if (local == null) return false;
        if (!IsAvailableFixedVolume(local))
        {
            failure = new(ShortcutProbeState.Unknown, "The volume is not verified as available fixed-local storage. Network mappings, removable/offline volumes and unknown readiness are not probed or mounted.");
            return false;
        }
        List<SafeFileHandle> handles = [];
        try
        {
            string root = local[..3];
            var components = local[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            for (int index = 0; index <= components.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (index > 0) current = Path.Combine(current, components[index - 1]);
                var handle = CreateFileW(current, ReadAttributes, ShareRead, IntPtr.Zero, OpenExisting,
                    BackupSemantics | OpenReparsePoint | OpenNoRecall, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    bool parents = index > 0 && ParentsRemainSafe(handles);
                    bool available = IsAvailableFixedVolume(local);
                    var state = error == 2 ? ShortcutProbeState.FileNotFound : error == 3 ? ShortcutProbeState.PathNotFound : ShortcutProbeState.Unknown;
                    failure = new(state,
                        error is 2 or 3
                            ? $"Native local metadata returned {(error == 2 ? "ERROR_FILE_NOT_FOUND" : "ERROR_PATH_NOT_FOUND")} beneath the checked ancestry."
                            : $"Local metadata access was unavailable or blocked (native code {error}); access/availability is unknown.",
                        available, parents);
                    return false;
                }
                handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var information))
                {
                    int error = Marshal.GetLastWin32Error();
                    failure = new(ShortcutProbeState.Unknown, $"Handle metadata could not be checked (native code {error}).");
                    return false;
                }
                if ((information.FileAttributes & UnsafeAttributes) != 0)
                {
                    failure = new(ShortcutProbeState.Unknown, "A path component is reparse-linked, offline, cloud/recall, encrypted or a device; it was not followed or hydrated.");
                    return false;
                }
                bool directoryRequired = index < components.Length || requireDirectory;
                if (directoryRequired && (information.FileAttributes & DirectoryAttribute) == 0)
                {
                    failure = new(ShortcutProbeState.Unknown, "A path component is not a supported accessible directory; no missing-target conclusion was made.");
                    return false;
                }
                if (index == components.Length)
                {
                    if (!IsAvailableFixedVolume(local))
                    {
                        failure = new(ShortcutProbeState.Unknown, "The fixed local volume became unavailable during metadata checks.");
                        return false;
                    }
                    lease = new(handles, information);
                    handles = [];
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            failure = new(ShortcutProbeState.Unknown, $"Local ancestry could not be validated (code 0x{ex.HResult:X8}); no absence conclusion was made.");
            return false;
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    internal static ShortcutTargetProbe ProbeTarget(string path, CancellationToken token)
    {
        if (!TryOpenAncestry(path, requireDirectory: false, token, out var lease, out var failure)) return failure;
        using (lease)
            return new(ShortcutProbeState.Exists, "Guarded local target attributes exist.", true, true);
    }

    internal static SafeFileHandle OpenReadOnlyLink(string path) => CreateFileW(path, GenericRead, ShareRead,
        IntPtr.Zero, OpenExisting, OpenReparsePoint | OpenNoRecall, IntPtr.Zero);

    internal static bool TryReadSafeFileInformation(SafeFileHandle handle, out BasicInformation information) =>
        GetFileInformationByHandle(handle, out information) && (information.FileAttributes & (UnsafeAttributes | DirectoryAttribute)) == 0;

    private static bool ParentsRemainSafe(List<SafeFileHandle> handles) => handles.Count > 0 && handles.All(handle =>
        GetFileInformationByHandle(handle, out var info) && (info.FileAttributes & UnsafeAttributes) == 0 &&
        (info.FileAttributes & DirectoryAttribute) != 0);

    private static bool IsAvailableFixedVolume(string local)
    {
        try
        {
            var drive = new DriveInfo(local[..3]);
            // Check the type before IsReady: never query readiness on a network mapping.
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFileTime { internal uint Low; internal uint High; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
        internal readonly ulong Bytes => ((ulong)FileSizeHigh << 32) | FileSizeLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BasicInformation information);

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int CoInitializeEx(IntPtr reserved, uint concurrency);

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void CoUninitialize();
}
