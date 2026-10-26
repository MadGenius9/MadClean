using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class FirmwareIdentityTests
{
    private static Dictionary<string, object?> Envelope() => new()
    {
        ["SchemaVersion"] = 1,
        ["ComputerSystem"] = Source(new() { ["Manufacturer"] = "Contoso Devices", ["Model"] = "Atlas 15", ["SystemSKUNumber"] = "SKU-A15" }),
        ["BaseBoard"] = Source(new() { ["Manufacturer"] = "Contoso Boards", ["Product"] = "BASE-15", ["Version"] = "Rev 1.2" }),
        ["Bios"] = Source(new() { ["Manufacturer"] = "Example Firmware", ["SMBIOSBIOSVersion"] = "F.12", ["ReleaseDate"] = "2026-10-25" })
    };
    private static Dictionary<string, object?> Source(Dictionary<string, object?> record) => new()
    {
        ["Status"] = "Available",
        ["Records"] = new object?[] { record }
    };
    private static Dictionary<string, object?> Provider(Dictionary<string, object?> envelope, string name) =>
        (Dictionary<string, object?>)envelope[name]!;
    private static Dictionary<string, object?> Record(Dictionary<string, object?> envelope, string name) =>
        (Dictionary<string, object?>)((object?[])Provider(envelope, name)["Records"]!)[0]!;
    private static CheckResult<FirmwareIdentity> Parse(Dictionary<string, object?> envelope) => FirmwareIdentityParser.Parse(JsonSerializer.Serialize(envelope));
    private static string[] SourceFields(FirmwareIdentity value, string source) => source switch
    {
        "ComputerSystem" => [value.PcManufacturer, value.PcModel, value.PcSku],
        "BaseBoard" => [value.BoardManufacturer, value.BoardProduct, value.BoardRevision],
        _ => [value.BiosManufacturer, value.BiosVersion, value.BiosReleaseDate]
    };

    [Fact]
    public void ValidProjectedIdentityRemainsFirmwareReportedRatherThanACompatibilityVerdict()
    {
        var result = Parse(Envelope());

        Assert.Equal(CheckState.Available, result.State);
        var value = Assert.Single(result.Items);
        Assert.Equal("Contoso Devices", value.PcManufacturer);
        Assert.Equal("Atlas 15", value.PcModel);
        Assert.Equal("SKU-A15", value.PcSku);
        Assert.Equal("BASE-15", value.BoardProduct);
        Assert.Equal("Rev 1.2", value.BoardRevision);
        Assert.Equal("F.12", value.BiosVersion);
        Assert.Equal("2026-10-25", value.BiosReleaseDate);
        Assert.Contains("Firmware-reported", result.Message);
        Assert.Contains("not manufacturer-verified compatibility", result.Message);
    }

    [Theory]
    [InlineData(" To Be Filled By O.E.M. ")]
    [InlineData("To be filled by OEM")]
    [InlineData("System Product Name")]
    [InlineData("Default string")]
    [InlineData("UNKNOWN")]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("   ")]
    public void PlaceholderModelsAreUnknownWithoutDiscardingOtherGenuineSourceFields(string placeholder)
    {
        var envelope = Envelope();
        Record(envelope, "ComputerSystem")["Model"] = placeholder;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        var value = Assert.Single(result.Items);
        Assert.Equal("Unknown", value.PcModel);
        Assert.Equal("Contoso Devices", value.PcManufacturer);
        Assert.Equal("F.12", value.BiosVersion);
    }

    [Theory]
    [InlineData("BaseBoard", "Product", "Base Board Product Name")]
    [InlineData("BaseBoard", "Manufacturer", "Base Board Manufacturer")]
    [InlineData("BaseBoard", "Version", "Base Board Version")]
    [InlineData("Bios", "SMBIOSBIOSVersion", "System BIOS Version")]
    public void GenericBoardAndBiosLabelsCannotBecomeKnownSpecificIdentity(string source, string field, string placeholder)
    {
        var envelope = Envelope();
        Record(envelope, source)[field] = placeholder;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        var value = Assert.Single(result.Items);
        string actual = field switch
        {
            "Product" => value.BoardProduct,
            "Manufacturer" => value.BoardManufacturer,
            "Version" => value.BoardRevision,
            _ => value.BiosVersion
        };
        Assert.Equal("Unknown", actual);
        Assert.Equal("Atlas 15", value.PcModel);
    }

    [Theory]
    [InlineData("ComputerSystem")]
    [InlineData("BaseBoard")]
    [InlineData("Bios")]
    public void MultipleProviderRecordsNeverBecomeAnInventedSoleIdentity(string source)
    {
        var envelope = Envelope();
        var record = Record(envelope, source);
        Provider(envelope, source)["Records"] = new object?[] { record, new Dictionary<string, object?>(record) };

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.All(SourceFields(Assert.Single(result.Items), source), value => Assert.Equal("Unknown", value));
        Assert.Contains("multiple records", result.Message);
    }

    [Theory]
    [InlineData("ComputerSystem")]
    [InlineData("BaseBoard")]
    [InlineData("Bios")]
    public void MissingProviderRecordsRemainUnknownAndExplicitlyIncomplete(string source)
    {
        var envelope = Envelope();
        Provider(envelope, source)["Records"] = Array.Empty<object>();

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.All(SourceFields(Assert.Single(result.Items), source), value => Assert.Equal("Unknown", value));
        Assert.Contains("returned no record", result.Message);
    }

    [Fact]
    public void SourceFailurePreservesOtherSourcesAndDoesNotExposeRawProviderErrors()
    {
        var envelope = Envelope();
        envelope["BaseBoard"] = new Dictionary<string, object?>
        {
            ["Status"] = "Failed",
            ["Records"] = Array.Empty<object>(),
            ["Error"] = "private-provider-error-detail"
        };

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        var value = Assert.Single(result.Items);
        Assert.All(SourceFields(value, "BaseBoard"), field => Assert.Equal("Unknown", field));
        Assert.Equal("Atlas 15", value.PcModel);
        Assert.Equal("F.12", value.BiosVersion);
        Assert.Contains("BaseBoard query failed", result.Message);
        Assert.DoesNotContain("private-provider-error-detail", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void FailedSourceWithUnexpectedRecordsIsNotUsedAsFallbackIdentity()
    {
        var envelope = Envelope();
        Provider(envelope, "ComputerSystem")["Status"] = "Failed";

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.All(SourceFields(Assert.Single(result.Items), "ComputerSystem"), value => Assert.Equal("Unknown", value));
        Assert.Contains("Parse error", result.Message);
    }

    [Fact]
    public void MalformedSourceIsDistinctFromQueryFailureAndPreservesOtherSources()
    {
        var envelope = Envelope();
        Provider(envelope, "BaseBoard")["Records"] = "not-an-array";

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        var value = Assert.Single(result.Items);
        Assert.Equal("Atlas 15", value.PcModel);
        Assert.All(SourceFields(value, "BaseBoard"), field => Assert.Equal("Unknown", field));
        Assert.Contains("Parse error: BaseBoard", result.Message);
    }

    [Fact]
    public void WronglyTypedFieldIsUnknownRatherThanAStringifiedModel()
    {
        var envelope = Envelope();
        Record(envelope, "ComputerSystem")["Model"] = true;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal("Unknown", Assert.Single(result.Items).PcModel);
        Assert.Contains("Parse error: ComputerSystem.Model", result.Message);
    }

    [Theory]
    [InlineData("Atlas\nspoofed status")]
    [InlineData("Atlas\u202e15")]
    public void ControlOrDirectionSpoofingTextIsNotDisplayedAsIdentity(string model)
    {
        var envelope = Envelope();
        Record(envelope, "ComputerSystem")["Model"] = model;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal("Unknown", Assert.Single(result.Items).PcModel);
    }

    [Fact]
    public void OversizedModelIsUnknownRatherThanSilentlyTruncatedIntoAnotherModel()
    {
        var envelope = Envelope();
        Record(envelope, "ComputerSystem")["Model"] = new string('X', 513);

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal("Unknown", Assert.Single(result.Items).PcModel);
    }

    [Theory]
    [InlineData("20261025233000.123456-660", "2026-10-25")]
    [InlineData("20261025003000.000000+660", "2026-10-25")]
    [InlineData("2024-02-29", "2024-02-29")]
    public void FirmwareDateIsACalendarDateWithoutInventedInstantOrTimezoneShift(string reported, string expected)
    {
        var envelope = Envelope();
        Record(envelope, "Bios")["ReleaseDate"] = reported;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(expected, Assert.Single(result.Items).BiosReleaseDate);
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("20261025240000.000000+000")]
    [InlineData("20260230000000.000000+000")]
    [InlineData("2026-10-25T00:00:00Z")]
    [InlineData("00000000000000.000000+000")]
    [InlineData("2026**********.******+***")]
    public void InvalidOrAmbiguousDateDoesNotInvalidateKnownVersionOrInventAReleaseDate(string reported)
    {
        var envelope = Envelope();
        Record(envelope, "Bios")["ReleaseDate"] = reported;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        var value = Assert.Single(result.Items);
        Assert.Equal("Unknown", value.BiosReleaseDate);
        Assert.Equal("F.12", value.BiosVersion);
    }

    [Fact]
    public void NullFieldsStayUnknownWithoutFallingBackToAnotherProvidersManufacturer()
    {
        var envelope = Envelope();
        Record(envelope, "BaseBoard")["Manufacturer"] = null;
        Record(envelope, "Bios")["ReleaseDate"] = null;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        var value = Assert.Single(result.Items);
        Assert.Equal("Unknown", value.BoardManufacturer);
        Assert.Equal("Unknown", value.BiosReleaseDate);
        Assert.Equal("Contoso Devices", value.PcManufacturer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":2}")]
    [InlineData("{\"SchemaVersion\":\"1\"}")]
    [InlineData("{\"SchemaVersion\":1}")]
    public void MalformedOrUnsupportedWholeResponseDoesNotInventAnyIdentity(string json)
    {
        var result = FirmwareIdentityParser.Parse(json);

        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void DuplicateVersionFieldsCannotChooseWhicheverValueHappenedToAppearLast()
    {
        var json = JsonSerializer.Serialize(Envelope()).Replace("\"SMBIOSBIOSVersion\":\"F.12\"", "\"SMBIOSBIOSVersion\":\"F.12\",\"SMBIOSBIOSVersion\":\"F.99\"", StringComparison.Ordinal);

        var result = FirmwareIdentityParser.Parse(json);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.All(SourceFields(Assert.Single(result.Items), "Bios"), value => Assert.Equal("Unknown", value));
        Assert.Contains("ambiguous", result.Message);
    }

    [Fact]
    public void AdditionalSerialUuidAndServiceTagFieldsNeverReachProjectedIdentity()
    {
        var envelope = Envelope();
        Record(envelope, "ComputerSystem")["UUID"] = "private-uuid-value";
        Record(envelope, "Bios")["SerialNumber"] = "private-serial-value";
        Record(envelope, "BaseBoard")["ServiceTag"] = "private-service-tag-value";

        var result = Parse(envelope);
        string output = JsonSerializer.Serialize(result);

        Assert.Equal(CheckState.Available, result.State);
        Assert.DoesNotContain("private-uuid-value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-serial-value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-service-tag-value", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedResponseIsUnknownInsteadOfAllocatingAnUnboundedProviderTree()
    {
        var result = FirmwareIdentityParser.Parse(new string(' ', 65_537));

        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
    }
}
