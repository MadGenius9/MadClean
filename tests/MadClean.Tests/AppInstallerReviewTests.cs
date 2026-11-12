using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class AppInstallerReviewTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Checked = Start.AddMinutes(5);
    private static AppPackageMetadataRequest Request() => new(Guid.NewGuid(), "Example.App", "1", "2", Start, Start.AddMinutes(2));
    private static CheckResult<AppInstallerReviewReport> Project(AppPackageMetadataRequest request) =>
        AppInstallerReviewPolicy.Project(request, true, "Msi", "Unknown", "X64", "User", "en-US", "ElevationProhibited", "None", Checked);

    [Fact]
    public void PreparationRetainsExactCurrentSelectionWithoutCreatingAnInstallCapability()
    {
        var selected = new AppUpdate("Example", "Example.App", "1", "2", "winget", CheckState.Available);
        var snapshot = new CheckResult<AppUpdate>(CheckState.Partial, "Incomplete coverage", [selected]);
        var result = AppInstallerReviewPolicy.Prepare(snapshot, selected, Start);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal("Example.App", Assert.Single(result.Items).PackageId);
        Assert.Empty(AppInstallerReviewPolicy.Prepare(snapshot, selected with { }, Start).Items);
        Assert.Empty(AppInstallerReviewPolicy.Prepare(snapshot with { Items = [selected, selected] }, selected, Start).Items);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AppInstallerReviewPolicy.Prepare(snapshot, selected, Start, cancellation.Token));
    }

    [Fact]
    public void ApplicableInstallerStaysPartialEvenWithKnownDeclarationsAndCannotGrantInstallation()
    {
        var request = Request(); var result = Project(request); var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.True(report.ApplicableInstaller);
        Assert.False(report.CanInstall);
        Assert.Equal("Msi", report.InstallerType.Value);
        Assert.Equal(CheckState.Unknown, report.NestedInstallerType.State);
        Assert.Equal("None", report.Authentication.Value);
        Assert.True(AppInstallerReviewPolicy.IsResultConsistent(request, result, Checked));
        Assert.Contains("restart behavior remain unknown", result.Message);
        Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result with { State = CheckState.Available }, Checked));
        var allUnknown = AppInstallerReviewPolicy.Project(request, true, null, null, null, null, null, null, null, Checked);
        Assert.Equal(CheckState.Partial, allUnknown.State);
        Assert.True(AppInstallerReviewPolicy.IsResultConsistent(request, allUnknown, Checked));
    }

    [Fact]
    public void NoApplicableInstallerIsAGenuineProfileFindingWithoutClaimingNoUpdates()
    {
        var request = Request();
        var result = AppInstallerReviewPolicy.Project(request, false, null, " ", null, null, null, null, null, Checked);
        var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Unavailable, result.State);
        Assert.False(report.ApplicableInstaller); Assert.False(report.CanInstall);
        Assert.All(new[] { report.InstallerType, report.NestedInstallerType, report.Architecture, report.Scope,
            report.Locale, report.Elevation, report.Authentication }, field => { Assert.Equal(CheckState.Unknown, field.State); Assert.Null(field.Value); });
        Assert.Contains("does not mean no update exists", result.Message);
        Assert.True(AppInstallerReviewPolicy.IsResultConsistent(request, result, Checked));
        Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result with { State = CheckState.Partial }, Checked));
        Assert.Empty(AppInstallerReviewPolicy.Project(request, false, "Msi", null, null, null, null, null, null, Checked).Items);
        Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result with { Items = [report with { InstallerType = new(CheckState.Available, "Msi") }] }, Checked));
    }

    [Theory]
    [InlineData("future-value")] [InlineData("msi")] [InlineData("Msi\n")] [InlineData("\u202eMsi")]
    public void UnrecognizedEnumOrUnsafeTextCannotBecomeAKnownDeclaration(string value)
    {
        var request = Request();
        var result = AppInstallerReviewPolicy.Project(request, true, value, value, value, value, value, value, value, Checked);
        var report = Assert.Single(result.Items);
        Assert.All(new[] { report.InstallerType, report.NestedInstallerType, report.Architecture, report.Scope,
            report.Elevation, report.Authentication }, field => Assert.Equal(new(CheckState.Unknown, null), field));
        Assert.True(AppInstallerReviewPolicy.IsResultConsistent(request, result, Checked));
        Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result with { Items = [report with { InstallerType = new(CheckState.Available, value) }] }, Checked));
    }

    [Fact]
    public void LocaleHasABoundedLiteralAsciiShapeAndAuthenticationUsesExactPinnedNames()
    {
        var request = Request();
        foreach (var locale in new[] { "en-US", "zh-Hant-TW", new string('a', 85) })
            Assert.Equal(locale, Assert.Single(AppInstallerReviewPolicy.Project(request, true, null, null, null, null, locale,
                null, "MicrosoftEntraIdForAzureBlobStorage", Checked).Items).Locale.Value);
        foreach (var locale in new[] { "Unknown", "en_US", "en.US", "-en", "en-", "en--US", "en US", new string('a', 86), new string('\ud800', 1) })
            Assert.Equal(CheckState.Unknown, Assert.Single(AppInstallerReviewPolicy.Project(request, true, null, null, null, null,
                locale, null, "AzureBlobStorage", Checked).Items).Locale.State);
        var report = Assert.Single(AppInstallerReviewPolicy.Project(request, true, "Portable", "Zip", "X86OnArm64", "System", "en",
            "ElevatesSelf", "MicrosoftEntraIdForAzureBlobStorage", Checked).Items);
        Assert.Equal("MicrosoftEntraIdForAzureBlobStorage", report.Authentication.Value);
        Assert.False(report.CanInstall);
    }

    [Fact]
    public void AdmissionExpiryDoesNotEraseAnAlreadyOwnedGetterReturningLater()
    {
        var request = Request();
        Assert.False(AppPackageMetadataPolicy.IsRequestConsistent(request, Checked));
        Assert.True(AppInstallerReviewPolicy.IsResultConsistent(request, Project(request), Checked));
        foreach (var invalid in new[] { request with { RequestId = Guid.Empty }, request with { ExpiresUtc = request.ExpiresUtc.AddTicks(1) },
                     request with { PackageId = "Unknown" }, request with { InstalledVersion = "hidden\u202e" } })
            Assert.Empty(Project(invalid).Items);
        Assert.Empty(AppInstallerReviewPolicy.Project(request, true, null, null, null, null, null, null, null, Start.AddTicks(-1)).Items);
        Assert.Empty(AppInstallerReviewPolicy.Project(request, true, null, null, null, null, null, null, null, DateTimeOffset.UtcNow.AddDays(1)).Items);
    }

    [Fact]
    public void ResultIdentityTimeAndFieldStateMustRemainBoundToTheExactRequest()
    {
        var request = Request(); var result = Project(request); var report = Assert.Single(result.Items);
        foreach (var invalid in new[] { report with { RequestId = Guid.NewGuid() }, report with { PackageId = "EXAMPLE.APP" },
                     report with { InstalledVersion = "other" }, report with { OfferedVersion = "other" }, report with { CheckedUtc = Start.AddTicks(-1) },
                     report with { CheckedUtc = Checked.AddTicks(1) }, report with { CheckedUtc = Checked.ToOffset(TimeSpan.FromHours(1)) },
                     report with { InstallerType = null! }, report with { Locale = new(CheckState.Unknown, "en") },
                     report with { Scope = new(CheckState.Failed, null) }, report with { Authentication = new(CheckState.Available, "Unknown") } })
            Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result with { Items = [invalid] }, Checked));
        Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result with { Items = [report, report] }, Checked));
        Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result with { Items = [null!] }, Checked));
        Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result, Checked.AddTicks(-1)));
        Assert.False(AppInstallerReviewPolicy.IsResultConsistent(request, result, DateTimeOffset.UtcNow.AddDays(1)));
    }
}
