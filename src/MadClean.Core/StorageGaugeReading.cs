namespace MadClean.Core;

// A display reading needs both a completed readable source and internally valid values.
// Null preserves an unchecked/unknown state; it must never be replaced with a measured zero.
public sealed record StorageGaugeReading(long TotalBytes, long FreeBytes, double FreeFraction)
{
    public static StorageGaugeReading? From(CheckState? state, DiskInfo? disk)
    {
        if (state is not (CheckState.Available or CheckState.Partial) || disk == null ||
            disk.TotalBytes <= 0 || disk.FreeBytes < 0 || disk.FreeBytes > disk.TotalBytes)
            return null;

        // Convert before division: no intermediate long multiplication or subtraction can overflow.
        var fraction = (double)disk.FreeBytes / disk.TotalBytes;
        return new(disk.TotalBytes, disk.FreeBytes, fraction);
    }
}
