using MadClean.Validation;
using Xunit;

namespace MadClean.Tests;

public sealed class LocalUiValidationEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(1);
    private static readonly LocalUiCheckFacts Complete = new(true, true, true, true, true, true, true, true);
    private static readonly LocalUiCheckFacts Missing = new(null, null, null, null, null, null, null, null);
    private static LocalUiValidationReport Report(LocalUiCheckFacts? facts = null, int? error = null) =>
        LocalUiValidationEvidence.Create(Start, End, facts ?? Complete, error);
    private static bool Valid(LocalUiValidationReport? report) => LocalUiValidationEvidence.IsConsistent(report, End);

    [Fact]
    public void CompleteNeutralFactsQualifyOnlyTheFixedLocalScope()
    {
        var result = Report();
        Assert.True(Valid(result));
        Assert.Equal("passed", result.State);
        Assert.Equal(1, result.FormatVersion);
        Assert.Equal("local-neutral-ui", result.Context);
        Assert.Equal("unverified", result.WindowsProviders);
        Assert.Equal("unverified", result.Maintenance);
        Assert.Equal("unverified", result.PhysicalInput);
    }

    [Fact]
    public void EveryMissingFactPreventsPassAndCannotBeForgedIntoOne()
    {
        foreach (var facts in new[] { Missing, Complete with { InitialNeutral = null, FinalNeutral = null }, Complete with { WideLayout = null },
                     Complete with { CompactLayout = null }, Complete with { NavigationObserved = null },
                     Complete with { FinalNeutral = null }, Complete with { OwnershipSettled = null },
                     Complete with { WindowClosed = null, OwnershipSettled = null } })
        {
            var report = Report(facts);
            Assert.Equal("inconclusive", report.State);
            Assert.True(Valid(report));
            Assert.False(Valid(report with { State = "passed" }));
        }
        // These missing prerequisites also contradict already-recorded dependent true facts.
        Assert.False(Valid(Report(Complete with { WindowObserved = null })));
        Assert.False(Valid(Report(Complete with { WindowClosed = null })));
    }

    [Fact]
    public void ExplicitFailureAndAnyCapturedExceptionRemainFailedEvenForHResultZero()
    {
        foreach (var facts in new[] { Complete with { WideLayout = false }, Complete with { CompactLayout = false },
                     Complete with { NavigationObserved = false }, Missing with { WindowObserved = false } })
        {
            var report = Report(facts);
            Assert.Equal("failed", report.State);
            Assert.True(Valid(report));
            Assert.False(Valid(report with { State = "inconclusive" }));
        }
        foreach (int error in new[] { 0, -1, int.MinValue, int.MaxValue })
        {
            var report = Report(error: error);
            Assert.Equal("failed", report.State);
            Assert.Equal(error, report.ExceptionHResult);
            Assert.True(Valid(report));
            Assert.False(Valid(report with { State = "passed" }));
        }
    }

    [Fact]
    public void DependentTrueObservationsRequireAnObservedWindowAndTheActualPrerequisites()
    {
        foreach (var facts in new[] { Missing with { InitialNeutral = true }, Missing with { WideLayout = true },
                     Missing with { CompactLayout = true }, Missing with { NavigationObserved = true },
                     Missing with { FinalNeutral = true }, Missing with { WindowClosed = true }, Missing with { OwnershipSettled = true },
                     Complete with { WindowObserved = false }, Complete with { InitialNeutral = false }, Complete with { WindowClosed = false } })
            Assert.False(Valid(Report(facts)));
        Assert.True(Valid(Report(Missing with { WindowObserved = true })));
        Assert.True(Valid(Report(Missing with { WindowObserved = true, InitialNeutral = true, FinalNeutral = true })));
        Assert.True(Valid(Report(Missing with { WindowObserved = true, WindowClosed = true, OwnershipSettled = true })));
    }

    [Fact]
    public void UnknownSchemaScopeOrCoverageCannotPromoteProviderMaintenanceOrPhysicalInputClaims()
    {
        var report = Report();
        foreach (var invalid in new[] { report with { FormatVersion = 0 }, report with { FormatVersion = 2 },
                     report with { Context = "passive-windows" }, report with { Context = "LOCAL-NEUTRAL-UI" },
                     report with { State = "Passed" }, report with { State = "unknown" }, report with { State = null! },
                     report with { WindowsProviders = "passed" }, report with { Maintenance = "verified" },
                     report with { PhysicalInput = "observed" }, report with { Facts = null! } })
            Assert.False(Valid(invalid));
        Assert.False(Valid(null));
    }

    [Fact]
    public void UtcChronologyIsBoundedWithoutAcceptingDefaultFutureReversedOrOffsetTimes()
    {
        var report = Report();
        Assert.True(LocalUiValidationEvidence.IsConsistent(report with { CompletedUtc = Start }, End));
        Assert.True(LocalUiValidationEvidence.IsConsistent(report with { CompletedUtc = Start.AddMinutes(10) }, Start.AddMinutes(10)));
        Assert.False(LocalUiValidationEvidence.IsConsistent(report with { CompletedUtc = Start.AddMinutes(10).AddTicks(1) }, Start.AddMinutes(11)));
        foreach (var invalid in new[] { report with { StartedUtc = default }, report with { CompletedUtc = default },
                     report with { StartedUtc = End.AddTicks(1) }, report with { CompletedUtc = End.AddTicks(1) },
                     report with { StartedUtc = Start.ToOffset(TimeSpan.FromHours(1)) },
                     report with { CompletedUtc = End.ToOffset(TimeSpan.FromHours(-1)) } })
            Assert.False(Valid(invalid));
        Assert.False(LocalUiValidationEvidence.IsConsistent(report, default));
        Assert.False(LocalUiValidationEvidence.IsConsistent(report, End.ToOffset(TimeSpan.FromHours(1))));
    }
}
