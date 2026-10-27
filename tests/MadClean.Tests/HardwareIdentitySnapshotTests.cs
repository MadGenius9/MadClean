using System.Text.Json.Nodes;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class HardwareIdentitySnapshotTests
{
    private static JsonObject Source(bool truncated = false) => new()
    {
        ["Class"] = "Win32_ComputerSystem", ["Status"] = "Available", ["Truncated"] = truncated,
        ["Records"] = new JsonArray(new JsonObject
        {
            ["Manufacturer"] = "Microsoft Corporation", ["Model"] = "Surface Laptop 6 for Business",
            ["SystemSKUNumber"] = "Surface_Laptop_6_for_Business_2033", ["SystemFamily"] = "Surface", ["SystemType"] = "x64-based PC"
        })
    };
    private static CheckResult<HardwareFact> Parse(params JsonObject[] sources) => HardwareInventoryParser.Parse(
        new JsonObject { ["Version"] = 1, ["Sources"] = new JsonArray(sources.Select(source => (JsonNode)source).ToArray()) }.ToJsonString());
    [Fact]
    public void ACompletePcIdentityRemainsUsableWhileMemoryMeaningAndOtherCoverageStayUnknown()
    {
        var memory = new JsonObject
        {
            ["Class"] = "Win32_PhysicalMemory", ["Status"] = "Available", ["Truncated"] = false,
            ["Records"] = new JsonArray(new JsonObject { ["SMBIOSMemoryType"] = 34 })
        };
        var result = Parse(Source(), memory);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Contains(result.Items, fact => fact.Source.EndsWith(".SMBIOSMemoryType") && fact.Value.Contains("Unknown meaning"));
        var identity = HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(result.Items);
        Assert.NotNull(identity);
        Assert.Equal("Surface_Laptop_6_for_Business_2033", identity.Sku);
    }
    [Fact]
    public void TruncationCannotAuthorizeAnOnlineExactIdentityCheck()
    {
        var result = Parse(Source(true));
        Assert.Null(HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(result.Items));
    }
    [Fact]
    public void DuplicateSourcesAndMultiplePcRecordsStayAmbiguous()
    {
        Assert.Null(HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(Parse(Source(), Source()).Items));
        var source = Source();
        var records = (JsonArray)source["Records"]!;
        records.Add(records[0]!.DeepClone());
        Assert.Null(HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(Parse(source).Items));
    }
    [Theory]
    [InlineData("Manufacturer")]
    [InlineData("Model")]
    [InlineData("SystemSKUNumber")]
    public void MissingRequiredIdentityNeverUsesAnotherFieldAsASubstitute(string field)
    {
        var source = Source(); ((JsonObject)source["Records"]![0]!).Remove(field);
        Assert.Null(HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(Parse(source).Items));
    }
    [Fact]
    public void ManuallyConstructedUnattestedFactsAndUnexpectedEnvelopesStayUnverified()
    {
        var complete = Parse(Source()).Items;
        Assert.Null(HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(complete.Select(fact => fact with { SourceEnumerationComplete = false })));
        Assert.Null(HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(Parse(Source(), new JsonObject { ["Class"] = "UnknownSource" }).Items));
    }
    [Fact]
    public void AFailedPcSourceDoesNotReturnAnIdentityEvenWhenRecordsArePresent()
    {
        var source = Source(); source["Status"] = "Failed";
        Assert.Null(HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(Parse(source).Items));
    }
}
