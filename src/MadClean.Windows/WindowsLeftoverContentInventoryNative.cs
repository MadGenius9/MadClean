using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

// Independent metadata-only enumeration boundary. No content-open or action APIs are reused.
internal sealed class WindowsLeftoverContentInventoryNative(LeftoverContentScope scope, CancellationToken token)
{
    private const uint ReadAttributes = 0x80, Backup = 0x02000000, OpenReparse = 0x00200000, NoRecall = 0x00100000;
    private const FileAttributes Unsafe = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted |
        FileAttributes.System | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
    private const FileAttributes UnsafeAncestor = Unsafe & ~FileAttributes.System;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly List<LeftoverContentEntry> entries = [];
    private readonly List<LeftoverContentIssue> issues = [];
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directoryIdentities = new(StringComparer.Ordinal);
    private bool truncated;
    private int visitedFiles, visitedDirectories;
    private ulong volume;

    public CheckResult<LeftoverContentReport> Read()
    {
        Check();
        var now = DateTimeOffset.UtcNow;
        if (scope.ValidatedUtc.Offset != TimeSpan.Zero || scope.ValidatedUtc > now || now - scope.ValidatedUtc > LeftoverContentPolicy.ScopeLifetime)
            return CheckResult<LeftoverContentReport>.Empty(CheckState.Unknown, "Selected-folder validation expired before the worker started. Refresh registrations and validate again; no traversal occurred.");
        string? normalized = InstalledApplicationPaths.NormalizeLocalDirectory(scope.RootPath);
        if (normalized == null || !string.Equals(normalized, scope.RootPath, StringComparison.OrdinalIgnoreCase) ||
            new DriveInfo(Path.GetPathRoot(normalized)!).DriveType != DriveType.Fixed || ProtectedRoot(normalized))
            return CheckResult<LeftoverContentReport>.Empty(CheckState.Unavailable,
                "The selected folder is not a supported isolated fixed-local install root, or overlaps a protected actual Windows/user/application location. No traversal occurred.");
        using var ancestry = GuardParents(normalized);
        using var root = Open(normalized, true);
        var rootStamp = Inspect(root, normalized, true);
        volume = rootStamp.Volume;
        directoryIdentities.Add(rootStamp.Identity);
        paths.Add(normalized);
        visitedDirectories = 1;
        Visit(normalized, root, rootStamp, 0);
        Check();
        string[] coverage =
        [
            "Only the explicitly selected validated prior install root was inspected. Registration disappearance is not proof of uninstall completion; remaining names are not proven junk.",
            scope.SourceEvidence,
            scope.SourceCaution,
            scope.OwnershipCoverage,
            "All entries have ActiveUse Unknown. Metadata-only handles do not prove inactivity, absence of mapped views, exclusive ownership, deletion eligibility or safe cleanup.",
            "Only known unnamed-stream logical file lengths are shown; directory aggregate bytes, alternate-stream totals, physical allocation and reclaimable space are not measured. Hard-link names may repeat logical bytes.",
            "Protected user/data/save/settings/credential/browser names and sensitive extensions, reparse/offline/recall/encrypted/system entries, inaccessible or changing metadata were omitted. Omitted descendants remain unknown; no content was read.",
            "Handle metadata and enumeration are sequential observations, not an atomic filesystem snapshot. Unregistered/shared dependencies and concurrent registration/filesystem changes remain unknown. No removal, uninstaller, registry change, elevation or download occurred.",
            "Bounded to 128 attempted files, 128 directories including the selected root, depth 8 and 256 issue details; native work has a cooperative 15-second budget."
        ];
        var report = new LeftoverContentReport(normalized, entries.ToArray(), issues.ToArray(), visitedFiles, visitedDirectories, truncated, coverage);
        return new(issues.Count != 0 || truncated ? CheckState.Partial : CheckState.Available,
            issues.Count != 0 || truncated ? "Selected leftover metadata preview has gaps; omitted names and sizes remain unknown. Active use is Unknown."
                : "Bounded selected-folder metadata observations completed. Remaining files are not proven junk; active use and exclusive ownership remain Unknown.", [report]);
    }

