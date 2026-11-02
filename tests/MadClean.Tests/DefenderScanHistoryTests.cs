using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DefenderScanHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static Dictionary<string, object?> Status() => new()
    {
        ["AntivirusEnabled"] = true, ["RealTimeProtectionEnabled"] = false, ["AMServiceEnabled"] = true,
        ["AMRunningMode"] = "Passive", ["AntivirusSignatureVersion"] = "1.2.3", ["SignatureUpdated"] = null,
        ["FindingsState"] = "Available", ["Findings"] = Array.Empty<object>()
    };
    private static DefenderScanHistory Parse(Dictionary<string, object?> fields)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(fields));
        return DefenderScanHistoryParser.Read(json.RootElement, Now);
    }

    [Fact]
    public void OldStatusPayloadKeepsItsFlagsAndFindingsCoverageWhileScanTimesStayUnknown()
    {
        var result = WindowsResponseParser.Defender(JsonSerializer.Serialize(Status()));
        Assert.Equal(CheckState.Available, result.State);
        var info = Assert.Single(result.Items);
        Assert.True(info.AntivirusEnabled); Assert.False(info.RealTimeEnabled); Assert.Equal("Available", info.FindingsState);
        var history = Assert.IsType<DefenderScanHistory>(info.ScanHistory);
        Assert.Equal(CheckState.Unknown, history.State);
        Assert.Null(history.QuickScanStartUtc); Assert.Null(history.QuickScanEndUtc);
        Assert.Null(history.FullScanStartUtc); Assert.Null(history.FullScanEndUtc);
    }

    [Fact]
    public void IndependentOffsetFieldsNormalizeWithoutCorrelatingStartAndEnd()
    {
        var history = Parse(new()
        {
            ["QuickScanStartTime"] = "2026-11-02T09:00:00+02:00",
            ["QuickScanEndTime"] = "2026-10-20T12:00:00Z",
            ["FullScanStartTime"] = "2026-10-01T01:02:03.1234567-04:00",
            ["FullScanEndTime"] = "2026-10-01T06:00:00+00:00"
        });
        Assert.Equal(CheckState.Available, history.State);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero), history.QuickScanStartUtc);
        Assert.True(history.QuickScanStartUtc > history.QuickScanEndUtc);
        Assert.Equal(TimeSpan.Zero, history.FullScanStartUtc!.Value.Offset);
        Assert.Contains("may refer to different scans", history.Coverage, StringComparison.Ordinal);
        Assert.Contains("does not establish a running scan", history.Coverage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-25T12:00:00")]
    [InlineData("2026-10-25")]
    [InlineData("1601-01-01T00:00:00Z")]
    [InlineData("1601-01-01T00:00:00+02:00")]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.9999999Z")]
    [InlineData("2026-11-03T00:00:00Z")]
    [InlineData("2026-10-25T12:00:00+99:00")]
    [InlineData("private-unparsed-provider-field")]
    public void MissingUnsupportedSentinelAndFutureTimeDoNotEraseOtherKnownFields(string? invalid)
    {
        var history = Parse(new() { ["QuickScanStartTime"] = invalid, ["FullScanEndTime"] = "2026-11-01T12:00:00Z" });
        Assert.Equal(CheckState.Partial, history.State); Assert.Null(history.QuickScanStartUtc);
        Assert.NotNull(history.FullScanEndUtc);
        Assert.DoesNotContain("private-unparsed-provider-field", history.Coverage, StringComparison.Ordinal);
    }

    [Fact]
    public void NonStringAndOversizedHistoryDoNotPromoteOrFailTheExistingStatusContract()
    {
        var status = Status(); status["QuickScanStartTime"] = new { Secret = "private-ignored-value" };
        status["QuickScanEndTime"] = new string('x', 65);
        status["FullScanStartTime"] = true; status["FullScanEndTime"] = 0;
        status["FindingsState"] = "Failed";
        var result = WindowsResponseParser.Defender(JsonSerializer.Serialize(status));
        Assert.Equal(CheckState.Partial, result.State);
        var info = Assert.Single(result.Items); Assert.Equal("Failed", info.FindingsState);
        Assert.Equal(CheckState.Unknown, info.ScanHistory!.State);
        Assert.DoesNotContain("private-ignored-value", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousDuplicateTimeIsUnknownRatherThanLastValueWins()
    {
        using var json = JsonDocument.Parse("""
            {"QuickScanStartTime":"2026-10-25T12:00:00Z","QuickScanStartTime":"2026-10-26T12:00:00Z",
             "QuickScanEndTime":"2026-10-25T13:00:00Z"}
            """);
        var history = DefenderScanHistoryParser.Read(json.RootElement, Now);
        Assert.Equal(CheckState.Partial, history.State); Assert.Null(history.QuickScanStartUtc); Assert.NotNull(history.QuickScanEndUtc);
    }

    [Fact]
    public void MissingObservationClockDoesNotInventAnAgeOrQualifiedTimestamp()
    {
        using var json = JsonDocument.Parse("""{"QuickScanStartTime":"2026-10-25T12:00:00Z"}""");
        var history = DefenderScanHistoryParser.Read(json.RootElement, default);
        Assert.Equal(CheckState.Unknown, history.State); Assert.Null(history.QuickScanStartUtc);
    }
}
