using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class StructuredWingetDiscoveryTests
{
    private static StructuredWingetCatalogFacts Official => new(StructuredWingetPolicy.SourceName,
        StructuredWingetPolicy.SourceType, StructuredWingetPolicy.SourceArgument, StructuredWingetPolicy.SourceIdentifier,
        false, true, true);
    private static StructuredWingetPackageFacts Package => new("Example", "Example.App", "2026-beta", "rolling-current", true, true);

    [Fact]
    public void FamiliarSourceNameCannotAuthorizeAnAlteredEndpointOrIdentifier()
    {
        Assert.True(StructuredWingetPolicy.CanConnect(Official));
        Assert.False(StructuredWingetPolicy.CanConnect(Official with { Argument = "https://example.invalid/cache" }));
        Assert.False(StructuredWingetPolicy.CanConnect(Official with { Identifier = "Another.Source" }));
        Assert.False(StructuredWingetPolicy.CanConnect(Official with { Type = "Microsoft.Rest" }));
        Assert.False(StructuredWingetPolicy.CanConnect(Official with { Argument = StructuredWingetPolicy.SourceArgument + "/" }));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, null, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, null)]
    [InlineData(false, true, false)]
    public void MissingAgreementAuthenticationOrNonCompositeEvidenceRefusesBeforeConnection(bool? composite, bool? agreements, bool? auth)
        => Assert.False(StructuredWingetPolicy.CanConnect(Official with { IsComposite = composite, AgreementsDisabled = agreements, AuthenticationNone = auth }));

    [Fact]
    public void ProviderOrderingIsUsedWithoutInventingVersionSyntax()
    {
        var result = Project(Package);
        Assert.Equal(CheckState.Available, Assert.Single(result.Items).Status);
        Assert.Contains("not verified compatibility", result.Message);
        var reversed = Project(Package with { InstalledVersion = "99.0", OfferedVersion = "2.0" });
        Assert.Equal(CheckState.Available, Assert.Single(reversed.Items).Status); // Provider fact, not local lexical/numeric ordering.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void NoPositiveNewerObservationNeverBecomesUpToDate(bool? newer)
    {
        var row = Assert.Single(Project(Package with { ReportsNewerVersion = newer }).Items);
        Assert.Equal(CheckState.Unknown, row.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("unknown")]
    [InlineData("-")]
    public void PositiveProviderFlagWithoutUsableInstalledOrOfferedVersionRemainsUnknown(string? version)
    {
        Assert.Equal(CheckState.Unknown, Assert.Single(Project(Package with { InstalledVersion = version }).Items).Status);
        Assert.Equal(CheckState.Unknown, Assert.Single(Project(Package with { OfferedVersion = version }).Items).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void OfferedVersionFromAnUnmatchedOrUnknownSourceCannotBecomeAvailable(bool? source)
        => Assert.Equal(CheckState.Unknown, Assert.Single(Project(Package with { OfferedSourceMatched = source }).Items).Status);

    [Fact]
    public void AmbiguousDuplicateCorrelationDowngradesBothRowsRegardlessOfCasing()
    {
        var result = StructuredWingetPolicy.Project([Package, Package with { Id = "example.app", InstalledVersion = "another" }], false, false, 0);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.All(result.Items, row => Assert.Equal(CheckState.Unknown, row.Status));
    }

    [Fact]
    public void TruncationDoesNotEraseValidPositiveRowsOrClaimCompleteCoverage()
    {
        var many = Enumerable.Range(0, 257).Select(i => Package with { Id = "Example." + i }).ToArray();
        var result = StructuredWingetPolicy.Project(many, false, false, 0);
        Assert.Equal(256, result.Items.Count);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.All(result.Items, row => Assert.Equal(CheckState.Available, row.Status));
        Assert.Equal(CheckState.Partial, StructuredWingetPolicy.Project([], true, false, 0).State);
        Assert.Equal(CheckState.Partial, StructuredWingetPolicy.Project([], null, false, 0).State);
        Assert.Equal(CheckState.Partial, StructuredWingetPolicy.Project([], false, null, 0).State);
    }

    [Fact]
    public void EmptySuccessfulQueryAndDroppedPropertiesNeverMeanAllSoftwareCurrent()
    {
        var empty = StructuredWingetPolicy.Project([], false, false, 0);
        Assert.Empty(empty.Items);
        Assert.Contains("Empty results do not establish", empty.Message);
        Assert.Equal(CheckState.Partial, StructuredWingetPolicy.Project([], false, false, 1).State);
    }

    [Fact]
    public void OversizedOrControlContainingMetadataIsNotDisplayedOrAccepted()
    {
        foreach (string bad in new[] { new string('x', 513), "app\nsecret", "app\0" })
        {
            var result = Project(Package with { Name = bad });
            Assert.Equal(CheckState.Partial, result.State);
            Assert.Equal("Unknown", Assert.Single(result.Items).Name);
            Assert.Equal(CheckState.Unknown, Assert.Single(result.Items).Status);
        }
    }

    private static CheckResult<AppUpdate> Project(StructuredWingetPackageFacts package) =>
        StructuredWingetPolicy.Project([package], false, false, 0);
}
