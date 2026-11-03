using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

/// <summary>Actual owned published harness query. Never launches from passive validation.</summary>
internal static class StructuredWingetPublishedQualification
{
    private const string DescriptorName = "structured-winget-published-descriptor.json";
    private const string SummaryName = "structured-winget-published-observation.json";
    private const string Check = "structured-winget-published-query-contract";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static readonly object Gate = new();
    private static Task<Summary>? parentOwner;
    private static readonly List<object> Retained = [];
    private sealed record Context(string SourceCommit, string RunId, int RunAttempt);
    private sealed record Descriptor(string SourceCommit, string RunId, int RunAttempt, string ScopeId, string OwnerMarker);
    private sealed record Child(string SourceCommit, string RunId, int RunAttempt, string ScopeId, string OwnerMarker,
        bool OwnExecutableObserved, bool PendingNativeReadAtExit, StructuredWingetProbe.Observation Observation);
    private sealed record Summary(int FormatVersion, string SourceCommit, string RunId, int RunAttempt,
        DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, string State, bool DescriptorValidated,
        bool ExecutableLeased, bool LaunchAttempted, bool ProcessStarted, bool? ExitObserved, int? ExitCode,
        bool RequiresRetention, bool ChildReportValidated, StructuredWingetProbe.Observation? Observation);

    internal static async Task<int> RunParentAsync(string repository, CancellationToken token)
    {
        Context context;
        try { context = Current(repository); }
        catch (Exception) { return 1; }
        if (token.IsCancellationRequested) return 2;
        Task<Summary> task;
        lock (Gate)
        {
            if (parentOwner is not null) return 2;
            parentOwner = Task.Run(() => Parent(repository, context), CancellationToken.None);
            task = parentOwner;
        }
        Summary summary;
        try { summary = await task.WaitAsync(TimeSpan.FromSeconds(150), token).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            // Actual process owner continues waiting; never kill or drop its leases.
            // Main must await WaitForParentCompletionAsync before allowing the CLR to exit.
            // No summary is fabricated: launch/exit facts belong to the still-running
            // owner. A missing final report remains Unknown in passive publication.
            return 2;
        }
        catch (Exception) { return 1; }
        try
        {
            using var output = new Scope(repository, context);
            WriteNew(Path.Combine(repository, "artifacts", SummaryName), summary);
        }
        catch (Exception) { return 1; }
        return summary.State == "passed" ? 0 : summary.State == "failed" ? 1 : 2;
    }

    internal static async Task WaitForParentCompletionAsync()
    {
        Task<Summary>? owner;
        lock (Gate) owner = parentOwner;
        if (owner is not null) try { await owner.ConfigureAwait(false); } catch (Exception) { }
    }

