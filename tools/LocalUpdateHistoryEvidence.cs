using MadClean.Core;

namespace MadClean.Validation;

internal sealed record LocalUpdateHistoryFacts(bool? QueryTaskSettled, bool ObservationBudgetExpired,
    string? ProviderState, bool? ResponseShapeValid, bool? ResponseAccepted, int? TotalBefore,
    int? RequestedCount, int? ReturnedEntryCount, bool? FieldProjectionObserved, int? ExceptionHResult)
{
    public bool? QueryTaskSettledWhenBudgetObserved { get; init; }
}

internal sealed record LocalUpdateHistoryReport(int FormatVersion, string Context, DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc, string State, LocalUpdateHistoryFacts Facts, string WindowsServiceCompletion,
    string CurrentInstallation, string Maintenance, string PhysicalInput, string SourceAuthenticity);

/// <summary>
/// A bounded metadata-response observation, never current installation or Windows-service
/// completion evidence. The exact original task remains awaited after budget expiry.
/// This fixture helper starts no query, cancellation, maintenance, native work or retry.
/// </summary>
internal static class LocalUpdateHistoryEvidence
{
    internal static readonly TimeSpan ObservationBudget = TimeSpan.FromSeconds(120);
    internal static LocalUpdateHistoryReport Create(DateTimeOffset start, DateTimeOffset complete,
        LocalUpdateHistoryFacts facts) => new(1, "local-update-history", start, complete, Outcome(facts), facts,
            "unverified", "unverified", "unverified", "unverified", "unverified");

    internal static bool IsConsistent(LocalUpdateHistoryReport? report, DateTimeOffset receivedUtc) =>
        report != null && report.FormatVersion == 1 && report.Context == "local-update-history" &&
        report.WindowsServiceCompletion == "unverified" && report.CurrentInstallation == "unverified" &&
        report.Maintenance == "unverified" && report.PhysicalInput == "unverified" && report.SourceAuthenticity == "unverified" &&
        Utc(receivedUtc) && Utc(report.StartedUtc) && Utc(report.CompletedUtc) &&
        report.StartedUtc <= report.CompletedUtc && report.CompletedUtc <= receivedUtc &&
        report.Facts != null && Bounded(report.Facts) && report.State == Outcome(report.Facts);

    internal static async Task<LocalUpdateHistoryFacts> ObserveAsync(Task<CheckResult<WindowsUpdateHistoryReport>> actual,
        Task deadline, DateTimeOffset startedUtc, TimeProvider? timeProvider = null, long? observationStartedTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(deadline);
        var clock = timeProvider ?? TimeProvider.System;
        long stamp = 0;
        int? clockError = null;
        bool expired = false;
        bool? settledWhenBudgetObserved = null;
        try { stamp = observationStartedTimestamp ?? clock.GetTimestamp(); }
        catch (Exception exception) { clockError = exception.HResult; }

        void ObserveBudget()
        {
            bool knownExpired = deadline.IsCompleted;
            if (!clockError.HasValue)
            {
                try
                {
                    if (clock.TimestampFrequency <= 0) throw new InvalidOperationException();
                    var elapsed = clock.GetElapsedTime(stamp);
                    if (elapsed < TimeSpan.Zero) throw new InvalidOperationException();
                    // A delayed ThreadPool timer callback cannot turn an over-budget
                    // observation into a pass. This is NOT a provider-drain deadline.
                    knownExpired |= elapsed >= ObservationBudget;
                }
                catch (Exception exception) { clockError = exception.HResult; }
            }
            knownExpired |= deadline.IsCompleted;
            if (knownExpired && !expired)
            {
                // This is the actual task's state when expiry was first observed,
                // not a reconstructed state at an unobserved earlier instant.
                settledWhenBudgetObserved = actual.IsCompleted;
                expired = true;
            }
        }
        LocalUpdateHistoryFacts Finish(LocalUpdateHistoryFacts facts)
        {
            ObserveBudget();
            return clockError.HasValue
                ? new(true, expired, null, null, null, null, null, null, null, clockError)
                    { QueryTaskSettledWhenBudgetObserved = settledWhenBudgetObserved }
                : facts with { ObservationBudgetExpired = expired, QueryTaskSettledWhenBudgetObserved = settledWhenBudgetObserved };
        }

        ObserveBudget();
        await Task.WhenAny(actual, deadline).ConfigureAwait(false);
        ObserveBudget();
        try
        {
            // A deadline bounds observation, not ownership. Do not return, cancel,
            // replace or abandon the actual provider task when the deadline wins.
            var response = await actual.ConfigureAwait(false);
            ObserveBudget();
            var received = DateTimeOffset.UtcNow;
            bool stateKnown = response != null && Enum.IsDefined(response.State);
            string? state = response == null ? null : stateKnown ? response.State.ToString() : "Unrecognized";
            bool positive = state == "Partial" && WindowsUpdateHistoryPolicy.IsConsistent(response, received);
            bool negative = state is "Unknown" or "Unavailable" or "Unsupported" or "Failed" or "Cancelled" &&
                response?.Items is { Count: 0 };
            bool shape = Utc(startedUtc) && (positive || negative);
            bool accepted = shape && positive && response!.Items[0].QueryStartedUtc >= startedUtc;
            if (!accepted)
                return Finish(new(true, expired, state, shape, false, null, null, null, null, null));
            var report = response!.Items[0];
            bool projected = report.Entries.Any(row =>
                row.Title.State == UpdateHistoryFieldState.Known || row.DateUtc.State == UpdateHistoryFieldState.Known ||
                row.Operation.State == UpdateHistoryFieldState.Known || row.ResultCode.State == UpdateHistoryFieldState.Known ||
                row.HResult.State == UpdateHistoryFieldState.Known || row.UpdateId.State == UpdateHistoryFieldState.Known ||
                row.Revision.State == UpdateHistoryFieldState.Known);
            return Finish(new(true, expired, state, true, true, report.TotalBefore, report.RequestedCount,
                report.Entries.Count, projected, null));
        }
        catch (Exception exception)
        {
            ObserveBudget();
            // Numeric exception evidence only. No provider response/state or Windows
            // cancellation/service completion is invented for a faulted client task.
            return new(true, expired, null, null, null, null, null, null, null, exception.HResult)
                { QueryTaskSettledWhenBudgetObserved = settledWhenBudgetObserved };
        }
    }

