using System.Diagnostics;
using System.IO;
using System.Text;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

// Fixed development-only mode in this existing harness. No arbitrary command or path is executed.
internal static class InertProcessChild
{
    internal const string Switch = "--inert-process-fixture";
    internal const string ReadyName = "ready.flag", ReleaseName = "release.flag", OwnerName = ".child-owner";

    internal static bool TryRun(string repository, string[] args, out int exitCode)
    {
        exitCode = 2;
        if (args.Length == 0 || args[0] != Switch) return false;
        try
        {
            var lifetime = Stopwatch.StartNew();
            if (args.Length != 4 || args[1] is not ("zero" or "nonzero" or "wait") ||
                !IsValidChild(repository, args[2], args[3], requireOwnerOnly: true)) return true;
            WriteNew(Path.Combine(args[2], ReadyName), "ready");
            if (args[1] == "zero") { exitCode = 0; return true; }
            if (args[1] == "nonzero") { exitCode = 23; return true; }
            // Cooperative self-deadline; no parent kill/termination protocol exists. A stalled
            // OS call is not declared stopped merely because the parent stops observing it.
            while (lifetime.Elapsed < TimeSpan.FromSeconds(10))
            {
                string release = Path.Combine(args[2], ReleaseName);
                try
                {
                    var attributes = File.GetAttributes(release);
                    if ((attributes & (FileAttributes.Directory | Unsafe)) != 0 || !ReadExact(release, "release")) return true;
                    exitCode = 0; return true;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 32 or 33) { }
                Thread.Sleep(20);
            }
            exitCode = 42; // Distinct from the caller's genuine release signal.
        }
        catch (Exception) { exitCode = 2; } // Never expose paths, arguments or owner markers.
        return true;
    }

    internal static bool IsValidFixture(string repository, string fixture, string owner)
    {
        if (!Opaque(owner) || !OrdinaryPath(repository) || !OrdinaryPath(fixture)) return false;
        string repo = Path.GetFullPath(repository), full = Path.GetFullPath(fixture);
        const string prefix = "windows-validation-fixtures-";
        string name = Path.GetFileName(full);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty ||
            name[prefix.Length..] != id.ToString("N") ||
            !string.Equals(full, Path.Combine(repo, "artifacts", name), StringComparison.OrdinalIgnoreCase) ||
            !SafeDirectoryChain(full)) return false;
        return ReadExact(Path.Combine(full, ".madclean-fixture-owner"), owner);
    }
    internal static bool IsValidChild(string repository, string child, string owner, bool requireOwnerOnly = false)
    {
        if (!OrdinaryPath(child)) return false;
        string full = Path.GetFullPath(child);
        string? fixture = Path.GetDirectoryName(full);
        const string prefix = "inert-process-";
        string name = Path.GetFileName(full);
        if (fixture == null || !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty || name[prefix.Length..] != id.ToString("N") ||
            !IsValidFixture(repository, fixture, owner) || !SafeDirectoryChain(full) ||
            !ReadExact(Path.Combine(full, OwnerName), owner)) return false;
        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(full))
        {
            if (++count > 3) return false;
            string leaf = Path.GetFileName(entry);
            if (leaf != OwnerName && (requireOwnerOnly || leaf is not (ReadyName or ReleaseName))) return false;
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Directory | Unsafe)) != 0) return false;
        }
        return !requireOwnerOnly || count == 1;
    }
    internal static bool ReadExact(string path, string expected)
    {
        using var handle = SystemToolTrustNative.OpenFile(path);
        SystemToolTrustNative.ValidateAttributes(handle, false);
        SystemToolTrustNative.ValidateFinalPath(handle, path);
        using var file = new FileStream(handle, FileAccess.Read);
        if (expected.Length > 32 || file.Length != expected.Length) return false;
        byte[] bytes = new byte[expected.Length];
        try
        {
            file.ReadExactly(bytes);
            return file.ReadByte() == -1 && Encoding.ASCII.GetString(bytes) == expected;
        }
        finally { Array.Clear(bytes); }
    }
    internal static void WriteNew(string path, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); stream.Write(bytes); }
        finally { Array.Clear(bytes); }
    }
    private const FileAttributes Unsafe = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted |
        (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
    private static bool OrdinaryPath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length is >= 4 and <= 2048 &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && !path.Any(char.IsControl) &&
        string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    private static bool Opaque(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool SafeDirectoryChain(string path)
    {
        var stack = new Stack<string>();
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        { if (stack.Count >= 64) return false; stack.Push(current); }
        while (stack.Count > 0)
        {
            var attributes = File.GetAttributes(stack.Pop());
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & Unsafe) != 0) return false;
        }
        return true;
    }
}
