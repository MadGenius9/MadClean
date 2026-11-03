using System.Globalization;
using System.IO;
using System.Text.Json;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

/// <summary>Separate opt-in disposable-CI provider observation. No normal harness invocation or UI injection.</summary>
internal static class UpdateDiscoveryFixture
{
    private const string DirectoryName = "update-discovery-validation";
    private const int MaximumReportBytes = 8192, MaximumApps = 20000, MaximumUpdates = 256;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static Owner? rootedOwner;
    private sealed class Owner
    {
        internal readonly WindowsInventory Inventory = new();
        internal readonly CancellationTokenSource Stop;
        internal readonly Task<WingetQueryObservation> Apps;
        internal readonly Task<CheckResult<SystemUpdate>> Updates;
        internal readonly Task<StructuredWingetProbe.Observation> StructuredWinget;
        internal Task? Cancellation;
        internal bool Completed => Apps.IsCompleted && Updates.IsCompleted && StructuredWinget.IsCompletedSuccessfully &&
            !StructuredWinget.Result.PendingNativeRead && (Cancellation?.IsCompleted ?? true);
        internal Owner(CancellationToken token)
        {
            Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Even Process.Start is owned by these workers: providers execute a synchronous
            // prefix before their first await. A blocked spawn must not block this reporter.
            Apps = Task.Run(() => Inventory.AppsObservedAsync(Stop.Token), CancellationToken.None);
            Updates = Task.Run(() => Inventory.SystemUpdatesAsync(Stop.Token), CancellationToken.None);
            StructuredWinget = Task.Run(() => StructuredWingetProbe.ObserveAsync(Stop.Token), CancellationToken.None);
            _ = Apps.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _ = Updates.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _ = StructuredWinget.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
    private sealed record Context(string SourceCommit, string RunId, int RunAttempt);
    private sealed record Provider(string TaskOutcome, string? ResultState, bool SchemaAccepted, int? ItemCount,
        int? KnownPackageOffers, int? UnknownPackageDeterminations, int? SoftwareOffers, int? DriverOffers,
        int? FirmwareOffers, int? UnclassifiedOffers);
    private sealed record WingetDiagnostics(string Outcome, int? ExitCode, bool? OutputHasContent, string? ParseIssue);
    private sealed record MetadataCoverage(bool Accepted, int? AvailableRows, int? PartialRows, int? UnknownRows);
    private sealed record Observation(int FormatVersion, string SourceCommit, string RunId, int RunAttempt,
        string StartedUtc, string CompletedUtc, bool ObservationWaitExpired, bool WindowsServiceCompletionVerified,
        Provider Apps, Provider SystemUpdates)
    {
        public MetadataCoverage? OfferMetadata { get; init; }
        public WingetDiagnostics? Winget { get; init; }
        public string? StructuredWingetTaskOutcome { get; init; }
        public StructuredWingetProbe.Observation? StructuredWinget { get; init; }
    }

    internal static async Task<int> RunAsync(string repository, CancellationToken token)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || rootedOwner != null) return 1;
            token.ThrowIfCancellationRequested();
            var context = CurrentContext();
            string output = FixedOutput(repository);
            CheckAncestry(output, allowMissing: true);
            Directory.CreateDirectory(output); CheckAncestry(output, allowMissing: false);
            string final = Path.Combine(output, "report.json"), temporary = Path.Combine(output, "report.json.new");
            if (Exists(final) || Exists(temporary)) return 1; // Never reset or overwrite a stale report.
            var started = DateTimeOffset.UtcNow;
            var owner = new Owner(token); rootedOwner = owner;
            bool expired = false;
            try { await Task.WhenAll(owner.Apps, owner.Updates, owner.StructuredWinget).WaitAsync(TimeSpan.FromSeconds(210), token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                expired = true;
                // CancelAsync roots/observes callbacks without blocking this reporter on a
                // synchronous query-host stop callback. No servicing/service process is killed here.
                owner.Cancellation = owner.Stop.CancelAsync();
                try { await Task.WhenAll(owner.Apps, owner.Updates, owner.StructuredWinget, owner.Cancellation).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch (Exception) { /* Typed task outcomes below; never output provider exception text. */ }
            }
            catch (Exception) { /* Both tasks are complete; typed fault outcome below. */ }
            var apps = ObserveApps(owner.Apps); var updates = ObserveUpdates(owner.Updates);
            if (!Valid(apps, true) || !Valid(updates, false)) return 1;
            var diagnostics = ObserveWinget(owner.Apps);
            if (!ValidWinget(diagnostics, apps, legacy: false)) return 1;
            var structuredOutcome = TaskOutcome(owner.StructuredWinget);
            var structured = owner.StructuredWinget.IsCompletedSuccessfully ? owner.StructuredWinget.Result : null;
            if (!ValidStructured(structuredOutcome, structured, legacy: false)) return 1;
            var observed = new Observation(4, context.SourceCommit, context.RunId, context.RunAttempt,
                Stamp(started), Stamp(DateTimeOffset.UtcNow), expired, false, apps, updates) { OfferMetadata = ObserveMetadata(owner.Updates, updates), Winget = diagnostics,
                    StructuredWingetTaskOutcome = structuredOutcome, StructuredWinget = structured };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(observed, JsonOptions);
            if (bytes.Length > MaximumReportBytes) return 1;
            CheckAncestry(output, allowMissing: false);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, final, overwrite: false);
            if (owner.Completed) { owner.Stop.Dispose(); rootedOwner = null; }
            // Pending owner/tasks/CTS deliberately remain rooted. Returning never establishes
            // that Windows Update services or provider background/cache work stopped.
            return Positive(apps) && Positive(updates) && structured is { State: "passed" } && !expired ? 0 : 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or OperationCanceledException or System.Security.SecurityException)
        { return 1; }
    }

