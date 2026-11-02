using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

internal static class CleanupFileEvidenceNative
{
    private const uint ReadAttributes = 0x80, OpenReparse = 0x00200000, NoRecall = 0x00100000, Backup = 0x02000000;
    private const uint UnsafeAncestors = 0x40 | 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;
    private const uint UnsafeFiles = UnsafeAncestors | 0x2 | 0x4 | 0x10;
    internal sealed record Snapshot(ulong Volume, Guid Id, long Bytes, uint Links, long Created, long Written, long Changed, uint Attributes)
    {
        // Guid's byte array restores the native FILE_ID_128 memory ordering.
        internal string Identity => $"{Volume:X16}:" + Convert.ToHexString(Id.ToByteArray());
    }
    internal sealed class DirectoryLease : IDisposable
    {
        private readonly List<(string Path, SafeFileHandle Handle, Snapshot Before)> handles;
        internal Snapshot At(string path) => handles.Single(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)).Before;
        internal DirectoryLease(List<(string Path, SafeFileHandle Handle, Snapshot Before)> handles) => this.handles = handles;
        internal void Recheck(CancellationToken token)
        {
            foreach (var entry in handles)
            {
                token.ThrowIfCancellationRequested();
                var current = Observe(entry.Handle, entry.Path, true);
                if (current.Volume != entry.Before.Volume || current.Id != entry.Before.Id || current.Attributes != entry.Before.Attributes)
                    throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.ChangedDuringRead);
            }
        }
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Handle.Dispose(); }
    }
    internal static DirectoryLease OpenDirectories(string directory, CancellationToken token)
    {
        var chain = new Stack<string>();
        for (string? path = directory; path != null; path = Path.GetDirectoryName(path))
        {
            if (chain.Count >= 64) throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.CurrentRootUnavailable);
            chain.Push(path);
        }
        List<(string, SafeFileHandle, Snapshot)> handles = [];
        try
        {
            while (chain.TryPop(out string? path))
            {
                token.ThrowIfCancellationRequested();
                var handle = Open(path, true);
                try { handles.Add((path, handle, Observe(handle, path, true))); }
                catch { handle.Dispose(); throw; }
            }
            return new(handles);
        }
        catch { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Item2.Dispose(); throw; }
    }
    internal static void VerifyOwnedMarker(string path, string marker, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var handle = CreateFileW(path, 0x80000000, 1, 0, 3, OpenReparse | NoRecall, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var before = Observe(handle, path, false);
        if (before.Bytes != 32) throw new InvalidDataException("The owned marker is not the fixed bounded format.");
        using var stream = new FileStream(handle, FileAccess.Read, 32, false);
        byte[] bytes = new byte[32];
        try
        {
            stream.ReadExactly(bytes); token.ThrowIfCancellationRequested();
            if (Encoding.ASCII.GetString(bytes) != marker || before != Observe(handle, path, false))
                throw new InvalidDataException("The owned fixture marker was not established.");
        }
        finally { Array.Clear(bytes); }
    }
    internal static SafeFileHandle OpenFile(string path) => Open(path, false);
    private static SafeFileHandle Open(string path, bool directory)
    {
        if (Marshal.SizeOf<IdentityInformation>() != 24 || Marshal.SizeOf<BasicInformation>() != 40 || Marshal.SizeOf<StandardInformation>() != 24)
            throw new NotSupportedException("The metadata ABI is unsupported.");
        // Attribute access is not a content/inactivity reservation. No file data,
        // delete, write, executable or elevation right is requested.
        var handle = CreateFileW(path, ReadAttributes, directory ? 3u : 7u, 0, 3,
            OpenReparse | NoRecall | (directory ? Backup : 0), 0);
        if (!handle.IsInvalid) return handle;
        int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code);
    }
    internal static Snapshot Observe(SafeFileHandle handle, string path, bool directory)
    {
        if (!Identity(handle, 18, out var id, 24) || !Basic(handle, 0, out var basic, 40) ||
            !Standard(handle, 1, out var standard, 24)) throw new Win32Exception(Marshal.GetLastWin32Error());
        uint unsafeFlags = directory ? UnsafeAncestors : UnsafeFiles;
        if (!directory && standard.Links > 1) throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.MultipleLinks);
        if (id.Volume == 0 || id.Id == Guid.Empty || (basic.Attributes & unsafeFlags) != 0 ||
            ((basic.Attributes & 0x10) != 0) != directory || standard.Directory != directory || standard.DeletePending ||
            standard.Bytes < 0 || standard.Allocation < 0 || !directory && standard.Links != 1)
            throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.UnsafeMetadata);
        var final = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= final.Capacity) throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.UnsafeMetadata);
        string actual = final.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            throw new CleanupEvidenceUnavailable(CleanupEvidenceUnavailableReason.UnsafeMetadata);
        return new(id.Volume, id.Id, standard.Bytes, standard.Links, basic.Created, basic.Written, basic.Changed, basic.Attributes);
    }
    [StructLayout(LayoutKind.Sequential)] private struct IdentityInformation { internal ulong Volume; internal Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInformation { internal long Created, Accessed, Written, Changed; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInformation
    { internal long Allocation, Bytes; internal uint Links; [MarshalAs(UnmanagedType.U1)] internal bool DeletePending; [MarshalAs(UnmanagedType.U1)] internal bool Directory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Identity(SafeFileHandle handle, int kind, out IdentityInformation value, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Basic(SafeFileHandle handle, int kind, out BasicInformation value, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Standard(SafeFileHandle handle, int kind, out StandardInformation value, uint bytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder value, uint characters, uint flags);
}
