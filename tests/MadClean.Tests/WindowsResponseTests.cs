using MadClean.Core;
using Xunit;
namespace MadClean.Tests;

public sealed class WindowsResponseTests
{
    private const string Status = """
        {"AntivirusEnabled":true,"RealTimeProtectionEnabled":false,"AMServiceEnabled":true,
         "AMRunningMode":"Passive","AntivirusSignatureVersion":"1.2.3","SignatureUpdated":null,
         "FindingsState":"Available","Findings":[]}
        """;
    [Fact]
    public void DisabledProtectionIsNotPromotedToHealthy()
    {
        var result = WindowsResponseParser.Defender(Status);
        var status = Assert.Single(result.Items);
        Assert.False(status.RealTimeEnabled); Assert.Equal("Passive", status.Mode);
        Assert.Equal("Unknown", status.SignatureUpdated); Assert.Empty(status.Findings);
    }
    [Fact]
    public void UnreadableDetectionHistoryPreservesStatusButMarksPartial()
    {
        var result = WindowsResponseParser.Defender(Status.Replace("\"FindingsState\":\"Available\"", "\"FindingsState\":\"Failed\""));
        Assert.Equal(CheckState.Partial, result.State); Assert.True(Assert.Single(result.Items).AntivirusEnabled);
    }
    [Fact]
    public void MissingFlagIsUnknownRatherThanFalseOrTrue()
    {
        var result = WindowsResponseParser.Defender(Status.Replace("\"AntivirusEnabled\":true", "\"AntivirusEnabled\":null"));
        Assert.Equal(CheckState.Partial, result.State); Assert.Null(Assert.Single(result.Items).AntivirusEnabled);
    }
    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("{\"AntivirusEnabled\":\"True\"}")]
    [InlineData("[]")]
    public void MalformedDefenderDataNeverBecomesHealthy(string output)
    {
        var result = WindowsResponseParser.Defender(output);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }
    [Fact]
    public void FailedWindowsSearchDoesNotMeanNoUpdates()
    {
        var result = WindowsResponseParser.SystemUpdates("{\"ResultCode\":4,\"Updates\":[]}");
        Assert.Equal(CheckState.Failed, result.State);
    }
    [Fact]
    public void PartiallySuccessfulSearchKeepsGenuineDriverOffers()
    {
        var result = WindowsResponseParser.SystemUpdates("""
            {"ResultCode":3,"Updates":[{"Title":"Example driver offer","Type":2,"Id":"test-id","RebootRequired":false}]}
            """);
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal("Driver", Assert.Single(result.Items).Kind);
    }
    [Fact]
    public void EmptySuccessfulSearchIsAnOfferResultNotAGlobalUpToDateClaim()
    {
        var result = WindowsResponseParser.SystemUpdates("{\"ResultCode\":2,\"Updates\":[]}");
        Assert.Equal(CheckState.Available, result.State); Assert.Empty(result.Items);
        Assert.DoesNotContain("up to date", result.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void MalformedSystemOfferFailsClosed()
    {
        var result = WindowsResponseParser.SystemUpdates("{\"ResultCode\":2,\"Updates\":[{\"Title\":\"App\"}]}");
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }
}
