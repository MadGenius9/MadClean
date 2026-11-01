namespace MadClean.Core;

public interface IShortcutRecheck
{
    Task<CheckResult<ShortcutObservation>> ReadAsync(ShortcutRecheckScope scope, CancellationToken token);
}

/// <summary>Immutable historical selection for another read; never an action token or current identity proof.</summary>
public sealed class ShortcutRecheckScope
{
    public string RootPath { get; }
    public string ShortcutPath { get; }
    public ShortcutObservation PriorObservation { get; }
    public CheckState SourceCoverageState { get; }
    public DateTimeOffset ValidatedUtc { get; }

    private ShortcutRecheckScope(string root, string path, ShortcutObservation prior, CheckState coverage, DateTimeOffset validated)
    { RootPath = root; ShortcutPath = path; PriorObservation = prior with { }; SourceCoverageState = coverage; ValidatedUtc = validated; }

    public static CheckResult<ShortcutRecheckScope> Create(string root, CheckResult<ShortcutReport>? snapshot,
        ShortcutObservation? selected, CancellationToken token = default)
    {
        CheckResult<ShortcutRecheckScope> Empty(CheckState state, string message) =>
            CheckResult<ShortcutRecheckScope>.Empty(token.IsCancellationRequested ? CheckState.Cancelled : state,
                token.IsCancellationRequested ? "Shortcut recheck selection cancelled; no query or action was authorized." : message);
        if (token.IsCancellationRequested) return Empty(CheckState.Cancelled, "");
        string? localRoot = ShortcutPathPolicy.NormalizeLocalPath(root, allowDriveRoot: false);
        if (localRoot == null) return Empty(CheckState.Unavailable, "Choose a supported explicit local folder; drive roots, network/device paths and ambiguous syntax cannot scope a recheck.");
        if (snapshot == null || snapshot.State is not (CheckState.Available or CheckState.Partial) || snapshot.Items == null ||
            snapshot.Items.Count != 1 || snapshot.Items[0] is not { } report || selected == null)
            return Empty(CheckState.Unavailable, "A selected observation from exactly one available or partial shortcut report is required; unknown or failed discovery is not a recheck scope.");
        if (report.Entries == null || report.Entries.Count is 0 or > 1000 || report.Issues == null || report.Issues.Count > 100 ||
            report.VisitedFiles is < 1 or > 10000 || report.SkippedFiles < 0 || report.SkippedFiles > report.VisitedFiles ||
            report.Entries.Count > report.VisitedFiles || !SafeText(report.Coverage, 8192, allowEmpty: false) ||
            (snapshot.State == CheckState.Available && (report.Truncated || report.Issues.Count > 0)))
            return Empty(CheckState.Unavailable, "The shortcut report has missing or invalid bounded coverage metadata; refresh discovery before selecting a recheck.");
        int matches = 0;
        foreach (var entry in report.Entries)
        {
            if (token.IsCancellationRequested) return Empty(CheckState.Cancelled, "");
            if (entry == null) return Empty(CheckState.Unavailable, "The shortcut report contains invalid observation metadata; refresh discovery.");
            if (ReferenceEquals(entry, selected)) matches++;
        }
        foreach (var issue in report.Issues)
        {
            if (token.IsCancellationRequested) return Empty(CheckState.Cancelled, "");
            if (issue == null || !SafeText(issue.Path, 32768, allowEmpty: false) || !SafeText(issue.Reason, 4096, allowEmpty: false))
                return Empty(CheckState.Unavailable, "The shortcut report contains invalid bounded issue metadata; refresh discovery.");
        }
        var now = DateTimeOffset.UtcNow;
        if (matches != 1 || selected.Source != ShortcutTargetClassifier.Source || !Enum.IsDefined(selected.State) ||
            selected.CheckedAt == default || selected.CheckedAt.Offset != TimeSpan.Zero || selected.CheckedAt > now ||
            !SafeText(selected.Reason, 4096, allowEmpty: false) || selected.TargetPath == null || selected.TargetPath.Length > 32768)
            return Empty(CheckState.Unavailable, "The exact selected observation is absent, ambiguous or has an unverified source/time/state; select a genuine discovery entry.");
        string? link = ShortcutPathPolicy.NormalizeLocalPath(selected.ShortcutPath, allowDriveRoot: false);
        if (link == null || !link.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            !link.StartsWith(localRoot + "\\", StringComparison.OrdinalIgnoreCase))
            return Empty(CheckState.Unavailable, "The selected supported .lnk path must be strictly inside the chosen local folder; neighboring paths and ambiguous link names are outside scope.");
        if (token.IsCancellationRequested) return Empty(CheckState.Cancelled, "");
        return new(snapshot.State, "Historical selection accepted for one new read only. This does not establish file identity continuity, current target absence, obsolescence or removal safety; fresh Windows guards remain required.",
            [new(localRoot, link, selected, snapshot.State, now)]);
    }

    private static bool SafeText(string? value, int maximum, bool allowEmpty) => value != null && value.Length <= maximum &&
        (allowEmpty || !string.IsNullOrWhiteSpace(value)) && ShortcutPathPolicy.HasSafeUnicode(value);
}
