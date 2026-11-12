using System.Globalization;
using System.Text;

namespace MadClean.Core;

/// <summary>Displays an evidence instant in UTC and an explicitly supplied zone; it does not interpret local clock input.</summary>
public static class DiagnosticTimePresentation
{
    private const string ClockFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";
    private const int ZoneLabelLimit = 128;

    public static string Format(DateTimeOffset? instant, TimeZoneInfo zone)
    {
        if (instant is not { } value || value == default) return "Unknown";

        var utc = value.ToUniversalTime();
        string prefix = utc.ToString(ClockFormat, CultureInfo.InvariantCulture) + " UTC; local ";
        if (zone == null || !SafeLabel(zone.Id)) return prefix + "Unknown";

        try
        {
            // ConvertTime clamps out-of-range dates. Check the intended clock ticks first,
            // then verify the returned instant instead of presenting a plausible boundary date.
            var offset = zone.GetUtcOffset(utc);
            long localTicks = checked(utc.Ticks + offset.Ticks);
            if (localTicks < DateTime.MinValue.Ticks || localTicks > DateTime.MaxValue.Ticks)
                return prefix + "Unknown";

            var local = TimeZoneInfo.ConvertTime(utc, zone);
            if (local.Ticks != localTicks || local.Offset != offset || local.UtcTicks != utc.UtcTicks)
                return prefix + "Unknown";

            return prefix + local.ToString(ClockFormat + " zzz", CultureInfo.InvariantCulture) + " [" + zone.Id + "]";
        }
        catch (ArgumentException) { return prefix + "Unknown"; }
        catch (InvalidTimeZoneException) { return prefix + "Unknown"; }
        catch (OverflowException) { return prefix + "Unknown"; }
    }

    private static bool SafeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > ZoneLabelLimit || label != label.Trim()) return false;
        for (int index = 0; index < label.Length;)
        {
            if (!Rune.TryGetRuneAt(label, index, out var rune)) return false;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            // Keep the bracketed label structurally unambiguous without interpreting it as markup.
            if (rune.Value is '[' or ']') return false;
            index += rune.Utf16SequenceLength;
        }
        return true;
    }
}
