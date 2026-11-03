using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class PackagedAppSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static PackagedApplication Package(string version = "1.0.0.0", string type = "Main") =>
        new("Example.App", $"Example.App_{version}_x64__abcdefghijklm", "Example.App_abcdefghijklm", version,
            "CN=Example", "X64", type, false, false, false, "Store");
    private static CheckResult<PackagedApplication> Source(params PackagedApplication[] rows) => new(CheckState.Available, "Fixture metadata", rows);

    [Fact]
    public void PreviewRequiresActualSourceReferenceAndCanonicalBareFamilyDestination()
    {
        var selected = Package(); var source = Source(selected);
        Assert.Null(PackagedAppSettingsPolicy.Create(source, selected with { }, Now, Now));
        var preview = Assert.IsType<PackagedAppSettingsPreview>(PackagedAppSettingsPolicy.Create(source, selected, Now, Now));
        Assert.Equal("ms-settings:appsfeatures-app?Example.App_abcdefghijklm", preview.TargetUri.OriginalString);
        Assert.True(PackagedAppSettingsPolicy.IsAllowed(preview, Now));
        Assert.Contains("package family", PackagedAppSettingsPreview.Effect);
        Assert.Contains("not this exact version", PackagedAppSettingsPreview.Limits);
    }

    [Theory]
    [InlineData("Example.App_abcdefghijklm&other=target")]
    [InlineData("Example.App_abcdefghijklm#fragment")]
    [InlineData("Example.App_abcdefghijklm%26target")]
    [InlineData("Example.App_abcdefghijklm?target")]
    [InlineData("Example.App_abcdefghijklm\n")]
    [InlineData("Other.App_abcdefghijklm")]
    public void HostileOrMismatchedFamiliesNeverBecomeSettingsUris(string family)
    {
        var row = Package() with { FamilyName = family };
        Assert.Null(PackagedAppSettingsPolicy.Create(Source(row), row, Now, Now));
    }

    [Fact]
    public void StaleFutureOrUnusableInventoryDoesNotProduceAHandoff()
    {
        var row = Package(); var source = Source(row);
        Assert.Null(PackagedAppSettingsPolicy.Create(source, row, Now.AddSeconds(1), Now));
        Assert.Null(PackagedAppSettingsPolicy.Create(source, row, Now.AddMinutes(-6), Now));
        Assert.Null(PackagedAppSettingsPolicy.Create(source with { State = CheckState.Unknown }, row, Now, Now));
        Assert.Null(PackagedAppSettingsPolicy.Create(Source(row, row), row, Now, Now));
        var preview = PackagedAppSettingsPolicy.Create(source, row, Now, Now)!;
        Assert.False(PackagedAppSettingsPolicy.IsAllowed(preview, Now.AddMinutes(6)));
        Assert.Null(PackagedAppSettingsPolicy.PrepareHandoff(preview, source, Now.AddSeconds(1), Now));
    }

    [Fact]
    public void FreshMetadataChangesOrMissingAmbiguousRegistrationsRequireNewReview()
    {
        var row = Package(); var source = Source(row);
        var preview = PackagedAppSettingsPolicy.Create(source, row, Now, Now)!;
        Assert.Null(PackagedAppSettingsPolicy.PrepareHandoff(preview, Source(), Now, Now));
        Assert.Null(PackagedAppSettingsPolicy.PrepareHandoff(preview, Source(row with { NonRemovable = true }), Now, Now));
        Assert.Null(PackagedAppSettingsPolicy.PrepareHandoff(preview, Source(Package("2.0.0.0")), Now, Now));
        Assert.Null(PackagedAppSettingsPolicy.PrepareHandoff(preview, Source(row, row with { }), Now, Now));
        var conflict = Package("2.0.0.0") with { Name = "Different.App" };
        Assert.Null(PackagedAppSettingsPolicy.PrepareHandoff(preview, Source(row, conflict), Now, Now));
    }

    [Fact]
    public void MatchingFreshPartialMainBundleFamilyIsNavigationOnlyAndExpiresAtDispatch()
    {
        var row = Package(); var bundle = Package("1.0.0.0", "Bundle") with
            { FullName = "Example.App_1.0.0.0_neutral_~_abcdefghijklm", Architecture = "Neutral" };
        var source = Source(row, bundle) with { State = CheckState.Partial };
        var preview = PackagedAppSettingsPolicy.Create(source, row, Now, Now)!;
        Assert.Equal(CheckState.Partial, preview.SourceCoverage);
        var handoff = Assert.IsType<PackagedAppSettingsHandoff>(PackagedAppSettingsPolicy.PrepareHandoff(preview,
            Source(row with { }, bundle with { }) with { State = CheckState.Partial }, Now.AddSeconds(1), Now.AddSeconds(1)));
        Assert.True(PackagedAppSettingsPolicy.IsAllowed(handoff, Now.AddSeconds(30)));
        Assert.False(PackagedAppSettingsPolicy.IsAllowed(handoff, Now.AddSeconds(32)));
        Assert.Contains("No uninstall", PackagedAppSettingsPreview.Effect);
    }

    [Fact]
    public void UnknownTypeOrDependencyFlagsAndInconsistentFullIdentityRemainUnsupported()
    {
        var row = Package();
        foreach (var invalid in new[] { row with { IsFramework = null }, row with { IsFramework = true },
            row with { IsResourcePackage = true }, row with { PackageType = "Unknown" },
            row with { Version = "2.0.0.0" }, row with { Architecture = "Arm64" },
            row with { FullName = new string('x', 10000) } })
            Assert.Null(PackagedAppSettingsPolicy.Create(Source(invalid), invalid, Now, Now));
    }
}