    internal static void ReadIntoReport(string repository, string expectedSourceCommit, SmokeReport report)
    {
        const string check = "update-discovery-provider-contract";
        const string metadataCheck = "wua-offer-metadata-contract";
        const string wingetCheck = "winget-query-observation-contract";
        const string structuredCheck = "structured-winget-discovery-contract";
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidDataException();
            var expected = CurrentContext();
            if (expectedSourceCommit != expected.SourceCommit) throw new InvalidDataException();
            string output = FixedOutput(repository); CheckAncestry(output, allowMissing: false);
            string path = Path.Combine(output, "report.json");
            CheckFile(path);
            var guards = SystemToolTrustNative.LockDirectoryChain(output);
            try
            {
            using var handle = SystemToolTrustNative.OpenFile(path);
            SystemToolTrustNative.ValidateAttributes(handle, directory: false);
            SystemToolTrustNative.ValidateFinalPath(handle, path);
            using var stream = new FileStream(handle, FileAccess.Read, 4096, false);
            if (stream.Length is <= 0 or > MaximumReportBytes) throw new InvalidDataException();
            byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!root.TryGetProperty("formatVersion", out var versionJson) || !versionJson.TryGetInt32(out int version) || version is not (2 or 3 or 4)) throw new InvalidDataException();
            string[] baseFields = ["formatVersion", "sourceCommit", "runId", "runAttempt", "startedUtc", "completedUtc",
                "observationWaitExpired", "windowsServiceCompletionVerified", "apps", "systemUpdates", "offerMetadata"];
            string[] versionFields = version switch
            {
                4 => [.. baseFields, "winget", "structuredWingetTaskOutcome", "structuredWinget"],
                3 => [.. baseFields, "winget"],
                _ => baseFields
            };
            if (!Fields(root, versionFields)) throw new InvalidDataException();
            if (version >= 3)
            {
                var wingetJson = root.GetProperty("winget");
                if (wingetJson.ValueKind != JsonValueKind.Null && !Fields(wingetJson, ["outcome", "exitCode", "outputHasContent", "parseIssue"])) throw new InvalidDataException();
            }
            if (version == 4)
            {
                var structuredJson = root.GetProperty("structuredWinget");
                if (structuredJson.ValueKind != JsonValueKind.Null && !StructuredWingetProbe.ValidJson(structuredJson))
                    throw new InvalidDataException();
            }
            string[] providerFields = ["taskOutcome", "resultState", "schemaAccepted", "itemCount", "knownPackageOffers", "unknownPackageDeterminations",
                "softwareOffers", "driverOffers", "firmwareOffers", "unclassifiedOffers"];
            if (!Fields(root.GetProperty("apps"), providerFields) || !Fields(root.GetProperty("systemUpdates"), providerFields)) throw new InvalidDataException();
            var metadataJson = root.GetProperty("offerMetadata");
            if (metadataJson.ValueKind != JsonValueKind.Null && !Fields(metadataJson, ["accepted", "availableRows", "partialRows", "unknownRows"])) throw new InvalidDataException();
            var data = JsonSerializer.Deserialize<Observation>(bytes, JsonOptions) ?? throw new InvalidDataException();
            if (data.FormatVersion != version || data.SourceCommit != expected.SourceCommit || data.RunId != expected.RunId || data.RunAttempt != expected.RunAttempt ||
                data.WindowsServiceCompletionVerified || !Date(data.StartedUtc, out var start) || !Date(data.CompletedUtc, out var end) || start > end ||
                end > DateTimeOffset.UtcNow || end - start > TimeSpan.FromMinutes(4) || DateTimeOffset.UtcNow - end > TimeSpan.FromMinutes(10) ||
                !Valid(data.Apps, true) || !Valid(data.SystemUpdates, false) ||
                !ValidStructured(data.StructuredWingetTaskOutcome, data.StructuredWinget, legacy: version < 4) ||
                data.StructuredWinget is { } sdkTime && (sdkTime.StartedUtc < start || sdkTime.CompletedUtc > end) || !ValidWinget(data.Winget, data.Apps, legacy: version == 2) || !ValidMetadata(data.OfferMetadata, data.SystemUpdates)) throw new InvalidDataException();
            string detail = $"WinGet task={data.Apps.TaskOutcome}/state={data.Apps.ResultState ?? "not returned"}/schema={data.Apps.SchemaAccepted}/rows={Count(data.Apps.ItemCount)}/knownOfferFields={Count(data.Apps.KnownPackageOffers)}/unknownDeterminations={Count(data.Apps.UnknownPackageDeterminations)}; " +
                $"WUA task={data.SystemUpdates.TaskOutcome}/state={data.SystemUpdates.ResultState ?? "not returned"}/schema={data.SystemUpdates.SchemaAccepted}/rows={Count(data.SystemUpdates.ItemCount)}/software={Count(data.SystemUpdates.SoftwareOffers)}/drivers={Count(data.SystemUpdates.DriverOffers)}/firmware={Count(data.SystemUpdates.FirmwareOffers)}/unclassified={Count(data.SystemUpdates.UnclassifiedOffers)}; waitExpired={data.ObservationWaitExpired}. " +
                "Read-only configured-source metadata only; zero offers is not up to date, task completion is not Windows-service completion, and package/source labels are not authenticity or action permission. Populated UI selection is not validated. No raw values, provider messages, source identifiers or captures exported.";
            if (Positive(data.Apps) && Positive(data.SystemUpdates) && !data.ObservationWaitExpired) report.Pass(check, detail);
            else report.Unknown(check, detail);
            string wingetDetail = data.Winget is { } observedWinget
                ? $"outcome={observedWinget.Outcome}; processResultReturned={observedWinget.Outcome is nameof(WingetQueryOutcome.ParsedOutput) or nameof(WingetQueryOutcome.NonzeroExit)}; exitCode={observedWinget.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unobserved"}; stdoutHasNonWhitespace={observedWinget.OutputHasContent?.ToString() ?? "unobserved"}; parserIssue={observedWinget.ParseIssue ?? "unobserved"}; sourceState={data.Apps.ResultState ?? "unreturned"}. Same single fixed WinGet list call only. Header/layout rejection does not establish localization, source agreements, network failure or another cause. Stdout observations say nothing about stderr; absent process result does not prove no process started. No raw output, names, identifiers, versions, messages or source labels exported; diagnostic compatibility is not source success or an up-to-date verdict."
                : "No typed same-query WinGet diagnostics returned (legacy, cancelled, faulted or pending observation). No second query, guessed cause or update verdict substituted.";
            if (wingetDetail.Length > 1000)
                report.Unknown(wingetCheck, "The typed WinGet diagnostic summary exceeded its export bound; no complete process/parser observation is claimed.");
            else if (data.Winget is { Outcome: nameof(WingetQueryOutcome.ParsedOutput) or nameof(WingetQueryOutcome.NonzeroExit) } && !data.ObservationWaitExpired)
                report.Pass(wingetCheck, wingetDetail);
            else report.Unknown(wingetCheck, wingetDetail);
            string sdkDetail = data.StructuredWinget is { } sdk
                ? $"task={data.StructuredWingetTaskOutcome}; preCancellationVerified={sdk.PreCancellationVerified}; schema={sdk.ResultSchemaAccepted}; result={sdk.ResultState ?? "unobserved"}; rows={Count(sdk.ItemCount)}; availableRows={Count(sdk.AvailableRows)}; unknownRows={Count(sdk.UnknownRows)}; pendingNativeRead={sdk.PendingNativeRead}; stage={sdk.Diagnostic?.Stage ?? "unobserved"}; outcome={sdk.Diagnostic?.Outcome ?? "unobserved"}. Fixed read-only SDK source scope only. Empty results do not establish all software is up to date. Native-query completion is not shared-server or metadata-refresh completion. No names, IDs, versions, paths or provider messages exported."
                : $"task={data.StructuredWingetTaskOutcome ?? "legacy-unobserved"}; no returned source-bound structured SDK observation. Pending, offline and unavailable checks remain unknown; no source success, all-software coverage or up-to-date verdict is substituted.";
            if (data.StructuredWinget is { State: "failed" }) report.Fail(structuredCheck, sdkDetail);
            else if (data.StructuredWinget is { State: "passed" } && !data.ObservationWaitExpired) report.Pass(structuredCheck, sdkDetail);
            else report.Unknown(structuredCheck, sdkDetail);
            string metadataDetail = $"WUA source={data.SystemUpdates.ResultState ?? "not returned"}; accepted={data.OfferMetadata?.Accepted}; availableRows={Count(data.OfferMetadata?.AvailableRows)}; partialRows={Count(data.OfferMetadata?.PartialRows)}; unknownRows={Count(data.OfferMetadata?.UnknownRows)}. Actual optional provider metadata only; no values or identifiers exported. Software offers can qualify this shared projection; driver/firmware fields and populated selection require their own runtime coverage. Declarations are not compatibility, OEM prerequisites, elevation, installation or restart permission.";
            if (Positive(data.SystemUpdates) && data.OfferMetadata is { Accepted: true, AvailableRows: > 0 } && !data.ObservationWaitExpired) report.Pass(metadataCheck, metadataDetail);
            else report.Unknown(metadataCheck, metadataDetail);
            }
            finally { foreach (var guard in guards.AsEnumerable().Reverse()) guard.Dispose(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            JsonException or FormatException or System.Security.SecurityException or SystemToolTrustFailure)
        {
            report.Unknown(check, "Separate online source evidence is absent, stale, malformed, pending or mismatched to this source/run/attempt. No online query, report reset, synthetic offer or successful check was substituted.");
            report.Unknown(metadataCheck, "No accepted source-bound optional WUA metadata observation is available; no query or synthetic metadata was substituted.");
            report.Unknown(wingetCheck, "No accepted source-bound typed WinGet observation is available; no provider message parsing, second query or failure-cause guess was substituted.");
            report.Unknown(structuredCheck, "No accepted source-bound structured WinGet observation is available; no query, source success or up-to-date verdict was substituted.");
        }
    }

    private static string TaskOutcome(Task task) => task.IsCompletedSuccessfully ? "Returned" : task.IsCanceled ? "Cancelled" : task.IsFaulted ? "Faulted" : "Pending";
    private static bool ValidStructured(string? outcome, StructuredWingetProbe.Observation? observation, bool legacy)
    {
        if (legacy) return outcome is null && observation is null;
        if (outcome is not ("Returned" or "Cancelled" or "Faulted" or "Pending")) return false;
        return outcome == "Returned" ? observation is not null && StructuredWingetProbe.Valid(observation) : observation is null;
    }

    private static Provider ObserveApps(Task<WingetQueryObservation> task)
    {
        if (!task.IsCompletedSuccessfully) return Unreturned(task);
        var result = task.Result.Result;
        bool state = Enum.IsDefined(result.State);
        if (result.Items == null || result.Items.Count > MaximumApps)
            return new("Returned", state ? result.State.ToString() : "Unknown", false, null, null, null, null, null, null, null);
        bool schema = state && (result.State is CheckState.Available or CheckState.Partial || result.Items.Count == 0);
        int known = 0, unknown = 0;
        foreach (var row in result.Items)
        {
            if (row == null || row.Status is not (CheckState.Available or CheckState.Unknown)) { schema = false; break; }
            if (row.Status == CheckState.Available) known++; else unknown++;
        }
        return new("Returned", state ? result.State.ToString() : "Unknown", schema, result.Items.Count,
            schema ? known : null, schema ? unknown : null, null, null, null, null);
    }
    private static WingetDiagnostics? ObserveWinget(Task<WingetQueryObservation> task)
    {
        if (!task.IsCompletedSuccessfully) return null;
        var observation = task.Result;
        var diagnostic = observation.Diagnostics;
        if (observation.Result.Items == null || diagnostic == null ||
            !diagnostic.IsConsistent(observation.Result.State, observation.Result.Items.Count)) throw new InvalidDataException();
        return new(diagnostic.Outcome.ToString(), diagnostic.ExitCode, diagnostic.OutputHasContent, diagnostic.ParseIssue?.ToString());
    }
    private static bool ValidWinget(WingetDiagnostics? value, Provider provider, bool legacy)
    {
        if (provider.TaskOutcome != "Returned") return value == null;
        if (legacy) return value == null;
        if (value == null || !provider.SchemaAccepted || !provider.ItemCount.HasValue ||
            !Enum.TryParse<WingetQueryOutcome>(value.Outcome, false, out var outcome) || !Enum.IsDefined(outcome) || outcome.ToString() != value.Outcome ||
            !Enum.TryParse<CheckState>(provider.ResultState, false, out var state) || !Enum.IsDefined(state)) return false;
        WingetParseIssue? issue = null;
        if (value.ParseIssue != null)
        {
            if (!Enum.TryParse<WingetParseIssue>(value.ParseIssue, false, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != value.ParseIssue) return false;
            issue = parsed;
        }
        return new WingetQueryDiagnostics(outcome, value.ExitCode, value.OutputHasContent, issue).IsConsistent(state, provider.ItemCount.Value);
    }
    private static Provider ObserveUpdates(Task<CheckResult<SystemUpdate>> task)
    {
        if (!task.IsCompletedSuccessfully) return Unreturned(task);
        var result = task.Result;
        bool state = Enum.IsDefined(result.State);
        if (result.Items == null || result.Items.Count > MaximumUpdates)
            return new("Returned", state ? result.State.ToString() : "Unknown", false, null, null, null, null, null, null, null);
        bool schema = state && (result.State is CheckState.Available or CheckState.Partial || result.Items.Count == 0);
        int software = 0, driver = 0, firmware = 0, unclassified = 0;
        foreach (var row in result.Items)
        {
            if (row == null) { schema = false; break; }
            switch (row.Kind)
            { case "Software": software++; break; case "Driver": driver++; break; case "Firmware": firmware++; break; case "Unknown": unclassified++; break; default: schema = false; break; }
        }
        return new("Returned", state ? result.State.ToString() : "Unknown", schema, result.Items.Count, null, null,
            schema ? software : null, schema ? driver : null, schema ? firmware : null, schema ? unclassified : null);
    }
    private static MetadataCoverage? ObserveMetadata(Task<CheckResult<SystemUpdate>> task, Provider provider)
    {
        if (!task.IsCompletedSuccessfully || provider.TaskOutcome != "Returned" || !provider.SchemaAccepted) return null;
        int available = 0, partial = 0, unknown = 0;
        foreach (var row in task.Result.Items)
        {
            if (row.OfferMetadata == null) { unknown++; continue; }
            int type = row.Kind is "Driver" or "Firmware" ? 2 : row.Kind == "Software" ? 1 : 0;
            if (!WindowsUpdateOfferMetadataParser.IsConsistent(row.OfferMetadata, type)) return new(false, null, null, null);
            switch (row.OfferMetadata.State)
            {
                case CheckState.Available: available++; break;
                case CheckState.Partial: partial++; break;
                case CheckState.Unknown: unknown++; break;
                default: return new(false, null, null, null);
            }
        }
        return new(true, available, partial, unknown);
    }
    private static bool ValidMetadata(MetadataCoverage? metadata, Provider provider)
    {
        if (provider.TaskOutcome != "Returned" || !provider.SchemaAccepted) return metadata == null;
        if (metadata == null) return false;
        int?[] counts = [metadata.AvailableRows, metadata.PartialRows, metadata.UnknownRows];
        if (!metadata.Accepted) return counts.All(value => value == null);
        return counts.All(value => value is >= 0 and <= 256) && metadata.AvailableRows + metadata.PartialRows + metadata.UnknownRows == provider.ItemCount;
    }

    private static Provider Unreturned(Task task) => new(task.IsCanceled ? "Cancelled" : task.IsFaulted ? "Faulted" : "Pending",
        null, false, null, null, null, null, null, null, null);
    private static bool Positive(Provider value) => value.TaskOutcome == "Returned" && value.SchemaAccepted && (value.ResultState is "Available" or "Partial");
    private static bool Valid(Provider value, bool apps)
    {
        if (value == null || value.TaskOutcome is not ("Returned" or "Cancelled" or "Faulted" or "Pending")) return false;
        int?[] all = [value.ItemCount, value.KnownPackageOffers, value.UnknownPackageDeterminations, value.SoftwareOffers, value.DriverOffers, value.FirmwareOffers, value.UnclassifiedOffers];
        if (value.TaskOutcome != "Returned") return value.ResultState == null && !value.SchemaAccepted && all.All(number => number == null);
        if (!Enum.TryParse<CheckState>(value.ResultState, false, out var state) || !Enum.IsDefined(state) || state.ToString() != value.ResultState ||
            all.Any(number => number is < 0 || number > (apps ? MaximumApps : MaximumUpdates))) return false;
        if (!value.SchemaAccepted) return (apps ? value.KnownPackageOffers == null && value.UnknownPackageDeterminations == null : value.SoftwareOffers == null && value.DriverOffers == null && value.FirmwareOffers == null && value.UnclassifiedOffers == null) &&
            (apps ? value.SoftwareOffers == null && value.DriverOffers == null && value.FirmwareOffers == null && value.UnclassifiedOffers == null : value.KnownPackageOffers == null && value.UnknownPackageDeterminations == null);
        if (value.ItemCount == null || state is not (CheckState.Available or CheckState.Partial) && value.ItemCount != 0) return false;
        if (apps && (state is CheckState.Available or CheckState.Partial) && value.ItemCount == 0) return false;
        if (!apps && state == CheckState.Available && value.UnclassifiedOffers > 0) return false;
        return apps ? value.KnownPackageOffers.HasValue && value.UnknownPackageDeterminations.HasValue && value.KnownPackageOffers + value.UnknownPackageDeterminations == value.ItemCount &&
                value.SoftwareOffers == null && value.DriverOffers == null && value.FirmwareOffers == null && value.UnclassifiedOffers == null
            : value.KnownPackageOffers == null && value.UnknownPackageDeterminations == null && value.SoftwareOffers.HasValue && value.DriverOffers.HasValue && value.FirmwareOffers.HasValue && value.UnclassifiedOffers.HasValue &&
                value.SoftwareOffers + value.DriverOffers + value.FirmwareOffers + value.UnclassifiedOffers == value.ItemCount;
    }
    private static Context CurrentContext()
    {
        string? source = Environment.GetEnvironmentVariable("GITHUB_SHA"), run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        if (source is not { Length: 40 } || source.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            run is not { Length: > 0 and <= 32 } || run[0] == '0' || run.Any(value => value is < '0' or > '9') ||
            attempt == null || attempt.Length > 9 || !int.TryParse(attempt, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0 || number.ToString(CultureInfo.InvariantCulture) != attempt)
            throw new InvalidDataException();
        return new(source, run, number);
    }
    private static string FixedOutput(string repository)
    {
        string root = Path.GetFullPath(repository);
        if (root != Path.GetFullPath(Environment.CurrentDirectory) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException();
        CheckAncestry(root, false); CheckFile(Path.Combine(root, "global.json"));
        CheckAncestry(Path.Combine(root, "src", "MadClean.App"), false); CheckFile(Path.Combine(root, "src", "MadClean.App", "MadClean.App.csproj"));
        string? drive = Path.GetPathRoot(root);
        if (drive == null || new DriveInfo(drive).DriveType != DriveType.Fixed) throw new InvalidDataException();
        return Path.Combine(root, "artifacts", DirectoryName);
    }
    private static bool Exists(string path)
    { try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static void CheckFile(string path)
    { var attributes = File.GetAttributes(path); if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new InvalidDataException(); }
    private static void CheckAncestry(string path, bool allowMissing)
    {
        List<string> chain = []; string? current = path;
        while (current != null) { chain.Add(current); current = Path.GetDirectoryName(current); }
        foreach (string entry in chain.AsEnumerable().Reverse())
        {
            try { var flags = File.GetAttributes(entry); if ((flags & FileAttributes.ReparsePoint) != 0 || (flags & FileAttributes.Directory) == 0) throw new InvalidDataException(); }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }
    private static bool Fields(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
        return seen.Count == names.Length;
    }
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static bool Date(string value, out DateTimeOffset date) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) && date != default && date.Offset == TimeSpan.Zero && Stamp(date) == value;
    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "not observed";
}
