using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class WindowsUpdateOfferSnapshotTests
{
    private static readonly SystemUpdate Firmware = new("Firmware offer", "Firmware",
        "79df6a1d-683a-4dfd-8d0c-295b95547720", false, "Firmware");

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    public void UnusableResultDoesNotPublishEmbeddedFirmware(CheckState state)
    {
        var result = WindowsUpdateOfferSnapshot.Accept(new(state, "Provider details", [Firmware]));
        Assert.Equal(state, result.State);
        Assert.Empty(result.Items);
        Assert.Contains("discarded", result.Message);
    }

    [Fact]
    public void PartialResultsPreserveExactSelectionReferencesAndFreezeMembership()
    {
        var rows = new List<SystemUpdate> { Firmware };
        var result = WindowsUpdateOfferSnapshot.Accept(new(CheckState.Partial, "Limited coverage", rows));

        rows.Clear();
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal("Limited coverage", result.Message);
        Assert.Same(Firmware, Assert.Single(result.Items));
    }

    [Fact]
    public void MalformedResponseCannotLeakTheValidPrefixIntoAnyView()
    {
        var result = WindowsUpdateOfferSnapshot.Accept(
            new(CheckState.Available, "Reported success", [Firmware, Firmware with { Kind = "Unexpected" }]));
        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void CompletedEmptySearchRetainsItsClaimOnlyAboutTheConfiguredSource()
    {
        var result = WindowsUpdateOfferSnapshot.Accept(
            CheckResult<SystemUpdate>.Empty(CheckState.Available, "The configured source returned no offers."));
        Assert.Equal(CheckState.Available, result.State);
        Assert.Empty(result.Items);
        Assert.Equal("The configured source returned no offers.", result.Message);
    }
}
