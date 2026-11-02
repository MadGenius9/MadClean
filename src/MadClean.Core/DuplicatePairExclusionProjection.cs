namespace MadClean.Core;

/// <summary>Qualifies a current pair observation with independent current exclusion coverage; never action permission.</summary>
public static class DuplicatePairExclusionProjection
{
    public static CheckResult<DuplicatePairRecheckReport> Apply(CheckResult<DuplicatePairRecheckReport> result,
        CheckState currentCoverage)
    {
        ArgumentNullException.ThrowIfNull(result);
        var coverage = Enum.IsDefined(currentCoverage) ? currentCoverage : CheckState.Unknown;
        bool currentCompleteEnough = coverage is CheckState.Available or CheckState.Partial;
        bool positive = result.State is CheckState.Available or CheckState.Partial;
        if (result.Items == null || result.Items.Count != 1 || result.Items[0] is not { } report)
            return positive ? CheckResult<DuplicatePairRecheckReport>.Empty(CheckState.Unknown,
                "A unique current pair observation was not established; exclusion coverage cannot validate equality.") : result;
        var projected = report with { CurrentExclusionCoverage = coverage };
        bool equality = report.State is DuplicatePairRecheckState.Equal or DuplicatePairRecheckState.Different;
        if (!positive)
        {
            // A plausible row cannot override the provider's failed/unknown outcome.
            if (equality) projected = projected with
            {
                State = DuplicatePairRecheckState.Unknown,
                Reason = "The provider did not establish a completed pair comparison; equality was not accepted."
            };
            return result with { Items = [projected] };
        }
        if (!currentCompleteEnough || !equality || report.SourceCoverageState is not (CheckState.Available or CheckState.Partial))
        {
            if (equality) projected = projected with
            {
                State = DuplicatePairRecheckState.Unknown,
                Reason = "Essential current exclusion coverage or comparison provenance was not established; no equality conclusion was accepted. Actual read observations remain historical evidence only."
            };
            return new(CheckState.Unknown,
                "Current pair equality is unverified. Review independent historical and current exclusion coverage; observed read bytes do not establish protection or action permission.", [projected]);
        }
        var state = result.State == CheckState.Partial || report.SourceCoverageState == CheckState.Partial || coverage == CheckState.Partial
            ? CheckState.Partial : CheckState.Available;
        return result with { State = state, Items = [projected] };
    }
}
