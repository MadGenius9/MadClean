using MadClean.Core;
using Xunit;
namespace MadClean.Tests;

public sealed class UpdaterTests
{
    private static string Table(string rows, bool available = true) => available
        ? $"Name                 Id                       Version       Available     Source\n--------------------------------------------------------------------------------------\n{rows}"
        : $"Name                 Id                       Version\n------------------------------------------------------------\n{rows}";
    private static string Row(string name, string id, string version, string available) =>
        $"{name,-21}{id,-25}{version,-14}{available,-14}winget";
    [Fact]
    public void ReportAvailableUpdateOnlyWhenProviderSuppliesIt()
    {
        var result = WingetParser.Parse(Table(Row("Git", "Git.Git", "2.40", "2.50")));
        Assert.Equal(CheckState.Available, result.State);
        var app = Assert.Single(result.Items); Assert.Equal("Git.Git", app.Id);
        Assert.Equal("2.50", app.AvailableVersion); Assert.Equal(CheckState.Available, app.Status);
    }
    [Fact]
    public void MissingAvailableColumnCannotMeanUpToDate()
    {
        var result = WingetParser.Parse(Table($"{"Example",-21}{"Example.App",-25}1.0", false));
        Assert.Equal(CheckState.Unknown, Assert.Single(result.Items).Status);
    }
    [Fact]
    public void BlankAvailableAndUnknownVersionRemainUnknown()
    {
        var result = WingetParser.Parse(Table(Row("Example", "Example.App", "Unknown", "")));
        Assert.Equal(CheckState.Unknown, Assert.Single(result.Items).Status);
    }
    [Theory]
    [InlineData("")]
    [InlineData("Keine installierten Pakete gefunden.")]
    [InlineData("Name    Kennung       Version\n------------------------------\nExample Example.App 1.0")]
    [InlineData("Network source agreement was not accepted")]
    public void UnrecognizedOutputNeverClaimsSuccessfulCheck(string output)
    {
        var result = WingetParser.Parse(output);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }
    [Fact]
    public void MalformedRowsDoNotProducePlausibleUpdates()
    {
        var result = WingetParser.Parse(Table("truncated"));
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }
    [Fact]
    public void TruncatedPackageIdentifierIsRejected()
    {
        var result = WingetParser.Parse(Table(Row("App", "Vendor.Long…", "1.0", "2.0")));
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }
}
