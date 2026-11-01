using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DuplicatePairReadDiagnosticTests
{
    private static DuplicatePairReadDiagnostic Valid => new(DuplicatePairReadStage.RegistrationExclusions,
        DuplicatePairReadReason.RegistrationEnumerationInvalid, 234, 8, 42, 7, 1);

    [Fact]
    public void GenuineNativeResultsStayDistinctFromMissingResult()
    {
        foreach (int? code in new int?[] { null, 0, 5, 234, unchecked((int)0x80070005) })
        {
            var source = Valid with { RegistryNativeResult = code };
            var result = DuplicatePairReadDiagnosticPolicy.Normalize(source);
            Assert.Equal(code, result.RegistryNativeResult);
            Assert.Equal(source, result);
            Assert.NotSame(source, result);
        }
    }

    [Fact]
    public void ObservedCounterCeilingsRemainRepresentableWithoutInventedTruncation()
    {
        var source = Valid with { KnownFoldersChecked = 32, RegistrationKeysVisited = 10001,
            RegisteredLocationsAccepted = 2048, ExclusionIssues = 10020 };
        Assert.Equal(source, DuplicatePairReadDiagnosticPolicy.Normalize(source));
    }

    [Fact]
    public void ActualObservedZeroCountsAreDistinctFromUnverifiedCounts()
    {
        var source = Valid with { KnownFoldersChecked = 0, RegistrationKeysVisited = 0,
            RegisteredLocationsAccepted = 0, ExclusionIssues = 0 };
        var result = DuplicatePairReadDiagnosticPolicy.Normalize(source);
        Assert.Equal(source, result);
        Assert.Equal(0, result.KnownFoldersChecked);
        Assert.Null(DuplicatePairReadDiagnosticPolicy.Normalize(null).KnownFoldersChecked);
    }

    [Fact]
    public void InvalidCountsAndEnumsCannotExposePlausibleDiagnostics()
    {
        DuplicatePairReadDiagnostic[] invalid =
        [ Valid with { KnownFoldersChecked = -1 }, Valid with { KnownFoldersChecked = 33 },
          Valid with { RegistrationKeysVisited = -1 }, Valid with { RegistrationKeysVisited = 10002 },
          Valid with { RegisteredLocationsAccepted = -1 }, Valid with { RegisteredLocationsAccepted = 2049 },
          Valid with { ExclusionIssues = -1 }, Valid with { ExclusionIssues = 10021 },
          Valid with { KnownFoldersChecked = null }, Valid with { RegistrationKeysVisited = null },
          Valid with { RegisteredLocationsAccepted = null }, Valid with { ExclusionIssues = null },
          Valid with { Stage = (DuplicatePairReadStage)999 }, Valid with { Reason = (DuplicatePairReadReason)999 } ];
        foreach (var source in invalid) AssertUnknown(DuplicatePairReadDiagnosticPolicy.Normalize(source));
        AssertUnknown(DuplicatePairReadDiagnosticPolicy.Normalize(null));
    }

    [Fact]
    public void FailureStageAndReasonRemainIndependentOfActualCodeZero()
    {
        var value = Valid with { RegistryNativeResult = 0, Reason = DuplicatePairReadReason.RegistrationLocationInvalid };
        var result = DuplicatePairReadDiagnosticPolicy.Normalize(value);
        Assert.Equal(DuplicatePairReadStage.RegistrationExclusions, result.Stage);
        Assert.Equal(DuplicatePairReadReason.RegistrationLocationInvalid, result.Reason);
        Assert.Equal(0, result.RegistryNativeResult);
        Assert.Equal(1, result.ExclusionIssues);
    }

    private static void AssertUnknown(DuplicatePairReadDiagnostic value)
    {
        Assert.Equal(DuplicatePairReadStage.Unknown, value.Stage);
        Assert.Equal(DuplicatePairReadReason.ContentNotEstablished, value.Reason);
        Assert.Null(value.RegistryNativeResult);
        Assert.Null(value.KnownFoldersChecked); Assert.Null(value.RegistrationKeysVisited);
        Assert.Null(value.RegisteredLocationsAccepted); Assert.Null(value.ExclusionIssues);
    }
}
