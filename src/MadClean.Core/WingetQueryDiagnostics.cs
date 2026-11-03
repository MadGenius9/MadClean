namespace MadClean.Core;

/// <summary>Fixed same-query observations; never provider messages, identities or failure-cause guesses.</summary>
public enum WingetQueryOutcome
{
    ProfileUnavailable, AliasUnavailable, ParsedOutput, NonzeroExit, TimedOut, QueryFailed, UnexpectedResponse
}

public sealed record WingetQueryDiagnostics(WingetQueryOutcome Outcome, int? ExitCode,
    bool? OutputHasContent, WingetParseIssue? ParseIssue)
{
    public bool IsConsistent(CheckState state, int itemCount)
    {
        if (!Enum.IsDefined(Outcome) || !Enum.IsDefined(state) || itemCount < 0 ||
            ParseIssue is { } issue && !Enum.IsDefined(issue)) return false;
        if (Outcome == WingetQueryOutcome.ParsedOutput)
        {
            if (ExitCode != 0 || OutputHasContent == null || ParseIssue == null) return false;
            if (ParseIssue == WingetParseIssue.Accepted)
                return OutputHasContent == true && state == CheckState.Available && itemCount > 0;
            return state == CheckState.Unknown && itemCount == 0 &&
                (ParseIssue == WingetParseIssue.EmptyOutput ? OutputHasContent == false : OutputHasContent == true);
        }
        if (itemCount != 0 || ParseIssue != null) return false;
        if (Outcome == WingetQueryOutcome.NonzeroExit)
            return ExitCode is { } code && code != 0 && OutputHasContent.HasValue && state == CheckState.Failed;
        if (ExitCode != null || OutputHasContent != null) return false;
        return Outcome switch
        {
            WingetQueryOutcome.ProfileUnavailable or WingetQueryOutcome.AliasUnavailable => state == CheckState.Unavailable,
            WingetQueryOutcome.TimedOut or WingetQueryOutcome.QueryFailed => state == CheckState.Failed,
            WingetQueryOutcome.UnexpectedResponse => state == CheckState.Unknown,
            _ => false
        };
    }
}