    private void Visit(string directory, SafeFileHandle directoryHandle, Stamp directoryStamp, int depth)
    {
        Check();
        if (Inspect(directoryHandle, directory, true) != directoryStamp)
        { Issue(directory, "Directory metadata changed before enumeration; descendants were not inspected."); return; }
        using var search = FindFirstFileExW(Path.Combine(directory, "*"), 1, out var found, 0, IntPtr.Zero, 0);
        if (search.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            if (code != 2) Issue(directory, "Directory enumeration was inaccessible, changed or unsupported; descendants remain unknown.");
            return;
        }
        while (true)
        {
            Check();
            if (issues.Count >= LeftoverContentPolicy.MaximumIssues) { truncated = true; break; }
            string name = found.FileName;
            if (name is not ("." or ".."))
            {
                if (name == null || name.Length == 0 || name.Any(char.IsControl) || name.IndexOfAny(['\\', '/', ':']) >= 0)
                { Issue(directory, "An entry name was malformed; it was not opened."); }
                else
                {
                    string path = Path.Combine(directory, name);
                    bool isDirectory = (found.Attributes & FileAttributes.Directory) != 0;
                    if (path.Length > 32767 || !paths.Add(path))
                        Issue(directory, "An entry path exceeded its bound or appeared more than once; it was not opened.");
                    else if ((found.Attributes & Unsafe) != 0 || LeftoverContentPolicy.IsProtectedDescendant(name, isDirectory))
                        Issue(path, "Protected user/data/settings/save/credential, system, reparse, encrypted or offline/recall entry; excluded without content access.");
                    else if (isDirectory && depth >= LeftoverContentPolicy.MaximumDepth)
                    { truncated = true; Issue(path, "Depth limit reached; this directory and its descendants remain unknown."); }
                    else if ((isDirectory && visitedDirectories >= LeftoverContentPolicy.MaximumDirectories) ||
                        (!isDirectory && visitedFiles >= LeftoverContentPolicy.MaximumFiles))
                    { truncated = true; Issue(directory, "File/directory bound reached; remaining entries and sizes are unknown."); break; }
                    else
                    {
                        if (isDirectory) visitedDirectories++; else visitedFiles++;
                        try
                        {
                            Check();
                            using var handle = Open(path, isDirectory);
                            var stamp = Inspect(handle, path, isDirectory);
                            Check();
                            if (stamp.Volume != volume || Inspect(handle, path, isDirectory) != stamp)
                                throw new IOException("Changed or cross-volume entry.");
                            if (isDirectory)
                            {
                                if (!directoryIdentities.Add(stamp.Identity))
                                    throw new IOException("Directory identity appeared more than once.");
                                entries.Add(new(path, true, null, stamp.Identity, "Unknown"));
                                Visit(path, handle, stamp, depth + 1);
                            }
                            else entries.Add(new(path, false, stamp.Bytes, stamp.Identity, "Unknown"));
                        }
                        catch (Exception ex) when (PathError(ex))
                        { Issue(path, "Handle metadata is inaccessible, locked, changed, protected or unsupported; entry/descendant sizes remain unknown. Check access and retry."); }
                    }
                }
            }
            Check();
            if (!FindNextFileW(search, out found))
            {
                if (Marshal.GetLastWin32Error() != 18) Issue(directory, "Enumeration ended with an error; unobserved entries remain unknown.");
                break;
            }
        }
        if (Inspect(directoryHandle, directory, true) != directoryStamp)
            Issue(directory, "Directory changed during enumeration; observations are non-atomic and coverage is incomplete.");
    }

