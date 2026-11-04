using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MadClean.WindowsSmoke;

/// <summary>Separate actual published-app read-only query; never called by passive smoke.</summary>
internal static class PublishedAppUpdateQualification
{
    private const string DirectoryName = "published-app-update-validation";
    private const int MaximumBytes = 16384;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private sealed record Context(string SourceCommit, string RunId, int RunAttempt);
    private sealed record Observation(int FormatVersion, string SourceCommit, string RunId, int RunAttempt,
        DateTimeOffset StartedUtc, DateTimeOffset ObservedUtc, string State,
        bool DescriptorValidated, bool ExecutableLeased, bool? MetadataSidecarLeased, bool ProcessStarted,
        bool OwnedWindowObserved, bool NeutralOverviewObserved, bool OverviewNoDataObserved,
        bool OwnedBundledRuntimeObserved, int? RequestedUpdateCommandInvocations,
        bool? UpdateCommandInvoked, bool? ResultObserved, bool? FreshResultObserved, bool? QueryOwnershipSettled,
        string? ResultState, int? ItemCount, int? AvailableRows, int? UnknownRows, DateTimeOffset? CheckedUtc,
        bool CloseRequested, bool? ExitObserved, int? ExitCode, bool CleanupConfirmed, bool RequiresRetention,
        string Stage, int? NativeError);