    private static string Outcome(LocalUpdateHistoryFacts? facts)
    {
        if (facts == null || !Bounded(facts) || facts.ExceptionHResult.HasValue || facts.ResponseShapeValid == false)
            return "failed";
        if (facts.QueryTaskSettledWhenBudgetObserved.HasValue && !facts.ObservationBudgetExpired ||
            facts.QueryTaskSettledWhenBudgetObserved == true && facts.QueryTaskSettled == false) return "failed";
        bool counts = facts.TotalBefore.HasValue || facts.RequestedCount.HasValue || facts.ReturnedEntryCount.HasValue;
        bool response = facts.ProviderState != null || facts.ResponseShapeValid.HasValue || facts.ResponseAccepted.HasValue ||
            counts || facts.FieldProjectionObserved.HasValue;
        if (response && facts.QueryTaskSettled != true || facts.ProviderState is "Available" or "Unrecognized") return "failed";
        if (facts.ResponseAccepted == true && (facts.ProviderState != "Partial" || facts.ResponseShapeValid != true)) return "failed";
        if (facts.ProviderState == "Partial" && facts.ResponseAccepted == false) return "failed";
        if (counts && facts.ResponseAccepted != true) return "failed";
        if (facts.FieldProjectionObserved.HasValue && (facts.ResponseAccepted != true || !facts.ReturnedEntryCount.HasValue)) return "failed";
        if (facts.FieldProjectionObserved == true && facts.ReturnedEntryCount == 0) return "failed";
        if (facts.TotalBefore.HasValue && facts.RequestedCount.HasValue &&
            facts.RequestedCount != Math.Min(facts.TotalBefore.Value, WindowsUpdateHistoryPolicy.MaximumEntries)) return "failed";
        if (facts.ReturnedEntryCount.HasValue && facts.RequestedCount.HasValue && facts.ReturnedEntryCount > facts.RequestedCount)
            return "failed";
        if (facts.ReturnedEntryCount.HasValue && facts.TotalBefore.HasValue &&
            facts.ReturnedEntryCount > Math.Min(facts.TotalBefore.Value, WindowsUpdateHistoryPolicy.MaximumEntries)) return "failed";
        if (facts.ObservationBudgetExpired) return "inconclusive";
        return facts.QueryTaskSettled == true && facts.ProviderState == "Partial" && facts.ResponseShapeValid == true &&
            facts.ResponseAccepted == true && facts.TotalBefore.HasValue && facts.RequestedCount.HasValue &&
            facts.ReturnedEntryCount.HasValue && facts.FieldProjectionObserved.HasValue ? "passed" : "inconclusive";
    }

    private static bool Bounded(LocalUpdateHistoryFacts facts) =>
        (facts.ProviderState == null || facts.ProviderState is "Unknown" or "Partial" or "Unavailable" or "Unsupported" or
            "Failed" or "Cancelled" or "Available" or "Unrecognized") &&
        (!facts.TotalBefore.HasValue || facts.TotalBefore >= 0) &&
        (!facts.RequestedCount.HasValue || facts.RequestedCount is >= 0 and <= WindowsUpdateHistoryPolicy.MaximumEntries) &&
        (!facts.ReturnedEntryCount.HasValue || facts.ReturnedEntryCount is >= 0 and <= WindowsUpdateHistoryPolicy.MaximumEntries);
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
