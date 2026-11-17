using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DashboardStatusSummaryTests
{
    private static readonly StartupEntry Entry = new("Registered app", "HKCU Run", "Unknown", "Not executed");
    private static readonly DefenderInfo Enabled = new(true, true, true, "Normal", "1", "Unknown", "Available", []);
    private static readonly DiskInfo Disk = new("C:\\", 4096, 1024);

    [Fact]
    public void ActualUnreadableDefenderHistoryKeepsItsPartialStatusOnTheDashboard()
    {
        var parsed = WindowsResponseParser.Defender("""
            {"AntivirusEnabled":true,"RealTimeProtectionEnabled":true,"AMServiceEnabled":true,
             "AMRunningMode":"Normal","AntivirusSignatureVersion":"1.2.3","SignatureUpdated":null,
             "FindingsState":"Failed","Findings":[]}
            """);
        Assert.Equal(CheckState.Partial, parsed.State);
        Assert.True(Assert.Single(parsed.Items).RealTimeEnabled);
        Assert.Equal("Partial · Real-time: Enabled", DashboardStatusSummary.Defender(parsed));
    }

    [Theory]
    [InlineData(CheckState.Unknown, "Unknown")]
    [InlineData(CheckState.Unavailable, "Unavailable")]
    [InlineData(CheckState.Unsupported, "Unsupported")]
    [InlineData(CheckState.Failed, "Failed")]
    [InlineData(CheckState.Cancelled, "Cancelled")]
    [InlineData((CheckState)999, "Unknown")]
    public void UnusableProviderStatesCannotBePromotedByAttachedRows(CheckState state, string expected)
    {
        Assert.Equal(expected, DashboardStatusSummary.Startup(new(state, "ignored", [Entry])));
        Assert.Equal(expected, DashboardStatusSummary.Startup(new(state, "ignored", [])));
        Assert.Equal(expected, DashboardStatusSummary.Defender(new(state, "ignored", [Enabled])));
        Assert.Equal(expected, DashboardStatusSummary.Disks(new(state, "ignored", [Disk])));
    }

    [Fact]
    public void MissingProviderResultsStayUnknownRatherThanZeroOrEnabled()
    {
        Assert.Equal("Unknown", DashboardStatusSummary.Startup(null));
        Assert.Equal("Unknown", DashboardStatusSummary.Defender(null));
        Assert.Equal("Unknown", DashboardStatusSummary.Disks(null));
    }

    [Fact]
    public void EmptyCompleteStartupInventoryAndIncompleteObservationAreDistinct()
    {
        Assert.Equal("0 entries", DashboardStatusSummary.Startup(new(CheckState.Available, "", [])));
        Assert.Equal("Partial · 0 observed", DashboardStatusSummary.Startup(new(CheckState.Partial, "", [])));
        Assert.Equal("2 entries", DashboardStatusSummary.Startup(new(CheckState.Available, "", [Entry, Entry])));
        Assert.Equal("Partial · 2 observed", DashboardStatusSummary.Startup(new(CheckState.Partial, "", [Entry, Entry])));
    }

    [Theory]
    [InlineData(CheckState.Available, "Unknown")]
    [InlineData(CheckState.Partial, "Partial · Unknown")]
    public void MalformedRowsRetainCoverageWithoutSupplyingMeasurements(CheckState state, string expected)
    {
        Assert.Equal(expected, DashboardStatusSummary.Startup(new(state, "", null!)));
        Assert.Equal(expected, DashboardStatusSummary.Startup(new(state, "", [Entry, null!])));
        Assert.Equal(expected, DashboardStatusSummary.Defender(new(state, "", null!)));
        Assert.Equal(expected, DashboardStatusSummary.Defender(new(state, "", [])));
        Assert.Equal(expected, DashboardStatusSummary.Defender(new(state, "", [null!])));
        Assert.Equal(expected, DashboardStatusSummary.Defender(new(state, "", [Enabled, Enabled])));
        Assert.Equal(expected, DashboardStatusSummary.Disks(new(state, "", null!)));
        Assert.Equal(expected, DashboardStatusSummary.Disks(new(state, "", [])));
        Assert.Equal(expected, DashboardStatusSummary.Disks(new(state, "", [Disk, null!])));
    }

    [Theory]
    [InlineData(true, "Enabled")]
    [InlineData(false, "Disabled")]
    [InlineData(null, "Unknown")]
    public void RealTimeSummaryDescribesOnlyTheReturnedSetting(bool? setting, string label)
    {
        var status = Enabled with { RealTimeEnabled = setting, AntivirusEnabled = false, ServiceEnabled = false, Mode = "Passive" };
        Assert.Equal("Real-time: " + label, DashboardStatusSummary.Defender(new(CheckState.Available, "", [status])));
        Assert.Equal("Partial · Real-time: " + label, DashboardStatusSummary.Defender(new(CheckState.Partial, "", [status])));
    }

    [Fact]
    public void DiskSummaryAddsOnlyValidReportedFreeSpaceAndPreservesPartialCoverage()
    {
        DiskInfo[] rows = [Disk, new("D:\\", 8192, 1024)];
        Assert.Equal("2 KB free", DashboardStatusSummary.Disks(new(CheckState.Available, "", rows)));
        Assert.Equal("Partial · 2 KB free", DashboardStatusSummary.Disks(new(CheckState.Partial, "", rows)));
        Assert.Equal("0 B free", DashboardStatusSummary.Disks(new(CheckState.Available, "", [Disk with { FreeBytes = 0 }])));
    }

    [Theory]
    [InlineData("C:\\", 0, 0)]
    [InlineData("C:\\", -1, 0)]
    [InlineData("C:\\", 10, -1)]
    [InlineData("C:\\", 10, 11)]
    [InlineData("", 10, 5)]
    [InlineData("   ", 10, 5)]
    public void InvalidDiskReadingsCannotProduceCapacityTotals(string name, long total, long free)
    {
        var invalid = new DiskInfo(name, total, free);
        Assert.Equal("Unknown", DashboardStatusSummary.Disks(new(CheckState.Available, "", [invalid])));
        Assert.Equal("Partial · Unknown", DashboardStatusSummary.Disks(new(CheckState.Partial, "", [Disk, invalid])));
    }

    [Fact]
    public void DuplicateDrivesAndOverflowNeverDoubleCountOrWrapFreeSpace()
    {
        Assert.Equal("Unknown", DashboardStatusSummary.Disks(new(CheckState.Available, "", [Disk, Disk with { Name = "c:\\" }])));
        DiskInfo[] overflow = [new("C:\\", long.MaxValue, long.MaxValue), new("D:\\", 1, 1)];
        Assert.Equal("Unknown", DashboardStatusSummary.Disks(new(CheckState.Available, "", overflow)));
        Assert.Equal("Partial · Unknown", DashboardStatusSummary.Disks(new(CheckState.Partial, "", overflow)));
        Assert.Equal(Display.Bytes(long.MaxValue) + " free", DashboardStatusSummary.Disks(new(CheckState.Available, "", [overflow[0]])));
    }
}
