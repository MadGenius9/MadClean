using System.Text.Json;

namespace MadClean.Core;

public sealed record MadCleanReleaseConfiguration(string? Repository, string? Error)
{
    public const string FileName = "madclean-updates.json";
    public bool IsConfigured => Repository != null && Error == null;

    // Explicit deployment configuration beside the executable. Nothing is enabled or written by default.
    public static MadCleanReleaseConfiguration Load(string applicationDirectory)
    {
        if (!Path.IsPathFullyQualified(applicationDirectory))
            return new(null, "The application configuration location could not be resolved. No release check was made.");
        try
        {
            using var stream = new FileStream(Path.Combine(applicationDirectory, FileName), FileMode.Open, FileAccess.Read, FileShare.Read);
            var data = new byte[4097];
            int length = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            if (length > 4096) return Invalid();
            using var json = JsonDocument.Parse(data.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
            var properties = json.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "repository" || properties[0].Value.ValueKind != JsonValueKind.String)
                return Invalid();
            if (!string.Equals(properties[0].Value.GetString(), MadCleanReleaseSource.TrustedRepository, StringComparison.OrdinalIgnoreCase))
                return Invalid();
            return new(MadCleanReleaseSource.TrustedRepository, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(null, null); }
        catch (JsonException) { return Invalid(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(null, "Release configuration could not be read. Check access to madclean-updates.json; no release check was made."); }
    }
    private static MadCleanReleaseConfiguration Invalid() => new(null,
        "Release configuration is invalid. Use only the documented trusted repository field in madclean-updates.json, without credentials; no release check was made.");
}

// An unavailable recheck must not silently erase a release already confirmed during this session.
public sealed class MadCleanReleaseReminder
{
    public MadCleanReleaseResult? Release { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    // Explicit source-choice changes clear the previous context; failed checks still retain it.
    public void Clear()
    {
        Release = null;
        ConfirmedAt = null;
    }
    public void Apply(MadCleanReleaseResult result, DateTimeOffset now)
    {
        if (result.State == MadCleanReleaseState.NewerRelease && result.ReviewUri != null &&
            MadCleanReleaseSource.IsTrustedReviewUri(result.ReviewUri) && !string.IsNullOrWhiteSpace(result.Version))
        { Release = result; ConfirmedAt = now; }
        else if (result.State == MadCleanReleaseState.Current)
        { Release = null; ConfirmedAt = null; }
    }
}
