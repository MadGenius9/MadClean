namespace MadClean.Windows;

// Fixed development-source fences, not runner authentication, an owned scope proof,
// native qualification, or permission to open a production journal.
internal sealed record ExistingJournalReaderQualificationContext(string? Repository, string? SourceRef,
    string? Event, string? Job, string? OptIn, string? Actions, string? RunnerEnvironment,
    string? SourceSha, string? RunId, string? RunAttempt, string? Workspace, string? WorkingDirectory);

internal static class ExistingJournalReaderQualificationProfile
{
    internal const string Repository = "MadGenius9/MadClean";
    internal const string RecoveryRef = "refs/heads/recovery/validated-20261112";
    internal const string Job = "windows-existing-journal-reader";
    internal const string Flag = "MADCLEAN_EXISTING_READER_QUALIFICATION";

    internal static bool IsCurrentSourceAllowed() => CaptureCurrent() != null;

    internal static ExistingJournalReaderQualificationContext? CaptureCurrent()
    {
        var context = new ExistingJournalReaderQualificationContext(
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"), Environment.GetEnvironmentVariable("GITHUB_REF"),
            Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME"), Environment.GetEnvironmentVariable("GITHUB_JOB"),
            Environment.GetEnvironmentVariable(Flag), Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), Environment.GetEnvironmentVariable("GITHUB_SHA"),
            Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            Environment.GetEnvironmentVariable("GITHUB_WORKSPACE"), Environment.CurrentDirectory);
        return IsAllowed(context) ? context : null;
    }

    internal static bool IsAllowed(ExistingJournalReaderQualificationContext? context)
    {
        if (context is not { Repository: Repository, SourceRef: RecoveryRef, Event: "workflow_dispatch", Job: Job,
                OptIn: "1", Actions: "true", RunnerEnvironment: "github-hosted", SourceSha.Length: 40 } ||
            !context.SourceSha.All(char.IsAsciiHexDigit) || !PositiveDecimal(context.RunId, 32) ||
            !PositiveDecimal(context.RunAttempt, 10) || context.Workspace is not { Length: > 0 and <= 4096 } ||
            context.WorkingDirectory is not { Length: > 0 and <= 4096 }) return false;
        try
        {
            return Path.IsPathFullyQualified(context.Workspace) && Path.IsPathFullyQualified(context.WorkingDirectory) &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.Workspace)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.WorkingDirectory)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        { return false; }
    }

    private static bool PositiveDecimal(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit);
}
