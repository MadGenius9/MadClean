using System.Net;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MadCleanReleaseSourcePolicyTests
{
    private static readonly Version RunningVersion = new(0, 1, 0);
    private static readonly DateTimeOffset ConfirmedUtc = new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static MadCleanReleaseResult Newer() => new(MadCleanReleaseState.NewerRelease, "Confirmed newer metadata.",
        "0.2.0", "Reviewed notes.", new("https://github.com/MadGenius9/MadClean/releases/tag/v0.2.0"));

    [Fact]
    public async Task MissingOrDisabledSourceCannotStartARequestOrBecomeCurrent()
    {
        using var handler = new FixedHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var checker = new MadCleanReleaseChecker(client);
        var missing = MadCleanReleaseSourcePolicy.Resolve(new(null, null), MadCleanReleaseSourceChoice.Deployment);
        var disabled = MadCleanReleaseSourcePolicy.Resolve(new(MadCleanReleaseSource.TrustedRepository, null), MadCleanReleaseSourceChoice.Disabled);
        foreach (var configuration in new[] { missing, disabled })
        {
            Assert.False(configuration.IsConfigured);
            Assert.Null(configuration.Error);
            var result = await checker.CheckAsync(configuration.Repository, RunningVersion, default);
            Assert.Equal(MadCleanReleaseState.NotConfigured, result.State);
        }
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("MadGenius9/MadClean")]
    [InlineData("madgenius9/madclean")]
    [InlineData("MADGENIUS9/MADCLEAN")]
    public void ValidDeploymentSpellingResolvesToCanonicalPinnedSource(string repository)
    {
        var original = new MadCleanReleaseConfiguration(repository, null);
        var resolved = MadCleanReleaseSourcePolicy.Resolve(original, MadCleanReleaseSourceChoice.Deployment);
        Assert.True(resolved.IsConfigured);
        Assert.Equal(MadCleanReleaseSource.TrustedRepository, resolved.Repository);
        Assert.Equal(repository, original.Repository);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" MadGenius9/MadClean")]
    [InlineData("MadGenius9/MadClean ")]
    [InlineData("OtherOwner/MadClean")]
    [InlineData("https://github.com/MadGenius9/MadClean")]
    [InlineData("https://private-token@github.com/MadGenius9/MadClean")]
    [InlineData("MadGenius9/MadClean?private-token")]
    [InlineData("MadGenius9/MadClean\u200b")]
    public void ForgedDeploymentCannotConfigureArbitraryCredentialOrAmbiguousSource(string repository)
    {
        var original = new MadCleanReleaseConfiguration(repository, null);
        var resolved = MadCleanReleaseSourcePolicy.Resolve(original, MadCleanReleaseSourceChoice.Deployment);
        Assert.False(resolved.IsConfigured);
        Assert.Null(resolved.Repository);
        Assert.NotNull(resolved.Error);
        Assert.DoesNotContain("private-token", resolved.Error!, StringComparison.Ordinal);
        Assert.Equal(repository, original.Repository);
    }

    [Fact]
    public void InvalidDeploymentErrorIsNotEchoedAndExplicitOverrideDoesNotModifyIt()
    {
        var deployment = new MadCleanReleaseConfiguration(MadCleanReleaseSource.TrustedRepository, "private-token-and-path");
        var invalid = MadCleanReleaseSourcePolicy.Resolve(deployment, MadCleanReleaseSourceChoice.Deployment);
        Assert.False(invalid.IsConfigured);
        Assert.DoesNotContain("private-token", invalid.Error!, StringComparison.Ordinal);
        var enabled = MadCleanReleaseSourcePolicy.Resolve(deployment, MadCleanReleaseSourceChoice.OfficialRepository);
        Assert.True(enabled.IsConfigured);
        Assert.Equal(MadCleanReleaseSource.TrustedRepository, enabled.Repository);
        Assert.Null(enabled.Error);
        var disabled = MadCleanReleaseSourcePolicy.Resolve(deployment, MadCleanReleaseSourceChoice.Disabled);
        Assert.False(disabled.IsConfigured);
        Assert.Null(disabled.Error);
        Assert.Equal("private-token-and-path", deployment.Error);
        Assert.NotNull(MadCleanReleaseSourcePolicy.Resolve(deployment, MadCleanReleaseSourceChoice.Deployment).Error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void UnsupportedChoiceCannotFallBackToAConfiguredDeployment(int choice)
    {
        var resolved = MadCleanReleaseSourcePolicy.Resolve(new(MadCleanReleaseSource.TrustedRepository, null),
            (MadCleanReleaseSourceChoice)choice);
        Assert.False(resolved.IsConfigured);
        Assert.Null(resolved.Repository);
        Assert.NotNull(resolved.Error);
    }

    [Fact]
    public async Task ExplicitPinnedCheckPrivateOrMissingResponseNeverBecomesCurrent()
    {
        using var handler = new FixedHandler(HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        var effective = MadCleanReleaseSourcePolicy.Resolve(new(null, "Invalid deployment."), MadCleanReleaseSourceChoice.OfficialRepository);
        Assert.Equal(0, handler.Requests); // Selecting a source itself performs no check.
        var result = await new MadCleanReleaseChecker(client).CheckAsync(effective.Repository, RunningVersion, default);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("https://api.github.com/repos/MadGenius9/MadClean/releases/latest", handler.RequestUri?.AbsoluteUri);
        Assert.False(handler.SawCredentials);
        Assert.Equal(MadCleanReleaseState.Unavailable, result.State);
        Assert.Null(result.Version);
        Assert.Contains("cannot distinguish", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableRecheckRetainsConfirmationUntilExplicitSourceContextClear()
    {
        var reminder = new MadCleanReleaseReminder();
        var newer = Newer();
        reminder.Apply(newer, ConfirmedUtc);
        using var handler = new FixedHandler(HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        var source = MadCleanReleaseSourcePolicy.Resolve(new(null, null), MadCleanReleaseSourceChoice.OfficialRepository);
        var unavailable = await new MadCleanReleaseChecker(client).CheckAsync(source.Repository, RunningVersion, default);
        reminder.Apply(unavailable, ConfirmedUtc.AddMinutes(1));
        Assert.Same(newer, reminder.Release);
        Assert.Equal(ConfirmedUtc, reminder.ConfirmedAt);
        reminder.Clear();
        Assert.Null(reminder.Release);
        Assert.Null(reminder.ConfirmedAt);
        reminder.Clear(); // Repeated disable/reset does not revive old notes or comparison evidence.
        Assert.Null(reminder.Release);
    }

    [Theory]
    [InlineData(MadCleanReleaseState.Failed)]
    [InlineData(MadCleanReleaseState.Cancelled)]
    [InlineData(MadCleanReleaseState.Unknown)]
    public void FailedObservationStillRetainsReminderWhileCurrentObservationClearsIt(MadCleanReleaseState state)
    {
        var reminder = new MadCleanReleaseReminder();
        var newer = Newer();
        reminder.Apply(newer, ConfirmedUtc);
        reminder.Apply(new(state, "No current comparison established."), ConfirmedUtc.AddMinutes(1));
        Assert.Same(newer, reminder.Release);
        reminder.Apply(new(MadCleanReleaseState.Current, "No newer stable release found."), ConfirmedUtc.AddMinutes(2));
        Assert.Null(reminder.Release);
        Assert.Null(reminder.ConfirmedAt);
    }

    private sealed class FixedHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Uri? RequestUri { get; private set; }
        public bool SawCredentials { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            RequestUri = request.RequestUri;
            SawCredentials |= request.Headers.Authorization != null || request.Headers.Contains("Cookie") || request.Headers.Contains("Proxy-Authorization");
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
