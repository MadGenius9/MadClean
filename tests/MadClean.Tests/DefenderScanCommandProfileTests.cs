using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DefenderScanCommandProfileTests
{
    [Fact]
    public void ObservedProfileRequiresACompleteMarkerAndSuccessfulClientExit()
    {
        const string observed = "MadClean.DefenderProfile/1:observed\r\n";
        Assert.Equal(DefenderScanCommandObservation.ProfileObserved,
            DefenderScanCommandProfile.Interpret(new(0, observed)));
        Assert.Equal(DefenderScanCommandObservation.Unknown,
            DefenderScanCommandProfile.Interpret(new(1, observed)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("MadClean.DefenderProfile/2:observed")]
    [InlineData("MadClean.DefenderProfile/1:observed\r\nMadClean.DefenderProfile/1:module")]
    [InlineData("Warning: unknown provider content\nMadClean.DefenderProfile/1:observed")]
    public void IncompleteOrConflictingEvidenceDoesNotBecomeAnObservation(string output) =>
        Assert.Equal(DefenderScanCommandObservation.Unknown,
            DefenderScanCommandProfile.Interpret(new(0, output)));

    [Theory]
    [InlineData("module", DefenderScanCommandObservation.ModuleNotAccepted)]
    [InlineData("command", DefenderScanCommandObservation.CommandNotAccepted)]
    [InlineData("parameters", DefenderScanCommandObservation.CustomParametersNotAccepted)]
    public void ACompletedProbePreservesWhichPrerequisiteWasNotAccepted(string marker,
        DefenderScanCommandObservation expected) =>
        Assert.Equal(expected, DefenderScanCommandProfile.Interpret(new(0, "MadClean.DefenderProfile/1:" + marker)));
}
