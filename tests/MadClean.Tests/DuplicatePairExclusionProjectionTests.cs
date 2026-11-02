using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DuplicatePairExclusionProjectionTests
{
    private static CheckResult<DuplicatePairRecheckReport> Result(CheckState historical = CheckState.Available,
        CheckState outer = CheckState.Available, DuplicatePairRecheckState comparison = DuplicatePairRecheckState.Equal)
    {
        DuplicateEntry Entry(string path, string identity) => new("g", path, 4, identity, "Independent", "Bytes compared");
        var end = DateTimeOffset.UtcNow;
        var diagnostic = new DuplicatePairReadDiagnostic(DuplicatePairReadStage.RegistrationExclusions,
            DuplicatePairReadReason.RegistrationLocationUnsupported, null, 8, 458, 22, 1);
        var report = new DuplicatePairRecheckReport(comparison, "Actual comparison", Entry(@"C:\Selected\a", "A"),
            Entry(@"C:\Selected\b", "B"), new("A", 4, 1, 1, 1), new("B", 4, 1, 1, 1), end.AddSeconds(-1), end,
            8, historical, "Original read coverage") { Diagnostic = diagnostic };
        return new(outer, "Actual provider result", [report]);
    }

    [Theory]
    [InlineData(CheckState.Available, CheckState.Available, CheckState.Available)]
    [InlineData(CheckState.Available, CheckState.Partial, CheckState.Partial)]
    [InlineData(CheckState.Partial, CheckState.Available, CheckState.Partial)]
    [InlineData(CheckState.Partial, CheckState.Partial, CheckState.Partial)]
    public void HistoricalAndCurrentCoverageRemainIndependentWithoutPartialPromotion(CheckState historical, CheckState current, CheckState expected)
    {
        foreach (var comparison in new[] { DuplicatePairRecheckState.Equal, DuplicatePairRecheckState.Different })
        {
            var source = Result(historical, comparison: comparison);
            var result = DuplicatePairExclusionProjection.Apply(source, current);
            var report = Assert.Single(result.Items);
            Assert.Equal(expected, result.State); Assert.Equal(comparison, report.State);
            Assert.Equal(historical, report.SourceCoverageState); Assert.Equal(current, report.CurrentExclusionCoverage);
            Assert.Equal(source.Items[0].Diagnostic, report.Diagnostic);
            Assert.Equal(8, report.ComparedBytes); Assert.False(report.CanRemove);
        }
    }

    [Fact]
    public void AlreadyPartialComparisonCannotBeUpgradedByBothAvailableCoverageSources()
    {
        var result = DuplicatePairExclusionProjection.Apply(Result(outer: CheckState.Partial), CheckState.Available);
        Assert.Equal(CheckState.Partial, result.State);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    [InlineData((CheckState)999)]
    public void EssentialCoverageGapBlocksEqualityWithoutPretendingNoIoOccurred(CheckState current)
    {
        var source = Result(CheckState.Partial);
        var result = DuplicatePairExclusionProjection.Apply(source, current);
        var report = Assert.Single(result.Items);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Equal(DuplicatePairRecheckState.Unknown, report.State);
        Assert.Equal(8, report.ComparedBytes);
        Assert.Equal(source.Items[0].CandidateStamp, report.CandidateStamp);
        Assert.Equal(source.Items[0].Diagnostic, report.Diagnostic);
        Assert.Equal(CheckState.Partial, report.SourceCoverageState); Assert.False(report.CanRemove);
        Assert.Equal(Enum.IsDefined(current) ? current : CheckState.Unknown, report.CurrentExclusionCoverage);
    }

    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    public void AvailableExclusionCoverageNeverPromotesNonpositiveProviderOutcome(CheckState outcome)
    {
        var source = Result(outer: outcome);
        var result = DuplicatePairExclusionProjection.Apply(source, CheckState.Available);
        Assert.Equal(outcome, result.State);
        Assert.Equal(DuplicatePairRecheckState.Unknown, Assert.Single(result.Items).State);
        Assert.Equal(8, result.Items[0].ComparedBytes);
        Assert.Equal(source.Items[0].Diagnostic, result.Items[0].Diagnostic);
    }

    [Fact]
    public void ChangedObservationAndEmptyFailureRemainNonpositive()
    {
        var changed = DuplicatePairExclusionProjection.Apply(Result(outer: CheckState.Unknown,
            comparison: DuplicatePairRecheckState.Changed), CheckState.Partial);
        Assert.Equal(CheckState.Unknown, changed.State);
        Assert.Equal(DuplicatePairRecheckState.Changed, Assert.Single(changed.Items).State);
        var failed = CheckResult<DuplicatePairRecheckReport>.Empty(CheckState.Failed, "No report");
        Assert.Same(failed, DuplicatePairExclusionProjection.Apply(failed, CheckState.Available));
    }
}