    private void Check()
    {
        token.ThrowIfCancellationRequested();
        if (watch.Elapsed >= TimeSpan.FromSeconds(15)) throw new IOException("Cooperative metadata budget elapsed.");
    }
    private void Issue(string path, string reason)
    {
        if (issues.Count < LeftoverContentPolicy.MaximumIssues) issues.Add(new(path, reason));
        else truncated = true;
    }
    private static bool ProtectedRoot(string path)
    {
        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Personal, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos })
        {
            string root = Environment.GetFolderPath(special);
            if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
            {
                if (special is Environment.SpecialFolder.Windows or Environment.SpecialFolder.UserProfile or Environment.SpecialFolder.CommonApplicationData)
                    return true; // Missing essential exclusion evidence blocks traversal, never CWD fallback.
                continue;
            }
            if (Overlap(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))) return true;
        }
        return Overlap(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)));
    }
    private static bool Overlap(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private Guard GuardParents(string root)
    {
        var parents = new Stack<string>();
        for (string? path = Path.GetDirectoryName(root); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if (parents.Count >= 64) throw new IOException("Parent depth exceeds supported bound.");
            parents.Push(path);
        }
        List<SafeFileHandle> handles = [];
        try
        {
            while (parents.Count > 0)
            {
                Check();
                string path = parents.Pop();
                var handle = Open(path, true);
                handles.Add(handle);
                var basic = Basic(handle);
                if ((basic.Attributes & FileAttributes.Directory) == 0 || (basic.Attributes & UnsafeAncestor) != 0 || !SamePath(Final(handle), path))
                    throw new IOException("Parent is redirected, unsafe or unsupported.");
            }
            return new(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private static SafeFileHandle Open(string path, bool directory)
    {
        // READ_ATTRIBUTES only; sharing excludes ordinary writers/deleters but is NOT active-use proof.
        var handle = CreateFileW(path, ReadAttributes, 1, IntPtr.Zero, 3, OpenReparse | NoRecall | (directory ? Backup : 0), IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose(); throw new IOException("Read-only metadata handle unavailable.");
    }
    private static Stamp Inspect(SafeFileHandle handle, string path, bool directory)
    {
        var basic = Basic(handle);
        if ((basic.Attributes & Unsafe) != 0 || ((basic.Attributes & FileAttributes.Directory) != 0) != directory ||
            !GetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()) || standard.Directory != directory || standard.DeletePending ||
            standard.EndOfFile < 0 || standard.NumberOfLinks == 0 ||
            !GetIdentity(handle, 18, out var id, (uint)Marshal.SizeOf<IdentityInfo>()) || (id.Low == 0 && id.High == 0) || !SamePath(Final(handle), path))
            throw new IOException("Safe current identity/metadata unavailable.");
        return new($"{id.Volume:X16}:{id.Low:X16}{id.High:X16}", id.Volume, standard.EndOfFile, standard.NumberOfLinks,
            basic.LastWriteTime, basic.ChangeTime, basic.Attributes);
    }
    private static BasicInfo Basic(SafeFileHandle handle)
    {
        if (!GetBasic(handle, 0, out var result, (uint)Marshal.SizeOf<BasicInfo>())) throw new IOException("Attributes unavailable.");
        return result;
    }
    private static string Final(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new IOException("Canonical name unavailable.");
        string path = buffer.ToString();
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Nonlocal canonical path.");
        return path[4..];
    }
    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    private static bool PathError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
    private sealed record Stamp(string Identity, ulong Volume, long Bytes, uint Links, long LastWrite, long Change, FileAttributes Attributes);
    private sealed class Guard(List<SafeFileHandle> handles) : IDisposable
    { public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); } }
    private sealed class FindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public FindHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct IdentityInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public FileAttributes Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    {
        public long AllocationSize, EndOfFile; public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FindData
    {
        public FileAttributes Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
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
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern FindHandle FindFirstFileExW(string pattern, int infoLevel, out FindData data, int searchOperation, IntPtr filter, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextFileW(FindHandle handle, out FindData data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
}