    private static Summary Parent(string repository, Context context)
    {
        var start = DateTimeOffset.UtcNow;
        Scope? scope = null;
        Process? process = null;
        bool descriptor = false, leased = false, launch = false, started = false, retained = false, childValid = false;
        bool? exit = null;
        int? code = null;
        StructuredWingetProbe.Observation? observation = null;
        string state = "inconclusive";
        try
        {
            scope = new Scope(repository, context);
            descriptor = true;
            scope.LeaseExecutable(); leased = true;
            if (Directory.EnumerateFileSystemEntries(scope.Cache).Any() || File.Exists(scope.ChildPath)) throw new InvalidDataException();
            var info = new ProcessStartInfo(scope.Executable) { UseShellExecute = false, WorkingDirectory = repository, CreateNoWindow = true };
            info.ArgumentList.Add("--structured-winget-published-validation");
            info.Environment.Clear();
            foreach (string name in new[] { "SystemRoot", "WINDIR", "SystemDrive", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
                "PROGRAMDATA", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS" })
                if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
            foreach (string name in new[] { "GITHUB_SHA", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_ACTIONS", "GITHUB_REPOSITORY", "GITHUB_REF", "GITHUB_JOB" })
                info.Environment[name] = Environment.GetEnvironmentVariable(name)!;
            info.Environment["MADCLEAN_WINDOWS_VALIDATION"] = "1";
            info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = scope.Cache;
            process = new Process { StartInfo = info };
            launch = true;
            if (!process.Start()) throw new InvalidOperationException();
            started = true;
            _ = process.SafeHandle; // Keep the handle of this exact spawn; no PID reopening.
            process.WaitForExit(); // No timeout/kill; caller wait and actual process lifetime are separate.
            exit = true; code = process.ExitCode;
            using var document = JsonDocument.Parse(scope.Read(scope.ChildPath, 16384));
            var element = document.RootElement;
            if (!Fields(element, ["sourceCommit", "runId", "runAttempt", "scopeId", "ownerMarker", "ownExecutableObserved",
                "pendingNativeReadAtExit", "observation"]) || !StructuredWingetProbe.ValidJson(element.GetProperty("observation"))) throw new InvalidDataException();
            var child = element.Deserialize<Child>(Json) ?? throw new InvalidDataException();
            if (child.SourceCommit != context.SourceCommit || child.RunId != context.RunId || child.RunAttempt != context.RunAttempt ||
                child.ScopeId != scope.Descriptor.ScopeId || child.OwnerMarker != scope.Descriptor.OwnerMarker ||
                !child.OwnExecutableObserved || child.PendingNativeReadAtExit) throw new InvalidDataException();
            if (child.Observation.StartedUtc < start || child.Observation.CompletedUtc > DateTimeOffset.UtcNow) throw new InvalidDataException();
            observation = child.Observation; childValid = true;
            int expected = observation.State == "passed" ? 0 : observation.State == "failed" ? 1 : 2;
            if (code != expected) { state = "failed"; }
            else state = observation.State;
        }
        catch (Exception)
        {
            // No raw exception/provider data is exported. A failed apphost start or missing
            // source-bound report never becomes a successful projection/query qualification.
            if (launch && !started || started && exit != true) retained = true;
        }
        finally
        {
            if (started && exit != true && process is not null)
            {
                // An observation error does not authorize dropping the actual spawned
                // process. Keep Main's owner alive until the same owned process exits.
                while (exit != true)
                {
                    try { process.WaitForExit(); code = process.ExitCode; exit = true; retained = false; }
                    catch (Exception) { Thread.Sleep(250); }
                }
            }
            if (retained)
            {
                lock (Retained) { if (scope is not null) Retained.Add(scope); if (process is not null) Retained.Add(process); }
                // A launch exception without a confirmed process association has no safe
                // teardown proof. Keep the console owner alive for the disposable VM;
                // an external job deadline is not an observed process exit.
                while (true) Thread.Sleep(250);
            }
            else { process?.Dispose(); scope?.Dispose(); }
        }
        if (exit == true && code is not null && code is not (0 or 2)) state = "failed";
        return new(1, context.SourceCommit, context.RunId, context.RunAttempt, start, DateTimeOffset.UtcNow,
            state, descriptor, leased, launch, started, exit, code, retained, childValid, observation);
    }

    internal static async Task<int> RunChildAsync(string repository, CancellationToken token)
    {
        try
        {
            var context = Current(repository);
            using var scope = new Scope(repository, context);
            scope.LeaseExecutable();
            if (!string.Equals(Environment.ProcessPath, scope.Executable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"), scope.Cache, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(scope.ChildPath)) return 1;
            StructuredWingetProbe.Observation observation;
            try { observation = await StructuredWingetProbe.ObserveAsync(token).ConfigureAwait(false); }
            finally { await StructuredWingetProbe.WaitForOwnedCompletionAsync().ConfigureAwait(false); }
            if (!StructuredWingetProbe.Valid(observation)) return 1;
            bool pending = new MadClean.Windows.WinGet.StructuredWingetInventory().HasPendingNativeRead;
            if (pending) return 2;
            WriteNew(scope.ChildPath, new Child(context.SourceCommit, context.RunId, context.RunAttempt,
                scope.Descriptor.ScopeId, scope.Descriptor.OwnerMarker, true, pending, observation));
            return observation.State == "passed" ? 0 : observation.State == "failed" ? 1 : 2;
        }
        catch (Exception) { return 1; }
    }

    internal static void ReadIntoReport(string repository, string expectedSourceCommit, SmokeReport report)
    {
        try
        {
            var context = Current(repository);
            if (context.SourceCommit != expectedSourceCommit) throw new InvalidDataException();
            using var scope = new Scope(repository, context);
            using var document = JsonDocument.Parse(scope.Read(Path.Combine(repository, "artifacts", SummaryName), 16384));
            var root = document.RootElement;
            if (!Fields(root, ["formatVersion", "sourceCommit", "runId", "runAttempt", "startedUtc", "completedUtc", "state",
                "descriptorValidated", "executableLeased", "launchAttempted", "processStarted", "exitObserved", "exitCode", "requiresRetention",
                "childReportValidated", "observation"])) throw new InvalidDataException();
            if (root.GetProperty("observation").ValueKind != JsonValueKind.Null && !StructuredWingetProbe.ValidJson(root.GetProperty("observation"))) throw new InvalidDataException();
            var value = root.Deserialize<Summary>(Json) ?? throw new InvalidDataException();
            if (value.FormatVersion != 1 || value.SourceCommit != context.SourceCommit || value.RunId != context.RunId || value.RunAttempt != context.RunAttempt ||
                value.CompletedUtc < value.StartedUtc || value.CompletedUtc > DateTimeOffset.UtcNow || value.CompletedUtc - value.StartedUtc > TimeSpan.FromMinutes(4) ||
                DateTimeOffset.UtcNow - value.CompletedUtc > TimeSpan.FromMinutes(10) || value.State is not ("passed" or "failed" or "inconclusive")) throw new InvalidDataException();
            bool qualified = value.DescriptorValidated && value.ExecutableLeased && value.LaunchAttempted && value.ProcessStarted &&
                value.ExitObserved == true && value.ExitCode == 0 && !value.RequiresRetention && value.ChildReportValidated && value.Observation is { State: "passed" };
            if (value.StartedUtc.Offset != TimeSpan.Zero || value.CompletedUtc.Offset != TimeSpan.Zero ||
                (value.State == "passed") != qualified || value.ChildReportValidated != (value.Observation is not null) ||
                value.ExecutableLeased && !value.DescriptorValidated || value.LaunchAttempted && !value.ExecutableLeased ||
                value.ProcessStarted && !value.LaunchAttempted || value.ExitObserved is not null && !value.ProcessStarted ||
                (value.ExitCode is not null) != (value.ExitObserved == true) ||
                value.Observation is { } observation && (observation.StartedUtc < value.StartedUtc || observation.CompletedUtc > value.CompletedUtc))
                throw new InvalidDataException();
            string detail = $"descriptor={value.DescriptorValidated}; exeLease={value.ExecutableLeased}; launch={value.LaunchAttempted}; started={value.ProcessStarted}; exit={value.ExitObserved}; code={value.ExitCode}; retention={value.RequiresRetention}; child={value.ChildReportValidated}; query={value.Observation?.State ?? "unobserved"}/{value.Observation?.Diagnostic?.Outcome ?? "unobserved"}; pendingAtObservation={value.Observation?.PendingNativeRead}. Actual owned self-contained single-file harness query only; no package values or raw output exported. Owned publish/cache left on disposable VM; no distribution, shared-server exit, fresh catalog, compatibility or up-to-date claim.";
            if (qualified) report.Pass(Check, detail);
            else if (value.State == "failed") report.Fail(Check, detail);
            else report.Unknown(Check, detail);
        }
        catch (Exception) { report.Unknown(Check, "No accepted source/run/attempt-bound published SDK child query and actual owned process exit. Compilation/startup alone do not qualify this path; no query or synthetic result substituted."); }
    }

    private static Context Current(string repository)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !Environment.Is64BitProcess ||
            repository != Path.GetFullPath(repository) || repository != Path.GetFullPath(Environment.CurrentDirectory) || repository.StartsWith(@"\\", StringComparison.Ordinal) ||
            Environment.GetEnvironmentVariable("MADCLEAN_WINDOWS_VALIDATION") != "1" || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "MadGenius9/MadClean" ||
            Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/feat/phase-one-foundation" ||
            Environment.GetEnvironmentVariable("GITHUB_JOB") != "windows-verification") throw new InvalidDataException();
        string source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "", run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "", attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "";
        if (source.Length != 40 || source.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            run.Length is < 1 or > 32 || run[0] == '0' || run.Any(c => c is < '0' or > '9') || attempt.Length is < 1 or > 9 ||
            !int.TryParse(attempt, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0 || number.ToString(CultureInfo.InvariantCulture) != attempt) throw new InvalidDataException();
        return new(source, run, number);
    }

    private sealed class Scope : IDisposable
    {
        private readonly List<IDisposable> leases = [];
        internal Descriptor Descriptor { get; }
        internal string Executable { get; }
        internal string Cache { get; }
        internal string ChildPath { get; }
        internal Scope(string repository, Context context)
        {
            try
            {
                string artifacts = Path.Combine(repository, "artifacts");
                leases.AddRange(SystemToolTrustNative.LockDirectoryChain(artifacts));
                using var document = JsonDocument.Parse(Read(Path.Combine(artifacts, DescriptorName), 512));
                if (!Fields(document.RootElement, ["sourceCommit", "runId", "runAttempt", "scopeId", "ownerMarker"])) throw new InvalidDataException();
                Descriptor = document.RootElement.Deserialize<Descriptor>(Json) ?? throw new InvalidDataException();
                if (Descriptor.SourceCommit != context.SourceCommit || Descriptor.RunId != context.RunId || Descriptor.RunAttempt != context.RunAttempt ||
                    !Guid.TryParseExact(Descriptor.ScopeId, "N", out var id) || id == Guid.Empty || Descriptor.ScopeId != id.ToString("N") ||
                    !Guid.TryParseExact(Descriptor.OwnerMarker, "N", out var marker) || marker == Guid.Empty || Descriptor.OwnerMarker != marker.ToString("N")) throw new InvalidDataException();
                string owned = Path.Combine(artifacts, "structured-winget-published-" + Descriptor.ScopeId);
                leases.AddRange(SystemToolTrustNative.LockDirectoryChain(owned));
                if (!Read(Path.Combine(owned, ".madclean-structured-winget-owner"), 32).AsSpan().SequenceEqual(System.Text.Encoding.ASCII.GetBytes(Descriptor.OwnerMarker))) throw new InvalidDataException();
                string publish = Path.Combine(owned, "publish"); Cache = Path.Combine(owned, "bundle-cache");
                leases.AddRange(SystemToolTrustNative.LockDirectoryChain(publish));
                leases.AddRange(SystemToolTrustNative.LockDirectoryChain(Cache));
                Executable = Path.Combine(publish, "MadClean.WindowsSmoke.exe"); ChildPath = Path.Combine(owned, "child-report.json");
            }
            catch { Dispose(); throw; }
        }
        internal void LeaseExecutable()
        {
            var handle = SystemToolTrustNative.OpenFile(Executable); leases.Add(handle);
            SystemToolTrustNative.ValidateAttributes(handle, false); SystemToolTrustNative.ValidateFinalPath(handle, Executable);
        }
        internal byte[] Read(string path, int maximum)
        {
            var handle = SystemToolTrustNative.OpenFile(path); leases.Add(handle);
            SystemToolTrustNative.ValidateAttributes(handle, false); SystemToolTrustNative.ValidateFinalPath(handle, path);
            var stream = new FileStream(handle, FileAccess.Read, 4096, false); leases.Add(stream);
            if (stream.Length is <= 0 || stream.Length > maximum) throw new InvalidDataException();
            byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException();
            return bytes;
        }
        public void Dispose() { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); leases.Clear(); }
    }
    private static void WriteNew<T>(string path, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 16384) throw new InvalidDataException();
        string temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, false);
    }
    private static bool Fields(JsonElement root, string[] expected)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject()) if (!seen.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) return false;
        return seen.Count == expected.Length;
    }
}
