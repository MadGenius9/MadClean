using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class PackagedApplicationTests
{
    private static Dictionary<string, object?> Row(string name = "Example", string full = "Example_1.2.3.4_x64__publicid") => new()
    {
        ["Name"] = name, ["PackageFullName"] = full, ["PackageFamilyName"] = "Example_publicid", ["Version"] = "1.2.3.4",
        ["Publisher"] = "CN=Example Publisher", ["Architecture"] = "x64", ["IsBundle"] = false, ["NonRemovable"] = false,
        ["IsFramework"] = false, ["IsResourcePackage"] = false, ["SignatureKind"] = "Developer"
    };
    private static Dictionary<string, object?> Envelope(params Dictionary<string, object?>[] rows) => new()
    { ["Version"] = 1, ["Scope"] = "CurrentUser", ["PackageTypes"] = new[] { "Main", "Bundle" }, ["Status"] = "Available", ["Truncated"] = false, ["Packages"] = rows };
    private static CheckResult<PackagedApplication> Parse(Dictionary<string, object?> envelope) => PackagedApplicationParser.Parse(JsonSerializer.Serialize(envelope));

    [Fact]
    public void ReadsIdentityMetadataWithoutCombiningBundleAndMainOrClaimingActions()
    {
        var bundle = Row(full: "Example_bundle"); bundle["IsBundle"] = true;
        var result = Parse(Envelope(Row(), bundle));
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, item => item.PackageType == "Main");
        Assert.Contains(result.Items, item => item.PackageType == "Bundle");
        Assert.All(result.Items, item => { Assert.Equal("X64", item.Architecture); Assert.Equal("1.2.3.4", item.Version); Assert.False(item.NonRemovable); });
        Assert.Contains("not unique app counts", result.Message);
        Assert.Contains("No update, removal eligibility", result.Message);
    }
    [Fact]
    public void SuccessfulEmptyIsScopedAndTruncatedEmptyRemainsPartial()
    {
        var envelope = Envelope();
        Assert.Equal(CheckState.Available, Parse(envelope).State);
        envelope["Truncated"] = true;
        var partial = Parse(envelope);
        Assert.Equal(CheckState.Partial, partial.State);
        Assert.Empty(partial.Items);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderPartialPreservesIncompleteCoverageForEmptyAndValidRows(bool hasRow)
    {
        var envelope = hasRow ? Envelope(Row()) : Envelope();
        envelope["Status"] = "Partial";
        var result = Parse(envelope);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(hasRow ? 1 : 0, result.Items.Count);
        Assert.Contains("missing package rows remain unknown", result.Message);
        Assert.DoesNotContain("up to date", result.Message, StringComparison.OrdinalIgnoreCase);
        // Presence-only status has no free-form warning field or diagnostic text to echo.
        envelope["Warning"] = "PRIVATE provider warning";
        var invalid = Parse(envelope);
        Assert.Equal(CheckState.Unknown, invalid.State);
        Assert.DoesNotContain("PRIVATE", invalid.Message);
    }
    [Theory]
    [InlineData("Unavailable", CheckState.Unavailable)]
    [InlineData("Failed", CheckState.Failed)]
    public void ProviderFailureDoesNotBecomeEmptySuccess(string status, CheckState expected)
    {
        var envelope = Envelope(); envelope["Status"] = status;
        Assert.Equal(expected, Parse(envelope).State);
        envelope["Packages"] = new[] { Row() };
        Assert.Equal(CheckState.Unknown, Parse(envelope).State);
    }
    [Fact]
    public void MissingFlagAndUnrecognizedMetadataRemainUnknownRatherThanFalse()
    {
        var row = Row();
        row["NonRemovable"] = null; row["IsBundle"] = null; row["IsFramework"] = "false"; row["IsResourcePackage"] = 0;
        row["Architecture"] = "FutureArchitecture"; row["SignatureKind"] = "future"; row["Version"] = "1.2.3"; row["Publisher"] = null;
        var result = Parse(Envelope(row)); var item = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Null(item.NonRemovable); Assert.Null(item.IsFramework); Assert.Null(item.IsResourcePackage);
        Assert.Equal("Unknown", item.PackageType); Assert.Equal("Unknown", item.Version);
        Assert.Equal("Unknown", item.Publisher); Assert.Equal("Unknown", item.Architecture); Assert.Equal("Unknown", item.SignatureKind);
    }
    [Fact]
    public void DuplicateFullNamesOmitAllAmbiguousRowsIncludingMalformedOrExcludedDuplicates()
    {
        foreach (var mode in new[] { "case", "missing", "extra", "framework" })
        {
            var second = Row(full: "EXAMPLE_1.2.3.4_X64__PUBLICID");
            if (mode == "missing") second.Remove("Name");
            if (mode == "extra") second["InstallLocation"] = "PRIVATE";
            if (mode == "framework") second["IsFramework"] = true;
            var result = Parse(Envelope(Row(), second, Row(full: "Example_1.2.3.4_x64__publicid")));
            Assert.Equal(CheckState.Partial, result.State); Assert.Empty(result.Items); Assert.DoesNotContain("PRIVATE", result.Message);
        }
    }
    [Fact]
    public void FrameworkResourceAndInvalidIdentityRowsCannotMasqueradeAsMainApps()
    {
        foreach (string field in new[] { "IsFramework", "IsResourcePackage", "Name", "PackageFullName", "PackageFamilyName" })
        {
            var row = Row(); row[field] = field.StartsWith("Is", StringComparison.Ordinal) ? true : null;
            var result = Parse(Envelope(row)); Assert.Equal(CheckState.Partial, result.State); Assert.Empty(result.Items);
        }
    }
    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("65535.65535.65535.65535", true)]
    [InlineData("65536.0.0.0", false)]
    [InlineData("01.2.3.4", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("+1.2.3.4", false)]
    [InlineData("1.2.3.4-beta", false)]
    public void VersionsRequireExactlyFourCanonicalUnsignedSixteenBitParts(string version, bool valid)
    {
        var row = Row(); row["Version"] = version;
        var result = Parse(Envelope(row)); Assert.Equal(valid ? CheckState.Available : CheckState.Partial, result.State);
        Assert.Equal(valid ? version : "Unknown", Assert.Single(result.Items).Version);
    }
    [Theory]
    [InlineData("X86")][InlineData("Arm")][InlineData("X64")][InlineData("Neutral")][InlineData("Arm64")][InlineData("X86OnArm64")]
    public void AcceptsDocumentedArchitectureNames(string architecture)
    {
        var row = Row(); row["Architecture"] = architecture;
        Assert.Equal(architecture, Assert.Single(Parse(Envelope(row)).Items).Architecture);
    }
    [Fact]
    public void RejectsWrongScopePrivateEnvelopeFieldsDuplicateKeysAndMalformedJson()
    {
        foreach (string field in new[] { "Scope", "PackageTypes", "Version", "Truncated", "PrivateOutput" })
        {
            var envelope = Envelope(Row()); envelope[field] = field switch
            { "Scope" => "AllUsers", "PackageTypes" => new[] { "Main", "Framework" }, "Version" => 2, "Truncated" => "false", _ => "PRIVATE" };
            var result = Parse(envelope); Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items); Assert.DoesNotContain("PRIVATE", result.Message);
        }
        foreach (string json in new[] { "{", "null", JsonSerializer.Serialize(Envelope()).Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal) })
            Assert.Equal(CheckState.Unknown, PackagedApplicationParser.Parse(json).State);
    }
    [Fact]
    public void EnforcesRecordResponseAndUnicodeBoundsWithoutTruncatingIdentity()
    {
        Assert.Equal(CheckState.Unknown, PackagedApplicationParser.Parse(new string(' ', PackagedApplicationParser.ResponseLimit + 1)).State);
        Assert.Equal(CheckState.Unknown, Parse(Envelope(Enumerable.Range(0, 1001).Select(i => Row(full: "Package" + i)).ToArray())).State);
        Assert.Equal(1000, Parse(Envelope(Enumerable.Range(0, 1000).Select(i => Row(full: "Package" + i)).ToArray())).Items.Count);
        foreach (string invalid in new[] { new string('x', 257), "bad\nname", "bad\u202ename", " name" })
        {
            var result = Parse(Envelope(Row(name: invalid))); Assert.Equal(CheckState.Partial, result.State); Assert.Empty(result.Items);
        }
        Assert.Equal(CheckState.Available, Parse(Envelope(Row(name: "Example \U0001F4BB"))).State);
    }
    [Fact]
    public void CancellationTakesPrecedenceOverMalformedOrEmptySuccess()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => PackagedApplicationParser.Parse("{", stop.Token));
        Assert.Throws<OperationCanceledException>(() => PackagedApplicationParser.Parse(JsonSerializer.Serialize(Envelope()), stop.Token));
    }
    [Fact]
    public void SharedLiteralTextSearchRetainsNullRowAndInvalidFilterSemantics()
    {
        var empty = InstalledApplicationSearch.Create(null);
        Assert.False(empty.Matches(null)); Assert.True(empty.MatchesText(null, null));
        var filter = InstalledApplicationSearch.Create("publisher*");
        Assert.True(filter.MatchesText(null, "Literal Publisher*")); Assert.False(filter.MatchesText("Publisher", null));
        Assert.False(InstalledApplicationSearch.Create("\n").MatchesText("anything", "anything"));
    }
}
