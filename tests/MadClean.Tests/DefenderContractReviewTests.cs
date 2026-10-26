using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DefenderContractReviewTests
{
    private static string Status(string findings) => $$"""
        {
          "AntivirusEnabled": true,
          "RealTimeProtectionEnabled": false,
          "AMServiceEnabled": true,
          "AMRunningMode": "Passive",
          "AntivirusSignatureVersion": "1.2.3",
          "SignatureUpdated": "2026-10-25T12:00:00Z",
          "FindingsState": "Available",
          "Findings": {{findings}}
        }
        """;

    [Fact]
    public void RecordedDetectionPreservesFailedActionWithoutInventingRemediation()
    {
        var result = WindowsResponseParser.Defender(Status("""
            [{"ThreatID":2147483647,"ActionSuccess":false,"LastThreatStatusChangeTime":"2026-10-24T10:00:00Z"}]
            """));

        Assert.Equal(CheckState.Available, result.State);
        var status = Assert.Single(result.Items);
        Assert.Equal("Passive", status.Mode);
        Assert.False(status.RealTimeEnabled);
        var finding = Assert.Single(status.Findings);
        Assert.Equal("2147483647", finding.ThreatId);
        Assert.True(bool.TryParse(finding.ActionSuccess, out var actionSuccess));
        Assert.False(actionSuccess);
        Assert.Equal("2026-10-24T10:00:00Z", finding.LastStatusChange);
    }

    [Fact]
    public void NullDetectionCollectionIsUnknownRatherThanAnEmptySuccessfulHistory()
    {
        var result = WindowsResponseParser.Defender(Status("null"));

        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void UnprojectedDetectionResourcesNeverEnterTheDomainResult()
    {
        const string privateResource = "private-resource-that-must-not-be-persisted";
        var result = WindowsResponseParser.Defender(Status("""
            [{"ThreatID":42,"ActionSuccess":true,"LastThreatStatusChangeTime":null,
              "Resources":["private-resource-that-must-not-be-persisted"]}]
            """));

        Assert.Equal(CheckState.Available, result.State);
        Assert.Single(Assert.Single(result.Items).Findings);
        Assert.DoesNotContain(privateResource, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }
}
