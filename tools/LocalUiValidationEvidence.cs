namespace MadClean.Validation;

internal sealed record LocalUiCheckFacts(bool? WindowObserved, bool? InitialNeutral, bool? WideLayout,
    bool? CompactLayout, bool? NavigationObserved, bool? FinalNeutral, bool? WindowClosed, bool? OwnershipSettled);

internal sealed record LocalUiValidationReport(int FormatVersion, string Context, DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc, string State, LocalUiCheckFacts Facts, int? ExceptionHResult,
    string WindowsProviders, string Maintenance, string PhysicalInput);

/// <summary>
/// Fixed local neutral-window evidence only. These pure consistency checks cannot observe a window,
/// qualify Windows providers or maintenance, or establish physical input behavior.
/// </summary>
internal static class LocalUiValidationEvidence
{
    internal static LocalUiValidationReport Create(DateTimeOffset start, DateTimeOffset end,
        LocalUiCheckFacts facts, int? exceptionHResult = null) =>
        new(1, "local-neutral-ui", start, end, Outcome(facts, exceptionHResult), facts, exceptionHResult,
            "unverified", "unverified", "unverified");

    internal static bool IsConsistent(LocalUiValidationReport? report, DateTimeOffset now)
    {
        if (report == null || report.FormatVersion != 1 || report.Context != "local-neutral-ui" ||
            report.WindowsProviders != "unverified" || report.Maintenance != "unverified" || report.PhysicalInput != "unverified" ||
            report.Facts == null || !Utc(now) || !Utc(report.StartedUtc) || !Utc(report.CompletedUtc) ||
            report.CompletedUtc < report.StartedUtc || report.CompletedUtc > now ||
            report.CompletedUtc - report.StartedUtc > TimeSpan.FromMinutes(10) ||
            report.State != Outcome(report.Facts, report.ExceptionHResult)) return false;

        var facts = report.Facts;
        if (facts.WindowObserved != true && Values(facts).Skip(1).Any(value => value == true)) return false;
        if (facts.FinalNeutral == true && facts.InitialNeutral != true) return false;
        if (facts.OwnershipSettled == true && facts.WindowClosed != true) return false;
        return true;
    }

    private static string Outcome(LocalUiCheckFacts? facts, int? exceptionHResult)
    {
        if (exceptionHResult.HasValue) return "failed";
        if (facts == null) return "inconclusive";
        var values = Values(facts);
        return values.Any(value => value == false) ? "failed" : values.All(value => value == true) ? "passed" : "inconclusive";
    }

    private static bool?[] Values(LocalUiCheckFacts facts) =>
        [facts.WindowObserved, facts.InitialNeutral, facts.WideLayout, facts.CompactLayout,
            facts.NavigationObserved, facts.FinalNeutral, facts.WindowClosed, facts.OwnershipSettled];

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
