using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class MadCleanReleaseCheckerTests
{
    private static readonly Version RunningVersion = new(0, 1, 0, 0);
    private const string Repository = MadCleanReleaseSource.TrustedRepository;

    private static string Release(string tag = "v0.2.0", string? reviewUrl = null, object? draft = null,
        object? prerelease = null, object? body = null) => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = reviewUrl ?? $"https://github.com/{Repository}/releases/tag/{tag}",
            draft = draft ?? false,
            prerelease = prerelease ?? false,
            body = body ?? "Actual release notes."
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AbsentConfigurationDoesNotMakeANetworkRequest(string? configuredRepository)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("A request must not occur"));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(configuredRepository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.NotConfigured, result.State);
        Assert.Equal("Update checking not configured", result.Message);
        Assert.Equal(0, handler.Requests);
        Assert.Null(result.Version);
        Assert.Null(result.ReviewUri);
    }

    [Theory]
    [InlineData("Other/Project")]
    [InlineData("https://github.com/MadGenius9/MadClean")]
    [InlineData("madgenius9/madclean")]
    [InlineData("MadGenius9/MadClean/releases/latest")]
    public async Task UntrustedConfigurationCannotRedirectTheRequest(string configuredRepository)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("A request must not occur"));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(configuredRepository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task ValidNewerStableReleaseUsesOnlyThePinnedAnonymousMetadataEndpoint()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"https://api.github.com/repos/{Repository}/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "MadClean");
            return Task.FromResult(JsonResponse(Release(body: "# New release\nReview these changes.")));
        });
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.NewerRelease, result.State);
        Assert.Equal("v0.2.0", result.Version);
        Assert.Equal("# New release\nReview these changes.", result.Notes);
        Assert.True(MadCleanReleaseSource.IsTrustedReviewUri(result.ReviewUri!));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("v0.1.0", 0, 1, 0, 0)]
    [InlineData("0.1.0", 0, 1, 0, 9)]
    [InlineData("v0.1.0", 0, 2, 0, 0)]
    public async Task EqualOrOlderConfiguredStableReleaseIsCurrentOnlyForThatChannel(string latest,
        int major, int minor, int build, int revision)
    {
        using var handler = Reply(Release(latest));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository,
            new Version(major, minor, build, revision), default);
        Assert.Equal(MadCleanReleaseState.Current, result.State);
        Assert.Contains("configured repository", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not assess other release channels or installed software", result.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThreePartReleaseAndTwoPartRunningVersionCompareWithoutMissingBuildBias()
    {
        using var handler = Reply(Release("v0.1.0"));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, new Version(0, 1), default);
        Assert.Equal(MadCleanReleaseState.Current, result.State);
    }

    [Theory]
    [InlineData("v0.2.0-preview")]
    [InlineData("v0.2.0+build.1")]
    [InlineData("release-0.2.0")]
    [InlineData("v0.2")]
    [InlineData("v0.2.0.0")]
    [InlineData("v00.2.0")]
    [InlineData("v2147483648.2.0")]
    [InlineData("")]
    public async Task UnparseableOrNonStableTagNeverBecomesCurrent(string tag)
    {
        using var handler = Reply(Release(tag, $"https://github.com/{Repository}/releases"));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
        Assert.Null(result.Version);
        Assert.Null(result.ReviewUri);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DraftOrPrereleaseResponseDoesNotClaimAStableRelease(bool draft, bool prerelease)
    {
        using var handler = Reply(Release(draft: draft, prerelease: prerelease));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"draft\":\"false\",\"prerelease\":false}")]
    [InlineData("{\"draft\":false,\"prerelease\":false,\"tag_name\":2}")]
    public async Task MalformedSchemaCannotReportSuccessfulReleaseStatus(string body)
    {
        using var handler = Reply(body);
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
        Assert.Null(result.ReviewUri);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task MissingPrivateInaccessibleOrRateLimitedRepositoryIsUnavailableNotCurrent(int status)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unavailable, result.State);
        Assert.Null(result.Version);
        Assert.Null(result.ReviewUri);
    }

    [Fact]
    public async Task RedirectResponseIsNotFollowedOrTrusted()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://example.com/releases/latest") }
        }));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unavailable, result.State);
        Assert.Equal(1, handler.Requests);
        Assert.Null(result.ReviewUri);
    }

    [Fact]
    public async Task UnexpectedFinalResponseEndpointIsRejected()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Release()),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/releases/latest")
        }));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
        Assert.Null(result.ReviewUri);
    }

    [Theory]
    [InlineData("https://github.com/MadGenius9/MadClean/releases")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases/tag/v0.2.0")]
    [InlineData("https://github.com:443/madgenius9/madclean/releases/tag/v0.2.0")]
    public void OnlyPinnedReviewPathsAreTrusted(string url) =>
        Assert.True(MadCleanReleaseSource.IsTrustedReviewUri(new Uri(url)));

    [Theory]
    [InlineData("http://github.com/MadGenius9/MadClean/releases")]
    [InlineData("https://example.com/MadGenius9/MadClean/releases")]
    [InlineData("https://github.com.evil.example/MadGenius9/MadClean/releases")]
    [InlineData("https://github.com:8443/MadGenius9/MadClean/releases")]
    [InlineData("https://user@github.com/MadGenius9/MadClean/releases")]
    [InlineData("https://github.com/Other/Project/releases")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases?target=example.com")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases#fragment")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases/tag/")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases/tag/v0.2.0/asset.exe")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases/download/v0.2.0/MadClean.exe")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases/tag/%2E%2E")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases/tag/../")]
    [InlineData("https://github.com/MadGenius9/MadClean/releases/tag/v0.2.0%2Fasset.exe")]
    public void ReviewLinksCannotBroadenHostRepositoryOrAction(string url) =>
        Assert.False(MadCleanReleaseSource.IsTrustedReviewUri(new Uri(url)));

    [Fact]
    public async Task UntrustedReleaseReviewLinkCannotBeExposedAsAnAvailableRelease()
    {
        using var handler = Reply(Release(reviewUrl: "https://example.com/download.exe"));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
        Assert.Null(result.ReviewUri);
    }

    [Fact]
    public async Task ReleaseNotesStayBoundedLiteralTextRatherThanExecutableMarkup()
    {
        using var handler = Reply(Release(body: "<script>not executed</script>\u202e\u001b" + new string('x', 20_000)));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.NewerRelease, result.State);
        Assert.StartsWith("<script>not executed</script>", result.Notes);
        Assert.DoesNotContain("\u202e", result.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", result.Notes, StringComparison.Ordinal);
        Assert.True(result.Notes!.Length < 8_250);
        Assert.Contains("[Release notes truncated]", result.Notes);
    }

    [Fact]
    public async Task NonTextReleaseNotesAreUnknownRatherThanTrustedMetadata()
    {
        using var handler = Reply(Release(body: new { unexpected = "object" }));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
    }

    [Fact]
    public async Task DeclaredOversizedResponseIsNotParsed()
    {
        using var handler = Reply(Release(body: new string('x', 270_000)));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
        Assert.Null(result.ReviewUri);
    }

    [Fact]
    public async Task UndeclaredOversizedResponseIsBoundedWhileReading()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnmeasuredContent(Encoding.UTF8.GetBytes(Release(body: new string('x', 270_000))))
        }));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Unknown, result.State);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToCurrentOrUnavailable()
    {
        using var source = new CancellationTokenSource();
        using var handler = new FakeHandler((_, token) =>
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation must have been observed");
        });
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, source.Token);
        Assert.Equal(MadCleanReleaseState.Cancelled, result.State);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task PreCancelledCheckMakesNoRequest()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        using var handler = Reply(Release());
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, source.Token);
        Assert.Equal(MadCleanReleaseState.Cancelled, result.State);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task TransportTimeoutIsFailedWithoutExposingRawExceptionText()
    {
        using var handler = new FakeHandler((_, _) => throw new TaskCanceledException("sensitive proxy data"));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Failed, result.State);
        Assert.Contains("timed out", result.Message);
        Assert.DoesNotContain("sensitive", result.Message);
    }

    [Fact]
    public async Task NetworkFailureDoesNotClaimAReleaseResultOrPersistRawError()
    {
        using var handler = new FakeHandler((_, _) => throw new HttpRequestException("sensitive proxy data"));
        using var client = new HttpClient(handler);
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Failed, result.State);
        Assert.DoesNotContain("sensitive", result.Message);
        Assert.Null(result.ReviewUri);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("Proxy-Authorization")]
    public async Task CredentialBearingClientIsRejectedBeforeAnyRequest(string header)
    {
        using var handler = Reply(Release());
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.TryAddWithoutValidation(header, "must-not-send");
        var result = await new MadCleanReleaseChecker(client).CheckAsync(Repository, RunningVersion, default);
        Assert.Equal(MadCleanReleaseState.Failed, result.State);
        Assert.Equal(0, handler.Requests);
    }

    private static FakeHandler Reply(string body) => new((_, _) => Task.FromResult(JsonResponse(body)));
    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class UnmeasuredContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
