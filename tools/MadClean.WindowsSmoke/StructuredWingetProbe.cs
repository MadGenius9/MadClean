using System.Text.Json;
using MadClean.Core;
using MadClean.Windows.WinGet;

namespace MadClean.WindowsSmoke;

/// <summary>Explicit read-only SDK observation; never invoked by ordinary inventory.</summary>
internal static class StructuredWingetProbe
{
    private static readonly object Gate = new();
    private static Owner? rootedOwner;

    internal sealed record Diagnostic(string Stage, string Outcome, int? HResult, bool Attempted,
        DateTimeOffset? StartedUtc, DateTimeOffset? CompletedUtc, int ProjectedCount, int PropertyFailureCount,
        bool? SourceMatched, bool? AgreementsDisabled, bool? AuthenticationNone, bool? ConnectSucceeded,
        bool? FindSucceeded, bool? NativeLimitExceeded, bool? ProjectionLimitExceeded);

    // This is deliberately a new fixed projection, rather than serializing the provider
    // result, its messages, exception objects, or any returned package metadata.
    internal sealed record Observation(int FormatVersion, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
        string State, bool PreCancellationVerified, string TaskOutcome, bool ResultSchemaAccepted,
        string? ResultState, int? ItemCount, int? AvailableRows, int? UnknownRows,
        bool PendingNativeRead, Diagnostic? Diagnostic);

    private sealed class Owner(StructuredWingetInventory inventory, CancellationToken token)
    {
        internal readonly StructuredWingetInventory Inventory = inventory;
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal Task<StructuredWingetObservation>? Query;
        internal Task? StopCallbacks;
    }

