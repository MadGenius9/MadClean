using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

// Pure source-fence tests only. No environment, filesystem, native API or fixture
// is changed; acceptance supplies neither runner authentication nor frame authority.
public sealed class ExistingJournalReaderQualificationProfileTests
{
    private static ExistingJournalReaderQualificationContext Valid()
    {
        string repository = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "madclean-reader-profile"));
        return new("MadGenius9/MadClean", "refs/heads/recovery/validated-20261112", "workflow_dispatch",
            "windows-existing-journal-reader", "1", "true", "github-hosted", new string('a', 40), "123456789", "1",
            repository, repository);
    }

    [Fact]
    public void ExactManualContextIsAcceptedWithoutInspectingAnyNativeScope()
    {
        Assert.True(ExistingJournalReaderQualificationProfile.IsAllowed(Valid()));
        Assert.False(ExistingJournalReaderQualificationProfile.IsAllowed(null));
        Assert.True(ExistingJournalReaderQualificationProfile.IsAllowed(Valid() with { SourceSha = new string('F', 40) }));
    }

    [Theory]
    [InlineData("push")]
    [InlineData("pull_request")]
    [InlineData("pull_request_target")]
    [InlineData("schedule")]
    [InlineData("workflow_run")]
    [InlineData("workflow_dispatch ")]
    [InlineData(null)]
    public void OtherEventsCannotEnterTheCandidate(string? value)
        => Assert.False(ExistingJournalReaderQualificationProfile.IsAllowed(Valid() with { Event = value }));

    [Fact]
    public void SimilarRepositoryRefJobOrRunnerContextsAreNotEquivalent()
    {
        var valid = Valid();
        foreach (var changed in new[]
        {
            valid with { Repository = "MadGenius9/MadClean-fork" },
            valid with { Repository = "madgenius9/madclean" },
            valid with { SourceRef = "refs/heads/main" },
            valid with { SourceRef = "refs/heads/recovery/validated-20261112-copy" },
            valid with { SourceRef = "refs/pull/1/merge" },
            valid with { Job = "windows-verification" },
            valid with { Job = "windows-existing-journal-reader " },
            valid with { RunnerEnvironment = "self-hosted" },
            valid with { RunnerEnvironment = null },
            valid with { Actions = "True" },
            valid with { Actions = null }
        }) Assert.False(ExistingJournalReaderQualificationProfile.IsAllowed(changed));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")]
    [InlineData("True")] [InlineData("01")] [InlineData(" 1")] [InlineData("1 ")]
    public void OnlyTheExactOptInFlagMatches(string? value)
        => Assert.False(ExistingJournalReaderQualificationProfile.IsAllowed(Valid() with { OptIn = value }));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("01")]
    [InlineData("-1")] [InlineData("+1")] [InlineData("1.0")]
    [InlineData(" 1")] [InlineData("1 ")] [InlineData("١")]
    [InlineData("123456789012345678901234567890123")]
    public void RunIdentityAndAttemptMustBeBoundedPositiveAsciiDecimal(string? value)
    {
        Assert.False(ExistingJournalReaderQualificationProfile.IsAllowed(Valid() with { RunId = value }));
        Assert.False(ExistingJournalReaderQualificationProfile.IsAllowed(Valid() with { RunAttempt = value }));
    }

    [Fact]
    public void MissingOrMalformedSourceShaCannotQualify()
    {
        foreach (string? value in new string?[] { null, "", new('a', 39), new('a', 41), new('g', 40), new('Ａ', 40) })
            Assert.False(ExistingJournalReaderQualificationProfile.IsAllowed(Valid() with { SourceSha = value }));
    }

    [Fact]
    public void WorkspaceMustBeAbsoluteAndActuallyMatchTheWorkingDirectory()
    {
        var valid = Valid();
        Assert.True(ExistingJournalReaderQualificationProfile.IsAllowed(valid with { Workspace = valid.Workspace + Path.DirectorySeparatorChar }));
        foreach (var changed in new[]
        {
            valid with { Workspace = null }, valid with { WorkingDirectory = null },
            valid with { Workspace = "" }, valid with { WorkingDirectory = "" },
            valid with { Workspace = ".", WorkingDirectory = "." },
            valid with { Workspace = valid.Workspace + "-other" },
            valid with { Workspace = "\0" }, valid with { WorkingDirectory = new string('x', 4097) }
        }) Assert.False(ExistingJournalReaderQualificationProfile.IsAllowed(changed));
    }
}
