using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class CleanupFileEvidenceValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string FileIdentity = "0000000000000001:00000000000000000000000000000001";
    private const string RootIdentity = "0000000000000001:00000000000000000000000000000002";
    private static (CleanupFileEvidenceScope Scope, CheckResult<CleanupFileEvidenceReport> Result) Valid(bool partial = false, long bytes = 37)
    {
        var candidate = new CleanupCandidate("User temporary files", @"C:\Temp\old.tmp", bytes, Now.AddDays(-10).UtcDateTime);
        var source = new CleanupReport([candidate], partial ? [new("Cleanup", "", "Missing location.")] : [], 0, false);
        var scope = Assert.Single(CleanupFileEvidenceScope.Create(source, candidate, Now.AddMinutes(-1), [], Now.AddSeconds(-10)).Items);
        var report = new CleanupFileEvidenceReport(scope.Candidate, scope.RootKind, CleanupEvidenceSourceKind.WindowsTemporary,
            new(candidate.Path, FileIdentity, bytes, 1, Now.AddDays(-20), Now.AddDays(-10), Now.AddMinutes(-1), 0),
            @"C:\Temp", RootIdentity, Now.AddSeconds(-9), Now.AddSeconds(-1), true, true, true,
            CheckState.Available, scope.SourceCoverageState, ["Current metadata only; active use and ownership remain unknown."])
        { StreamScope = new(CleanupStreamScopeState.UnnamedDataOnly, CleanupStreamScopeReason.Observed, 1, bytes, 2, null, null, true) };
        return (scope, new(partial ? CheckState.Partial : CheckState.Available, "Actual provider-shaped portable fixture only.", [report]));
    }
    private static bool Accept(CleanupFileEvidenceScope scope, CheckResult<CleanupFileEvidenceReport> original, CleanupFileEvidenceReport changed,
        CheckState? state = null) => CleanupFileEvidenceValidation.IsConsistent(original with { State = state ?? original.State, Items = [changed] }, scope, Now);

    [Fact]
    public void ProviderShapedCompletePartialChangedHistoryAndUnknownStreamsRetainTheirDistinctMeaning()
    {
        foreach (bool partial in new[] { false, true })
        {
            var (scope, result) = Valid(partial); Assert.True(CleanupFileEvidenceValidation.IsConsistent(result, scope, Now));
            Assert.False(result.Items[0].CanDelete); Assert.Equal("Unknown", result.Items[0].ActiveUse);
        }
        var (s, r) = Valid(); var data = r.Items[0];
        var changed = data with { File = data.File with { Bytes = 38, LastWriteUtc = Now.AddDays(-8) }, HistoricalSizeMatches = false, HistoricalModifiedMatches = false,
            StreamScope = data.StreamScope! with { UnnamedStreamBytes = 38 } };
        Assert.True(Accept(s, r, changed, CheckState.Partial)); Assert.False(Accept(s, r, changed));
        Assert.False(Accept(s, r, data with { File = data.File with { LastWriteUtc = Now.AddDays(-6) }, HistoricalModifiedMatches = false }, CheckState.Partial));
        Assert.True(Accept(s, r, data with { StreamScope = null }, CheckState.Partial));
        Assert.False(Accept(s, r, data with { StreamScope = null }));
        var unknown = data with { StreamScope = new(CleanupStreamScopeState.Unknown, CleanupStreamScopeReason.NativeQueryFailed, null, null, 2, 50, null, null) };
        Assert.True(Accept(s, r, unknown, CheckState.Partial)); Assert.False(Accept(s, r, unknown));
        var alternate = data with { StreamScope = data.StreamScope! with { State = CleanupStreamScopeState.AlternateStreamsObserved, StreamCount = 2 } };
        Assert.True(Accept(s, r, alternate, CheckState.Partial)); Assert.False(Accept(s, r, alternate));
    }

    [Fact]
    public void DevelopmentSourcesUnknownStatesAndCoveragePromotionAreRejected()
    {
        var (scope, result) = Valid(); var data = result.Items[0];
        Assert.False(Accept(scope, result, data with { SourceKind = CleanupEvidenceSourceKind.OwnedDevelopmentFixture }));
        foreach (var state in new[] { CheckState.Unknown, CheckState.Failed, CheckState.Unavailable, CheckState.Unsupported, CheckState.Cancelled })
            Assert.False(CleanupFileEvidenceValidation.IsConsistent(result with { State = state }, scope, Now));
        var (partialScope, partial) = Valid(true);
        Assert.False(CleanupFileEvidenceValidation.IsConsistent(partial with { State = CheckState.Available }, partialScope, Now));
        Assert.False(Accept(scope, result, data with { SourceCoverageState = CheckState.Partial }));
        Assert.False(Accept(scope, result, data with { ExclusionCoverageState = CheckState.Partial }));
        Assert.True(Accept(scope, result, data with { ExclusionCoverageState = CheckState.Partial }, CheckState.Partial));
    }

    [Fact]
    public void StaleFutureNonUtcAndMisorderedIntervalsCannotBecomeCurrentEvidence()
    {
        var (scope, result) = Valid(); var data = result.Items[0];
        Assert.False(CleanupFileEvidenceValidation.IsConsistent(result, scope, Now.AddMinutes(3)));
        Assert.False(CleanupFileEvidenceValidation.IsConsistent(result, scope, default));
        Assert.False(CleanupFileEvidenceValidation.IsConsistent(result, scope, Now.ToOffset(TimeSpan.FromHours(1))));
        foreach (var changed in new[] { data with { QueryStartedUtc = scope.ValidatedUtc.AddTicks(-1) }, data with { QueryCompletedUtc = Now.AddSeconds(1) },
            data with { QueryCompletedUtc = data.QueryStartedUtc.AddTicks(-1) }, data with { QueryStartedUtc = data.QueryStartedUtc.ToOffset(TimeSpan.FromHours(1)) },
            data with { File = data.File with { CreationUtc = Now.AddSeconds(1) } }, data with { File = data.File with { ChangeUtc = Now.AddSeconds(1) } } })
            Assert.False(Accept(scope, result, changed));
    }

    [Fact]
    public void ExactCanonicalPathIdentityLinksAndHistoricalFlagsAreMandatory()
    {
        var (scope, result) = Valid(); var data = result.Items[0];
        foreach (var changed in new[] { data with { File = data.File with { Path = @"C:\Temp-other\old.tmp" } }, data with { RootPath = @"C:\Temp-other" },
            data with { RootPath = @"C:\Temp\..\Temp" }, data with { RootIdentity = "Unknown" }, data with { File = data.File with { Identity = new string('0', 16) + ":" + new string('0', 32) } },
            data with { File = data.File with { LinkCount = 2 } }, data with { File = data.File with { Bytes = -1 } },
            data with { HistoricalPathMatches = false }, data with { HistoricalSizeMatches = false }, data with { HistoricalModifiedMatches = false },
            data with { Prior = data.Prior with { Bytes = 3 } }, data with { RootKind = CleanupRootKind.WindowsTemporary } })
            Assert.False(Accept(scope, result, changed));
    }

    [Fact]
    public void MalformedContainersCoverageAndStreamShapesDoNotPassSharedValidation()
    {
        var (scope, result) = Valid(); var data = result.Items[0];
        Assert.False(CleanupFileEvidenceValidation.IsConsistent(null, scope, Now));
        Assert.False(CleanupFileEvidenceValidation.IsConsistent(result, null, Now));
        foreach (var items in new IReadOnlyList<CleanupFileEvidenceReport>[] { [], [data, data], [null!], null! })
            Assert.False(CleanupFileEvidenceValidation.IsConsistent(result with { Items = items }, scope, Now));
        foreach (var coverage in new IReadOnlyList<string>[] { [], ["bad\nraw"], [new string('a', 4097)], Enumerable.Repeat("Good", 33).ToArray(), null! })
            Assert.False(Accept(scope, result, data with { Coverage = coverage }));
        Assert.False(Accept(scope, result, data with { StreamScope = data.StreamScope! with { BookendsMatch = false } }, CheckState.Partial));
        Assert.False(Accept(scope, result, data with { StreamScope = data.StreamScope! with { UnnamedStreamBytes = 100 } }, CheckState.Partial));
    }

    [Fact]
    public void IndividualLongMaxLogicalLengthIsPreservedWithoutDoingAReclaimableOrUniqueTotal()
    {
        var (scope, result) = Valid(bytes: long.MaxValue);
        Assert.True(CleanupFileEvidenceValidation.IsConsistent(result, scope, Now));
        Assert.Equal(long.MaxValue, result.Items[0].File.Bytes);
        Assert.Equal(long.MaxValue, result.Items[0].StreamScope!.UnnamedStreamBytes);
        Assert.False(result.Items[0].CanDelete);
    }
}
