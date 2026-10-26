using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class FirmwareSupportPolicyTests
{
    private static FirmwareIdentity Identity(string manufacturer = "Microsoft Corporation", string model = "Surface Pro 9",
        string boardManufacturer = "Unknown", string boardProduct = "Unknown", string biosVersion = "Unknown") =>
        new(manufacturer, model, "Unknown", boardManufacturer, boardProduct, "Unknown", "Unknown", biosVersion, "Unknown");

    [Fact]
    public void RecognizedSurfacePcGetsOnlyGenericGuidanceWithUnverifiedApplicability()
    {
        var guide = FirmwareSupportPolicy.FindGuide(Identity());
        Assert.NotNull(guide);
        Assert.Equal("Microsoft Surface", guide.Vendor);
        Assert.Contains("not an available-update check", guide.Scope, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not exact-model/revision verified", guide.MatchStatus, StringComparison.Ordinal);
        Assert.Contains("unknown until you verify", guide.MatchStatus, StringComparison.Ordinal);
        Assert.True(FirmwareSupportPolicy.IsTrustedGuideUri(guide.SupportUri));
        Assert.True(FirmwareSupportPolicy.IsTrustedGuideUri(guide.PrecautionsUri));
    }

    [Theory]
    [InlineData("Surface Pro 9")]
    [InlineData("Surface Laptop 6")]
    [InlineData("Surface Laptop Studio 2")]
    [InlineData("Surface Book 3")]
    [InlineData("Surface Go 4")]
    [InlineData("Surface Studio 2+")]
    public void CatalogIsLimitedToIdentifiedWindowsSurfaceFamilies(string model) =>
        Assert.NotNull(FirmwareSupportPolicy.FindGuide(Identity(model: model)));

    [Fact]
    public void CaseAndOrdinaryWhitespaceAreNormalizedWithoutFuzzyOemMatching()
    {
        var guide = FirmwareSupportPolicy.FindGuide(Identity(" microsoft   corporation ", " surface   pro 9 "));
        Assert.NotNull(guide);
    }

    [Theory]
    [InlineData("Microsoft Corporation Example")]
    [InlineData("Contoso Microsoft Corporation")]
    [InlineData("Microsoft")]
    [InlineData("Unknown")]
    [InlineData("Dell Inc.")]
    [InlineData("HP")]
    [InlineData("LENOVO")]
    [InlineData("ASUSTeK COMPUTER INC.")]
    public void UnmatchedOrNotFreshlyVerifiedOemsDoNotReceiveAnInferredGuide(string manufacturer) =>
        Assert.Null(FirmwareSupportPolicy.FindGuide(Identity(manufacturer)));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Default string")]
    [InlineData("System Product Name")]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("Surface")]
    [InlineData("Virtual Machine")]
    [InlineData("Surface Hub 2S")]
    [InlineData("Surface Duo")]
    public void PlaceholderNonPcOrUnsupportedModelsRemainUnmatched(string model) =>
        Assert.Null(FirmwareSupportPolicy.FindGuide(Identity(model: model)));

    [Fact]
    public void ABaseboardVendorCannotOverrideAnUnknownComputerOem()
    {
        var identity = Identity("Unknown", "Surface Pro 9", "Microsoft Corporation", "Surface Pro 9");
        Assert.Null(FirmwareSupportPolicy.FindGuide(identity));
    }

    [Fact]
    public void RetailBoardInformationCannotOverrideTheComputerOem()
    {
        var guide = FirmwareSupportPolicy.FindGuide(Identity(boardManufacturer: "ASUSTeK COMPUTER INC.", boardProduct: "Retail Board"));
        Assert.NotNull(guide);
        Assert.Equal("Microsoft Surface", guide.Vendor);
    }

    [Fact]
    public void BiosVersionDoesNotBecomeAnAvailableVersionComparison()
    {
        var olderLooking = FirmwareSupportPolicy.FindGuide(Identity(biosVersion: "0.0.1"));
        var newerLooking = FirmwareSupportPolicy.FindGuide(Identity(biosVersion: "9999.0.0"));
        Assert.Equal(olderLooking, newerLooking);
    }

    [Theory]
    [InlineData("Microsoft\nCorporation", "Surface Pro 9")]
    [InlineData("Microsoft Corporation", "Surface Pro\u202e 9")]
    public void AmbiguousControlOrFormattingIdentityIsNotNormalizedIntoAMatch(string manufacturer, string model) =>
        Assert.Null(FirmwareSupportPolicy.FindGuide(Identity(manufacturer, model)));

    [Fact]
    public void PrecautionsDoNotInventBatteryThresholdsOrCollectRecoveryKeys()
    {
        var guide = FirmwareSupportPolicy.FindGuide(Identity());
        Assert.NotNull(guide);
        Assert.Contains("AC-power and battery prerequisites", guide.Precautions);
        Assert.Contains("model-specific requirements have not been verified", guide.Precautions);
        Assert.Contains("does not collect keys, change encryption settings", guide.Precautions);
        Assert.Contains("manufacturer and organizational policy", guide.Precautions);
        Assert.DoesNotContain("percent", guide.Precautions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%", guide.Precautions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://learn.microsoft.com/en-us/surface/manage-surface-driver-and-firmware-updates")]
    [InlineData("https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/user-experience-for-uefi-firmware-updates")]
    public void OnlyExactVerifiedCatalogUrisAreTrusted(string url) =>
        Assert.True(FirmwareSupportPolicy.IsTrustedGuideUri(new Uri(url)));

    [Theory]
    [InlineData("http://learn.microsoft.com/en-us/surface/manage-surface-driver-and-firmware-updates")]
    [InlineData("https://example.com/en-us/surface/manage-surface-driver-and-firmware-updates")]
    [InlineData("https://learn.microsoft.com.evil.example/en-us/surface/manage-surface-driver-and-firmware-updates")]
    [InlineData("https://user@learn.microsoft.com/en-us/surface/manage-surface-driver-and-firmware-updates")]
    [InlineData("https://learn.microsoft.com:8443/en-us/surface/manage-surface-driver-and-firmware-updates")]
    [InlineData("https://learn.microsoft.com/en-us/surface/manage-surface-driver-and-firmware-updates?model=Surface")]
    [InlineData("https://learn.microsoft.com/en-us/surface/manage-surface-driver-and-firmware-updates#download")]
    [InlineData("https://learn.microsoft.com/en-us/surface/other-page")]
    [InlineData("https://learn.microsoft.com/en-us/surface/download/firmware.exe")]
    [InlineData("https://learn.microsoft.com/en-us/surface/%6danage-surface-driver-and-firmware-updates")]
    [InlineData("https://learn.microsoft.com/en-us/surface/unused/../manage-surface-driver-and-firmware-updates")]
    [InlineData("https://support.microsoft.com/help/4023505")]
    [InlineData("https://www.dell.com/support/home/en-us")]
    public void QueriesRedirectAliasesUnverifiedCatalogEntriesAndOtherRoutesCannotWidenBrowserTrust(string url) =>
        Assert.False(FirmwareSupportPolicy.IsTrustedGuideUri(new Uri(url)));
}
