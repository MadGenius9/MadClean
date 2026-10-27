using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

// Official contracts checked: CreateFileW, GetFileInformationByHandleEx, FILE_ID_INFO,
// FILE_BASIC_INFO, FILE_STANDARD_INFO, FILE_STREAM_INFO and GetFinalPathNameByHandleW.
// See https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
internal static class DuplicateWindowsNative
{
    private const uint ReadAttributes = 0x80, GenericRead = 0x80000000;
    private const uint OpenReparse = 0x00200000, NoRecall = 0x00100000, BackupSemantics = 0x02000000, Sequential = 0x08000000;
    internal const FileAttributes Unsafe = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted |
        FileAttributes.System | FileAttributes.Hidden | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
    private const FileAttributes UnsafeAncestor = FileAttributes.ReparsePoint | FileAttributes.Offline |
        FileAttributes.Encrypted | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    internal static DirectoryGuard GuardDirectories(string directory, CancellationToken token)
    {
        var paths = new Stack<string>();
        for (string? current = Path.TrimEndingDirectorySeparator(directory); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (paths.Count >= 64) throw new DuplicateReadException("The ancestry depth limit was reached; choose a shallower local folder.");
            paths.Push(current);
        }
        List<SafeFileHandle> handles = [];
        try
        {
            while (paths.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string path = paths.Pop();
                var handle = Create(path, ReadAttributes, 3, BackupSemantics | OpenReparse | NoRecall);
                handles.Add(handle);
                var basic = Basic(handle);
                if ((basic.Attributes & FileAttributes.Directory) == 0 || (basic.Attributes & UnsafeAncestor) != 0 || !SamePath(FinalPath(handle), path))
                    throw new DuplicateReadException("A directory/ancestor is a link, offline/recall object, encrypted, changed or unsupported; no traversal performed.");
            }
            return new(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    internal static IDuplicateReadHandle Open(string path, bool content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var ancestry = GuardDirectories(Path.GetDirectoryName(path)!, token);
        // Attribute-only access is exempt from data-sharing restrictions. It can identify an alias
        // while an existing content handle denies read/write/delete sharing for the actual file.
        var handle = Create(path, content ? GenericRead | ReadAttributes : ReadAttributes, content ? 0u : 7u,
            OpenReparse | NoRecall | (content ? Sequential | 0x40000000u : 0));
        try { return new ReadHandle(path, handle, content); }
        catch { handle.Dispose(); throw; }
    }

    internal static FileAttributes DirectoryAttributes(DirectoryGuard guard) => Basic(guard.Last).Attributes;

    private static SafeFileHandle Create(string path, uint access, uint sharing, uint flags)
    {
        var handle = CreateFileW(path, access, sharing, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int code = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new DuplicateReadException(code is 32 or 33
            ? "The file/folder is active or locked; it was not forced open."
            : $"The file/folder could not be opened safely (Windows code {code}); coverage is incomplete.");
    }

    private static DuplicateFileStamp Stamp(SafeFileHandle handle, string path)
    {
        var basic = Basic(handle);
        if ((basic.Attributes & (Unsafe | FileAttributes.Directory)) != 0)
            throw new DuplicateReadException("Protected, hidden, encrypted, offline, recall or reparse files are outside duplicate discovery.");
        if (!GetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()) || standard.Directory || standard.DeletePending || standard.EndOfFile < 0 || standard.NumberOfLinks == 0)
            throw new DuplicateReadException("File size/link/stability metadata is unavailable; no comparison performed.");
        if (!GetIdentity(handle, 18, out var id, (uint)Marshal.SizeOf<IdentityInfo>()) || (id.Low == 0 && id.High == 0))
            throw new DuplicateReadException("Supported 128-bit file identity is unavailable; paths are not treated as proof of independent copies.");
        if (!SamePath(FinalPath(handle), path)) throw new DuplicateReadException("The path/object was renamed, redirected or is not canonical; no comparison performed.");
        RequireOnlyUnnamedStream(handle, standard.EndOfFile);
        return new($"{id.Volume:X16}:{id.Low:X16}{id.High:X16}", standard.EndOfFile, basic.LastWriteTime, basic.ChangeTime, standard.NumberOfLinks);
    }

    private static BasicInfo Basic(SafeFileHandle handle)
    {
        if (!GetBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()))
            throw new DuplicateReadException("Supported handle attributes/timestamps are unavailable; no traversal or content comparison performed.");
        return basic;
    }
    private static string FinalPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new DuplicateReadException("The open handle's canonical local path could not be established.");
        string value = path.ToString();
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) && !value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        return value;
    }
    private static bool SamePath(string actual, string expected) => string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase);

    private static void RequireOnlyUnnamedStream(SafeFileHandle handle, long bytes)
    {
        // Whole-file policy: reject ADS or an incomplete/unsupported stream query. No ADS contents/names persist.
        var buffer = new byte[65_536];
        if (!GetBuffer(handle, 7, buffer, (uint)buffer.Length))
            throw new DuplicateReadException("Alternate-stream enumeration is unsupported, inaccessible or exceeds its bound; whole-file comparison is not established.");
        int next = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        int nameBytes = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
        long streamBytes = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8));
        if (next != 0 || nameBytes <= 0 || nameBytes > buffer.Length - 24 || nameBytes % 2 != 0 || streamBytes != bytes ||
            Encoding.Unicode.GetString(buffer, 24, nameBytes) != "::$DATA")
            throw new DuplicateReadException("Named/alternate data streams are present or stream scope is ambiguous; file excluded from whole-file duplicate groups.");
    }

    internal sealed class DirectoryGuard(List<SafeFileHandle> handles) : IDisposable
    {
        public SafeFileHandle Last => handles[^1];
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
    }
    private sealed class ReadHandle : IDuplicateReadHandle
    {
        private readonly string path;
        private readonly SafeFileHandle handle;
        private readonly FileStream? content;
        public DuplicateFileStamp Stamp { get; }
        public Stream Content => content ?? throw new InvalidOperationException("Metadata-only handle has no content stream.");
        public ReadHandle(string path, SafeFileHandle handle, bool readContent)
        {
            this.path = path; this.handle = handle;
            Stamp = DuplicateWindowsNative.Stamp(handle, path);
            if (readContent) content = new FileStream(handle, FileAccess.Read, 65_536, isAsync: true);
        }
        public bool IsStable() => Stamp == DuplicateWindowsNative.Stamp(handle, path);
        public void Dispose() { content?.Dispose(); handle.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IdentityInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public FileAttributes Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int informationClass, out IdentityInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasic(SafeFileHandle handle, int informationClass, out BasicInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandard(SafeFileHandle handle, int informationClass, out StandardInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBuffer(SafeFileHandle handle, int informationClass, [Out] byte[] info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
