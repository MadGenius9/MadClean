using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DriverUpdateReviewTests
{
    private static readonly DateTimeOffset Attempt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Received = Attempt.AddMinutes(1), Now = Attempt.AddMinutes(2);
    private static SystemUpdate Offer() => new("Reported network driver", "Driver", "01234567-89ab-cdef-0123-456789abcdef", false,
        "Net", "2026-01-01", "Reported description\r\nSecond line");
    private static CheckResult<SystemUpdate> Source(params SystemUpdate[] items) => new(CheckState.Partial, "Incomplete configured service coverage", items);
    private static CheckResult<DriverUpdateReviewReport> Review(SystemUpdate selected, CheckResult<SystemUpdate>? source = null,
        IReadOnlyList<SystemUpdate>? loaded = null) => DriverUpdateReviewPolicy.Review(source ?? Source(selected), loaded ?? [selected], selected, Attempt, Received, Now);
    private static WindowsUpdateOfferMetadata AllKnown() => new(CheckState.Available,
        new(CheckState.Available, "Declared OEM"), new(CheckState.Available, "Declared provider"), new(CheckState.Available, "Declared model"),
        new(CheckState.Available, false), new(CheckState.Available, false),
        new(CheckState.Available, WindowsUpdateInstallationImpact.Minor), new(CheckState.Available, WindowsUpdateRebootBehavior.NeverReboots));

    [Fact]
    public void CompleteConservativeDeclarationsNeverEstablishTruePrerequisitesOrGrantInstallation()
    {
        var offer = Offer() with { OfferMetadata = AllKnown() };
        var result = Review(offer); var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(CheckState.Unknown, report.State);
        Assert.False(report.CanInstall); Assert.Same(offer, report.SelectedOffer);
        Assert.Equal(Attempt, report.AttemptedUtc); Assert.Equal(Received, report.ReceivedUtc); Assert.Equal(Now, report.ReviewedUtc);
        Assert.Equal(CheckState.Partial, report.SourceState);
        Assert.Equal("NeverReboots", Assert.Single(report.Requirements, row => row.Title == "Declared reboot behavior").Evidence);
        string[] actual = ["Exact device/OEM match", "Installed/offered driver versions", "Official prerequisites and release notes",
            "Required elevation and consent", "Dependencies and rollback", "Actual restart controls", "Actual result verification"];
        foreach (string title in actual) Assert.Equal(CheckState.Unknown, Assert.Single(report.Requirements, row => row.Title == title).State);
        Assert.Contains("No download, installation", result.Message);
        Assert.Null(typeof(DriverUpdateReviewReport).GetProperty(nameof(DriverUpdateReviewReport.CanInstall))!.SetMethod);
        Assert.Null(typeof(DriverUpdateReviewReport).GetProperty(nameof(DriverUpdateReviewReport.State))!.SetMethod);
    }

    [Fact]
    public void ProviderRevisionAndCompatibleIdDoNotProveVersionsOrDisambiguateDuplicateUpdateIds()
    {
        var offer = Offer() with { IdentityMetadata = new(CheckState.Available,
            new(CheckState.Available, 7), new(CheckState.Available, @"PCI\VEN_1234")) };
        var report = Assert.Single(Review(offer).Items);
        Assert.Equal("7", Assert.Single(report.Requirements, row => row.Title == "Provider update revision").Evidence);
        Assert.Equal(@"PCI\VEN_1234", Assert.Single(report.Requirements, row => row.Title == "Declared hardware or compatible ID").Evidence);
        Assert.Equal(CheckState.Unknown, Assert.Single(report.Requirements, row => row.Title == "Installed/offered driver versions").State);
        Assert.Equal(CheckState.Unknown, Assert.Single(report.Requirements, row => row.Title == "Exact device/OEM match").State);
        Assert.False(report.CanInstall);
        var differentRevision = offer with { IdentityMetadata = offer.IdentityMetadata with { RevisionNumber = new(CheckState.Available, 8) } };
        Assert.Empty(Review(offer, Source(offer, differentRevision), [offer, differentRevision]).Items);
    }

    [Fact]
    public void MissingOptionalMetadataStillProducesAnHonestUsableReview()
    {
        var report = Assert.Single(Review(Offer()).Items);
        Assert.All(report.Requirements.Where(row => row.Title.StartsWith("Declared", StringComparison.Ordinal)), row => Assert.Equal(CheckState.Unknown, row.State));
        Assert.False(report.CanInstall);
    }

    [Fact]
    public void OptionalUnsupportedAndFailedFieldsRetainTheirIndividualStates()
    {
        var metadata = new WindowsUpdateOfferMetadata(CheckState.Partial,
            new(CheckState.Available, "Declared manufacturer"), new(CheckState.Unsupported, null), new(CheckState.Failed, null),
            new(CheckState.Unknown, null), new(CheckState.Failed, null), new(CheckState.Unsupported, null), new(CheckState.Unknown, null));
        var report = Assert.Single(Review(Offer() with { OfferMetadata = metadata }).Items);
        Assert.Equal(CheckState.Unsupported, Assert.Single(report.Requirements, row => row.Title == "Declared provider").State);
        Assert.Equal(CheckState.Failed, Assert.Single(report.Requirements, row => row.Title == "Declared intended model").State);
        Assert.Equal(CheckState.Available, Assert.Single(report.Requirements, row => row.Title == "Declared manufacturer").State);
    }

    [Theory]
    [InlineData(CheckState.Available)]
    [InlineData(CheckState.Partial)]
    public void PositivePartialOrAvailableSourceNeverPromotesReviewReadiness(CheckState state)
    {
        var offer = Offer(); var source = Source(offer) with { State = state };
        var result = Review(offer, source); var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State); Assert.Equal(state, report.SourceState); Assert.False(report.CanInstall);
    }

    [Theory]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Cancelled)]
    [InlineData((CheckState)99)]
    public void NegativeOrUndefinedSourceCannotContributeEvenWithPlausibleRows(CheckState state)
    {
        var offer = Offer(); var result = Review(offer, Source(offer) with { State = state });
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public void EqualValuedCloneCannotSupplyCurrentSelectionOrLoadedMembership()
    {
        var offer = Offer(); var clone = offer with { };
        Assert.Empty(Review(clone, Source(offer), [offer]).Items);
        Assert.Empty(Review(offer, Source(offer), [clone]).Items);
        Assert.Empty(Review(offer, Source(clone), [offer]).Items);
    }

    [Fact]
    public void UnpublishedDriverSiblingForeignRowAndRepeatedReferencesBlockReview()
    {
        var offer = Offer(); var sibling = Offer() with { Identity = "11234567-89ab-cdef-0123-456789abcdef" };
        Assert.Empty(Review(offer, Source(offer, sibling), [offer]).Items);
        Assert.Empty(Review(offer, Source(offer), [offer, sibling]).Items);
        Assert.Empty(Review(offer, Source(offer), [offer, offer]).Items);
        Assert.Empty(Review(offer, Source(offer, offer), [offer]).Items);
        Assert.Single(Review(offer, Source(offer, sibling), [sibling, offer]).Items);
    }

    [Fact]
    public void HiddenFirmwareOrSoftwareIdentityCollisionIsNotIgnored()
    {
        var offer = Offer();
        var hiddenFirmware = offer with { Kind = "Firmware", DriverClass = "Firmware", Identity = offer.Identity.ToUpperInvariant() };
        var hiddenSoftware = hiddenFirmware with { Kind = "Software" };
        Assert.Empty(Review(offer, Source(offer, hiddenFirmware), [offer]).Items);
        Assert.Empty(Review(offer, Source(offer, hiddenSoftware), [offer]).Items);
        Assert.Single(Review(offer, Source(offer, hiddenFirmware with { Identity = "11234567-89ab-cdef-0123-456789abcdef" }), [offer]).Items);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(30.0001, false)]
    public void ConservativeAgeBoundIncludesAttemptRatherThanRefreshingAgeAtReceipt(double minutes, bool allowed)
    {
        var offer = Offer(); var result = DriverUpdateReviewPolicy.Review(Source(offer), [offer], offer,
            Attempt, Attempt.AddMinutes(29), Attempt.AddMinutes(minutes));
        Assert.Equal(allowed ? 1 : 0, result.Items.Count);
    }

    [Fact]
    public void FutureInvertedDefaultOrNonUtcTimesDoNotCreateAReview()
    {
        var offer = Offer(); var source = Source(offer);
        Assert.Empty(DriverUpdateReviewPolicy.Review(source, [offer], offer, default, Received, Now).Items);
        Assert.Empty(DriverUpdateReviewPolicy.Review(source, [offer], offer, Received, Attempt, Now).Items);
        Assert.Empty(DriverUpdateReviewPolicy.Review(source, [offer], offer, Attempt, Now.AddTicks(1), Now).Items);
        Assert.Empty(DriverUpdateReviewPolicy.Review(source, [offer], offer, Attempt.ToOffset(TimeSpan.FromHours(1)), Received, Now).Items);
        Assert.Empty(DriverUpdateReviewPolicy.Review(source, [offer], offer, Attempt, Received, Now.ToOffset(TimeSpan.FromHours(1))).Items);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData(" 01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("update-id")]
    public void NonCanonicalOrEmptyIdentityIsNotAnExactOfferSelector(string identity) =>
        Assert.Empty(Review(Offer() with { Identity = identity }).Items);

    [Fact]
    public void FirmwareCannotEnterTheDriverReviewThroughMisclassification()
    {
        Assert.Empty(Review(Offer() with { DriverClass = "fIrMwArE" }).Items);
        Assert.Empty(Review(Offer() with { DriverClass = " \tFirmware " }).Items);
        Assert.Empty(Review(Offer() with { DriverClass = " Firmware " }).Items);
        Assert.Empty(Review(Offer() with { Kind = "Firmware", DriverClass = "Firmware" }).Items);
        Assert.Empty(Review(Offer() with { Kind = "Software" }).Items);
    }

    [Fact]
    public void MalformedOrContradictoryMetadataIsNotProjectedAsVerifiedDeclarations()
    {
        var known = AllKnown();
        Assert.Empty(Review(Offer() with { OfferMetadata = known with { State = CheckState.Unknown } }).Items);
        Assert.Empty(Review(Offer() with { OfferMetadata = known with { Model = new(CheckState.Unknown, "hidden value") } }).Items);
        Assert.Empty(Review(Offer() with { OfferMetadata = known with { RebootBehavior = new(CheckState.Available, (WindowsUpdateRebootBehavior)99) } }).Items);
    }

    [Fact]
    public void UnsafeOversizedAndUnparseableDisplayMetadataIsWithheldWithoutTruncatingIntoPlausibility()
    {
        var offer = Offer();
        Assert.Empty(Review(offer with { Title = new string('a', 2049) }).Items);
        Assert.Empty(Review(offer with { Title = "Display\u202Espoof" }).Items);
        Assert.Empty(Review(offer with { Title = "Broken" + (char)0xD800 }).Items);
        Assert.Empty(Review(offer with { DriverClass = "Net\0hidden" }).Items);
        Assert.Empty(Review(offer with { PackageDate = "2026-02-30" }).Items);
        Assert.Empty(Review(offer with { PackageDate = "01/01/2026" }).Items);
        Assert.Empty(Review(offer with { Description = new string('a', 2301) }).Items);
        Assert.Empty(Review(offer with { Description = "Message\u001bcommand" }).Items);
        Assert.Single(Review(offer with { Title = "Reported 驱动程序 😀", PackageDate = "Unknown" }).Items);
    }

    [Fact]
    public void SourceBoundAndCopiedReviewCannotBeChangedByLaterCollectionMutation()
    {
        var offer = Offer(); var items = new List<SystemUpdate> { offer };
        var result = Review(offer, new(CheckState.Partial, "Partial source", items), items);
        var report = Assert.Single(result.Items);
        items.Clear();
        Assert.Same(offer, report.SelectedOffer); Assert.Equal(19, report.Requirements.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<DriverUpdateReviewRequirement>)report.Requirements).Clear());
        var tooMany = Enumerable.Range(1, 257).Select(i => offer with { Identity = i.ToString("x8") + "-89ab-cdef-0123-456789abcdef" }).ToArray();
        Assert.Empty(DriverUpdateReviewPolicy.Review(Source(tooMany), tooMany, tooMany[0], Attempt, Received, Now).Items);
        Assert.Empty(DriverUpdateReviewPolicy.Review(null, [offer], offer, Attempt, Received, Now).Items);
        Assert.Empty(DriverUpdateReviewPolicy.Review(Source(offer), null, offer, Attempt, Received, Now).Items);
    }
}
