using MadClean.Validation;
using Xunit;

namespace MadClean.Tests;

public sealed class LocalRepairReviewEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(1);
    private static LocalUiCheckFacts Base() => new(true, true, true, true, true, true, true, true);
    private static LocalRepairReviewFacts Repair() => new(true, true, true, true, true, true, true);
    private static LocalRepairReviewReport Good() => LocalRepairReviewEvidence.Create(Start, End, Base(), Repair());

    [Fact]
    public void PureFixtureCanBeInternallyConsistentWithoutClaimingWindowsOrPhysicalInputEvidence()
    {
        var report = Good(); Assert.Equal("passed", report.State); Assert.True(LocalRepairReviewEvidence.IsConsistent(report, End));
        Assert.Equal("local-repair-review", report.Context); Assert.Equal(1, report.FormatVersion);
        Assert.Equal("unverified", report.WindowsProviders); Assert.Equal("unverified", report.Maintenance); Assert.Equal("unverified", report.PhysicalInput);
        // These fabricated DTOs test policy only. Passing this test does not observe a WPF window.
        Assert.False(LocalUiValidationEvidence.IsConsistent(new(report.FormatVersion, report.Context, Start, End,
            report.State, report.Facts, null, report.WindowsProviders, report.Maintenance, report.PhysicalInput), End));
    }
    [Theory]
    [InlineData("context")]
    [InlineData("version")]
    [InlineData("provider")]
    [InlineData("maintenance")]
    [InlineData("input")]
    [InlineData("outcome")]
    public void ForgedCoverageAndCrossModeClaimsAreRejected(string kind)
    {
        var report = Good();
        report = kind switch
        {
            "context" => report with { Context = "local-neutral-ui" },
            "version" => report with { FormatVersion = 2 },
            "provider" => report with { WindowsProviders = "verified" },
            "maintenance" => report with { Maintenance = "passed" },
            "input" => report with { PhysicalInput = "verified" },
            _ => report with { State = "inconclusive" }
        };
        Assert.False(LocalRepairReviewEvidence.IsConsistent(report, End));
    }
    [Theory]
    [InlineData("window")]
    [InlineData("initial")]
    [InlineData("navigation")]
    [InlineData("closure")]
    public void RepairFactsCannotReplaceMissingNeutralNavigationAndOwnershipProof(string kind)
    {
        var facts = kind switch
        {
            "window" => Base() with { WindowObserved = null },
            "initial" => Base() with { InitialNeutral = null },
            "navigation" => Base() with { NavigationObserved = null },
            _ => Base() with { WindowClosed = false }
        };
        var report = LocalRepairReviewEvidence.Create(Start, End, facts, Repair());
        Assert.False(LocalRepairReviewEvidence.IsConsistent(report, End));
    }
    [Theory]
    [InlineData("gates")]
    [InlineData("controls")]
    [InlineData("drafts")]
    [InlineData("acks")]
    [InlineData("blocked")]
    public void LaterPositiveRepairStagesRequireTheirObservedPrerequisites(string missing)
    {
        var repair = missing switch
        {
            "gates" => Repair() with { ClosedGates = null },
            "controls" => Repair() with { ControlsBound = null },
            "drafts" => Repair() with { DraftsObserved = null },
            "acks" => Repair() with { AcknowledgmentsObserved = null },
            _ => Repair() with { ConfirmationBlocked = null }
        };
        Assert.False(LocalRepairReviewEvidence.IsConsistent(LocalRepairReviewEvidence.Create(Start, End, Base(), repair), End));
    }
    [Fact]
    public void HonestStoppedPartialSequenceRemainsInconclusiveAndAnObservedFailureRemainsFailed()
    {
        var partial = new LocalRepairReviewFacts(true, true, null, null, null, null, true);
        var report = LocalRepairReviewEvidence.Create(Start, End, Base(), partial);
        Assert.Equal("inconclusive", report.State); Assert.True(LocalRepairReviewEvidence.IsConsistent(report, End));
        report = LocalRepairReviewEvidence.Create(Start, End, Base(), partial with { DraftsObserved = false });
        Assert.Equal("failed", report.State); Assert.True(LocalRepairReviewEvidence.IsConsistent(report, End));
        Assert.False(LocalRepairReviewEvidence.IsConsistent(report with { State = "passed" }, End));
    }
    [Fact]
    public void ExceptionCannotBeHiddenBySuccessfulFactsAndUnknownFactsCannotBeTurnedIntoPass()
    {
        var report = LocalRepairReviewEvidence.Create(Start, End, Base(), Repair(), unchecked((int)0x80004005));
        Assert.Equal("failed", report.State); Assert.True(LocalRepairReviewEvidence.IsConsistent(report, End));
        Assert.False(LocalRepairReviewEvidence.IsConsistent(report with { State = "passed" }, End));
        var unknown = LocalRepairReviewEvidence.Create(Start, End,
            new(null, null, null, null, null, null, null, null), new(null, null, null, null, null, null, null));
        Assert.Equal("inconclusive", unknown.State); Assert.True(LocalRepairReviewEvidence.IsConsistent(unknown, End));
        Assert.False(LocalRepairReviewEvidence.IsConsistent(unknown with { State = "passed" }, End));
    }
    [Fact]
    public void TimeCoverageIsUtcOrderedNotFutureAndBoundedToTenMinutes()
    {
        var report = Good();
        Assert.True(LocalRepairReviewEvidence.IsConsistent(report with { CompletedUtc = Start.AddMinutes(10) }, Start.AddMinutes(10)));
        var oversized = report with { CompletedUtc = Start.AddMinutes(10).AddTicks(1) };
        Assert.False(LocalRepairReviewEvidence.IsConsistent(oversized, oversized.CompletedUtc));
        foreach (var altered in new[]
        {
            report with { StartedUtc = default }, report with { CompletedUtc = Start.AddTicks(-1) },
            report with { CompletedUtc = Start.AddMinutes(10).AddTicks(1) },
            report with { StartedUtc = Start.ToOffset(TimeSpan.FromHours(1)) },
            report with { CompletedUtc = End.AddTicks(1) }
        }) Assert.False(LocalRepairReviewEvidence.IsConsistent(altered, End));
        Assert.False(LocalRepairReviewEvidence.IsConsistent(report, default));
        Assert.False(LocalRepairReviewEvidence.IsConsistent(report, End.ToOffset(TimeSpan.FromHours(1))));
    }
    [Fact]
    public void MissingPacketsAndLostFinalNeutralOrClosureCannotBePassed()
    {
        Assert.False(LocalRepairReviewEvidence.IsConsistent(null, End));
        Assert.False(LocalRepairReviewEvidence.IsConsistent(Good() with { Facts = null! }, End));
        Assert.False(LocalRepairReviewEvidence.IsConsistent(Good() with { Repair = null! }, End));
        var report = LocalRepairReviewEvidence.Create(Start, End, Base() with { FinalNeutral = null, WindowClosed = null, OwnershipSettled = null }, Repair());
        Assert.Equal("inconclusive", report.State); Assert.True(LocalRepairReviewEvidence.IsConsistent(report, End));
        Assert.False(LocalRepairReviewEvidence.IsConsistent(report with { State = "passed" }, End));
    }
}
