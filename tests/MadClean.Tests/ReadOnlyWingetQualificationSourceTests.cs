using MadClean.WindowsSmoke;
using Xunit;

namespace MadClean.Tests;

public sealed class ReadOnlyWingetQualificationSourceTests
{
    [Theory]
    [InlineData("refs/heads/feat/phase-one-foundation")]
    [InlineData("refs/heads/recovery/validated-20261112")]
    public void ExistingFeatureAndExplicitRecoveryBranchesAreAccepted(string sourceRef) =>
        Assert.True(ReadOnlyWingetQualificationSource.IsAllowedRef(sourceRef));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("refs/heads/main")]
    [InlineData("refs/heads/recovery/validated-20261113")]
    [InlineData("refs/heads/recovery/validated-20261112-extra")]
    [InlineData("refs/heads/recovery/validated-20261112/child")]
    [InlineData("refs/heads/feat/phase-one-foundation-extra")]
    [InlineData("refs/heads/feat/phase-one-foundation/child")]
    [InlineData("refs/tags/recovery/validated-20261112")]
    [InlineData("refs/tags/feat/phase-one-foundation")]
    [InlineData("refs/pull/123/merge")]
    [InlineData("refs/pull/123/head")]
    [InlineData("recovery/validated-20261112")]
    [InlineData("feat/phase-one-foundation")]
    [InlineData("Refs/heads/recovery/validated-20261112")]
    [InlineData("refs/heads/Recovery/validated-20261112")]
    [InlineData("refs/heads/feat/Phase-one-foundation")]
    [InlineData(" refs/heads/recovery/validated-20261112")]
    [InlineData("refs/heads/recovery/validated-20261112 ")]
    [InlineData("refs/heads/feat/phase-one-foundation\n")]
    [InlineData("refs/heads/recovery/validated-20261112\0")]
    public void ForeignRefsAndNoncanonicalLookalikesCannotEnterTheReadOnlyQualification(string? sourceRef) =>
        Assert.False(ReadOnlyWingetQualificationSource.IsAllowedRef(sourceRef));
}
