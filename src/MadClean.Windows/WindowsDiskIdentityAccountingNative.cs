using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

// Independent metadata-only boundary: no Duplicate/content-open API is reused.
internal sealed class WindowsDiskIdentityAccountingNative(Stopwatch watch) : IDiskIdentityAccountingProbe
{
    private const uint ReadAttributes = 0x80, Backup = 0x02000000, OpenReparse = 0x00200000, NoRecall = 0x00100000;
    private const FileAttributes Unsafe = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted |
        FileAttributes.System | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
    private const FileAttributes UnsafeAncestor = Unsafe & ~FileAttributes.System;
    public DiskIdentityAccountingStamp Probe(string path, CancellationToken token)
    {
        Check(token);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.IndexOf(':', 2) >= 0 || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new IOException("Only ordinary fixed-local paths are supported.");
        var ancestry = new Stack<string>();
        for (string? parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (ancestry.Count >= 64) throw new IOException("Ancestry depth exceeds the supported bound.");
            ancestry.Push(parent);
        }
        var guards = new List<SafeFileHandle>();
        try
        {
            // Lexical root-to-leaf validation; deny parent rename/delete while leaf metadata is read.
            while (ancestry.Count > 0)
            {
                Check(token);
                string parent = ancestry.Pop();
                var guard = Open(parent, Backup);
                guards.Add(guard);
                var basic = Basic(guard);
                if ((basic.Attributes & FileAttributes.Directory) == 0 || (basic.Attributes & UnsafeAncestor) != 0 || !SamePath(Final(guard), parent))
                    throw new IOException("An ancestor is redirected, offline, encrypted or unsupported.");
            }
            Check(token);
            using var file = Open(path, 0);
            var before = Read(file, path, out long? allocationBefore);
            Check(token);
            var after = Read(file, path, out long? allocationAfter);
            Check(token);
            bool stable = before == after;
            // Allocation is an independent default-stream observation from the same
            // existing queries. Unknown allocation must not alter logical identity stability.
            long? allocation = stable && allocationBefore.HasValue && allocationBefore == allocationAfter
                ? allocationBefore : null;
            return new(before.Identity, before.Bytes, before.Links, stable, allocation);
        }
        finally { for (int i = guards.Count - 1; i >= 0; i--) guards[i].Dispose(); }
    }
    private void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (watch.Elapsed >= TimeSpan.FromSeconds(15)) throw new IOException("Cooperative metadata budget elapsed.");
    }
    private static SafeFileHandle Open(string path, uint flags)
    {
        // FILE_READ_ATTRIBUTES only. No READ_DATA, backup privilege, write, delete or disposition rights.
        var handle = CreateFileW(path, ReadAttributes, 3, IntPtr.Zero, 3, OpenReparse | NoRecall | flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new IOException("Metadata-only handle could not be opened.");
    }
    private static Stamp Read(SafeFileHandle handle, string path, out long? allocation)
    {
        var basic = Basic(handle);
        if ((basic.Attributes & (Unsafe | FileAttributes.Directory)) != 0 ||
            !GetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()) || standard.Directory || standard.DeletePending ||
            standard.EndOfFile < 0 || standard.NumberOfLinks == 0 ||
            !GetIdentity(handle, 18, out var id, (uint)Marshal.SizeOf<IdentityInfo>()) || (id.Low == 0 && id.High == 0) ||
            !SamePath(Final(handle), path))
            throw new IOException("Safe, stable file identity metadata is unavailable.");
        // Already returned in FILE_STANDARD_INFO; no extra query, rights or path lookup.
        // Sparse/compressed streams can report less allocation than logical length, including zero.
        allocation = standard.AllocationSize >= 0 ? standard.AllocationSize : null;
        return new($"{id.Volume:X16}:{id.Low:X16}{id.High:X16}", standard.EndOfFile, standard.NumberOfLinks,
            basic.LastWriteTime, basic.ChangeTime, basic.Attributes);
    }
    private static BasicInfo Basic(SafeFileHandle handle)
    {
        if (!GetBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>())) throw new IOException("Handle attributes unavailable.");
        return basic;
    }
    private static string Final(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new IOException("Canonical local path unavailable.");
        string path = buffer.ToString();
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Nonlocal path identity unavailable.");
        return path[4..];
    }
    private static bool SamePath(string actual, string expected) => string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase);
    private sealed record Stamp(string Identity, long Bytes, uint Links, long LastWrite, long Change, FileAttributes Attributes);
    [StructLayout(LayoutKind.Sequential)] private struct IdentityInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public FileAttributes Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    {
        public long AllocationSize, EndOfFile; public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdentity(SafeFileHandle handle, int kind, out IdentityInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBasic(SafeFileHandle handle, int kind, out BasicInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
