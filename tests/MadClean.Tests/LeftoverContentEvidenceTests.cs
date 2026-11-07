using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class LeftoverContentEvidenceTests
{
    private const string Root = @"C:\Apps\Example";
    private static LeftoverContentScope Scope(string root = Root)
    {
        var app = new InstalledApplication("chosen", "Example", "1", "Publisher", "ARP", root, "Unknown");
        var candidate = new LeftoverCandidate("chosen", root, "Prior path survived a registration comparison.", "Review only; not proven junk.");
        var before = new CheckResult<InstalledApplication>(CheckState.Available, "Complete fixture", [app]);
        var after = new CheckResult<InstalledApplication>(CheckState.Available, "Complete fixture", []);
        return Assert.Single(LeftoverContentReview.ValidateScope(candidate, before, after,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).Items);
    }
    private static LeftoverContentReport Report(string root = Root) => new(root,
        [new(root + @"\program.bin", false, 10, "opaque-file-observation"), new(root + @"\cache", true, null, "opaque-directory-observation")],
        [], 1, 2, false, ["Metadata only; ownership and active use remain Unknown."]);
    private static CheckResult<LeftoverContentReport> Result(LeftoverContentReport? report = null, CheckState state = CheckState.Available) =>
        new(state, "Provider text does not establish eligibility.", [report ?? Report()]);

    [Fact]
    public void FactoryScopeAcceptsBoundedMetadataWithoutCreatingOwnershipOrActiveUseEvidence()
    {
        var result = Result();
        Assert.True(LeftoverContentEvidence.IsConsistent(Scope(), result));
        Assert.Equal(CheckState.Available, result.State);
        Assert.All(result.Items[0].Entries, entry => Assert.Equal("Unknown", entry.ActiveUse));
    }

    [Theory]
    [InlineData(CheckState.Unknown)] [InlineData(CheckState.Cancelled)] [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Unavailable)] [InlineData(CheckState.Unsupported)] [InlineData((CheckState)99)]
    public void PlausibleRetainedPayloadCannotPromoteAnUnqualifiedSource(CheckState state) =>
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(state: state)));

    [Fact]
    public void MissingAndAmbiguousReportsCannotSupplySelectedScopeEvidence()
    {
        Assert.False(LeftoverContentEvidence.IsConsistent(null, Result()));
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), null));
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result() with { Items = [] }));
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result() with { Items = [Report(), Report()] }));
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result() with { Items = [null!] }));
    }

    [Theory]
    [InlineData(@"C:\Apps\Example-other")]
    [InlineData(@"D:\Apps\Example")]
    [InlineData(@"C:\Apps\Example\..\Other")]
    public void AForeignOrTraversingRootCannotReplaceTheSelectedFactoryScope(string path) =>
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with { RootPath = path })));

    [Theory]
    [InlineData(@"C:\Apps\Example-other\program.bin")]
    [InlineData(@"C:\Apps\Example\..\program.bin")]
    [InlineData(@"C:\Apps\Example")]
    [InlineData(@"\\server\share\program.bin")]
    public void EntryPathsMustBeStrictDescendantsWithASeparatorBoundary(string path) =>
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with
        { Entries = [new(path, false, 10, "observed")] })));

    [Theory]
    [InlineData(@"C:\Apps\Example\Documents\program.bin")]
    [InlineData(@"C:\Apps\Example\cache\secret.key")]
    [InlineData(@"C:\Apps\Example\Settings")]
    public void ProtectedNamesRemainExcludedFromAcceptedEntries(string path) =>
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with
        { Entries = [new(path, false, 10, "observed")] })));

    [Fact]
    public void ProtectedOrMalformedExcludedNamesMayRemainContainedIssuesWithoutBecomingEntries()
    {
        var report = Report() with { Issues = [new(Root + @"\Documents\secret.key", "Protected; excluded."),
            new(Root + "\\\u200bexcluded.key", "Unusable name; excluded."), new(Root + @"\NUL", "Unsupported name; excluded.")] };
        var result = Result(report, CheckState.Partial);
        Assert.True(LeftoverContentEvidence.IsConsistent(Scope(), result));
        Assert.Equal(CheckState.Partial, result.State);
        Assert.Equal(2, report.Entries.Count);
    }

    [Fact]
    public void IssuesCannotSmuggleAnotherRootOrTraversalIntoThePreview()
    {
        foreach (string path in new[] { @"C:\Apps\Example-other\blocked", Root + @"\..\blocked" })
            Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with
            { Issues = [new(path, "Excluded.")] }, CheckState.Partial)));
    }

    [Fact]
    public void CaseAndSeparatorAliasesAreOneObservedPath()
    {
        var report = Report() with { Entries = [new(Root + @"\program.bin", false, 1, "first"),
            new("c:/apps/example/PROGRAM.BIN", false, 1, "second")], VisitedFiles = 2 };
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(report)));
        Assert.True(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with { RootPath = "c:/apps/example" })));
    }

    [Theory]
    [InlineData(-1)] [InlineData(129)]
    public void FileCountersOutsideTheNativeBudgetAreRejected(int files) =>
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with { VisitedFiles = files })));

    [Theory]
    [InlineData(0)] [InlineData(129)]
    public void DirectoryCountersMustIncludeTheRootWithinBudget(int directories) =>
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with { VisitedDirectories = directories })));

    [Fact]
    public void AcceptedRowsCannotExceedAttemptedMetadataReadsAndTheRootIsNotADisplayedChild()
    {
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with { VisitedFiles = 0 })));
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with { VisitedDirectories = 1 })));
    }

    [Fact]
    public void ByteAndActiveUseClaimsCannotBecomeQualifiedObservations()
    {
        foreach (var entry in new LeftoverContentEntry[] { new LeftoverContentEntry(Root + @"\program.bin", false, -1, "id"),
            new(Root + @"\program.bin", false, null, "id"), new(Root + @"\cache", true, 10, "id"),
            new(Root + @"\program.bin", false, 1, "id", "Inactive"), new(Root + @"\program.bin", false, 1, "") })
            Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(Report() with { Entries = [entry] })));
    }

    [Fact]
    public void GapsCannotBecomeAvailableAndPartialStaysPartial()
    {
        var report = Report() with { Truncated = true, Issues = [new(Root, "Budget gap.")] };
        Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(report)));
        var partial = Result(report, CheckState.Partial);
        Assert.True(LeftoverContentEvidence.IsConsistent(Scope(), partial));
        Assert.Equal(CheckState.Partial, partial.State);
    }

    [Fact]
    public void NullOrOversizedListsAndMissingCoverageDoNotBecomeCompleteReports()
    {
        foreach (var report in new[] { Report() with { Entries = null! }, Report() with { Issues = null! },
            Report() with { Coverage = null! }, Report() with { Coverage = [] },
            Report() with { Issues = Enumerable.Repeat(new LeftoverContentIssue(Root, "Omitted."), 257).ToArray() },
            Report() with { Coverage = [new string('x', 16_385)] } })
            Assert.False(LeftoverContentEvidence.IsConsistent(Scope(), Result(report, CheckState.Partial)));
    }

    [Fact]
    public void LongRecordedRootDoesNotLowerTheNativeDescendantPathBoundToTheRegistrationStringLimit()
    {
        string root = @"C:\Apps\" + string.Join('\\', Enumerable.Repeat(new string('r', 250), 32)) + @"\Example";
        string file = root + "\\" + new string('f', 200) + ".bin";
        Assert.True(file.Length > 8192);
        var report = new LeftoverContentReport(root, [new(file, false, 10, "opaque")], [], 1, 1, false, ["Bounded metadata only."]);
        Assert.True(LeftoverContentEvidence.IsConsistent(Scope(root), Result(report)));
    }
}
