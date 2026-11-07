using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class AppPackageMetadataTests
{
    private static readonly DateTimeOffset Started = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static AppUpdate Offer() => new("Catalog package", "Vendor.Package", "1.2.3", "2.0-beta.7", "winget", CheckState.Available);
    private static CheckResult<AppUpdate> Snapshot(AppUpdate selected, CheckState state = CheckState.Partial) =>
        new(state, "Source coverage is independent.", [selected, new("Other", "Other.Package", "Unknown", "Unknown", "winget", CheckState.Unknown)]);
    private static AppPackageMetadataRequest Request()
    {
        var selected = Offer();
        return Assert.Single(AppPackageMetadataPolicy.Prepare(Snapshot(selected), selected, Started).Items);
    }
    private static CheckResult<AppPackageMetadataReport> Report(AppPackageMetadataRequest request) =>
        AppPackageMetadataPolicy.Project(request, "Reported publisher", "Catalog description", "MIT", "Release notes", Started.AddSeconds(1));
    private static bool Accept(AppPackageMetadataRequest request, CheckResult<AppPackageMetadataReport> result) =>
        AppPackageMetadataPolicy.IsResultConsistent(request, result, Started.AddSeconds(2));

    [Fact]
    public void ExactCurrentAvailableSelectionCanBeReviewedWithinPartialSourceCoverage()
    {
        var selected = Offer(); var snapshot = Snapshot(selected);
        var result = AppPackageMetadataPolicy.Prepare(snapshot, selected, Started);
        var request = Assert.Single(result.Items);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(selected.Id, request.PackageId);
        Assert.Equal(selected.InstalledVersion, request.InstalledVersion);
        Assert.Equal(selected.AvailableVersion, request.OfferedVersion); // No generic vendor version ordering.
        Assert.NotEqual(Guid.Empty, request.RequestId);
        Assert.Equal(Started.AddMinutes(2), request.ExpiresUtc);
        Assert.Equal(CheckState.Partial, snapshot.State);
        Assert.Same(selected, snapshot.Items[0]);
        Assert.Contains("metadata read only", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualCloneRepeatedReferenceAndAmbiguousIdsCannotSelectAnotherObservation()
    {
        var selected = Offer();
        Assert.Empty(AppPackageMetadataPolicy.Prepare(Snapshot(selected), selected with { }, Started).Items);
        foreach (var rows in new AppUpdate[][]
        {
            [selected, selected],
            [selected, selected with { Name = "Different", Id = "vendor.package" }],
            [selected, null!]
        })
            Assert.Empty(AppPackageMetadataPolicy.Prepare(new(CheckState.Available, "", rows), selected, Started).Items);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Unavailable)]
    [InlineData((CheckState)100)]
    public void NonpositiveSnapshotCannotPromoteAPlausibleRetainedOffer(CheckState state)
    {
        var selected = Offer();
        Assert.Empty(AppPackageMetadataPolicy.Prepare(Snapshot(selected, state), selected, Started).Items);
    }

    [Fact]
    public void UnsupportedSourceOrUnknownVersionCannotPrepareAQuery()
    {
        var original = Offer();
        foreach (var selected in new[]
        {
            original with { Source = "other" }, original with { Source = "Winget" },
            original with { Status = CheckState.Unknown }, original with { InstalledVersion = "Unknown" },
            original with { AvailableVersion = "-" }, original with { Id = "Vendor.Package\u200b" },
            original with { AvailableVersion = new string('x', 513) }
        })
            Assert.Empty(AppPackageMetadataPolicy.Prepare(Snapshot(selected), selected, Started).Items);
    }

    [Fact]
    public void ScopeAndRequestWorkIsBoundedAndCancellationOccursBeforeProjection()
    {
        var selected = Offer();
        Assert.Empty(AppPackageMetadataPolicy.Prepare(new(CheckState.Available, "",
            Enumerable.Repeat(selected, StructuredWingetPolicy.ResultLimit + 1).ToArray()), selected, Started).Items);
        Assert.Empty(AppPackageMetadataPolicy.Prepare(Snapshot(selected), selected, default).Items);
        Assert.Empty(AppPackageMetadataPolicy.Prepare(Snapshot(selected), selected, Started.ToOffset(TimeSpan.FromHours(1))).Items);
        Assert.Empty(AppPackageMetadataPolicy.Prepare(Snapshot(selected), selected, DateTimeOffset.UtcNow.AddDays(1)).Items);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AppPackageMetadataPolicy.Prepare(Snapshot(selected), selected, Started, cancellation.Token));
    }

    [Fact]
    public void RequestExpiresForAdmissionButNotForAnAlreadyOwnedGetterReturn()
    {
        var request = Request();
        Assert.True(AppPackageMetadataPolicy.IsRequestConsistent(request, Started));
        Assert.False(AppPackageMetadataPolicy.IsRequestConsistent(request, request.ExpiresUtc));
        Assert.False(AppPackageMetadataPolicy.IsRequestConsistent(request, Started.AddTicks(-1)));
        var report = AppPackageMetadataPolicy.Project(request, "Publisher", "Description", "License", "Notes", Started.AddMinutes(5));
        Assert.True(AppPackageMetadataPolicy.IsResultConsistent(request, report, Started.AddMinutes(6)));
        Assert.False(Assert.Single(report.Items).CanInstall);
    }

    [Fact]
    public void RequestFieldsCannotBeWidenedIntoAnUnboundedOrMalformedSelector()
    {
        var original = Request();
        foreach (var request in new[]
        {
            original with { RequestId = Guid.Empty }, original with { PackageId = "Unknown" },
            original with { PackageId = "private\ncommand" }, original with { InstalledVersion = "1\u200b.0" },
            original with { PreparedUtc = default }, original with { ExpiresUtc = original.ExpiresUtc.AddDays(1) }
        })
            Assert.False(AppPackageMetadataPolicy.IsRequestConsistent(request, Started));
    }

    [Fact]
    public void MultilineUnicodeClaimsStayPlainTextWithoutBecomingPublisherTrustOrAnAction()
    {
        var request = Request();
        const string description = "説明 😀\r\n\tSecond paragraph <script>display only</script>";
        var result = AppPackageMetadataPolicy.Project(request, " Publisher ", description, "MIT", "**Notes**\nNo installer requested.", Started.AddSeconds(1));
        var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal("Publisher", report.Publisher.Value);
        Assert.Equal(description, report.Description.Value);
        Assert.True(Accept(request, result));
        Assert.False(report.CanInstall);
        Assert.Contains("not verified publisher", result.Message, StringComparison.Ordinal);
        Assert.Contains("No URL, installer, package download", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultilinePublisherAndLicenseLabelsAreWithheldWhileParagraphFieldsRemainUsable()
    {
        var request = Request();
        var result = AppPackageMetadataPolicy.Project(request, "Publisher\nAnother line", "Description\r\nParagraph",
            "MIT\tLicense", "Notes\nParagraph", Started.AddSeconds(1));
        var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(CheckState.Unknown, report.Publisher.State);
        Assert.Null(report.Publisher.Value);
        Assert.Equal(CheckState.Unknown, report.License.State);
        Assert.Null(report.License.Value);
        Assert.Equal("Description\r\nParagraph", report.Description.Value);
        Assert.Equal("Notes\nParagraph", report.ReleaseNotes.Value);
        Assert.True(Accept(request, result));
        Assert.False(Accept(request, result with { Items = [report with { Publisher = new(CheckState.Available, "Publisher\nAnother line") }] }));
        Assert.False(Accept(request, result with { Items = [report with { License = new(CheckState.Available, "MIT\tLicense") }] }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("hidden\u200btext")]
    [InlineData("direction\u202etext")]
    [InlineData("control\0text")]
    public void MissingOrUnsafeFieldsRemainUnknownWithoutErasingQualifiedFields(string? value)
    {
        var request = Request();
        var result = AppPackageMetadataPolicy.Project(request, "Publisher", value, "MIT", null, Started.AddSeconds(1));
        var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(CheckState.Unknown, report.Description.State);
        Assert.Null(report.Description.Value);
        Assert.Equal("Publisher", report.Publisher.Value);
        Assert.True(Accept(request, result));
    }

    [Fact]
    public void InvalidSurrogatesAndOversizedValuesAreWithheldWithoutTruncatingIntoPlausibleClaims()
    {
        var request = Request();
        string invalidUtf16 = "prefix" + (char)0xD800;
        var result = AppPackageMetadataPolicy.Project(request, new string('p', 513), invalidUtf16,
            new string('l', 1025), new string('n', 8193), Started.AddSeconds(1));
        Assert.Equal(CheckState.Unknown, result.State);
        Assert.Empty(result.Items);
        Assert.DoesNotContain("prefix", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var boundary = AppPackageMetadataPolicy.Project(request, new string('p', 512), new string('d', 8192),
            new string('l', 1024), new string('n', 8192), Started.AddSeconds(1));
        Assert.True(Accept(request, boundary));
    }

    [Fact]
    public void AnotherRequestPackageVersionOrTimeCannotSupplyCurrentMetadata()
    {
        var request = Request(); var result = Report(request); var report = Assert.Single(result.Items);
        foreach (var foreign in new[]
        {
            report with { RequestId = Guid.NewGuid() }, report with { PackageId = "Vendor.Other" },
            report with { InstalledVersion = "1.2.4" }, report with { OfferedVersion = "2.0" },
            report with { CheckedUtc = Started.AddTicks(-1) }, report with { CheckedUtc = Started.AddSeconds(3) },
            report with { CheckedUtc = Started.ToOffset(TimeSpan.FromHours(1)) }
        })
            Assert.False(Accept(request, result with { Items = [foreign] }));
        Assert.False(AppPackageMetadataPolicy.IsResultConsistent(request, result, default));
        Assert.False(AppPackageMetadataPolicy.IsResultConsistent(request, result, DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Empty(AppPackageMetadataPolicy.Project(request, "Publisher", null, null, null, Started.AddSeconds(-1)).Items);
    }

    [Fact]
    public void StateOrFieldPayloadForgeryCannotPromotePartialOrFailedEvidence()
    {
        var request = Request(); var result = Report(request); var report = Assert.Single(result.Items);
        foreach (var state in new[] { CheckState.Unknown, CheckState.Cancelled, CheckState.Failed, CheckState.Partial, (CheckState)100 })
            Assert.False(Accept(request, result with { State = state }));
        Assert.False(Accept(request, result with { Items = [report, report] }));
        Assert.False(Accept(request, result with { Items = null! }));
        Assert.False(Accept(request, result with { Items = [report with { Publisher = new(CheckState.Unknown, "private-hidden") }] }));
        Assert.False(Accept(request, result with { Items = [report with { Publisher = new(CheckState.Available, "bad\u200bpublisher") }] }));
        Assert.False(Accept(request, result with { Items = [report with { Publisher = null! }] }));
        var partial = AppPackageMetadataPolicy.Project(request, "Publisher", null, null, null, Started.AddSeconds(1));
        Assert.True(Accept(request, partial));
        Assert.False(Accept(request, partial with { State = CheckState.Available }));
    }
}
