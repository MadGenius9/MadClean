using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class FirmwareOfferTests
{
    [Theory]
    [InlineData("Firmware", "Firmware")]
    [InlineData("firmware", "Firmware")]
    [InlineData("Display", "Driver")]
    public void FirmwareClassificationUsesSupportedClassNotTitle(string driverClass, string expected)
    {
        var response = WindowsResponseParser.SystemUpdates($$"""
            {"ResultCode":2,"Updates":[{"Title":"Provider title mentions BIOS","Type":2,"Id":"test-id","RebootRequired":false,"DriverClass":"{{driverClass}}","DriverVerDate":"2026-01-02","Description":"Provider package description"}]}
            """);
        Assert.Equal(CheckState.Available, response.State);
        var item = Assert.Single(response.Items);
        Assert.Equal(expected, item.Kind); Assert.Equal("2026-01-02", item.PackageDate);
        Assert.Equal("Provider package description", item.Description);
    }
    [Fact]
    public void PackageDateRetainsGregorianNamespaceUnderNonGregorianUiCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            var result = WindowsResponseParser.SystemUpdates("""
                {"ResultCode":2,"Updates":[{"Title":"Firmware offer","Type":2,"Id":"test-id","RebootRequired":false,"DriverClass":"Firmware","DriverVerDate":"2026-01-02"}]}
                """);
            Assert.Equal("2026-01-02", Assert.Single(result.Items).PackageDate);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
    [Fact]
    public void MissingClassDoesNotTurnATitleIntoAFirmwareOrCompleteCoverageClaim()
    {
        var result = WindowsResponseParser.SystemUpdates("""
            {"ResultCode":2,"Updates":[{"Title":"BIOS firmware update 99.0","Type":2,"Id":"test-id","RebootRequired":false}]}
            """);
        Assert.Equal(CheckState.Partial, result.State);
        var offer = Assert.Single(result.Items);
        Assert.Equal("Driver", offer.Kind); Assert.Equal("Unknown", offer.DriverClass);
        Assert.Equal("Unknown", offer.PackageDate);
    }
    [Fact]
    public void MalformedClassificationCannotBecomeTrustedFirmware()
    {
        var result = WindowsResponseParser.SystemUpdates("""
            {"ResultCode":2,"Updates":[{"Title":"Offer","Type":2,"Id":"test-id","RebootRequired":false,"DriverClass":true}]}
            """);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }
    [Fact]
    public void InvalidPackageDateRemainsUnknownAndIsNotAnInstalledBiosDate()
    {
        var result = WindowsResponseParser.SystemUpdates("""
            {"ResultCode":2,"Updates":[{"Title":"Firmware offer","Type":2,"Id":"test-id","RebootRequired":false,"DriverClass":"Firmware","DriverVerDate":"not a date"}]}
            """);
        Assert.Equal("Unknown", Assert.Single(result.Items).PackageDate);
        Assert.DoesNotContain("up to date", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
