namespace MadClean.Core;

/// <summary>Checks a returned SDK observation before it becomes current UI data.</summary>
public static class StructuredWingetObservationPolicy
{
    public static bool IsConsistent(StructuredWingetObservation observation, DateTimeOffset attemptedUtc,
        DateTimeOffset observedUtc, bool nativePending)
    {
        if (observation is null || observation.Result is not { } result || observation.Diagnostic is not { } data ||
            !Utc(attemptedUtc) || !Utc(observedUtc) || attemptedUtc > observedUtc ||
            !Enum.IsDefined(result.State) || !Enum.IsDefined(data.Stage) || !Enum.IsDefined(data.Outcome) ||
            !StateMatches(data.Outcome, result.State) || result.Items is null ||
            result.Items.Count is < 0 or > StructuredWingetPolicy.ResultLimit ||
            data.ProjectedCount is < 0 or > StructuredWingetPolicy.ResultLimit ||
            data.PropertyFailureCount is < 0 or > 4096 ||
            data.Attempted != data.StartedUtc.HasValue)
            return false;

        if (data.StartedUtc is { } started && (!InWindow(started, attemptedUtc, observedUtc))) return false;
        if (data.CompletedUtc is { } completed && (!InWindow(completed, attemptedUtc, observedUtc) ||
            data.StartedUtc is { } requestStarted && completed < requestStarted)) return false;

        bool succeeded = data.Outcome is StructuredWingetOutcome.Completed or StructuredWingetOutcome.Partial;
        if (!succeeded && result.Items.Count != 0) return false;
        if (!data.Attempted && (data.Stage != StructuredWingetStage.NotStarted || data.ProjectedCount != 0 ||
            data.PropertyFailureCount != 0 || data.SourceMatched is not null || data.AgreementsDisabled is not null ||
            data.AuthenticationNone is not null || data.ConnectSucceeded is not null || data.FindSucceeded is not null ||
            data.NativeLimitExceeded is not null || data.ProjectionLimitExceeded is not null)) return false;
        if (data.Attempted && data.Stage == StructuredWingetStage.NotStarted) return false;
        if (data.ConnectSucceeded == true && (data.SourceMatched != true || data.AgreementsDisabled != true ||
            data.AuthenticationNone != true) || data.FindSucceeded == true && data.ConnectSucceeded != true) return false;

        // Busy/NotStarted are refusals before any new native attempt. An Unsupported
        // observation can instead follow actual activation when an interface is absent.
        if (data.Outcome is StructuredWingetOutcome.NotStarted or StructuredWingetOutcome.Busy)
        {
            if (data.Stage != StructuredWingetStage.NotStarted || data.Attempted || data.CompletedUtc is not null ||
                data.HResult is not null || data.ProjectedCount != 0 || data.PropertyFailureCount != 0 ||
                data.SourceMatched is not null || data.AgreementsDisabled is not null || data.AuthenticationNone is not null ||
                data.ConnectSucceeded is not null || data.FindSucceeded is not null ||
                data.NativeLimitExceeded is not null || data.ProjectionLimitExceeded is not null) return false;
        }

        foreach (var row in result.Items)
        {
            if (row is null || row.Status is not (CheckState.Available or CheckState.Unknown) ||
                row.Source != StructuredWingetPolicy.SourceName ||
                !StructuredWingetPolicy.IsKnownText(row.Name) || !StructuredWingetPolicy.IsKnownText(row.Id) ||
                !StructuredWingetPolicy.IsKnownText(row.InstalledVersion) ||
                !StructuredWingetPolicy.IsKnownText(row.AvailableVersion)) return false;
            if (row.Status == CheckState.Available &&
                (!KnownVersion(row.InstalledVersion) || !KnownVersion(row.AvailableVersion))) return false;
        }

        if (!succeeded) return true; // Historical counters do not supply current result rows.
        if (nativePending || data.Stage != StructuredWingetStage.Completed || !data.Attempted ||
            data.StartedUtc is null || data.CompletedUtc is null || data.HResult is not null ||
            data.SourceMatched != true || data.AgreementsDisabled != true || data.AuthenticationNone != true ||
            data.ConnectSucceeded != true || data.FindSucceeded != true || result.Items.Count != data.ProjectedCount)
            return false;
        if (data.Outcome == StructuredWingetOutcome.Completed &&
            (data.NativeLimitExceeded != false || data.ProjectionLimitExceeded != false || data.PropertyFailureCount != 0))
            return false;

        // Duplicate correlation may remain visible as Unknown; it cannot establish a
        // positive offer for either entry merely by repeating an identifier.
        return !result.Items.GroupBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1 && group.Any(row => row.Status == CheckState.Available));
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool InWindow(DateTimeOffset value, DateTimeOffset start, DateTimeOffset end) =>
        Utc(value) && value >= start && value <= end;
    private static bool KnownVersion(string value) =>
        !value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase) && value.Trim() != "-";
    private static bool StateMatches(StructuredWingetOutcome outcome, CheckState state) => outcome switch
    {
        StructuredWingetOutcome.Completed => state == CheckState.Available,
        StructuredWingetOutcome.Partial => state == CheckState.Partial,
        StructuredWingetOutcome.Unavailable => state == CheckState.Unavailable,
        StructuredWingetOutcome.Unsupported => state == CheckState.Unsupported,
        StructuredWingetOutcome.Failed => state == CheckState.Failed,
        StructuredWingetOutcome.Cancelled => state == CheckState.Cancelled,
        StructuredWingetOutcome.NotStarted or StructuredWingetOutcome.TimedOut or StructuredWingetOutcome.Busy => state == CheckState.Unknown,
        _ => false
    };
}
