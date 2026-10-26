using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MadClean.Core;

public enum MadCleanReleaseState
{
    NotConfigured, NotChecked, NewerRelease, Current, Unknown, Unavailable, Failed, Cancelled
}

public sealed record MadCleanReleaseResult(MadCleanReleaseState State, string Message,
    string? Version = null, string? Notes = null, Uri? ReviewUri = null);

public static class MadCleanReleaseSource
{
    public const string TrustedRepository = "MadGenius9/MadClean";

    public static bool IsTrustedReviewUri(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || !uri.IsWellFormedOriginalString() ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;

        // Uri normalizes dot segments and backslashes. Reject ambiguous originals rather than
        // trusting a normalized path that might hide the link the user was actually shown.
        if (uri.OriginalString.Contains('%') || uri.OriginalString.Contains('\\') ||
            uri.OriginalString.Split('/').Any(segment => segment is "." or ".."))
            return false;

        var segments = uri.AbsolutePath.Split('/');
        if (segments.Length is not (4 or 6) || segments[0].Length != 0 ||
            !segments[1].Equals("MadGenius9", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("MadClean", StringComparison.OrdinalIgnoreCase) || segments[3] != "releases")
            return false;

        return segments.Length == 4 || (segments[4] == "tag" && Regex.IsMatch(segments[5],
            @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking));
    }
}

/// <summary>
/// Explicit, anonymous metadata checking only. The caller must supply a dedicated HttpClient
/// with redirects/cookies/default credentials disabled; this class never retrieves release assets.
/// </summary>
public sealed class MadCleanReleaseChecker(HttpClient client)
{
    private const int MaximumResponseBytes = 262_144;
    private const int MaximumNotesCharacters = 8_192;
    private static readonly Uri LatestReleaseEndpoint = new(
        "https://api.github.com/repos/" + MadCleanReleaseSource.TrustedRepository + "/releases/latest");

    public async Task<MadCleanReleaseResult> CheckAsync(string? configuredRepository, Version currentVersion,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(configuredRepository))
            return new(MadCleanReleaseState.NotConfigured, "Update checking not configured");
        if (!configuredRepository.Equals(MadCleanReleaseSource.TrustedRepository, StringComparison.Ordinal))
            return new(MadCleanReleaseState.Unknown, "The configured release repository is not the trusted MadClean repository. No request was made.");
        if (currentVersion is null)
            return new(MadCleanReleaseState.Unknown, "The running MadClean version could not be determined. No request was made.");
        if (HasCredentialHeaders(client))
            return new(MadCleanReleaseState.Failed, "Release checking requires an anonymous client without credential headers. No request was made.");
        if (token.IsCancellationRequested)
            return new(MadCleanReleaseState.Cancelled, "MadClean release check cancelled.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MadClean", "0.1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is Uri actualUri && actualUri != LatestReleaseEndpoint)
                return Unknown("The release response came from an unexpected location; no release was trusted.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(MadCleanReleaseState.Unavailable,
                    "Release information is unavailable: the repository may be private, inaccessible or missing, or it may have no published stable release. Anonymous checking cannot distinguish these cases.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(MadCleanReleaseState.Unavailable,
                    "Release information is unavailable because anonymous access was denied or rate limited. No authentication was attempted.");
            if ((int)response.StatusCode is >= 300 and < 400)
                return new(MadCleanReleaseState.Unavailable,
                    "GitHub requested a redirect. Release checking does not follow redirects; no release was trusted.");
            if (response.StatusCode != HttpStatusCode.OK)
                return new(MadCleanReleaseState.Failed,
                    $"The GitHub release request failed (HTTP {(int)response.StatusCode}). Retry later.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return Unknown("The release response exceeded the metadata size limit; no release was trusted.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var content = new MemoryStream();
            var buffer = new byte[8_192];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (content.Length + read > MaximumResponseBytes)
                    return Unknown("The release response exceeded the metadata size limit; no release was trusted.");
                await content.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return Parse(content.ToArray(), currentVersion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(MadCleanReleaseState.Cancelled, "MadClean release check cancelled."); }
        catch (OperationCanceledException)
        { return new(MadCleanReleaseState.Failed, "The MadClean release request timed out. Retry when GitHub is reachable."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { return new(MadCleanReleaseState.Failed, "The MadClean release request could not complete. Check network access to api.github.com and retry."); }
    }

    private static bool HasCredentialHeaders(HttpClient client) => client.DefaultRequestHeaders.Any(header =>
        header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));

    private static MadCleanReleaseResult Parse(byte[] bytes, Version currentVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var release = document.RootElement;
            if (release.ValueKind != JsonValueKind.Object ||
                release.GetProperty("draft").ValueKind != JsonValueKind.False ||
                release.GetProperty("prerelease").ValueKind != JsonValueKind.False)
                return Unknown("The release response did not describe a published stable release.");
            var tag = release.GetProperty("tag_name").GetString();
            if (!TryStableVersion(tag, out var latest))
                return Unknown("The release tag was not a supported stable numeric version. No current-version claim was made.");
            string? url = release.GetProperty("html_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var reviewUri) || !MadCleanReleaseSource.IsTrustedReviewUri(reviewUri))
                return Unknown("The release response contained an untrusted review link; no release was trusted.");

            string? notes = null;
            if (release.TryGetProperty("body", out var body))
            {
                if (body.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                    return Unknown("The release notes had an unexpected format; no release was trusted.");
                if (body.ValueKind == JsonValueKind.String) notes = PlainNotes(body.GetString()!);
            }

            var installed = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(currentVersion.Build, 0));
            bool newer = latest! > installed;
            return new(newer ? MadCleanReleaseState.NewerRelease : MadCleanReleaseState.Current,
                newer
                    ? "A newer stable MadClean release was returned by the configured repository. Review the release manually."
                    : "No newer stable MadClean release was returned by the configured repository. This does not assess other release channels or installed software.",
                tag, notes, reviewUri);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return Unknown("GitHub returned an unexpected release response; no current-version claim was made."); }
    }

    private static bool TryStableVersion(string? tag, out Version? version)
    {
        version = null;
        if (tag is null || tag.Length > 128 || !Regex.IsMatch(tag,
            @"\A[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)) return false;
        return Version.TryParse(tag.TrimStart('v', 'V'), out version);
    }

    private static string PlainNotes(string notes)
    {
        var text = new StringBuilder(Math.Min(notes.Length, MaximumNotesCharacters));
        bool truncated = false;
        foreach (var rune in notes.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is not ('\n' or '\r' or '\t') &&
                category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (text.Length + rune.Utf16SequenceLength > MaximumNotesCharacters)
            {
                truncated = true;
                break;
            }
            text.Append(rune.ToString());
        }
        if (truncated) text.Append("\n[Release notes truncated]");
        return text.ToString();
    }

    private static MadCleanReleaseResult Unknown(string message) => new(MadCleanReleaseState.Unknown, message);
}
