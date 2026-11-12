using System.Globalization;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DiagnosticTimePresentationTests
{
    private static TimeZoneInfo Fixed(string id, int minutes) =>
        TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromMinutes(minutes), id, id);

    private static TimeZoneInfo Seasonal()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Fixture/Eastern", TimeSpan.FromHours(-5), "Fixture", "Standard", "Summer", [rule]);
    }

    [Fact]
    public void AutumnOverlapKeepsTwoInstantsDistinctDespiteIdenticalLocalClocks()
    {
        var zone = Seasonal();
        Assert.Equal("2026-11-01 05:30:00 UTC; local 2026-11-01 01:30:00 -04:00 [Fixture/Eastern]",
            DiagnosticTimePresentation.Format(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), zone));
        Assert.Equal("2026-11-01 06:30:00 UTC; local 2026-11-01 01:30:00 -05:00 [Fixture/Eastern]",
            DiagnosticTimePresentation.Format(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), zone));
    }

    [Fact]
    public void SpringTransitionDoesNotInventAnInstantInTheSkippedHour()
    {
        var zone = Seasonal();
        Assert.Equal("2026-03-08 06:59:59 UTC; local 2026-03-08 01:59:59 -05:00 [Fixture/Eastern]",
            DiagnosticTimePresentation.Format(new DateTimeOffset(2026, 3, 8, 6, 59, 59, TimeSpan.Zero), zone));
        Assert.Equal("2026-03-08 07:00:00 UTC; local 2026-03-08 03:00:00 -04:00 [Fixture/Eastern]",
            DiagnosticTimePresentation.Format(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), zone));
    }

    [Theory]
    [InlineData(330, "2026-11-09 01:45:00 +05:30")]
    [InlineData(-210, "2026-11-08 16:45:00 -03:30")]
    [InlineData(0, "2026-11-08 20:15:00 +00:00")]
    public void ExplicitNumericOffsetsIncludeHalfHoursAndDateRollover(int minutes, string local)
    {
        Assert.Equal("2026-11-08 20:15:00 UTC; local " + local + " [Fixture]",
            DiagnosticTimePresentation.Format(new DateTimeOffset(2026, 11, 8, 20, 15, 0, TimeSpan.Zero), Fixed("Fixture", minutes)));
    }

    [Fact]
    public void InputOffsetAndFractionArePreservedAsAnInstantWithInvariantFormatting()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var instant = new DateTimeOffset(2026, 11, 8, 12, 30, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
            Assert.Equal("2026-11-08 10:30:00.1234567 UTC; local 2026-11-08 16:00:00.1234567 +05:30 [Fixture]",
                DiagnosticTimePresentation.Format(instant, Fixed("Fixture", 330)));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void MissingOrDefaultInstantsStayUnknownEvenWithAValidZone()
    {
        Assert.Equal("Unknown", DiagnosticTimePresentation.Format(null, TimeZoneInfo.Utc));
        Assert.Equal("Unknown", DiagnosticTimePresentation.Format(default(DateTimeOffset), TimeZoneInfo.Utc));
    }

    [Fact]
    public void ClampedExtremaRetainUtcWithoutAnInventedLocalBoundary()
    {
        Assert.Equal("9999-12-31 23:59:59.9999999 UTC; local Unknown",
            DiagnosticTimePresentation.Format(DateTimeOffset.MaxValue, Fixed("East", 60)));
        Assert.Equal("0001-01-01 00:00:00.0000001 UTC; local Unknown",
            DiagnosticTimePresentation.Format(DateTimeOffset.MinValue.AddTicks(1), Fixed("West", -60)));
        Assert.Equal("9999-12-31 23:59:59.9999999 UTC; local 9999-12-31 22:59:59.9999999 -01:00 [West]",
            DiagnosticTimePresentation.Format(DateTimeOffset.MaxValue, Fixed("West", -60)));
        Assert.Equal("0001-01-01 00:00:00.0000001 UTC; local 0001-01-01 01:00:00.0000001 +01:00 [East]",
            DiagnosticTimePresentation.Format(DateTimeOffset.MinValue.AddTicks(1), Fixed("East", 60)));
    }

    [Theory]
    [InlineData("Unsafe\nlabel")]
    [InlineData("Unsafe\tlabel")]
    [InlineData("Unsafe\u202elabel")]
    [InlineData("Unsafe\u200blabel")]
    [InlineData("Unsafe\u2028label")]
    [InlineData("Unsafe\u2029label")]
    [InlineData("Unsafe[label]")]
    [InlineData(" outer whitespace")]
    public void UnsafeZoneLabelsAreWithheldWithoutLosingUtc(string label)
    {
        Assert.Equal("2026-11-08 00:00:00 UTC; local Unknown",
            DiagnosticTimePresentation.Format(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero), Fixed(label, 0)));
    }

    [Fact]
    public void ZoneLabelHasABoundedLengthAndNullZoneCannotHideUtc()
    {
        var instant = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        Assert.EndsWith("[" + new string('a', 128) + "]", DiagnosticTimePresentation.Format(instant, Fixed(new string('a', 128), 0)));
        Assert.Equal("2026-11-08 00:00:00 UTC; local Unknown", DiagnosticTimePresentation.Format(instant, Fixed(new string('a', 129), 0)));
        Assert.Equal("2026-11-08 00:00:00 UTC; local Unknown", DiagnosticTimePresentation.Format(instant, null!));
        Assert.Equal("2026-11-08 00:00:00 UTC; local Unknown",
            DiagnosticTimePresentation.Format(instant, Fixed("Unsafe" + new string('\ud800', 1) + "label", 0)));
    }
}
