using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemToolActionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    // These are metadata fixtures. No system executable is verified or launched by portable tests.
    private static SystemToolActionPreview Preview(SystemToolOperation operation = SystemToolOperation.DismScanHealth)
    {
        var definition = SystemToolActionsCatalog.Find(operation)!;
        return new(Guid.NewGuid(), definition, @"C:\Windows\System32", @"C:\Windows\System32\" + definition.FileName,
            "10.0.fixture", new string('a', 64), SystemToolTrustState.MicrosoftSignatureVerified,
            "Fixture trust observation, not production data", Now.AddSeconds(-10), Now.AddSeconds(60), true, "");
    }
    private static SystemToolLaunchRequest Request(SystemToolActionPreview preview) =>
        new(preview.PreviewId, preview.Definition.Operation, preview.Sha256, true, true);
    private static IReadOnlyList<string> Validate(SystemToolActionPreview preview, SystemToolLaunchRequest? request = null,
        bool productionValidated = true) => SystemToolActionsPolicy.ValidateLaunch(preview, request ?? Request(preview), Now, productionValidated);

    [Fact]
    public void ConsentAndPerfectFixtureMetadataCannotBypassTheProductionValidationGate()
    {
        var reasons = Validate(Preview(), productionValidated: false);
        Assert.Contains(reasons, reason => reason.Contains("Production execution is disabled", StringComparison.Ordinal));
    }

    [Fact]
    public void CompleteFixturePassesOnlyPureValidationNotAnExecutionOrIntegrityAssessment()
    {
        Assert.Empty(Validate(Preview()));
    }

    [Fact]
    public void UserSelectionAndInformedAcknowledgementAreBothNecessary()
    {
        var preview = Preview(); var request = Request(preview);
        Assert.NotEmpty(Validate(preview, request with { UserSelectedOperation = false }));
        Assert.NotEmpty(Validate(preview, request with { EffectsAndElevationAcknowledged = false }));
    }

    [Fact]
    public void StaleOrUnboundPreviewCannotLaunchEvenWhenItDescribesARealOperation()
    {
        var preview = Preview(); var request = Request(preview);
        Assert.NotEmpty(Validate(preview, request with { PreviewId = Guid.NewGuid() }));
        Assert.NotEmpty(Validate(preview, request with { PreviewSha256 = new string('b', 64) }));
        Assert.NotEmpty(Validate(preview with { ExpiresUtc = Now }));
        Assert.NotEmpty(Validate(preview with { CheckedUtc = Now.AddSeconds(1) }));
        Assert.NotEmpty(Validate(preview with { ExpiresUtc = Now.AddMinutes(3) }));
    }

    [Theory]
    [InlineData(SystemToolTrustState.Unknown)]
    [InlineData(SystemToolTrustState.Unavailable)]
    [InlineData(SystemToolTrustState.Failed)]
    public void UntrustedOrOfflineEvidenceIsNotConsentToUseASystemTool(SystemToolTrustState state)
    {
        Assert.NotEmpty(Validate(Preview() with { TrustState = state }));
    }

    [Fact]
    public void UnknownVersionOrDisabledPreviewCannotBecomeReadyThroughConsent()
    {
        Assert.NotEmpty(Validate(Preview() with { ToolVersion = "Unknown" }));
        Assert.NotEmpty(Validate(Preview() with { ToolVersion = new string('1', 129) }));
        Assert.NotEmpty(Validate(Preview() with { ToolVersion = "10.0\u202E" }));
        Assert.NotEmpty(Validate(Preview() with { CanLaunch = false }));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share\System32")]
    [InlineData(@"C:\Windows\System32:alternate")]
    [InlineData(@"C:\Windows\System32\..\Temp")]
    [InlineData(@"C:\Windows\System32.")]
    [InlineData(@"C:\Windows\System32 ")]
    [InlineData("C:\\Windows\\System32\u202E")]
    public void AmbiguousOrRemoteSystemDirectoryIsRejected(string directory)
    {
        var preview = Preview();
        Assert.NotEmpty(Validate(preview with { SystemDirectory = directory, ToolPath = directory + "\\" + preview.Definition.FileName }));
    }

    [Fact]
    public void AnotherExecutableOrChangedArgumentsCannotHideInsideACheckPreview()
    {
        var preview = Preview(); var request = Request(preview);
        Assert.NotEmpty(Validate(preview with { ToolPath = @"C:\Temp\Dism.exe" }, request));
        Assert.NotEmpty(Validate(preview with { Definition = preview.Definition with { FileName = "cmd.exe" } }, request));
        Assert.NotEmpty(Validate(preview with { Definition = preview.Definition with { Arguments = ["/Online", "/Cleanup-Image", "/RestoreHealth", "/Source:unreviewed"] } }, request));
        Assert.NotEmpty(Validate(preview with { Definition = preview.Definition with { Effect = "No system changes" } }, request));
        Assert.NotEmpty(Validate(preview, request with { Operation = SystemToolOperation.SfcScanNow }));
    }

    [Fact]
    public void RepairCannotBeRelabeledAsNonRepairThroughMutablePreviewMetadata()
    {
        var preview = Preview(SystemToolOperation.DismRestoreHealth);
        Assert.Equal(SystemToolActionKind.Repair, preview.Definition.Kind);
        Assert.NotEmpty(Validate(preview with { Definition = preview.Definition with { Kind = SystemToolActionKind.NonRepairCheck } }));
        Assert.Equal(SystemToolActionKind.Repair, SystemToolActionsCatalog.Find(SystemToolOperation.SfcScanNow)!.Kind);
        Assert.Equal(SystemToolActionKind.NonRepairCheck, SystemToolActionsCatalog.Find(SystemToolOperation.SfcVerifyOnly)!.Kind);
    }

    [Fact]
    public void FixedCatalogueCannotContainImageSourceShellOrAutomaticRestartEscapeHatches()
    {
        Assert.Null(SystemToolActionsCatalog.Find((SystemToolOperation)999));
        foreach (var definition in SystemToolActionsCatalog.All)
        {
            Assert.True(definition.FileName is "Dism.exe" or "sfc.exe");
            Assert.DoesNotContain(definition.Arguments, argument => argument.Contains(':') || argument.Contains('"') || argument.Contains(';'));
            Assert.DoesNotContain(definition.Arguments, argument => argument.Equals("/Quiet", StringComparison.OrdinalIgnoreCase) || argument.Contains("ResetBase", StringComparison.OrdinalIgnoreCase));
            if (definition.FileName == "Dism.exe") Assert.Contains("/NoRestart", definition.Arguments);
            Assert.Contains("Identity verification may contact", definition.Network);
        }
    }

    [Theory]
    [InlineData(SystemToolOperation.DismCheckHealth, 0)]
    [InlineData(SystemToolOperation.DismScanHealth, 0)]
    [InlineData(SystemToolOperation.DismRestoreHealth, 0)]
    [InlineData(SystemToolOperation.SfcVerifyOnly, 0)]
    [InlineData(SystemToolOperation.SfcScanNow, 0)]
    [InlineData(SystemToolOperation.DismRestoreHealth, 3010)]
    [InlineData(SystemToolOperation.SfcScanNow, 1)]
    public void ObservedClientExitNeverBecomesACannedHealthyOrRepairedVerdict(SystemToolOperation operation, int exitCode)
    {
        var description = SystemToolActionsPolicy.DescribeClientExit(operation, exitCode);
        Assert.Contains("does not establish", description);
        Assert.Contains("remains Unknown", description);
        Assert.Contains(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), description);
    }

    [Fact]
    public void StoppingObservationDoesNotClaimServicingStoppedOrRolledBack()
    {
        var description = SystemToolActionsPolicy.DescribeLostMonitoring();
        Assert.Contains("may continue", description);
        Assert.Contains("No cancellation, rollback", description);
        Assert.DoesNotContain(Enum.GetNames<SystemToolRunState>(), state => state is "Healthy" or "Repaired" or "Cancelled" or "Succeeded");
    }
}
