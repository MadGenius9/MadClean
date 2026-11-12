using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MadClean.Core;

public enum ActivityArea { Session, Disks, Cleanup, Startup, Defender, Apps, SystemUpdates, MadCleanUpdates, CleanupPreferences, InstalledApplications, LeftoverReview, DiskMap, FirmwareIdentity, HardwareInventory, DuplicateReview, ShortcutReview, DiagnosticsReport, GuidedWindowsHandoff, SystemToolIdentity, StartupRecovery, WerMetadata, ManufacturerServiceFacts, FirmwareReleaseHistory, DiskIdentityAccounting, LeftoverContentReview, MsiRegistrationVerification, PackagedApplications, ShortcutRecheck, ShortcutFileEvidence, DuplicatePairRecheck, CleanupFileEvidence, DeviceDrivers, MsiUninstallRequest, ComponentStoreStatus, SfcVerification, AppPackageMetadata, AppInstallerReview, MsiRegistrationWatch, DefenderLifecycle }
public enum ActivityOutcome { Started, Completed, Partial, Failed, Cancelled }

// Only enums + timestamp enter this log: never raw output, paths, names, arguments, or exceptions.
public sealed class ActivityLog(string path)
{
    private const long MaximumBytes = 1_048_576;
    private readonly object gate = new();
    public bool TryWrite(ActivityArea area, ActivityOutcome outcome)
    {
        // Undefined enum values must not cause directory creation or any other IO.
        if (!Enum.IsDefined(area) || !Enum.IsDefined(outcome)) return false;
        try
        {
            var fullPath = NormalizePath(path);
            var previous = fullPath + ".previous";
            var ancestors = Ancestors(fullPath);
            var record = JsonSerializer.SerializeToUtf8Bytes(new { utc = DateTimeOffset.UtcNow, area = area.ToString(), outcome = outcome.ToString() });
            if (record.Length > 1024) return false;
            lock (gate)
            {
                CheckScope(ancestors, fullPath, previous);
                for (int index = 0; index < ancestors.Length; index++)
                {
                    if (!TryAttributes(ancestors[index], out var attributes))
                    {
                        // Recheck the entire existing prefix before creating each missing component.
                        for (int prefix = 0; prefix < index; prefix++) RequireDirectory(ancestors[prefix]);
                        Directory.CreateDirectory(ancestors[index]);
                    }
                    else RequireDirectoryAttributes(attributes);
                    RequireDirectory(ancestors[index]);
                }
                CheckScope(ancestors, fullPath, previous);
                bool rotate;
                using (var stream = OpenAppend(fullPath))
                {
                    CheckScope(ancestors, fullPath, previous);
                    rotate = stream.Length > MaximumBytes;
                    if (!rotate) Append(stream, record);
                }
                if (rotate)
                {
                    CheckScope(ancestors, fullPath, previous);
                    File.Move(fullPath, previous, true);
                    CheckScope(ancestors, fullPath, previous);
                    using var stream = OpenAppend(fullPath);
                    CheckScope(ancestors, fullPath, previous);
                    Append(stream, record);
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static FileStream OpenAppend(string destination) => new(destination, FileMode.Append, FileAccess.Write, FileShare.None);
    private static void Append(FileStream stream, byte[] record)
    {
        stream.Write(record);
        stream.WriteByte((byte)'\n');
    }

    private static string NormalizePath(string selected)
    {
        if (string.IsNullOrWhiteSpace(selected) || selected.Length > 4096 || !Path.IsPathFullyQualified(selected)) throw new ArgumentException();
        for (int index = 0; index < selected.Length;)
        {
            if (!Rune.TryGetRuneAt(selected, index, out var rune) ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                throw new ArgumentException();
            index += rune.Utf16SequenceLength;
        }
        var fullPath = Path.GetFullPath(selected);
        if (!string.Equals(selected, fullPath, StringComparison.Ordinal) || string.IsNullOrEmpty(Path.GetFileName(fullPath))) throw new ArgumentException();
        if (OperatingSystem.IsWindows())
        {
            if (fullPath.Length < 4 || !char.IsAsciiLetter(fullPath[0]) || fullPath[1] != ':' || fullPath[2] != '\\' || fullPath.AsSpan(2).Contains(':'))
                throw new ArgumentException();
            foreach (var part in fullPath[3..].Split('\\'))
            {
                if (part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException();
                var stem = part.Split('.')[0];
                if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                    stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                        (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))) throw new ArgumentException();
            }
            // This rejects mapped network, removable and unknown drives before ancestor access.
            if (new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType != DriveType.Fixed) throw new IOException();
        }
        return fullPath;
    }

    private static string[] Ancestors(string fullPath)
    {
        var chain = new Stack<string>();
        for (var directory = Path.GetDirectoryName(fullPath); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (chain.Count >= 128) throw new ArgumentException();
            chain.Push(directory);
        }
        return chain.ToArray();
    }

    // These portable namespace checks reject detected links, including dangling links.
    // They do not establish hard-link identity, hostile-race safety, durability or a security journal.
    private static void CheckScope(string[] ancestors, string current, string previous)
    {
        foreach (var ancestor in ancestors)
        {
            if (!TryAttributes(ancestor, out var attributes)) return; // No probes through missing ancestry.
            RequireDirectoryAttributes(attributes);
        }
        foreach (var file in new[] { current, previous })
            if (TryAttributes(file, out var attributes) && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException();
    }

    private static void RequireDirectory(string directory)
    {
        if (!TryAttributes(directory, out var attributes)) throw new IOException();
        RequireDirectoryAttributes(attributes);
    }
    private static void RequireDirectoryAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
    }
    private static bool TryAttributes(string selected, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(selected); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { attributes = default; return false; }
    }
}
