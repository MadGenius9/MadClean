using System.Text.Json;

namespace MadClean.Core;

public enum ActivityArea { Session, Disks, Cleanup, Startup, Defender, Apps, SystemUpdates, MadCleanUpdates, CleanupPreferences, InstalledApplications, LeftoverReview, DiskMap, FirmwareIdentity, HardwareInventory, DuplicateReview, ShortcutReview, DiagnosticsReport, GuidedWindowsHandoff, SystemToolIdentity, StartupRecovery, WerMetadata, ManufacturerServiceFacts, FirmwareReleaseHistory }
public enum ActivityOutcome { Started, Completed, Partial, Failed, Cancelled }

// Only enums + timestamp enter this log: never raw output, paths, names, arguments, or exceptions.
public sealed class ActivityLog(string path)
{
    private readonly object gate = new();
    public bool TryWrite(ActivityArea area, ActivityOutcome outcome)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length > 1_048_576)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, area = area.ToString(), outcome = outcome.ToString() }) + Environment.NewLine);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
