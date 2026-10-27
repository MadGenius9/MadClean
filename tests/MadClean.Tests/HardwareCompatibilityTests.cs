using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class HardwareCompatibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    // All specification documents/values below are test fixtures, not manufacturer-verified limits.
    private static HardwareCompatibilityIdentity Oem() => new(HardwareCompatibilityPlatform.OemSystem,
        "Dell Inc.", "Fixture System", "FIXTURE-SKU-1", "ASUSTeK COMPUTER INC.", "Fixture Board", "1.0", false, "1.2.3", "FixtureVendor/Bios");
    private static HardwareCompatibilityScope OemScope() => new(HardwareCompatibilityPlatform.OemSystem,
        HardwareCompatibilityClaimKind.Memory, "Dell Inc.", "Fixture System", "FIXTURE-SKU-1", "", "", "");
    private static HardwareCompatibilitySource OemSource() => new(new("https://www.dell.com/support/manuals/test-fixture"),
        "Dell Inc.", "Fixture System exact configuration document", CheckState.Available, Now.AddDays(-1), true);
    private static HardwareCompatibilityPreferences Preferences() => new(500, "USD", "US", "Photo editing and keeping existing storage");
    private static HardwareCompatibilityMatch Match(HardwareCompatibilityIdentity? identity = null,
        HardwareCompatibilityScope? scope = null, HardwareCompatibilitySource? source = null,
        HardwareCompatibilityFirmwareEvidence? firmware = null) =>
        HardwareCompatibilityPolicy.MatchTarget(identity ?? Oem(), scope ?? OemScope(), source ?? OemSource(), Now, firmware);
    private static HardwareCompatibilityMatch MatchArea(HardwareCompatibilityUpgradeArea area) => Match(scope: OemScope() with
    {
        ClaimKind = area switch
        {
            HardwareCompatibilityUpgradeArea.Cpu => HardwareCompatibilityClaimKind.Cpu,
            HardwareCompatibilityUpgradeArea.Gpu => HardwareCompatibilityClaimKind.Expansion,
            HardwareCompatibilityUpgradeArea.Storage => HardwareCompatibilityClaimKind.Storage,
            _ => HardwareCompatibilityClaimKind.Memory
        }
    });
    private static HardwareCompatibilityCheck[] AllChecks(HardwareCompatibilityUpgradeArea area) =>
        HardwareCompatibilityPolicy.RequiredChecks(area).Select(kind => new HardwareCompatibilityCheck(kind, true, "Independent fixture evidence for " + kind)).ToArray();

    [Fact]
    public void ExactOemScopeIsOnlyAMatchGateNotACompatibilityOrSourceAuthenticityClaim()
    {
        var result = Match();
        Assert.Equal(HardwareCompatibilityMatchState.ScopeMatched, result.State);
        Assert.Contains("Actual document authenticity/content", Assert.Single(result.Reasons));
        Assert.Contains("independent verification", Assert.Single(result.Reasons));
    }

    [Theory]
    [InlineData("Fixture System Plus", "FIXTURE-SKU-1")]
    [InlineData("Fixture System", "FIXTURE-SKU-2")]
    [InlineData("Fixture System", "Unknown")]
    [InlineData("Default string", "FIXTURE-SKU-1")]
    public void SimilarNamesMissingIdentityOrDifferentSkuCannotMatch(string model, string sku)
    {
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(identity: Oem() with { Model = model, Sku = sku }).State);
    }

    [Fact]
    public void RetailBoardResemblanceCannotOverrideAnOemSystem()
    {
        var scope = OemScope() with { Platform = HardwareCompatibilityPlatform.RetailBoard, BoardManufacturer = "ASUSTeK COMPUTER INC.", BoardProduct = "Fixture Board", BoardRevision = "1.0" };
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(scope: scope).State);
    }

    [Theory]
    [InlineData(false, "1.0")]
    [InlineData(true, "1.1")]
    [InlineData(true, "Unknown")]
    public void RetailBoardRequiresExactIndependentlyVerifiedRevision(bool revisionVerified, string revision)
    {
        var identity = Oem() with { Platform = HardwareCompatibilityPlatform.RetailBoard, BoardRevisionVerified = revisionVerified, BoardRevision = revision };
        var scope = OemScope() with { Platform = HardwareCompatibilityPlatform.RetailBoard, BoardManufacturer = identity.BoardManufacturer, BoardProduct = identity.BoardProduct, BoardRevision = "1.0" };
        var source = OemSource() with { Issuer = identity.BoardManufacturer, Uri = new("https://www.asus.com/support/test-fixture") };
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(identity, scope, source).State);
    }

    [Fact]
    public void ExactVerifiedRetailRevisionCanPassOnlyTheScopeGate()
    {
        var identity = Oem() with { Platform = HardwareCompatibilityPlatform.RetailBoard, BoardRevisionVerified = true };
        var scope = OemScope() with { Platform = HardwareCompatibilityPlatform.RetailBoard, BoardManufacturer = identity.BoardManufacturer, BoardProduct = identity.BoardProduct, BoardRevision = identity.BoardRevision };
        var source = OemSource() with { Issuer = identity.BoardManufacturer, Uri = new("https://www.asus.com/support/test-fixture") };
        Assert.Equal(HardwareCompatibilityMatchState.ScopeMatched, Match(identity, scope, source).State);
    }

    [Theory]
    [InlineData("http://www.dell.com/support/manuals/test-fixture")]
    [InlineData("https://www.dell.com.evil.test/support/manuals/test-fixture")]
    [InlineData("https://evil@www.dell.com/support/manuals/test-fixture")]
    [InlineData("https://www.dell.com:8443/support/manuals/test-fixture")]
    [InlineData("https://www.dell.com/support/manuals/test-fixture?redirect=evil")]
    [InlineData("https://www.dell.com/support/manuals/test-fixture#other-model")]
    [InlineData("https://www.dell.com/support/%2e%2e/manuals/test-fixture")]
    [InlineData("https://www.dell.com./support/manuals/test-fixture")]
    [InlineData("https://www.dell.com/")]
    [InlineData("https://www.dell.com/support/home/en-us")]
    public void SourceUriAmbiguityAndGenericLandingPagesAreNotAccepted(string uri)
    {
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(source: OemSource() with { Uri = new(uri) }).State);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Partial)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    public void NonretrievedSourcesCannotBecomeVerifiedEvidence(CheckState state)
    {
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(source: OemSource() with { RetrievalState = state }).State);
    }

    [Fact]
    public void UnknownStaleFutureConflictingOrGenericSourceScopeBlocksTheGate()
    {
        foreach (var source in new[]
        {
            OemSource() with { RetrievedUtc = null }, OemSource() with { RetrievedUtc = Now.AddDays(-31) },
            OemSource() with { RetrievedUtc = Now.AddSeconds(1) }, OemSource() with { HasConflictingEvidence = true },
            OemSource() with { ExactProductScope = false }, OemSource() with { Issuer = "ASUSTeK COMPUTER INC." }
        }) Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(source: source).State);
    }

    [Fact]
    public void FirmwareApprovalMustBeTiedToCurrentExactVersionNamespaceAndEvidence()
    {
        var scope = OemScope() with { ClaimKind = HardwareCompatibilityClaimKind.Cpu, RequiresFirmwarePrerequisite = true, FirmwareNamespace = "FixtureVendor/Bios", FirmwareRequirement = "Fixture minimum version rule" };
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(scope: scope).State);
        foreach (var firmware in new[]
        {
            new HardwareCompatibilityFirmwareEvidence(false, "1.2.3", "FixtureVendor/Bios", "Fixture source rule"),
            new HardwareCompatibilityFirmwareEvidence(true, "1.2.4", "FixtureVendor/Bios", "Fixture source rule"),
            new HardwareCompatibilityFirmwareEvidence(true, "1.2.3", "OtherVendor/Bios", "Fixture source rule"),
            new HardwareCompatibilityFirmwareEvidence(true, "1.2.3", "FixtureVendor/Bios")
        }) Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(scope: scope, firmware: firmware).State);
        Assert.Equal(HardwareCompatibilityMatchState.ScopeMatched, Match(scope: scope,
            firmware: new(true, "1.2.3", "FixtureVendor/Bios", "Fixture source rule")).State);
    }

    [Theory]
    [InlineData("/en-us/surface/surface-system-sku-reference")]
    [InlineData("/surface/surface-system-sku-reference")]
    [InlineData("/en-gb/surface/surface-system-sku-reference")]
    public void IdentityReferenceCannotBeReusedAsRamSpecification(string path)
    {
        var identity = Oem() with { Manufacturer = "Microsoft Corporation" };
        var scope = OemScope() with { Manufacturer = identity.Manufacturer };
        var source = OemSource() with { Issuer = identity.Manufacturer, Uri = new("https://learn.microsoft.com" + path) };
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(identity, scope, source).State);
    }

    [Fact]
    public void CatalogRequiresExactDocumentedModelAndSkuAndNeverClaimsUpgradeLimits()
    {
        var result = HardwareCompatibilityIdentityCatalog.Find("Microsoft Corporation", "Surface Laptop 6 for Business", "Surface_Laptop_6_for_Business_2033");
        Assert.NotNull(result); Assert.Contains("13.5-inch", result.ProductLabel);
        Assert.Contains("MadClean has not retrieved the page in this session", result.Scope);
        Assert.Contains("no RAM limit", result.Scope);
        Assert.Null(HardwareCompatibilityIdentityCatalog.Find("Microsoft Corporation", "Surface Laptop 6 for Business", "Surface_Laptop_6_for_Business_2035_OTHER"));
        Assert.Null(HardwareCompatibilityIdentityCatalog.Find("Microsoft Corporation", "Surface Laptop 6", "Surface_Laptop_6_for_Business_2033"));
        Assert.Null(HardwareCompatibilityIdentityCatalog.Find("Dell Inc.", result.Model, result.Sku));
    }

    [Theory]
    [InlineData(HardwareCompatibilityUpgradeArea.Memory, HardwareCompatibilityCheckKind.ReplaceabilityOrSolderedDesign)]
    [InlineData(HardwareCompatibilityUpgradeArea.Memory, HardwareCompatibilityCheckKind.PhysicalOccupancy)]
    [InlineData(HardwareCompatibilityUpgradeArea.Cpu, HardwareCompatibilityCheckKind.RequiredFirmware)]
    [InlineData(HardwareCompatibilityUpgradeArea.Gpu, HardwareCompatibilityCheckKind.PsuCapacityAndConnectors)]
    [InlineData(HardwareCompatibilityUpgradeArea.Gpu, HardwareCompatibilityCheckKind.CaseAndGpuClearance)]
    [InlineData(HardwareCompatibilityUpgradeArea.Storage, HardwareCompatibilityCheckKind.PhysicalOccupancy)]
    public void MissingPhysicalOrManufacturerEvidenceCannotBeInferredFromInventory(HardwareCompatibilityUpgradeArea area, HardwareCompatibilityCheckKind unknown)
    {
        var checks = AllChecks(area).Select(check => check.Kind == unknown ? check with { Verified = false } : check).ToArray();
        var result = HardwareCompatibilityPolicy.AssessAdvice(area, MatchArea(area), Preferences(), checks);
        Assert.Equal(HardwareCompatibilityAdviceState.Unknown, result.State);
        Assert.Contains(result.Reasons, reason => reason.Contains(unknown.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void BudgetCurrencyRegionAndUseCaseAreRequiredEvenAfterEvidenceIsPresent()
    {
        foreach (var preferences in new[]
        {
            Preferences() with { Budget = null }, Preferences() with { Budget = 0 },
            Preferences() with { Currency = "ZZZ" }, Preferences() with { Region = "XX" }, Preferences() with { UseCase = "" }
        }) Assert.Equal(HardwareCompatibilityAdviceState.Unknown, HardwareCompatibilityPolicy.AssessAdvice(
            HardwareCompatibilityUpgradeArea.Memory, Match(), preferences, AllChecks(HardwareCompatibilityUpgradeArea.Memory)).State);
    }

    [Fact]
    public void CompleteInputsPermitManualReviewNotAProductRecommendationOrAction()
    {
        var result = HardwareCompatibilityPolicy.AssessAdvice(HardwareCompatibilityUpgradeArea.Gpu, MatchArea(HardwareCompatibilityUpgradeArea.Gpu), Preferences(), AllChecks(HardwareCompatibilityUpgradeArea.Gpu));
        Assert.Equal(HardwareCompatibilityAdviceState.ReadyForReview, result.State);
        Assert.Contains("No product, price, compatibility guarantee, purchase", Assert.Single(result.Reasons));
    }

    [Fact]
    public void DuplicateContradictoryCheckRecordsCannotChooseAConvenientSuccess()
    {
        var checks = AllChecks(HardwareCompatibilityUpgradeArea.Gpu);
        Assert.Equal(HardwareCompatibilityAdviceState.Unknown, HardwareCompatibilityPolicy.AssessAdvice(
            HardwareCompatibilityUpgradeArea.Gpu, MatchArea(HardwareCompatibilityUpgradeArea.Gpu), Preferences(), checks.Append(checks[0] with { Verified = false }).ToArray()).State);
    }
    [Fact]
    public void IdentityOnlyOrAnotherComponentsScopeCannotAuthorizeAdvisoryReview()
    {
        var identityOnly = Match(scope: OemScope() with { ClaimKind = HardwareCompatibilityClaimKind.Identity });
        Assert.Equal(HardwareCompatibilityAdviceState.Unknown, HardwareCompatibilityPolicy.AssessAdvice(
            HardwareCompatibilityUpgradeArea.Memory, identityOnly, Preferences(), AllChecks(HardwareCompatibilityUpgradeArea.Memory)).State);
        Assert.Equal(HardwareCompatibilityAdviceState.Unknown, HardwareCompatibilityPolicy.AssessAdvice(
            HardwareCompatibilityUpgradeArea.Gpu, Match(), Preferences(), AllChecks(HardwareCompatibilityUpgradeArea.Gpu)).State);
    }

    [Fact]
    public void MissingOrControlCharacterIdentityDoesNotThrowOrBecomeAModelMatch()
    {
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(identity: Oem() with { Manufacturer = null!, Model = null!, Sku = null! }).State);
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(identity: Oem() with { Model = "Fixture System\u202E" }).State);
        Assert.Null(HardwareCompatibilityIdentityCatalog.Find("Microsoft Corporation", "Surface Laptop 6 for Business", "Surface_Laptop_6_for_Business_2033\n"));
    }

    [Fact]
    public void UnknownUpgradeAreaCannotPassAnEmptyChecklist()
    {
        Assert.Equal(HardwareCompatibilityAdviceState.Unknown, HardwareCompatibilityPolicy.AssessAdvice(
            (HardwareCompatibilityUpgradeArea)999, Match(), Preferences(), []).State);
    }

    [Fact]
    public void ClaimingASpecificDocumentDoesNotInventAnUnknownCatalogRow()
    {
        var identity = Oem() with { Manufacturer = "Microsoft Corporation" };
        var scope = OemScope() with { Manufacturer = identity.Manufacturer, ClaimKind = HardwareCompatibilityClaimKind.Identity };
        var source = OemSource() with { Issuer = identity.Manufacturer, Uri = new("https://learn.microsoft.com/en-us/surface/surface-system-sku-reference") };
        Assert.Equal(HardwareCompatibilityMatchState.Unknown, Match(identity, scope, source).State);
    }

}
