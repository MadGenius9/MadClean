using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class LeftoverContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static InstalledApplication App(string id = "selected", string? path = @"C:\Apps\Example") =>
        new(id, "Example", "1", "Publisher", "ARP fixture", path, "Unknown");
    private static LeftoverCandidate Candidate(string id = "selected", string path = @"C:\Apps\Example") =>
        new(id, path, "registration no longer present; prior install path exists", "Review only; not proven junk.");
    private static CheckResult<InstalledApplication> Snapshot(params InstalledApplication[] apps) => new(CheckState.Available, "Complete synthetic ARP fixture", apps);
    private static CheckResult<LeftoverContentScope> Validate(LeftoverCandidate? candidate = null,
        CheckResult<InstalledApplication>? before = null, CheckResult<InstalledApplication>? after = null) =>
        LeftoverContentReview.ValidateScope(candidate ?? Candidate(), before ?? Snapshot(App()), after ?? Snapshot(), Now);

    [Fact]
    public void UniqueDisappearanceCreatesOnlyAnImmutableMetadataScopeWithSourceUncertainty()
    {
        var candidate = Candidate(); var scope = Assert.Single(Validate(candidate).Items);
        Assert.Equal(candidate.Path, scope.RootPath); Assert.Equal(candidate.ApplicationIdentity, scope.RegistrationIdentity);
        Assert.Equal(Now, scope.ValidatedUtc); Assert.Equal(candidate.Caution, scope.SourceCaution);
        Assert.Contains("not proof uninstall completed", scope.OwnershipCoverage);
        Assert.Contains("Active use remains Unknown", scope.OwnershipCoverage);
        Assert.Contains("not proof of exclusive ownership", scope.OwnershipCoverage);
    }

    [Theory]
    [InlineData(CheckState.Partial)] [InlineData(CheckState.Failed)] [InlineData(CheckState.Unknown)] [InlineData(CheckState.Cancelled)]
    public void IncompleteBeforeOrAfterNeverEstablishesDisappearance(CheckState state)
    {
        Assert.Empty(Validate(before: new(state, "incomplete", [App()])).Items);
        Assert.Empty(Validate(after: new(state, "incomplete", [])).Items);
    }

    [Fact]
    public void MissingStillPresentOrDuplicateIdentitiesBlockRatherThanInventingAnUninstall()
    {
        Assert.Empty(Validate(before: Snapshot()).Items);
        Assert.Empty(Validate(after: Snapshot(App("SELECTED"))).Items);
        Assert.Empty(Validate(before: Snapshot(App(), App("SELECTED"))).Items);
        Assert.Empty(Validate(after: Snapshot(App("other"), App("OTHER"))).Items);
    }

    [Theory]
    [InlineData("C:\\Apps")]
    [InlineData("C:\\Apps\\Example\\child")]
    [InlineData("C:\\Apps\\Other")]
    [InlineData("C:\\Apps\\Example\\..\\Example")]
    [InlineData("\\\\server\\Example")]
    [InlineData(" C:\\Apps\\Example")]
    public void ASelectedCandidateCannotWidenSubstituteOrRedirectTheRecordedInstallPath(string path) =>
        Assert.Empty(Validate(Candidate(path: path)).Items);

    [Theory]
    [InlineData("C:\\Program Files")]
    [InlineData("C:\\ProgramData\\Example")]
    [InlineData("C:\\Windows\\Example")]
    [InlineData("C:\\Users\\Someone\\Example")]
    [InlineData("D:\\Games\\Saved Games\\Example")]
    [InlineData("D:\\Apps\\Shared\\Example")]
    [InlineData("C:\\Apps\\Example\\Config")]
    public void FreeformSafeCautionCannotOverrideProtectedRootPolicy(string path)
    {
        var candidate = Candidate(path: path) with { Caution = "Safe; exclusively owned; delete all" };
        Assert.Equal(CheckState.Unsupported, Validate(candidate, Snapshot(App(path: path))).State);
    }

    [Theory]
    [InlineData("C:\\Apps\\Example")]
    [InlineData("C:\\Apps")]
    [InlineData("C:\\Apps\\Example\\plugin")]
    public void EqualAncestorAndDescendantOtherAppRootsAllBlock(string other)
    {
        Assert.Empty(Validate(before: Snapshot(App(), App("other", other))).Items);
        Assert.Empty(Validate(after: Snapshot(App("other", other))).Items);
    }

    [Fact]
    public void CommonStringPrefixIsNotDirectoryOverlapAndUnknownOwnershipStaysExplicit()
    {
        var result = Validate(before: Snapshot(App(), App("neighbor", @"C:\Apps\ExampleTwo"), App("unknown", null)));
        var scope = Assert.Single(result.Items);
        Assert.Contains("1 other snapshot entries", scope.OwnershipCoverage);
        Assert.Contains("unregistered/portable/packaged", scope.OwnershipCoverage);
    }

    [Theory]
    [InlineData("Settings", true)] [InlineData("User Data", true)] [InlineData("Saves", true)] [InlineData(".ssh", true)]
    [InlineData("Profile 12", true)] [InlineData("Credentials", true)] [InlineData("customer.db", false)]
    [InlineData("config.JSON", false)] [InlineData("private.pem", false)] [InlineData("game.sav", false)]
    [InlineData("..", true)] [InlineData("nested/file", false)] [InlineData("C:example", false)] [InlineData("CON.txt", false)]
    public void ProtectedDescendantsCannotBeEnumeratedByTreatingNamesAsSafeCommandsOrPaths(string name, bool directory) =>
        Assert.True(LeftoverContentPolicy.IsProtectedDescendant(name, directory));

    [Fact]
    public void OrdinaryApplicationMetadataNamesAreNotClaimsOfSafeRemoval()
    {
        Assert.False(LeftoverContentPolicy.IsProtectedDescendant("bin", true));
        Assert.False(LeftoverContentPolicy.IsProtectedDescendant("library.dll", false));
        Assert.Equal("Unknown", new LeftoverContentEntry(@"C:\Apps\Example\library.dll", false, 10, "fixture metadata").ActiveUse);
        Assert.Equal(128, LeftoverContentPolicy.MaximumFiles); Assert.Equal(128, LeftoverContentPolicy.MaximumDirectories);
    }

    [Fact]
    public void CancellationAndBoundedSnapshotsDoNotProduceAPartialScope()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LeftoverContentReview.ValidateScope(Candidate(), Snapshot(App()), Snapshot(), Now, stop.Token));
        Assert.Empty(Validate(before: new(CheckState.Available, "too many", Enumerable.Repeat(App(), 20_001).ToArray())).Items);
        Assert.Empty(LeftoverContentReview.ValidateScope(Candidate(), Snapshot(App()), Snapshot(), default).Items);
    }
}
