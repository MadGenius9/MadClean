using System.Globalization;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    // Presentation only: provider windows and source evidence remain UTC instants.
    private static string DiagnosticTimestamp(DateTimeOffset? instant) =>
        DiagnosticTimePresentation.Format(instant, TimeZoneInfo.Local);

    private static string DiagnosticWindowText(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var zone = TimeZoneInfo.Local;
        return $"UTC window [{startUtc.ToString("u", CultureInfo.InvariantCulture)}, {endUtc.ToString("u", CultureInfo.InvariantCulture)}); local bounds are shown with their individual offsets.\n" +
            $"Start (inclusive): {DiagnosticTimePresentation.Format(startUtc, zone)}\n" +
            $"End (exclusive): {DiagnosticTimePresentation.Format(endUtc, zone)}";
    }
}
