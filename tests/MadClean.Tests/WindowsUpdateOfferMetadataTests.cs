using System.Text.Json.Nodes;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WindowsUpdateOfferMetadataTests
{
    private static JsonObject Field(string state, object? value) => new() { ["State"] = state, ["Value"] = JsonSerializerNode(value) };
    private static JsonNode? JsonSerializerNode(object? value) => value == null ? null : System.Text.Json.JsonSerializer.SerializeToNode(value);
    private static JsonObject Metadata(int type = 2) => new()
    {
        ["Version"] = 1,
        ["Manufacturer"] = Field(type == 1 ? "Unsupported" : "Available", type == 1 ? null : "Reported manufacturer"),
        ["Provider"] = Field(type == 1 ? "Unsupported" : "Available", type == 1 ? null : "Reported provider"),
        ["Model"] = Field(type == 1 ? "Unsupported" : "Available", type == 1 ? null : "Reported intended model"),
        ["CanRequestUserInput"] = Field("Available", false), ["RequiresNetworkConnectivity"] = Field("Available", true),
        ["Impact"] = Field("Available", 2), ["RebootBehavior"] = Field("Available", 1)
    };
    private static JsonObject Row(JsonNode? metadata, int type = 2, bool include = true)
    {
        var row = new JsonObject { ["Title"] = "Reported offer", ["Id"] = "update-id", ["Type"] = type,
            ["RebootRequired"] = false, ["DriverClass"] = "Net", ["DriverVerDate"] = "2026-01-01", ["Description"] = "Provider description" };
        if (include) row["OfferMetadata"] = metadata;
        return row;
    }
    private static CheckResult<SystemUpdate> Read(JsonNode? metadata, int type = 2) => WindowsResponseParser.SystemUpdates(
        new JsonObject { ["ResultCode"] = 2, ["Updates"] = new JsonArray(Row(metadata?.DeepClone(), type)) }.ToJsonString());
    private static WindowsUpdateOfferMetadata Data(CheckResult<SystemUpdate> result) => Assert.IsType<WindowsUpdateOfferMetadata>(Assert.Single(result.Items).OfferMetadata);

    [Fact]
    public void GenuineTypedCharacteristicsPreserveFalseEnumsAndReportedNamesWithoutReadinessClaim()
    {
        var result = Read(Metadata()); var data = Data(result);
        Assert.Equal(CheckState.Available, result.State); Assert.Equal(CheckState.Available, data.State);
        Assert.Equal(false, data.CanRequestUserInput.Value); Assert.Equal(true, data.RequiresNetworkConnectivity.Value);
        Assert.Equal(WindowsUpdateInstallationImpact.RequiresExclusiveHandling, data.Impact.Value);
        Assert.Equal(WindowsUpdateRebootBehavior.AlwaysRequiresReboot, data.RebootBehavior.Value);
        Assert.Equal("Reported intended model", data.Model.Value);
        Assert.True(WindowsUpdateOfferMetadataParser.IsConsistent(data, 2));
        Assert.Contains("does not establish complete prerequisites", result.Message);
    }

    [Fact]
    public void OptionalMalformedFieldKeepsOtherFactsAndSuccessfulSearchStateIndependent()
    {
        var metadata = Metadata(); metadata["Provider"] = Field("Available", new string('x', 513));
        var result = Read(metadata); var data = Data(result);
        Assert.Equal(CheckState.Available, result.State); Assert.Equal(CheckState.Partial, data.State);
        Assert.Equal(CheckState.Unknown, data.Provider.State); Assert.Null(data.Provider.Value);
        Assert.Equal("Reported manufacturer", data.Manufacturer.Value);
        Assert.Equal(false, data.CanRequestUserInput.Value); Assert.Equal("Reported offer", result.Items[0].Title);
        Assert.True(WindowsUpdateOfferMetadataParser.IsConsistent(data, 2));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData(" N/A ")]
    [InlineData("")]
    [InlineData("name\u202Evalue")]
    [InlineData("name\nvalue")]
    [InlineData("name\u2028value")]
    public void UnsafeBlankOrPlaceholderDriverNamesNeverBecomeVerifiedValues(string value)
    {
        var metadata = Metadata(); metadata["Manufacturer"] = Field("Available", value);
        var data = Data(Read(metadata)); Assert.Equal(CheckState.Unknown, data.Manufacturer.State);
        Assert.Null(data.Manufacturer.Value); Assert.Equal(CheckState.Partial, data.State);
    }

    [Fact]
    public void LegitimateUnicodeAtTheExactFieldBoundIsNotTreatedAsUnknown()
    {
        var metadata = Metadata(); metadata["Manufacturer"] = Field("Available", new string('界', 512));
        var result = Read(metadata); Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(new string('界', 512), Data(result).Manufacturer.Value);
        Assert.True(WindowsUpdateOfferMetadataParser.IsConsistent(Data(result), 2));
    }

    [Fact]
    public void MissingNullOrFailedBehaviorNeverInventsFalseZeroOrKnownPrerequisites()
    {
        var metadata = Metadata();
        foreach (var name in new[] { "CanRequestUserInput", "RequiresNetworkConnectivity", "Impact", "RebootBehavior" })
            metadata[name] = Field("Unknown", null);
        var data = Data(Read(metadata));
        Assert.Null(data.CanRequestUserInput.Value); Assert.Null(data.Impact.Value); Assert.Null(data.RebootBehavior.Value);
        Assert.Equal(CheckState.Partial, data.State);
        foreach (var name in new[] { "Manufacturer", "Provider", "Model" }) metadata[name] = Field("Failed", null);
        data = Data(Read(metadata)); Assert.Equal(CheckState.Unknown, data.State);
        Assert.Equal(CheckState.Failed, data.Manufacturer.State); Assert.Null(data.Manufacturer.Value);
    }

    [Fact]
    public void StrictBooleansAndFutureEnumsRemainUnknownInsteadOfCoercing()
    {
        var metadata = Metadata(); metadata["CanRequestUserInput"] = Field("Available", 0);
        metadata["RequiresNetworkConnectivity"] = Field("Unknown", false);
        metadata["Impact"] = Field("Available", 99); metadata["RebootBehavior"] = Field("Available", "1");
        var data = Data(Read(metadata));
        Assert.Null(data.CanRequestUserInput.Value); Assert.Equal(CheckState.Unknown, data.CanRequestUserInput.State);
        Assert.Null(data.RequiresNetworkConnectivity.Value); Assert.Null(data.Impact.Value); Assert.Null(data.RebootBehavior.Value);
        Assert.Equal(CheckState.Partial, data.State);
    }

    [Fact]
    public void SoftwareDriverFieldsAreUnsupportedWithoutMakingKnownCommonBehaviorIncomplete()
    {
        var data = Data(Read(Metadata(1), 1));
        Assert.Equal(CheckState.Unsupported, data.Manufacturer.State); Assert.Null(data.Manufacturer.Value);
        Assert.Equal(CheckState.Available, data.State); Assert.True(WindowsUpdateOfferMetadataParser.IsConsistent(data, 1));
        Assert.False(WindowsUpdateOfferMetadataParser.IsConsistent(data with { Manufacturer = new(CheckState.Available, "Not applicable manufacturer") }, 1));
    }

    [Fact]
    public void WholeSchemaVersionDuplicateOrExtraFieldsCannotDiscardTheBaseOffer()
    {
        var missing = Metadata(); missing.Remove("Version");
        var version = Metadata(); version["Version"] = "1";
        var future = Metadata(); future["Version"] = 2;
        var extra = Metadata(); extra["MoreInfoUrls"] = new JsonArray("https://untrusted.invalid/");
        foreach (JsonNode? value in new JsonNode?[] { null, missing, version, future, extra, new JsonArray() })
        {
            var result = Read(value); Assert.Equal(CheckState.Available, result.State);
            Assert.Equal(CheckState.Unknown, Data(result).State); Assert.Single(result.Items);
        }
        var json = new JsonObject { ["ResultCode"] = 2, ["Updates"] = new JsonArray(Row(Metadata())) }.ToJsonString();
        var duplicateVersion = json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal);
        Assert.Equal(CheckState.Unknown, Data(WindowsResponseParser.SystemUpdates(duplicateVersion)).State);
        var duplicateField = json.Replace("\"State\":\"Available\"", "\"State\":\"Available\",\"State\":\"Available\"", StringComparison.Ordinal);
        Assert.Equal(CheckState.Unknown, Data(WindowsResponseParser.SystemUpdates(duplicateField)).State);
    }

    [Fact]
    public void LegacyResponseStaysSourceAvailableWithoutInventedNewMetadata()
    {
        var result = WindowsResponseParser.SystemUpdates(new JsonObject { ["ResultCode"] = 2,
            ["Updates"] = new JsonArray(Row(null, include: false)) }.ToJsonString());
        Assert.Equal(CheckState.Available, result.State); Assert.Null(Assert.Single(result.Items).OfferMetadata);
    }

    [Fact]
    public void NativePartialAndExplicitProjectionTruncationRemainPartialIndependentOfMetadata()
    {
        foreach (var response in new[] {
            new JsonObject { ["ResultCode"] = 3, ["Updates"] = new JsonArray(Row(Metadata())) },
            new JsonObject { ["ResultCode"] = 2, ["Truncated"] = true, ["Updates"] = new JsonArray(Row(Metadata())) } })
        {
            var result = WindowsResponseParser.SystemUpdates(response.ToJsonString());
            Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Available, Data(result).State);
        }
    }

    [Fact]
    public void OversizeEnvelopeMalformedRowsAndMalformedTruncationAreNotSilentlyDropped()
    {
        var array = new JsonArray(); for (int i = 0; i < 257; i++) array.Add(Row(Metadata()));
        var result = WindowsResponseParser.SystemUpdates(new JsonObject { ["ResultCode"] = 2, ["Updates"] = array }.ToJsonString());
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
        var badRow = Row(Metadata()); badRow["Title"] = false;
        result = WindowsResponseParser.SystemUpdates(new JsonObject { ["ResultCode"] = 2, ["Updates"] = new JsonArray(Row(Metadata()), badRow) }.ToJsonString());
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
        result = WindowsResponseParser.SystemUpdates(new JsonObject { ["ResultCode"] = 2, ["Truncated"] = "false", ["Updates"] = new JsonArray(Row(Metadata())) }.ToJsonString());
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public void DefensiveTypedValidationRejectsForgedKnownNullAndWrongOverallState()
    {
        var data = Data(Read(Metadata()));
        Assert.False(WindowsUpdateOfferMetadataParser.IsConsistent(data with { CanRequestUserInput = new(CheckState.Available, null) }, 2));
        Assert.False(WindowsUpdateOfferMetadataParser.IsConsistent(data with { State = CheckState.Unknown }, 2));
        Assert.False(WindowsUpdateOfferMetadataParser.IsConsistent(data with { Impact = new(CheckState.Available, (WindowsUpdateInstallationImpact)99) }, 2));
    }
}