    internal static async Task<Observation> ObserveAsync(CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var inventory = new StructuredWingetInventory();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return Empty(started, "Unsupported", false, inventory.HasPendingNativeRead);
        lock (Gate)
        {
            // Never silently replace an earlier timed-out observation or its owned tasks.
            if (rootedOwner is not null) return Empty(started, "Busy", false, inventory.HasPendingNativeRead);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        StructuredWingetObservation pre;
        try { pre = await inventory.ReadObservedAsync(cancelled.Token).ConfigureAwait(false); }
        catch (Exception) { return Empty(started, "PreCancellationFaulted", false, inventory.HasPendingNativeRead); }
        bool preVerified = pre is { Result.State: CheckState.Cancelled, Result.Items.Count: 0,
            Diagnostic.Outcome: StructuredWingetOutcome.Cancelled, Diagnostic.Attempted: false,
            Diagnostic.StartedUtc: null, Diagnostic.CompletedUtc: null } && !inventory.HasPendingNativeRead;
        if (!preVerified || token.IsCancellationRequested)
            return Empty(started, preVerified ? "Cancelled" : "PreCancellationUnverified", preVerified, inventory.HasPendingNativeRead);
        var owner = new Owner(inventory, token);
        lock (Gate)
        {
            if (rootedOwner is not null) { owner.Stop.Dispose(); return Empty(started, "Busy", preVerified, inventory.HasPendingNativeRead); }
            rootedOwner = owner; // Root before scheduling, including a blocked synchronous prefix.
            owner.Query = Task.Run(() => inventory.ReadObservedAsync(owner.Stop.Token), CancellationToken.None);
            _ = owner.Query.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        try
        {
            // The provider has its own bounded caller wait. This additional reporter bound
            // does not terminate, release, or claim cancellation of native/provider work.
            await owner.Query.WaitAsync(TimeSpan.FromSeconds(110), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            owner.StopCallbacks = owner.Stop.CancelAsync();
            _ = owner.StopCallbacks.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception) { /* Export only the fixed task outcome below. */ }
        bool pending = inventory.HasPendingNativeRead;
        Observation observed;
        if (!owner.Query.IsCompletedSuccessfully)
        {
            string outcome = owner.Query.IsFaulted ? "Faulted" : owner.Query.IsCanceled ? "Cancelled" : "Pending";
            _ = owner.Query.Exception; // Observe an already faulted task without exporting exception data.
            observed = Empty(started, outcome, preVerified, pending);
        }
        else observed = Project(started, preVerified, pending, owner.Query.Result);
        // Projected wrappers have normal runtime-managed lifetimes. No exact RCW reference
        // count, shared server exit, background refresh completion or forced release is claimed.
        if (owner.Query.IsCompleted && !pending && (owner.StopCallbacks?.IsCompleted ?? true))
        {
            owner.Stop.Dispose();
            lock (Gate) { if (ReferenceEquals(rootedOwner, owner)) rootedOwner = null; }
        }
        // Unsettled tasks/CTS/reader remain rooted; no fixture teardown is authorized here.
        return observed;
    }

    private static Observation Project(DateTimeOffset started, bool preVerified, bool pending, StructuredWingetObservation observation)
    {
        var end = DateTimeOffset.UtcNow;
        var data = observation.Diagnostic;
        var result = observation.Result;
        bool schema = data is not null && result is not null && Enum.IsDefined(data.Stage) && Enum.IsDefined(data.Outcome) &&
            Enum.IsDefined(result.State) && data.ProjectedCount is >= 0 and <= StructuredWingetPolicy.ResultLimit &&
            data.PropertyFailureCount is >= 0 and <= 4096 && Consistent(data) && result.Items is { Count: <= StructuredWingetPolicy.ResultLimit } &&
            StateMatches(data.Outcome, result.State) &&
            (data.Outcome is StructuredWingetOutcome.Completed or StructuredWingetOutcome.Partial
                ? result.Items.Count == data.ProjectedCount : result.Items.Count == 0) &&
            (data.StartedUtc is null || data.StartedUtc >= started && data.StartedUtc <= end) &&
            (data.CompletedUtc is null || data.CompletedUtc >= started && data.CompletedUtc <= end &&
                (data.StartedUtc is null || data.CompletedUtc >= data.StartedUtc));
        int available = 0, unknown = 0;
        if (schema)
        {
            foreach (var row in result!.Items)
            {
                if (row is null || row.Status is not (CheckState.Available or CheckState.Unknown) ||
                    row.Source != StructuredWingetPolicy.SourceName || !StructuredWingetPolicy.IsKnownText(row.Name) ||
                    !StructuredWingetPolicy.IsKnownText(row.Id) || !StructuredWingetPolicy.IsKnownText(row.InstalledVersion) ||
                    !StructuredWingetPolicy.IsKnownText(row.AvailableVersion)) { schema = false; break; }
                if (row.Status == CheckState.Available) available++; else unknown++;
            }
        }
        bool completed = schema && preVerified && !pending && data is { Attempted: true, Stage: StructuredWingetStage.Completed,
            Outcome: StructuredWingetOutcome.Completed or StructuredWingetOutcome.Partial, StartedUtc: not null, CompletedUtc: not null,
            SourceMatched: true, AgreementsDisabled: true, AuthenticationNone: true, ConnectSucceeded: true, FindSucceeded: true } &&
            result!.State is CheckState.Available or CheckState.Partial;
        string state = !schema ? "failed" : completed ? "passed" : "inconclusive";
        Diagnostic? safe = schema ? new(data!.Stage.ToString(), data.Outcome.ToString(), data.HResult, data.Attempted,
            data.StartedUtc, data.CompletedUtc, data.ProjectedCount, data.PropertyFailureCount, data.SourceMatched,
            data.AgreementsDisabled, data.AuthenticationNone, data.ConnectSucceeded, data.FindSucceeded,
            data.NativeLimitExceeded, data.ProjectionLimitExceeded) : null;
        return new(1, started, end, state, preVerified, "Returned", schema, schema ? result!.State.ToString() : null,
            schema ? result!.Items.Count : null, schema ? available : null, schema ? unknown : null, pending, safe);
    }

    internal static bool Valid(Observation value)
    {
        if (value is null || value.FormatVersion != 1 || value.StartedUtc.Offset != TimeSpan.Zero ||
            value.CompletedUtc.Offset != TimeSpan.Zero || value.CompletedUtc < value.StartedUtc ||
            value.CompletedUtc - value.StartedUtc > TimeSpan.FromMinutes(4) ||
            value.State is not ("passed" or "failed" or "inconclusive") ||
            value.TaskOutcome is not ("Returned" or "Faulted" or "Cancelled" or "Pending" or "Unsupported" or "Busy" or
                "PreCancellationFaulted" or "PreCancellationUnverified")) return false;
        if (!value.ResultSchemaAccepted)
            return value.Diagnostic is null && value.ResultState is null && value.ItemCount is null &&
                value.AvailableRows is null && value.UnknownRows is null &&
                value.State == (value.TaskOutcome == "Returned" ? "failed" : "inconclusive");
        if (value.TaskOutcome != "Returned" || !value.PreCancellationVerified || value.Diagnostic is not { } data ||
            !Enum.TryParse<StructuredWingetStage>(data.Stage, out var stage) || !Enum.IsDefined(stage) || stage.ToString() != data.Stage ||
            !Enum.TryParse<StructuredWingetOutcome>(data.Outcome, out var outcome) || !Enum.IsDefined(outcome) || outcome.ToString() != data.Outcome ||
            !Enum.TryParse<CheckState>(value.ResultState, out var state) || !Enum.IsDefined(state) || state.ToString() != value.ResultState ||
            !StateMatches(outcome, state) ||
            !Consistent(new StructuredWingetDiagnostic(stage, outcome, data.HResult, data.Attempted, data.StartedUtc, data.CompletedUtc,
                data.ProjectedCount, data.PropertyFailureCount, data.SourceMatched, data.AgreementsDisabled, data.AuthenticationNone,
                data.ConnectSucceeded, data.FindSucceeded, data.NativeLimitExceeded, data.ProjectionLimitExceeded)) || data.ProjectedCount is < 0 or > StructuredWingetPolicy.ResultLimit ||
            data.PropertyFailureCount is < 0 or > 4096 || value.ItemCount is null or < 0 or > StructuredWingetPolicy.ResultLimit ||
            value.AvailableRows is null or < 0 || value.UnknownRows is null or < 0 ||
            value.AvailableRows + value.UnknownRows != value.ItemCount ||
            (outcome is StructuredWingetOutcome.Completed or StructuredWingetOutcome.Partial
                ? value.ItemCount != data.ProjectedCount : value.ItemCount != 0) ||
            (data.StartedUtc is { } start && (start.Offset != TimeSpan.Zero || start < value.StartedUtc || start > value.CompletedUtc)) ||
            (data.CompletedUtc is { } end && (end.Offset != TimeSpan.Zero || end < value.StartedUtc || end > value.CompletedUtc ||
                data.StartedUtc is { } begin && end < begin))) return false;
        bool qualified = !value.PendingNativeRead && data.Attempted && stage == StructuredWingetStage.Completed &&
            outcome is StructuredWingetOutcome.Completed or StructuredWingetOutcome.Partial &&
            data.StartedUtc is not null && data.CompletedUtc is not null && data.SourceMatched == true &&
            data.AgreementsDisabled == true && data.AuthenticationNone == true && data.ConnectSucceeded == true && data.FindSucceeded == true;
        return value.State == (qualified ? "passed" : "inconclusive");
    }

    internal static bool ValidJson(JsonElement value)
    {
        if (!Fields(value, ["formatVersion", "startedUtc", "completedUtc", "state", "preCancellationVerified", "taskOutcome",
            "resultSchemaAccepted", "resultState", "itemCount", "availableRows", "unknownRows", "pendingNativeRead", "diagnostic"])) return false;
        var diagnostic = value.GetProperty("diagnostic");
        if (diagnostic.ValueKind != JsonValueKind.Null && !Fields(diagnostic, ["stage", "outcome", "hResult", "attempted",
            "startedUtc", "completedUtc", "projectedCount", "propertyFailureCount", "sourceMatched", "agreementsDisabled",
            "authenticationNone", "connectSucceeded", "findSucceeded", "nativeLimitExceeded", "projectionLimitExceeded"])) return false;
        try
        {
            var data = value.Deserialize<Observation>(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            });
            return data is not null && Valid(data);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException) { return false; }
    }

    private static bool Fields(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) return false;
        return names.Count == expected.Length;
    }

    private static bool Consistent(StructuredWingetDiagnostic data) =>
        (data.Attempted ? data.StartedUtc is not null : data.StartedUtc is null) &&
        (data.Outcome != StructuredWingetOutcome.Completed ||
            data.NativeLimitExceeded == false && data.ProjectionLimitExceeded == false && data.PropertyFailureCount == 0);

    private static bool StateMatches(StructuredWingetOutcome outcome, CheckState state) => outcome switch
    {
        StructuredWingetOutcome.Completed => state == CheckState.Available,
        StructuredWingetOutcome.Partial => state == CheckState.Partial,
        StructuredWingetOutcome.Cancelled => state == CheckState.Cancelled,
        StructuredWingetOutcome.Unsupported => state == CheckState.Unsupported,
        StructuredWingetOutcome.Unavailable => state == CheckState.Unavailable,
        StructuredWingetOutcome.Failed => state == CheckState.Failed,
        StructuredWingetOutcome.NotStarted or StructuredWingetOutcome.TimedOut or StructuredWingetOutcome.Busy => state == CheckState.Unknown,
        _ => false
    };

    private static Observation Empty(DateTimeOffset started, string outcome, bool preVerified, bool pending) =>
        new(1, started, DateTimeOffset.UtcNow, "inconclusive", preVerified, outcome, false, null, null, null, null, pending, null);
}
