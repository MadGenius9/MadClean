namespace MadClean.Core;

/// <summary>Compact provider observations, preserving incomplete and unavailable coverage.</summary>
public static class DashboardStatusSummary
{
    public static string Startup(CheckResult<StartupEntry>? source)
    {
        if (!Readable(source?.State)) return Status(source?.State);
        if (source!.Items == null || source.Items.Any(row => row == null)) return Unknown(source.State);
        return source.State == CheckState.Partial
            ? $"Partial · {source.Items.Count} observed"
            : $"{source.Items.Count} entries";
    }

    public static string Defender(CheckResult<DefenderInfo>? source)
    {
        if (!Readable(source?.State)) return Status(source?.State);
        if (source!.Items is not { Count: 1 } || source.Items[0] is not { } info) return Unknown(source.State);
        string flag = info.RealTimeEnabled switch { true => "Enabled", false => "Disabled", null => "Unknown" };
        // This names one reported setting, never protection, scan completion or a clean verdict.
        return Prefix(source.State) + "Real-time: " + flag;
    }

    public static string Disks(CheckResult<DiskInfo>? source)
    {
        if (!Readable(source?.State)) return Status(source?.State);
        if (source!.Items is not { Count: > 0 }) return Unknown(source.State);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long free = 0;
        foreach (var disk in source.Items)
        {
            if (disk == null || string.IsNullOrWhiteSpace(disk.Name) || !names.Add(disk.Name) ||
                disk.TotalBytes <= 0 || disk.FreeBytes < 0 || disk.FreeBytes > disk.TotalBytes)
                return Unknown(source.State);
            try { free = checked(free + disk.FreeBytes); }
            catch (OverflowException) { return Unknown(source.State); }
        }
        return Prefix(source.State) + Display.Bytes(free) + " free";
    }

    private static bool Readable(CheckState? state) => state is CheckState.Available or CheckState.Partial;
    private static string Prefix(CheckState state) => state == CheckState.Partial ? "Partial · " : "";
    private static string Unknown(CheckState state) => Prefix(state) + "Unknown";
    private static string Status(CheckState? state) => state switch
    {
        CheckState.Unavailable => "Unavailable",
        CheckState.Unsupported => "Unsupported",
        CheckState.Failed => "Failed",
        CheckState.Cancelled => "Cancelled",
        _ => "Unknown"
    };
}
