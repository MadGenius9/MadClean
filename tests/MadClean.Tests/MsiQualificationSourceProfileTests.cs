using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

// Pure source-fence checks only: no environment mutation, native calls, installer
// execution or authorization of an owned package/production request occurs here.
public sealed class MsiQualificationSourceProfileTests
{
    [Theory]
    [InlineData("push", false)]
    [InlineData("push", true)]
    [InlineData("workflow_dispatch", false)]
    [InlineData("workflow_dispatch", true)]
    public void LegacyFeatureSourceRetainsItsExistingEventProfile(string eventName, bool productionGuard)
    {
        foreach (string? optIn in new string?[] { null, "", "0", "1", "true", "arbitrary" })
            Assert.True(MsiQualificationSourceProfile.IsAllowed(MsiQualificationSourceProfile.FeatureRef,
                eventName, MsiQualificationSourceProfile.Job, optIn, productionGuard));
    }

    [Fact]
    public void RecoveryRequiresEveryManualBackendConditionAndRefusesLaterPublicActivation()
    {
        Assert.True(Recovery("workflow_dispatch", "1", false));
        Assert.False(Recovery("workflow_dispatch", "1", true));
        // Public activation does not redirect the recovery path into a UI cycle.
        Assert.True(MsiQualificationSourceProfile.IsAllowed(MsiQualificationSourceProfile.FeatureRef,
            "workflow_dispatch", MsiQualificationSourceProfile.Job, "1", true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1\n")]
    public void RecoveryIsOffWithoutTheExactOptIn(string? optIn)
        => Assert.False(Recovery("workflow_dispatch", optIn, false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("push")]
    [InlineData("pull_request")]
    [InlineData("pull_request_target")]
    [InlineData("schedule")]
    [InlineData("workflow_run")]
    [InlineData("workflow_dispatch ")]
    public void OptInNeverTurnsAnotherEventIntoManualRecoveryPermission(string? eventName)
    {
        Assert.False(Recovery(eventName, "1", false));
        Assert.False(Recovery(eventName, "1", true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("refs/heads/main")]
    [InlineData("refs/pull/1/merge")]
    [InlineData("refs/tags/recovery/validated-20261112")]
    [InlineData("recovery/validated-20261112")]
    [InlineData("refs/heads/recovery/validated-20261112-extra")]
    [InlineData("refs/heads/recovery/validated-20261112/child")]
    [InlineData("refs/heads/recovery/Validated-20261112")]
    [InlineData("refs/heads/recovery/validated-20261112 ")]
    public void ManualFlagDoesNotAdmitAnUnrecognizedOrSimilarSource(string? sourceRef)
        => Assert.False(MsiQualificationSourceProfile.IsAllowed(sourceRef, "workflow_dispatch",
            MsiQualificationSourceProfile.Job, "1", false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("windows-verification")]
    [InlineData("build-test-package")]
    [InlineData("windows-msi-request-extra")]
    [InlineData("windows-msi-request ")]
    [InlineData("Windows-msi-request")]
    public void BothSourceProfilesRequireTheExactDedicatedJob(string? job)
    {
        Assert.False(MsiQualificationSourceProfile.IsAllowed(MsiQualificationSourceProfile.RecoveryRef,
            "workflow_dispatch", job, "1", false));
        Assert.False(MsiQualificationSourceProfile.IsAllowed(MsiQualificationSourceProfile.FeatureRef,
            "push", job, null, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pull_request")]
    [InlineData("schedule")]
    [InlineData("workflow_run")]
    public void LegacyFeatureSourceDoesNotGainOtherEventKinds(string? eventName)
        => Assert.False(MsiQualificationSourceProfile.IsAllowed(MsiQualificationSourceProfile.FeatureRef,
            eventName, MsiQualificationSourceProfile.Job, null, false));

    private static bool Recovery(string? eventName, string? optIn, bool productionGuard) =>
        MsiQualificationSourceProfile.IsAllowed(MsiQualificationSourceProfile.RecoveryRef,
            eventName, MsiQualificationSourceProfile.Job, optIn, productionGuard);
}
