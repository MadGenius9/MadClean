using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WindowsUpdateIdentityMetadataTests
{
    private static WindowsUpdateIdentityMetadata Parse(string fields, int type = 2)
    {
        using var document = JsonDocument.Parse("{\"Version\":1," + fields + "}");
        return WindowsUpdateIdentityMetadataParser.Parse(document.RootElement, type);
    }
    private static string Revision(string value) => "\"RevisionNumber\":{\"State\":\"Available\",\"Value\":" + value + "}";
    private static string Hardware(string value) => "\"DriverHardwareID\":{\"State\":\"Available\",\"Value\":" + JsonSerializer.Serialize(value) + "}";

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2147483647", int.MaxValue)]
    public void AdmittedRevisionPreservesInt32BoundaryAndExactHardwareText(string revision, int expected)
    {
        var result = Parse(Revision(revision) + "," + Hardware(" PCI\\VEN_1234 😀 "));
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(expected, result.RevisionNumber.Value);
        Assert.Equal(" PCI\\VEN_1234 😀 ", result.DriverHardwareId.Value);
        Assert.True(WindowsUpdateIdentityMetadataParser.IsConsistent(result, 2));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("true")]
    [InlineData("\"7\"")]
    [InlineData("null")]
    public void InvalidRevisionDoesNotEraseValidHardwareOrBecomeZero(string revision)
    {
        var result = Parse(Revision(revision) + "," + Hardware("PCI\\VEN_1234"));
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(CheckState.Unknown, result.RevisionNumber.State); Assert.Null(result.RevisionNumber.Value);
        Assert.Equal("PCI\\VEN_1234", result.DriverHardwareId.Value);
        Assert.True(WindowsUpdateIdentityMetadataParser.IsConsistent(result, 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" uNkNoWn ")]
    [InlineData("N/A")]
    [InlineData("PCI\u202Espoof")]
    [InlineData("PCI\0tail")]
    public void UnsafeOrPlaceholderHardwareDoesNotHideValidRevision(string hardware)
    {
        var result = Parse(Revision("9") + "," + Hardware(hardware));
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(9, result.RevisionNumber.Value); Assert.Null(result.DriverHardwareId.Value);
        Assert.Equal(CheckState.Unknown, result.DriverHardwareId.State);
    }

    [Fact]
    public void HardwareLengthBoundIsAdmissionPolicyWithoutTruncation()
    {
        Assert.Equal(new string('a', 512), Parse(Revision("1") + "," + Hardware(new string('a', 512))).DriverHardwareId.Value);
        Assert.Null(Parse(Revision("1") + "," + Hardware(new string('a', 513))).DriverHardwareId.Value);
    }

    [Fact]
    public void MissingAndDuplicateOptionalFieldsPreserveTheUnambiguousCompanion()
    {
        Assert.Equal("PCI", Parse(Hardware("PCI")).DriverHardwareId.Value);
        Assert.Equal(7, Parse(Revision("7")).RevisionNumber.Value);
        var duplicateRevision = Parse(Revision("7") + "," + Revision("8") + "," + Hardware("PCI"));
        Assert.Null(duplicateRevision.RevisionNumber.Value); Assert.Equal("PCI", duplicateRevision.DriverHardwareId.Value);
        var duplicateHardware = Parse(Revision("7") + "," + Hardware("PCI") + "," + Hardware("ACPI"));
        Assert.Null(duplicateHardware.DriverHardwareId.Value); Assert.Equal(7, duplicateHardware.RevisionNumber.Value);
        Assert.Equal(CheckState.Unknown, Parse("\"Version\":1," + Revision("7") + "," + Hardware("PCI")).State);
    }

    [Theory]
    [InlineData("Failed", CheckState.Failed)]
    [InlineData("Unknown", CheckState.Unknown)]
    [InlineData("Unsupported", CheckState.Unsupported)]
    public void UnreadableFieldsKeepActualStateWithoutValue(string state, CheckState expected)
    {
        var result = Parse("\"RevisionNumber\":{\"State\":\"" + state + "\",\"Value\":null}," + Hardware("PCI"));
        Assert.Equal(expected, result.RevisionNumber.State); Assert.Null(result.RevisionNumber.Value);
        Assert.Equal(CheckState.Partial, result.State);
    }

    [Fact]
    public void SoftwareHardwareUnsupportedIsDistinctFromUnknownAndNeverADeviceDeclaration()
    {
        var supported = Parse(Revision("4") + ",\"DriverHardwareID\":{\"State\":\"Unsupported\",\"Value\":null}", 1);
        Assert.Equal(CheckState.Available, supported.State); Assert.Equal(CheckState.Unsupported, supported.DriverHardwareId.State);
        var invalid = Parse(Revision("4") + "," + Hardware("PCI"), 1);
        Assert.Equal(CheckState.Partial, invalid.State); Assert.Null(invalid.DriverHardwareId.Value);
        Assert.True(WindowsUpdateIdentityMetadataParser.IsConsistent(invalid, 1));
        Assert.False(WindowsUpdateIdentityMetadataParser.IsConsistent(supported with { DriverHardwareId = new(CheckState.Available, "PCI") }, 1));
    }

    [Theory]
    [InlineData("\"State\":\"Available\",\"State\":\"Failed\",\"Value\":7")]
    [InlineData("\"State\":\"Available\",\"Value\":7,\"Value\":8")]
    [InlineData("\"State\":\"Failed\",\"Value\":7")]
    [InlineData("\"State\":\"available\",\"Value\":7")]
    [InlineData("\"State\":\"2\",\"Value\":7")]
    public void AmbiguousOrContradictoryFieldCannotPublishPositiveMetadata(string field)
    {
        var result = Parse("\"RevisionNumber\":{" + field + "}," + Hardware("PCI"));
        Assert.Equal(CheckState.Unknown, result.RevisionNumber.State); Assert.Null(result.RevisionNumber.Value);
        Assert.Equal("PCI", result.DriverHardwareId.Value);
    }

    [Fact]
    public void OptionalMetadataFailuresDoNotChangeBaseOfferIdentityOrSearchCoverage()
    {
        const string row = "\"Title\":\"Driver\",\"Type\":2,\"Id\":\"01234567-89ab-cdef-0123-456789abcdef\",\"RebootRequired\":false";
        CheckResult<SystemUpdate> Read(string extra) => WindowsResponseParser.SystemUpdates("{\"ResultCode\":2,\"Updates\":[{" + row + extra + "}]}");
        var old = Read(""); Assert.Null(Assert.Single(old.Items).IdentityMetadata);
        var malformed = Read(",\"IdentityMetadata\":false");
        var duplicate = Read(",\"IdentityMetadata\":{},\"IdentityMetadata\":{}");
        foreach (var result in new[] { malformed, duplicate })
        {
            Assert.Equal(old.State, result.State);
            var offer = Assert.Single(result.Items);
            Assert.Equal(Assert.Single(old.Items).Identity, offer.Identity);
            Assert.Equal(CheckState.Unknown, offer.IdentityMetadata!.State);
        }
    }

    [Fact]
    public void InMemoryContradictionsCannotBeRenderedAsConsistentDeclarations()
    {
        var valid = Parse(Revision("7") + "," + Hardware("PCI"));
        Assert.False(WindowsUpdateIdentityMetadataParser.IsConsistent(valid with { State = CheckState.Unknown }, 2));
        Assert.False(WindowsUpdateIdentityMetadataParser.IsConsistent(valid with { RevisionNumber = new(CheckState.Available, -1) }, 2));
        Assert.False(WindowsUpdateIdentityMetadataParser.IsConsistent(valid with { DriverHardwareId = new(CheckState.Failed, "PCI") }, 2));
        Assert.False(WindowsUpdateIdentityMetadataParser.IsConsistent(valid, 99));
    }
}