    internal static async Task<int> RunAsync(string repository)
    {
        try
        {
            var context = Current(repository);
            string output = Path.Combine(repository, "artifacts", DirectoryName);
            CheckAncestry(output, true);
            Directory.CreateDirectory(output); CheckAncestry(output, false);
            string final = Path.Combine(output, "report.json"), temporary = final + ".new";
            if (Exists(final) || Exists(temporary)) return 1;
            var started = DateTimeOffset.UtcNow;
            var observed = await PublishedExecutableStartupFixture.RunAppUpdateQueryAsync(
                repository, context.SourceCommit, CancellationToken.None).ConfigureAwait(false);
            string state = observed.State == PublishedExecutableStartupState.Observed ? "passed"
                : observed.State == PublishedExecutableStartupState.Failed ? "failed" : "inconclusive";
            var value = new Observation(1, context.SourceCommit, context.RunId, context.RunAttempt, started,
                DateTimeOffset.UtcNow, state, observed.DescriptorValidated, observed.ExecutableLeased,
                observed.MetadataSidecarLeased, observed.ProcessStarted, observed.OwnedWindowObserved,
                observed.NeutralOverviewObserved, observed.OverviewNoDataObserved, observed.OwnedBundledRuntimeObserved,
                observed.RequestedUpdateCommandInvocations, observed.AppUpdateCommandInvoked,
                observed.AppUpdateResultObserved, observed.AppUpdateFreshResult, observed.AppUpdateOwnershipSettled,
                observed.AppUpdateResultState, observed.AppUpdateItemCount, observed.AppUpdateAvailableRows,
                observed.AppUpdateUnknownRows, observed.AppUpdateCheckedUtc, observed.CloseRequested,
                observed.ExitObserved, observed.ExitCode, observed.CleanupConfirmed,
                PublishedExecutableStartupFixture.HasPendingStartup || PublishedExecutableStartupFixture.RequiresRetention,
                observed.Stage, observed.NativeError);
            if (!Valid(value)) return 1;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > MaximumBytes) return 1;
            CheckAncestry(output, false);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, final, false);
            return state == "passed" ? 0 : state == "failed" ? 1 : 2;
        }
        catch (Exception) { return 1; }
    }

    private static bool Valid(Observation value)
    {
        if (value.FormatVersion != 1 || value.StartedUtc == default || value.ObservedUtc < value.StartedUtc ||
            value.StartedUtc.Offset != TimeSpan.Zero || value.ObservedUtc.Offset != TimeSpan.Zero ||
            value.State is not ("passed" or "failed" or "inconclusive") ||
            value.Stage.Length is < 1 or > 100 || value.Stage.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            value.RequestedUpdateCommandInvocations is < 0 or > 1 ||
            value.ExitCode is not null && value.ExitObserved != true ||
            value.ProcessStarted && (!value.DescriptorValidated || !value.ExecutableLeased || value.MetadataSidecarLeased != true) ||
            value.UpdateCommandInvoked == true && (!value.ProcessStarted || !value.OwnedWindowObserved ||
                !value.NeutralOverviewObserved || !value.OverviewNoDataObserved || !value.OwnedBundledRuntimeObserved ||
                value.RequestedUpdateCommandInvocations != 1) ||
            value.ResultObserved == true && value.UpdateCommandInvoked != true ||
            value.FreshResultObserved == true && value.ResultObserved != true ||
            value.QueryOwnershipSettled == true && value.ResultObserved != true ||
            value.CleanupConfirmed && (value.RequiresRetention || value.ProcessStarted && value.ExitObserved != true)) return false;
        if (value.ResultState is not null && value.ResultState is not
            ("Available" or "Partial" or "Failed" or "Unknown" or "Unavailable" or "Unsupported" or "Cancelled")) return false;
        if ((value.CheckedUtc is not null) != (value.ResultObserved == true) ||
            (value.ResultState is not null) != (value.ResultObserved == true)) return false;
        if (value.CheckedUtc is { } stamp && (stamp.Offset != TimeSpan.Zero || stamp < value.StartedUtc || stamp > value.ObservedUtc)) return false;
        if (value.ResultState is "Available" or "Partial")
        {
            if (value.ItemCount is null or < 0 or > 256 || value.AvailableRows is null or < 0 or > 256 ||
                value.UnknownRows is null or < 0 or > 256 || value.AvailableRows + value.UnknownRows != value.ItemCount) return false;
        }
        else if (value.ItemCount is not null || value.AvailableRows is not null || value.UnknownRows is not null) return false;
        bool qualified = value.DescriptorValidated && value.ExecutableLeased && value.MetadataSidecarLeased == true &&
            value.ProcessStarted && value.OwnedWindowObserved && value.NeutralOverviewObserved && value.OverviewNoDataObserved &&
            value.OwnedBundledRuntimeObserved && value.RequestedUpdateCommandInvocations == 1 && value.UpdateCommandInvoked == true &&
            value.ResultObserved == true && value.FreshResultObserved == true && value.QueryOwnershipSettled == true &&
            value.ResultState is "Available" or "Partial" && value.CheckedUtc is not null && value.CloseRequested &&
            value.ExitObserved == true && value.ExitCode == 0 && value.CleanupConfirmed && !value.RequiresRetention && value.NativeError is null;
        // Later observation/close failures may retain positive historical facts.
        // They remain unqualified rather than being promoted or discarded.
        return value.State != "passed" || qualified;
    }

    private static Context Current(string repository)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !Environment.Is64BitProcess ||
            repository != Path.GetFullPath(repository) || repository != Path.GetFullPath(Environment.CurrentDirectory) ||
            repository.StartsWith(@"\\", StringComparison.Ordinal) ||
            Environment.GetEnvironmentVariable("MADCLEAN_WINDOWS_VALIDATION") != "1" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "MadGenius9/MadClean" ||
            Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/feat/phase-one-foundation" ||
            Environment.GetEnvironmentVariable("GITHUB_JOB") != "windows-app-update-published") throw new InvalidDataException();
        string source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "";
        string run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "";
        string attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "";
        if (source.Length != 40 || source.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            run.Length is < 1 or > 32 || run[0] == '0' || run.Any(c => c is < '0' or > '9') ||
            attempt.Length is < 1 or > 9 || !int.TryParse(attempt, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
            number <= 0 || number.ToString(CultureInfo.InvariantCulture) != attempt) throw new InvalidDataException();
        CheckAncestry(repository, false);
        foreach (string path in new[] { Path.Combine(repository, "MadClean.sln"), Path.Combine(repository, "global.json") })
        { var attributes = File.GetAttributes(path); if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new InvalidDataException(); }
        return new(source, run, number);
    }

    private static bool Exists(string path)
    { try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static void CheckAncestry(string path, bool allowMissing)
    {
        var chain = new Stack<string>();
        for (string? current = path; current != null; current = Path.GetDirectoryName(current)) chain.Push(current);
        while (chain.TryPop(out var current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }
}
