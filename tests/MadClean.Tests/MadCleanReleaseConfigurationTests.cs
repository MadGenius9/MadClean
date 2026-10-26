using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MadCleanReleaseConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MadCleanReleaseTests-" + Guid.NewGuid());
    public MadCleanReleaseConfigurationTests() => Directory.CreateDirectory(directory);
    [Fact]
    public void AbsentConfigurationDoesNotEnableReleaseChecking()
    {
        var result = MadCleanReleaseConfiguration.Load(directory);
        Assert.False(result.IsConfigured); Assert.Null(result.Repository); Assert.Null(result.Error);
    }
    [Fact]
    public void ExplicitTrustedRepositoryCanBeConfiguredWithoutCredentials()
    {
        Write("{\"repository\":\"MadGenius9/MadClean\"}");
        var result = MadCleanReleaseConfiguration.Load(directory);
        Assert.True(result.IsConfigured); Assert.Equal("MadGenius9/MadClean", result.Repository);
    }
    [Theory]
    [InlineData("{\"repository\":\"someone/other\"}")]
    [InlineData("{\"repository\":\"https://github.com/MadGenius9/MadClean\"}")]
    [InlineData("{\"repository\":\"MadGenius9/MadClean\",\"token\":\"secret-fixture\"}")]
    [InlineData("{\"repository\":\"MadGenius9/MadClean\",\"repository\":\"someone/other\"}")]
    [InlineData("null")]
    [InlineData("invalid JSON")]
    public void UntrustedOrMalformedConfigurationFailsClosedWithoutEchoingItsContents(string value)
    {
        Write(value);
        var result = MadCleanReleaseConfiguration.Load(directory);
        Assert.False(result.IsConfigured); Assert.Null(result.Repository); Assert.NotNull(result.Error);
        Assert.DoesNotContain("secret-fixture", result.Error);
        Assert.DoesNotContain("someone/other", result.Error);
    }
    [Fact]
    public void OversizedConfigurationIsNotReadAsAnUnboundedDocument()
    {
        Write(new string(' ', 4097)); Assert.NotNull(MadCleanReleaseConfiguration.Load(directory).Error);
    }
    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void UnresolvedApplicationFolderCannotFallBackToWorkingDirectory(string folder)
    {
        var result = MadCleanReleaseConfiguration.Load(folder);
        Assert.False(result.IsConfigured); Assert.NotNull(result.Error);
    }
    [Theory]
    [InlineData(MadCleanReleaseState.Failed)]
    [InlineData(MadCleanReleaseState.Unavailable)]
    [InlineData(MadCleanReleaseState.Unknown)]
    [InlineData(MadCleanReleaseState.Cancelled)]
    public void ConfirmedReminderSurvivesAnUnsuccessfulRecheckWithOriginalTimestamp(MadCleanReleaseState state)
    {
        var reminder = new MadCleanReleaseReminder();
        var original = DateTimeOffset.UtcNow.AddHours(-1);
        var release = new MadCleanReleaseResult(MadCleanReleaseState.NewerRelease, "Newer", "0.2.0", "Notes",
            new Uri("https://github.com/MadGenius9/MadClean/releases/tag/v0.2.0"));
        reminder.Apply(release, original);
        reminder.Apply(new(state, "Check did not complete"), DateTimeOffset.UtcNow);
        Assert.Same(release, reminder.Release); Assert.Equal(original, reminder.ConfirmedAt);
    }
    [Fact]
    public void SuccessfulCurrentResultClearsPreviousReminder()
    {
        var reminder = new MadCleanReleaseReminder();
        reminder.Apply(new(MadCleanReleaseState.NewerRelease, "Newer", "0.2.0", null,
            new Uri("https://github.com/MadGenius9/MadClean/releases/tag/v0.2.0")), DateTimeOffset.UtcNow);
        reminder.Apply(new(MadCleanReleaseState.Current, "No newer stable release reported"), DateTimeOffset.UtcNow);
        Assert.Null(reminder.Release); Assert.Null(reminder.ConfirmedAt);
    }
    private void Write(string value) => File.WriteAllText(Path.Combine(directory, MadCleanReleaseConfiguration.FileName), value);
    public void Dispose() => Directory.Delete(directory, true);
}
