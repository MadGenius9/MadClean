using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ShortcutTests
{
    private const string Link = @"C:\Selected\Example.lnk";
    private const string Target = @"C:\Apps\Example\App.exe";
    private static readonly DateTimeOffset CheckedAt = new(2026, 10, 27, 1, 2, 3, TimeSpan.Zero);

    private static ShortcutObservation Inspect(ShortcutStoredMetadata metadata,
        Func<string, ShortcutTargetProbe>? probe = null) => ShortcutTargetClassifier.Classify(Link, metadata,
        probe ?? (_ => throw new InvalidOperationException("This input must not probe a target")), CheckedAt);

    [Fact]
    public void ExistingTargetRequiresGuardedFixedLocalEvidenceAndDoesNotClaimLaunchSuccess()
    {
        int probes = 0;
        var result = Inspect(new(Target), path =>
        {
            probes++;
            Assert.Equal(Target, path);
            return new(ShortcutProbeState.Exists, "Guarded attributes exist.", true, true);
        });
        Assert.Equal(1, probes);
        Assert.Equal(ShortcutTargetState.TargetExists, result.State);
        Assert.Equal(Link, result.ShortcutPath);
        Assert.Equal(Target, result.TargetPath);
        Assert.Equal(CheckedAt, result.CheckedAt);
        Assert.Equal(ShortcutTargetClassifier.Source, result.Source);
        Assert.Contains("No shortcut resolution", result.Reason);
        Assert.Contains("launch was performed", result.Reason);
    }

    [Theory]
    [InlineData(ShortcutProbeState.FileNotFound)]
    [InlineData(ShortcutProbeState.PathNotFound)]
    public void OnlyNativeMissingEvidenceWithValidatedVolumeAndAncestryBecomesLocalTargetMissing(ShortcutProbeState state)
    {
        var result = Inspect(new(Target), _ => new(state, "Native local not-found evidence.", true, true));
        Assert.Equal(ShortcutTargetState.LocalTargetMissing, result.State);
        Assert.Contains("stored local path only", result.Reason);
        Assert.Contains("not proof the shortcut is obsolete", result.Reason);
        Assert.Contains("authorizes no removal", result.Reason);
    }

    [Theory]
    [InlineData(ShortcutProbeState.FileNotFound, false, true)]
    [InlineData(ShortcutProbeState.PathNotFound, true, false)]
    [InlineData(ShortcutProbeState.FileNotFound, false, false)]
    [InlineData(ShortcutProbeState.Exists, false, false)]
    public void FailedOrIncompleteAvailabilityChecksNeverBecomeBrokenOrVerified(ShortcutProbeState state,
        bool fixedVolume, bool parents)
    {
        var result = Inspect(new(Target), _ => new(state, "Offline or inaccessible context.", fixedVolume, parents));
        Assert.Equal(ShortcutTargetState.Unknown, result.State);
    }

    [Fact]
    public void UnknownNativeMetadataRemainsUnknownEvenWhenTheVolumeIsFixed()
    {
        var result = Inspect(new(Target), _ => new(ShortcutProbeState.Unknown, "Sharing violation; no absence conclusion.", true, true));
        Assert.Equal(ShortcutTargetState.Unknown, result.State);
        Assert.Contains("Sharing violation", result.Reason);
    }

    [Fact]
    public void AccessDeniedDoesNotBecomeMissingAndDoesNotExposeRawExceptionText()
    {
        var result = Inspect(new(Target), _ => throw new UnauthorizedAccessException("raw secret provider text"));
        Assert.Equal(ShortcutTargetState.Unknown, result.State);
        Assert.DoesNotContain("raw secret", result.Reason);
        Assert.Contains("no missing-target conclusion", result.Reason);
    }

    [Fact]
    public void CancellationDuringTheTargetProbeIsNotSwallowedAsSuccessfulMetadata()
    {
        Assert.ThrowsAny<OperationCanceledException>(() => Inspect(new(Target), _ => throw new OperationCanceledException()));
    }

    [Theory]
    [InlineData(@"\\server\share\target.exe")]
    [InlineData("//server/share/target.exe")]
    public void NetworkTargetsAreNotContactedOrClassifiedAsBroken(string path)
    {
        var result = Inspect(new(path));
        Assert.Equal(ShortcutTargetState.Unknown, result.State);
        Assert.Contains("not contacted or authenticated", result.Reason);
    }

    [Theory]
    [InlineData(@"\\?\C:\Apps\target.exe")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void DeviceNamespaceTargetsAreUnsupportedWithoutProbing(string path)
    {
        var result = Inspect(new(path));
        Assert.Equal(ShortcutTargetState.Unsupported, result.State);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\notepad.exe")]
    [InlineData(@"C:\Apps\%UserName%\target.exe")]
    public void EnvironmentDependentTargetsRemainUnknownWithoutExpandingVariables(string path)
    {
        var result = Inspect(new(path));
        Assert.Equal(ShortcutTargetState.Unknown, result.State);
        Assert.Contains("not expanded or resolved", result.Reason);
    }

    [Fact]
    public void HiddenEnvironmentMetadataCannotBeOverriddenByAnApparentlyAbsoluteRawPath()
    {
        var result = Inspect(new(Target, HasEnvironmentMetadata: true));
        Assert.Equal(ShortcutTargetState.Unknown, result.State);
    }

    [Fact]
    public void ArgumentContextIsNotCollectedOrUsedToGuessAPackagedAppTarget()
    {
        var result = Inspect(new(Target, HasArguments: true));
        Assert.Equal(ShortcutTargetState.Unknown, result.State);
        Assert.Contains("Argument values and working directory were not collected", result.Reason);
    }

    [Fact]
    public void AdvertisedInstallerLinkDoesNotActivateMsiOrProbeAFile()
    {
        var result = Inspect(new(Target, IsAdvertised: true));
        Assert.Equal(ShortcutTargetState.Unsupported, result.State);
        Assert.Contains("No installer activation", result.Reason);
    }

    [Fact]
    public void ExtendedOrPackagedActivationMetadataIsNotAssumedToBeAPlainFilesystemLink()
    {
        var result = Inspect(new(Target, HasUnsupportedActivation: true));
        Assert.Equal(ShortcutTargetState.Unsupported, result.State);
    }

    [Theory]
    [InlineData("shell:AppsFolder\\App")]
    [InlineData("::{00000000-0000-0000-0000-000000000000}")]
    [InlineData("ms-appx:///App")]
    [InlineData("https://example.com/target")]
    public void NonFilesystemActivationFormsAreUnsupported(string path)
    {
        var result = Inspect(new(path));
        Assert.Equal(ShortcutTargetState.Unsupported, result.State);
    }

    [Fact]
    public void NamespaceIdentifierWithoutAPathDoesNotMeanBrokenShortcut()
    {
        var result = Inspect(new(null, HasItemIdList: true));
        Assert.Equal(ShortcutTargetState.Unsupported, result.State);
        Assert.Contains("not bound or resolved", result.Reason);
    }

    [Fact]
    public void ALocalRawPathAndStoredIdListCanBeObservedWithoutBindingTheIdList()
    {
        var result = Inspect(new(Target, HasItemIdList: true), _ => new(ShortcutProbeState.Exists, "Guarded existence.", true, true));
        Assert.Equal(ShortcutTargetState.TargetExists, result.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyMetadataRemainsUnresolvedRatherThanAnAbsenceClaim(string? path)
    {
        var result = Inspect(new(path));
        Assert.Equal(ShortcutTargetState.UnresolvedMetadata, result.State);
    }

    [Fact]
    public void MetadataParserFailureCannotBeReplacedWithAnApparentlyPlausibleTarget()
    {
        var result = Inspect(new(Target, MetadataProblem: "The link format could not be parsed."));
        Assert.Equal(ShortcutTargetState.UnresolvedMetadata, result.State);
    }

    [Theory]
    [InlineData("\"C:\\Apps\\App.exe\"")]
    [InlineData("C:\\Apps\\App\n.exe")]
    [InlineData("C:\\Apps\\App\u202e.exe")]
    public void MalformedOrControlMetadataIsNotProbed(string path)
    {
        var result = Inspect(new(path));
        Assert.Equal(ShortcutTargetState.UnresolvedMetadata, result.State);
    }

    [Fact]
    public void APathAtTheShellBufferBoundaryRemainsUnresolvedRatherThanPossiblyTruncated()
    {
        var result = Inspect(new("C:\\" + new string('x', 256)));
        Assert.Equal(ShortcutTargetState.UnresolvedMetadata, result.State);
    }

    [Theory]
    [InlineData("target.exe")]
    [InlineData("C:target.exe")]
    [InlineData("C:\\Apps\\..\\target.exe")]
    [InlineData("C:\\Apps\\target.exe:stream")]
    [InlineData("C:\\Apps\\CON")]
    [InlineData("C:\\Apps\\target.exe ")]
    public void RelativeAmbiguousOrDevicePathsRemainUnknownWithoutContextualResolution(string path)
    {
        var result = Inspect(new(path));
        Assert.Equal(ShortcutTargetState.Unknown, result.State);
    }

    [Fact]
    public void PathNormalizationIsPortableButDoesNotProveAvailability()
    {
        Assert.Equal(@"C:\Selected\Example.lnk", ShortcutPathPolicy.NormalizeLocalPath("c:/Selected/Example.lnk"));
        Assert.Null(ShortcutPathPolicy.NormalizeLocalPath(@"C:\", allowDriveRoot: false));
        Assert.Equal(@"C:\", ShortcutPathPolicy.NormalizeLocalPath(@"C:\"));
    }
}
