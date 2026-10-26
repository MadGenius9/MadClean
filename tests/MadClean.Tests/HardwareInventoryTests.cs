using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class HardwareInventoryTests
{
    private static Dictionary<string, object?> Source(string name, Dictionary<string, object?> record) => new()
    { ["Class"] = name, ["Status"] = "Available", ["Truncated"] = false, ["Records"] = new object?[] { record } };
    private static Dictionary<string, object?> Envelope() => new()
    {
        ["Version"] = 1,
        ["Sources"] = new[]
        {
            Source("Win32_ComputerSystem", new() { ["Manufacturer"] = "Contoso", ["Model"] = "Atlas", ["SystemFamily"] = "Atlas family", ["SystemSKUNumber"] = "SKU-15", ["SystemType"] = "x64-based PC" }),
            Source("Win32_BaseBoard", new() { ["Manufacturer"] = "BoardCo", ["Product"] = "Board 15", ["Version"] = "Rev 1.2" }),
            Source("Win32_Processor", new() { ["Manufacturer"] = "CpuCo", ["Name"] = "Reported CPU", ["NumberOfCores"] = 8, ["NumberOfLogicalProcessors"] = 16, ["MaxClockSpeed"] = 3000, ["SocketDesignation"] = "CPU0" }),
            Source("Win32_PhysicalMemory", new() { ["Manufacturer"] = "MemoryCo", ["PartNumber"] = "Part-16G", ["Capacity"] = "17179869184", ["SMBIOSMemoryType"] = 26, ["FormFactor"] = 12, ["ConfiguredClockSpeed"] = 1600 }),
            Source("Win32_VideoController", new() { ["Name"] = "Virtual display", ["VideoProcessor"] = "Reported processor", ["DriverVersion"] = "1.2.3" }),
            Source("Win32_DiskDrive", new() { ["Manufacturer"] = "DiskCo", ["Model"] = "Disk 1000", ["MediaType"] = "Fixed hard disk media", ["InterfaceType"] = "SCSI", ["Size"] = "1000000000000", ["FirmwareRevision"] = "D1" })
        }
    };
    private static Dictionary<string, object?>[] Sources(Dictionary<string, object?> envelope) => (Dictionary<string, object?>[])envelope["Sources"]!;
    private static Dictionary<string, object?> Record(Dictionary<string, object?> source) => (Dictionary<string, object?>)((object?[])source["Records"]!)[0]!;
    private static CheckResult<HardwareFact> Parse(Dictionary<string, object?> envelope) => HardwareInventoryParser.Parse(JsonSerializer.Serialize(envelope));
    private static HardwareFact Fact(CheckResult<HardwareFact> result, string source, string property) =>
        Assert.Single(result.Items, fact => fact.Source == $"root/cimv2:{source}.{property}");

    [Fact]
    public void ProjectedObservationsKeepRawCapacityUnitsAndSourceWithoutCompatibilityClaims()
    {
        var result = Parse(Envelope());

        Assert.Equal(CheckState.Partial, result.State); // Raw SMBIOS memory-type meaning is deliberately unverified.
        Assert.Equal(29, result.Items.Count);
        Assert.Equal("17179869184", Fact(result, "Win32_PhysicalMemory", "Capacity").Value);
        Assert.Contains("bytes", Fact(result, "Win32_PhysicalMemory", "Capacity").Property);
        Assert.Equal("1000000000000", Fact(result, "Win32_DiskDrive", "Size").Value);
        Assert.Contains("not manufacturer-verified", result.Message);
        Assert.Contains("do not establish physical connectors", result.Message);
        Assert.DoesNotContain(result.Items, fact => fact.Property.Contains("AdapterRAM", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedSourceDoesNotEraseOtherSourcesOrLeakRawProviderErrors()
    {
        var envelope = Envelope();
        var failed = Sources(envelope)[4]; failed["Status"] = "Failed"; failed["Records"] = Array.Empty<object>();
        failed["Error"] = "private-provider-detail";

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.DoesNotContain(result.Items, fact => fact.Category == "GPU");
        Assert.Equal("Atlas", Fact(result, "Win32_ComputerSystem", "Model").Value);
        Assert.Contains("Win32_VideoController: query failed", result.Message);
        Assert.DoesNotContain("private-provider-detail", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFailedSourceProducesFailedCheckWithActionableCoverageRatherThanEmptySuccess()
    {
        var envelope = Envelope();
        foreach (var source in Sources(envelope)) { source["Status"] = "Failed"; source["Records"] = Array.Empty<object>(); }

        var result = Parse(envelope);

        Assert.Equal(CheckState.Failed, result.State);
        Assert.Empty(result.Items);
        Assert.Contains("Win32_PhysicalMemory: query failed", result.Message);
        Assert.Contains("Win32_DiskDrive: query failed", result.Message);
    }

    [Fact]
    public void MissingAndEmptySourcesStayExplicitlyIncompleteAndDoNotMeanUnusedSlots()
    {
        var envelope = Envelope();
        Sources(envelope)[3]["Records"] = Array.Empty<object>();
        envelope["Sources"] = Sources(envelope).Where(source => (string)source["Class"]! != "Win32_BaseBoard").ToArray();

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Contains("Win32_BaseBoard: missing source", result.Message);
        Assert.Contains("Win32_PhysicalMemory: empty response", result.Message);
        Assert.DoesNotContain(result.Items, fact => fact.Category == "RAM");
        Assert.DoesNotContain(result.Items, fact => fact.Property.Contains("free", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Default string")]
    [InlineData("----")]
    [InlineData("   ")]
    public void PlaceholderIdentityCannotBecomeAKnownSpecificModel(string placeholder)
    {
        var envelope = Envelope(); Record(Sources(envelope)[0])["Model"] = placeholder;

        var result = Parse(envelope);

        Assert.Equal("Unknown", Fact(result, "Win32_ComputerSystem", "Model").Value);
        Assert.Contains("unknown/uninterpreted/invalid fields: Model", result.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1,024")]
    [InlineData("1e6")]
    [InlineData("18446744073709551616")]
    public void InvalidCapacityIsUnknownRatherThanRoundedOverflowedOrLocalized(string reported)
    {
        var envelope = Envelope(); Record(Sources(envelope)[3])["Capacity"] = reported;

        var result = Parse(envelope);

        Assert.Equal("Unknown", Fact(result, "Win32_PhysicalMemory", "Capacity").Value);
        Assert.Equal("Atlas", Fact(result, "Win32_ComputerSystem", "Model").Value);
    }

    [Fact]
    public void FullUnsignedCapacityIsPreservedWithoutConvertingThroughDoubleOrSignedLong()
    {
        var envelope = Envelope(); Record(Sources(envelope)[3])["Capacity"] = ulong.MaxValue;

        var result = Parse(envelope);

        Assert.Equal("18446744073709551615", Fact(result, "Win32_PhysicalMemory", "Capacity").Value);
    }

    [Fact]
    public void UnknownEnumsKeepRawEvidenceWithoutInferringMemoryTypeOrReplaceability()
    {
        var envelope = Envelope(); Record(Sources(envelope)[3])["FormFactor"] = 999;
        Record(Sources(envelope)[3])["SMBIOSMemoryType"] = 200;
        Record(Sources(envelope)[3])["ConfiguredClockSpeed"] = 0;

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Contains("Unknown", Fact(result, "Win32_PhysicalMemory", "FormFactor").Value);
        Assert.Contains("999", Fact(result, "Win32_PhysicalMemory", "FormFactor").Value);
        Assert.Contains("Unknown meaning", Fact(result, "Win32_PhysicalMemory", "SMBIOSMemoryType").Value);
        Assert.Contains("200", Fact(result, "Win32_PhysicalMemory", "SMBIOSMemoryType").Value);
        Assert.Equal("Unknown", Fact(result, "Win32_PhysicalMemory", "ConfiguredClockSpeed").Value);
    }

    [Fact]
    public void MultipleBoardsRemainSeparateObservedRecordsNotAChosenRetailMotherboard()
    {
        var envelope = Envelope(); var board = Sources(envelope)[1];
        board["Records"] = new object?[] { Record(board), new Dictionary<string, object?> { ["Manufacturer"] = "OtherCo", ["Product"] = "Another reported board", ["Version"] = "2" } };

        var result = Parse(envelope);

        Assert.Equal(2, result.Items.Where(fact => fact.Category == "Baseboard").Select(fact => fact.Component).Distinct().Count());
        Assert.Contains(result.Items, fact => fact.Value == "Another reported board");
        Assert.Contains("Win32_BaseBoard: 2 reported record(s)", result.Message);
    }

    [Fact]
    public void SourceRecordAndOutputWorkIsBoundedWithExplicitTruncation()
    {
        var envelope = Envelope(); var memory = Sources(envelope)[3]; var record = Record(memory);
        memory["Records"] = Enumerable.Range(0, 140).Select(_ => new Dictionary<string, object?>(record)).ToArray();

        var result = Parse(envelope);

        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(128 * 6, result.Items.Count(fact => fact.Category == "RAM"));
        Assert.Contains("Win32_PhysicalMemory: 128 reported record(s); TRUNCATED", result.Message);
    }

    [Fact]
    public void ExplicitSourceTruncationCannotBecomeCompleteWhenReturnedArrayIsSmall()
    {
        var envelope = Envelope(); Sources(envelope)[5]["Truncated"] = true;

        var result = Parse(envelope);

        Assert.Contains("Win32_DiskDrive: 1 reported record(s); TRUNCATED", result.Message);
    }

    [Fact]
    public void AmbiguousDuplicateSourcesAreNotResolvedByLastValueWins()
    {
        var envelope = Envelope(); envelope["Sources"] = Sources(envelope).Append(Sources(envelope)[0]).ToArray();

        var result = Parse(envelope);

        Assert.DoesNotContain(result.Items, fact => fact.Category == "PC");
        Assert.Contains("Win32_ComputerSystem: duplicate/ambiguous sources", result.Message);
    }

    [Fact]
    public void MalformedRecordDoesNotTurnIntoStringifiedHardwareOrEraseOtherProviders()
    {
        var envelope = Envelope(); Sources(envelope)[2]["Records"] = new object?[] { "not-a-processor" };

        var result = Parse(envelope);

        Assert.DoesNotContain(result.Items, fact => fact.Category == "CPU");
        Assert.Equal("Atlas", Fact(result, "Win32_ComputerSystem", "Model").Value);
        Assert.Contains("malformed record(s) omitted", result.Message);
    }

    [Fact]
    public void ExtraClassesAndUniqueIdentifierFieldsAreNotExposedInFactsOrMessages()
    {
        var envelope = Envelope();
        Record(Sources(envelope)[5])["SerialNumber"] = "private-serial-value";
        Record(Sources(envelope)[4])["PNPDeviceID"] = "private-instance-value";
        envelope["Sources"] = Sources(envelope).Append(Source("Win32_ComputerSystemProduct", new() { ["UUID"] = "private-uuid-value" })).ToArray();

        var result = Parse(envelope); string output = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("private-serial-value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-instance-value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-uuid-value", output, StringComparison.Ordinal);
        Assert.Contains("unexpected or unrecognized source", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"Version\":2,\"Sources\":[]}")]
    [InlineData("{\"Version\":1,\"Sources\":null}")]
    public void InvalidEnvelopeDoesNotInventSpecifications(string json)
    {
        var result = HardwareInventoryParser.Parse(json);

        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
    }
}
