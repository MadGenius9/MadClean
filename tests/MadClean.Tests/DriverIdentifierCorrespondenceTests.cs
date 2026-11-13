using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DriverIdentifierCorrespondenceTests
{
    private const string Id = @"PCI\VEN_1234&DEV_ABCD";
    private static readonly DateTimeOffset Attempt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Received = Attempt.AddMinutes(1), Now = Attempt.AddMinutes(2);
    private static DeviceDriverIdentifiers Ids(params string[] values) => new(DeviceDriverFieldState.Available, values, null);
    private static DeviceDriverObservation Row(int number = 1) => new(number,
        new(DeviceDriverFieldState.Unknown, null, null), new(DeviceDriverFieldState.Unknown, null, null),
        new(DeviceDriverFieldState.Unknown, null, null), new(DeviceDriverFieldState.Unknown, null, null),
        new(DeviceDriverFieldState.Unknown, null, null), Received) { HardwareIds = Ids(Id) };
    private static SystemUpdate Offer(string id = Id) => new("Reported driver", "Driver", "01234567-89ab-cdef-0123-456789abcdef", false,
        "Net", "Unknown", "Reported description")
        { IdentityMetadata = new(CheckState.Partial, new(CheckState.Unknown, null), new(CheckState.Available, id)) };
    private static DeviceDriverReviewSource Context(params DeviceDriverObservation[] rows) =>
        new(new(CheckState.Partial, "Incomplete present-device coverage", rows), rows, Attempt, Received);
    private static DriverUpdateReviewReport Report(DeviceDriverReviewSource? context, SystemUpdate? offer = null)
    {
        offer ??= Offer();
        var result = DriverUpdateReviewPolicy.Review(new(CheckState.Partial, "Configured service", [offer]), [offer], offer,
            Attempt, Received, Now, context);
        Assert.Equal(CheckState.Partial, result.State);
        var report = Assert.Single(result.Items);
        Assert.False(report.CanInstall); Assert.Equal(CheckState.Unknown, report.State);
        Assert.Equal(CheckState.Unknown, Assert.Single(report.Requirements, row => row.Title == "Exact device/OEM match").State);
        Assert.Equal(CheckState.Unknown, Assert.Single(report.Requirements, row => row.Title == "Installed/offered driver versions").State);
        return report;
    }
    private static DriverUpdateReviewRequirement Correspondence(DeviceDriverReviewSource? context, SystemUpdate? offer = null) =>
        Assert.Single(Report(context, offer).Requirements, row => row.Title == "Observed identifier correspondence");

    [Fact]
    public void BothIdentifierCategoriesContributeButOneObservationIsCountedOnlyOnce()
    {
        var row = Row() with { HardwareIds = Ids(Id, Id, "Other private identifier"), CompatibleIds = Ids(Id) };
        var result = Correspondence(Context(row));
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Contains("in 1 of 1 metadata observations", result.Evidence);
        Assert.Contains("hardware-list observations: 1", result.Evidence);
        Assert.Contains("compatible-list observations: 1", result.Evidence);
        Assert.Contains("may overlap", result.Evidence);
        Assert.DoesNotContain(Id, result.Evidence); Assert.DoesNotContain("Other private identifier", result.Evidence);
        Assert.Contains("not unique physical devices", result.Limit);
    }

    [Fact]
    public void CompatibleOnlyCorrespondenceSurvivesIndependentDeniedOrMalformedHardwareProperty()
    {
        foreach (var hardware in new[]
        {
            new DeviceDriverIdentifiers(DeviceDriverFieldState.AccessDenied, [], 5),
            new DeviceDriverIdentifiers(DeviceDriverFieldState.Invalid, [], null),
            new DeviceDriverIdentifiers(DeviceDriverFieldState.InsufficientBuffer, [], 122)
        })
        {
            var result = Correspondence(Context(Row() with { HardwareIds = hardware, CompatibleIds = Ids(Id) }));
            Assert.Equal(CheckState.Partial, result.State);
            Assert.Contains("Available hardware lists: 0; available compatible lists: 1", result.Evidence);
        }
    }

    [Fact]
    public void MultipleLiteralCorrespondencesRemainAmbiguousObservationsAndNeverAnExactPhysicalMatch()
    {
        var result = Correspondence(Context(Row(), Row(2) with { HardwareIds = Ids(), CompatibleIds = Ids(Id) }));
        Assert.Equal(CheckState.Partial, result.State); Assert.Contains("in 2 of 2 metadata observations", result.Evidence);
        Assert.Contains("not Windows device matching/ranking", result.Limit);
    }

    [Theory]
    [InlineData(@"pci\ven_1234&dev_abcd")] [InlineData(@"PCI\VEN_1234&DEV_ABCD-extra")]
    [InlineData(@" PCI\VEN_1234&DEV_ABCD")] [InlineData(@"PCI\VEN_1234&DEV_ABCD ")]
    public void CaseVariantsSubstringsAndWhitespaceAreNotNormalizedIntoAClaim(string reported)
    {
        var result = Correspondence(Context(Row()), Offer(reported));
        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Contains("in 0 of 1 metadata observations", result.Evidence);
    }

    [Fact]
    public void NamesProviderVersionsAndMissingOrEmptyListsCannotSupplyCorrespondence()
    {
        var row = Row() with
        {
            Name = new(DeviceDriverFieldState.Available, Id, null),
            Provider = new(DeviceDriverFieldState.Available, Id, null),
            InstalledVersion = new(DeviceDriverFieldState.Available, Id, null),
            HardwareIds = Ids(), CompatibleIds = new(DeviceDriverFieldState.Missing, [], 1168)
        };
        Assert.Equal(CheckState.Unknown, Correspondence(Context(row)).State);
        Assert.Equal(CheckState.Unknown, Correspondence(Context()).State);
        Assert.Equal(CheckState.Unknown, Correspondence(null).State);
        Assert.Equal(CheckState.Unknown, Correspondence(Context(row), Offer() with { IdentityMetadata = null }).State);
    }

    [Theory]
    [InlineData(CheckState.Unknown)] [InlineData(CheckState.Failed)] [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Unavailable)] [InlineData(CheckState.Unsupported)] [InlineData((CheckState)99)]
    public void ACompletedNegativeSourceCannotSupplyPlausibleMatchingRows(CheckState state)
    {
        var context = Context(Row());
        Assert.Equal(CheckState.Unknown, Correspondence(context with { Source = context.Source with { State = state } }).State);
    }

    [Fact]
    public void LoadedRowsRequireAllExactReferencesNotClonesForeignSiblingsOrDuplicates()
    {
        var row = Row(); var sibling = Row(2); var source = Context(row, sibling);
        foreach (var context in new[]
        {
            source with { Loaded = [row] },
            source with { Loaded = [row, sibling with { }] },
            source with { Loaded = [row, Row(3)] },
            source with { Loaded = [row, row] },
            Context(row, row),
            Context(row, Row())
        }) Assert.Equal(CheckState.Unknown, Correspondence(context).State);
        Assert.Equal(CheckState.Partial, Correspondence(source with { Loaded = [sibling, row] }).State);
    }

    [Fact]
    public void MalformedContradictoryOrUnboundedObservationsCannotContribute()
    {
        foreach (var row in new[]
        {
            Row() with { HardwareIds = new(DeviceDriverFieldState.Unknown, [Id], null) },
            Row() with { CompatibleIds = new(DeviceDriverFieldState.Available, ["hidden\0value"], null) },
            Row() with { ObservationNumber = 0 },
            Row() with { HardwareIds = null! },
            Row() with { CheckedUtc = Now.AddTicks(1) }
        }) Assert.Equal(CheckState.Unknown, Correspondence(Context(row)).State);
        var rows = Enumerable.Range(1, 513).Select(number => Row(number)).ToArray();
        Assert.Equal(CheckState.Unknown, Correspondence(Context(rows)).State);
        var valid = Context(Row());
        Assert.Equal(CheckState.Unknown, Correspondence(valid with { Loaded = null! }).State);
        Assert.Equal(CheckState.Unknown, Correspondence(valid with { Source = null! }).State);
    }

    [Fact]
    public void QueryTimesMustBoundEachObservationAndRemainUtcCurrentAndOrdered()
    {
        var original = Context(Row());
        foreach (var context in new[]
        {
            original with { AttemptedUtc = default },
            original with { ReceivedUtc = default },
            original with { AttemptedUtc = Received.AddTicks(1) },
            original with { ReceivedUtc = Now.AddTicks(1) },
            original with { AttemptedUtc = Attempt.AddHours(-1) },
            original with { AttemptedUtc = Attempt.ToOffset(TimeSpan.FromHours(1)) },
            original with { ReceivedUtc = Received.ToOffset(TimeSpan.FromHours(1)) },
            Context(Row() with { CheckedUtc = Attempt.AddTicks(-1) }),
            Context(Row() with { CheckedUtc = Received.AddTicks(1) })
        }) Assert.Equal(CheckState.Unknown, Correspondence(context).State);
    }

    [Theory]
    [InlineData(30, true)] [InlineData(30.0001, false)]
    public void AgeIncludesTheLocalAttemptRatherThanRefreshingItAtReceipt(double minutes, bool allowed)
    {
        var offer = Offer(); var now = Attempt.AddMinutes(minutes);
        var row = Row() with { CheckedUtc = Attempt.AddMinutes(29) };
        var context = Context(row) with { ReceivedUtc = row.CheckedUtc };
        var report = Assert.Single(DriverUpdateReviewPolicy.Review(new(CheckState.Partial, "source", [offer]), [offer], offer,
            now.AddMinutes(-1), now, now, context).Items);
        var result = Assert.Single(report.Requirements, item => item.Title == "Observed identifier correspondence");
        Assert.Equal(allowed ? CheckState.Partial : CheckState.Unknown, result.State);
    }

    [Fact]
    public void ReviewCopiesOnlyCountEvidenceSoLaterSourceMutationCannotRewriteHistory()
    {
        var row = Row(); var source = new List<DeviceDriverObservation> { row };
        var context = new DeviceDriverReviewSource(new(CheckState.Partial, "source", source), source, Attempt, Received);
        var result = Correspondence(context); source.Clear();
        Assert.Equal(CheckState.Partial, result.State); Assert.Contains("in 1 of 1 metadata observations", result.Evidence);
        Assert.DoesNotContain(Id, result.Evidence);
        Assert.Equal(CheckState.Unknown, Correspondence(context).State);
    }
}
