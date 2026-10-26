using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class InstalledApplicationTests
{
    private static InstalledApplication App(string identity = "HKCU/shared/App", string? location = @"C:\Apps\Example") =>
        new(identity, "Example", "1.0", "Example publisher", "HKCU shared", location, "Unknown");

    [Fact]
    public void UnchangedRegistrationDoesNotProbeAnyPaths()
    {
        var app = App();
        var result = LeftoverReview.Compare([app], [app], _ => throw new InvalidOperationException("No probe allowed"));
        Assert.Equal(CheckState.Available, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void CaseOnlyIdentityChangesDoNotInventAnUninstall()
    {
        var result = LeftoverReview.Compare([App("HKCU/shared/Example")], [App("hkcu/SHARED/example")],
            _ => throw new InvalidOperationException("No probe allowed"));
        Assert.Empty(result.Items);
    }

    [Fact]
    public void SurvivingPriorInstallPathIsOnlyAReviewCandidate()
    {
        List<string> probes = [];
        var app = App();
        var result = LeftoverReview.Compare([app], [], path => { probes.Add(path); return true; });
        var candidate = Assert.Single(result.Items);
        Assert.Equal(app.Identity, candidate.ApplicationIdentity);
        Assert.Equal(app.InstallLocation, Assert.Single(probes));
        Assert.Equal("registration no longer present; prior install path exists", candidate.Evidence);
        Assert.Contains("not proven junk", candidate.Caution, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not prove an uninstall completed", candidate.Caution, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No deletion or cleanup is authorized", candidate.Caution);
    }

    [Fact]
    public void KnownAbsentPathDoesNotBecomeCandidateOrGlobalCleanClaim()
    {
        var result = LeftoverReview.Compare([App()], [], _ => false);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Empty(result.Items);
        Assert.Contains("not a filesystem sweep or a junk verdict", result.Message);
    }

    [Fact]
    public void UnknownExistenceIsPartialRatherThanProofOfAbsence()
    {
        var result = LeftoverReview.Compare([App()], [], _ => null);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Empty(result.Items);
        Assert.Contains("1 existence checks unknown", result.Message);
    }

    [Fact]
    public void ProbeAccessDenialIsPartialAndDoesNotSuppressOtherCandidates()
    {
        var result = LeftoverReview.Compare([App("denied", @"C:\Apps\Denied"), App("readable", @"C:\Apps\Readable")], [],
            path => path.EndsWith("Denied", StringComparison.Ordinal) ? throw new UnauthorizedAccessException("raw secret error") : true);
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal("readable", Assert.Single(result.Items).ApplicationIdentity);
        Assert.DoesNotContain("raw secret", result.Message);
    }

    [Fact]
    public void CancelledProbeKeepsTheReviewCancelled()
    {
        var result = LeftoverReview.Compare([App()], [], _ => throw new OperationCanceledException());
        Assert.Equal(CheckState.Cancelled, result.State);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\Example")]
    [InlineData("C:Example")]
    [InlineData("\\\\server\\share\\Example")]
    [InlineData("\\\\?\\C:\\Apps\\Example")]
    [InlineData("C:\\Apps\\..\\Users")]
    [InlineData("C:\\")]
    [InlineData("%ProgramFiles%\\Example")]
    [InlineData(" C:\\Apps\\Example")]
    [InlineData("C:\\Apps\\Example ")]
    public void UnsupportedPriorLocationsAreNeverProbed(string? location)
    {
        var result = LeftoverReview.Compare([App(location: location)], [], _ => throw new InvalidOperationException("No probe allowed"));
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void CurrentInstallLocationCannotReplaceTheOnlyAllowedPriorProbePath()
    {
        var result = LeftoverReview.Compare([App("removed", @"C:\Old\App")], [App("different", @"C:\New\App")],
            path => { Assert.Equal(@"C:\Old\App", path); return true; });
        Assert.Equal(@"C:\Old\App", Assert.Single(result.Items).Path);
    }

    [Fact]
    public void SharedInstallPathIsProbedOnceAndEveryCandidateIsProtected()
    {
        int probes = 0;
        var result = LeftoverReview.Compare([App("one"), App("two")], [], _ => { probes++; return true; });
        Assert.Equal(1, probes);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Contains("overlaps another application's", item.Caution));
    }

    [Fact]
    public void ParentChildInstallLocationsAreRecognizedAsOverlapping()
    {
        var result = LeftoverReview.Compare([App("removed", @"C:\Apps\Suite")],
            [App("still-present", @"C:\Apps\Suite\Component")], _ => true);
        Assert.Contains("overlaps another application's", Assert.Single(result.Items).Caution);
    }

    [Fact]
    public void SiblingNamesWithSharedPrefixAreNotMistakenForOverlappingPaths()
    {
        var result = LeftoverReview.Compare([App("removed", @"C:\Apps\Example")],
            [App("still-present", @"C:\Apps\ExampleOther")], _ => true);
        Assert.DoesNotContain("overlaps another application's", Assert.Single(result.Items).Caution);
    }

    [Theory]
    [InlineData("C:\\Users\\Person\\AppData\\Local\\Example")]
    [InlineData("C:\\Users\\Person\\Documents\\Example")]
    [InlineData("D:\\Games\\Example\\Saves")]
    [InlineData("C:\\Windows\\Example")]
    public void UserSaveSettingsAndSystemLocationsRemainProtectedReviewOnly(string path)
    {
        var result = LeftoverReview.Compare([App(location: path)], [], _ => true);
        Assert.Contains("must not be treated as a cleaning target", Assert.Single(result.Items).Caution);
    }

    [Fact]
    public void BroadSharedRootCannotLookLikeAnIsolatedApplicationDirectory()
    {
        var result = LeftoverReview.Compare([App(location: @"C:\Program Files")], [], _ => true);
        Assert.Contains("broad shared installation root", Assert.Single(result.Items).Caution);
    }

    [Fact]
    public void ConflictingDuplicatePriorRegistrationDoesNotPickAnArbitraryDirectory()
    {
        var result = LeftoverReview.Compare([App(location: @"C:\Apps\One"), App(location: @"C:\Apps\Two")], [],
            _ => throw new InvalidOperationException("No probe allowed"));
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void ProbeAndCandidateLimitsAreReportedAsPartial()
    {
        var before = Enumerable.Range(0, 1_010).Select(i => App("app" + i, @"C:\Apps\Example" + i)).ToArray();
        int probes = 0;
        var result = LeftoverReview.Compare(before, [], _ => { probes++; return true; });
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(1_000, probes);
        Assert.Equal(1_000, result.Items.Count);
        Assert.Contains("Safety limit reached", result.Message);
    }

    [Theory]
    [InlineData("c:/Program Files/Example/", "C:\\Program Files\\Example")]
    [InlineData("D:\\Apps\\Example", "D:\\Apps\\Example")]
    public void LocalDirectoryNormalizationIsPortableAndDoesNotNeedARealFilesystem(string input, string expected) =>
        Assert.Equal(expected, InstalledApplicationPaths.NormalizeLocalDirectory(input));

    [Theory]
    [InlineData("C:\\Apps\\Example:stream")]
    [InlineData("C:\\Apps\\Example.")]
    [InlineData("C:\\Apps\\Example ")]
    [InlineData(" C:\\Apps\\Example")]
    [InlineData("C:\\Apps\\..\\Users")]
    [InlineData("C:\\Apps\\CON")]
    [InlineData("C:\\Apps\\LPT1.log")]
    [InlineData("C:\\Apps\\COM¹")]
    [InlineData("\"C:\\Apps\\Example\"")]
    [InlineData("C:\\Apps\\Example --silent\" /delete")]
    [InlineData("https://example.com/App")]
    public void DeviceStreamCommandOrAmbiguousPathsAreRejected(string input) =>
        Assert.Null(InstalledApplicationPaths.NormalizeLocalDirectory(input));

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void NoRemoveRestrictionOverridesWindowsInstallerAndRegisteredUninstallerHints(bool installer, bool registered)
    {
        string hint = InstalledApplicationSupport.Describe(installer, registered, removalRestricted: true);
        Assert.Equal("Removal restricted by registration; action unavailable", hint);
        Assert.DoesNotContain("action queued", hint);
    }

    [Theory]
    [InlineData(true, false, "Windows Installer; action queued")]
    [InlineData(false, true, "Registered uninstaller; action queued")]
    [InlineData(false, false, "Unknown")]
    public void UnrestrictedRegistrationHintsDoNotClaimAnExecutedUninstall(bool installer, bool registered, string expected)
    {
        Assert.Equal(expected, InstalledApplicationSupport.Describe(installer, registered, removalRestricted: false));
    }
}
