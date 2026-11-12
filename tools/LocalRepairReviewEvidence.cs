namespace MadClean.Validation;

internal sealed record LocalRepairReviewFacts(bool? ClosedGates, bool? ControlsBound, bool? DraftsObserved,
    bool? AcknowledgmentsObserved, bool? ConfirmationBlocked, bool? SelectionInvalidation, bool? NoRequestStateObserved);

internal sealed record LocalRepairReviewReport(int FormatVersion, string Context, DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc, string State, LocalUiCheckFacts Facts, LocalRepairReviewFacts Repair,
    int? ExceptionHResult, string WindowsProviders, string Maintenance, string PhysicalInput);

/// <summary>
/// Fixed prepare-only UI evidence consistency, not an observation source or authentication scheme.
/// It cannot qualify Windows providers, native actions, physical input or permission to run repairs.
/// The existing neutral-window report and its contract remain independent and unchanged.
/// </summary>
internal static class LocalRepairReviewEvidence
{
    internal static LocalRepairReviewReport Create(DateTimeOffset start, DateTimeOffset end,
        LocalUiCheckFacts baseFacts, LocalRepairReviewFacts repairFacts, int? exceptionHResult = null) =>
        new(1, "local-repair-review", start, end, Outcome(baseFacts, repairFacts, exceptionHResult),
            baseFacts, repairFacts, exceptionHResult, "unverified", "unverified", "unverified");

    internal static bool IsConsistent(LocalRepairReviewReport? report, DateTimeOffset now)
    {
        if (report == null || report.FormatVersion != 1 || report.Context != "local-repair-review" ||
            report.WindowsProviders != "unverified" || report.Maintenance != "unverified" || report.PhysicalInput != "unverified" ||
            report.Facts == null || report.Repair == null || report.State != Outcome(report.Facts, report.Repair, report.ExceptionHResult)) return false;
        // Reuse the existing base schema's time, neutral state and actual closure/ownership
        // causality checks without broadening or changing that separate mode's behavior.
        var neutral = LocalUiValidationEvidence.Create(report.StartedUtc, report.CompletedUtc, report.Facts, report.ExceptionHResult);
        if (!LocalUiValidationEvidence.IsConsistent(neutral, now)) return false;
        var facts = report.Repair;
        if (Values(facts).Any(value => value == true) && (report.Facts.WindowObserved != true ||
            report.Facts.InitialNeutral != true || report.Facts.NavigationObserved != true)) return false;
        if (facts.ControlsBound == true && facts.ClosedGates != true) return false;
        if (facts.DraftsObserved == true && facts.ControlsBound != true) return false;
        if (facts.AcknowledgmentsObserved == true && facts.DraftsObserved != true) return false;
        if (facts.ConfirmationBlocked == true && facts.AcknowledgmentsObserved != true) return false;
        if (facts.SelectionInvalidation == true && (facts.DraftsObserved != true || facts.AcknowledgmentsObserved != true || facts.ConfirmationBlocked != true)) return false;
        if (facts.NoRequestStateObserved == true && (facts.ClosedGates != true || facts.ControlsBound != true)) return false;
        return true;
    }
    private static string Outcome(LocalUiCheckFacts? baseFacts, LocalRepairReviewFacts? repairFacts, int? error)
    {
        if (error.HasValue) return "failed";
        if (baseFacts == null || repairFacts == null) return "inconclusive";
        var values = BaseValues(baseFacts).Concat(Values(repairFacts)).ToArray();
        return values.Any(value => value == false) ? "failed" : values.All(value => value == true) ? "passed" : "inconclusive";
    }
    private static bool?[] BaseValues(LocalUiCheckFacts facts) =>
        [facts.WindowObserved, facts.InitialNeutral, facts.WideLayout, facts.CompactLayout,
            facts.NavigationObserved, facts.FinalNeutral, facts.WindowClosed, facts.OwnershipSettled];
    private static bool?[] Values(LocalRepairReviewFacts facts) =>
        [facts.ClosedGates, facts.ControlsBound, facts.DraftsObserved, facts.AcknowledgmentsObserved,
            facts.ConfirmationBlocked, facts.SelectionInvalidation, facts.NoRequestStateObserved];
}
