using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private void AppendDefenderScanHistory(DefenderScanHistory? history)
    {
        static string Time(DateTimeOffset? value) => value?.ToString("u") ?? "Unknown";
        SecurityRows.Add(new("Quick scan reported history", $"Start UTC: {Time(history?.QuickScanStartUtc)}; end UTC: {Time(history?.QuickScanEndUtc)}", "Historical times; outcome unknown"));
        SecurityRows.Add(new("Full scan reported history", $"Start UTC: {Time(history?.FullScanStartUtc)}; end UTC: {Time(history?.FullScanEndUtc)}", "Historical times; outcome unknown"));
        SecurityRows.Add(new("Scan-history coverage", history?.Coverage ?? "No scan-history timestamps were established. No scan was started or attributed to MadClean.", history?.State.ToString() ?? "Unknown"));
    }
}
