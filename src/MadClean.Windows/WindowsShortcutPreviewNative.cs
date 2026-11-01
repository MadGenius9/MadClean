using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

internal enum ShortcutPreviewReadFailure { UnsupportedMetadata, IdentityUnavailable, LinkCountUnsupported, SizeUnsupported, UnsafeMetadata, PathUnverified, ReadChanged }
internal sealed class WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure reason) : Exception("Selected shortcut evidence was not established.")
{ internal ShortcutPreviewReadFailure Reason { get; } = reason; }

/// <summary>Read-only bookend observations on one held link; equality is not immutability or inactivity proof.</summary>
internal static class WindowsShortcutPreviewNative
{
    internal sealed record Snapshot(ulong Volume, Guid FileId, long Bytes, uint Links, long Allocation,
        long Created, long Accessed, long Written, long Changed, uint Attributes);

    internal static Snapshot Observe(SafeFileHandle handle, string expectedPath)
    {
        if (Marshal.SizeOf<IdentityInfo>() != 24 || Marshal.OffsetOf<IdentityInfo>(nameof(IdentityInfo.FileId)).ToInt32() != 8 ||
            Marshal.SizeOf<BasicInfo>() != 40 || Marshal.SizeOf<StandardInfo>() != 24)
            throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.UnsupportedMetadata);
        if (!GetFileInformationByHandleEx(handle, 18, out IdentityInfo identity, 24) ||
            !GetFileInformationByHandleEx(handle, 0, out BasicInfo basic, 40) ||
            !GetFileInformationByHandleEx(handle, 1, out StandardInfo standard, 24)) throw new Win32Exception(Marshal.GetLastWin32Error());
        const uint unsafeAttributes = 0x2 | 0x4 | 0x10 | 0x40 | 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;
        if (identity.Volume == 0 || identity.FileId == Guid.Empty) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.IdentityUnavailable);
        if (standard.Links != 1) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.LinkCountUnsupported);
        if (standard.Bytes is <= 0 or > ShortcutActionPreviewPolicy.MaximumBytes) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.SizeUnsupported);
        if (standard.Directory || standard.DeletePending || standard.Allocation < 0 || (basic.Attributes & unsafeAttributes) != 0)
            throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.UnsafeMetadata);
        var path = new StringBuilder(1024);
        uint count = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (count == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (count >= path.Capacity) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.PathUnverified);
        string actual = path.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, expectedPath, StringComparison.OrdinalIgnoreCase)) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.PathUnverified);
        return new(identity.Volume, identity.FileId, standard.Bytes, standard.Links, standard.Allocation,
            basic.Created, basic.Accessed, basic.Written, basic.Changed, basic.Attributes);
    }

    internal static byte[] Hash(FileStream stream, long expectedBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expectedBytes is <= 0 or > ShortcutActionPreviewPolicy.MaximumBytes || stream.Length != expectedBytes ||
            stream.Seek(0, SeekOrigin.Begin) != 0 || stream.Position != 0) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.ReadChanged);
        byte[] buffer = new byte[65_536];
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long observed = 0;
            while (observed < expectedBytes)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, expectedBytes - observed));
                if (read <= 0) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.ReadChanged);
                observed += read; digest.AppendData(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            if (stream.Length != expectedBytes || stream.ReadByte() != -1) throw new WindowsShortcutPreviewFailure(ShortcutPreviewReadFailure.ReadChanged);
            return digest.GetHashAndReset();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal static string? KnownFolder(Guid folder)
    {
        nint text = 0;
        try
        {
            // KF_FLAG_DONT_VERIFY only: do not request existence verification or CREATE.
            // The configured path is validated separately; unsupported redirects block the preview.
            if (SHGetKnownFolderPath(ref folder, 0x4000, 0, out text) != 0 || text == 0) return null;
            var result = new StringBuilder();
            for (int i = 0; i < 32768; i++)
            {
                char value = unchecked((char)Marshal.ReadInt16(text, i * 2));
                if (value == '\0') return result.ToString();
                result.Append(value);
            }
            return null;
        }
        finally { if (text != 0) Marshal.FreeCoTaskMem(text); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IdentityInfo { internal ulong Volume; internal Guid FileId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo
    { internal long Created, Accessed, Written, Changed; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    { internal long Allocation, Bytes; internal uint Links; [MarshalAs(UnmanagedType.U1)] internal bool DeletePending; [MarshalAs(UnmanagedType.U1)] internal bool Directory; }
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out IdentityInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint userToken, out nint path);
}
