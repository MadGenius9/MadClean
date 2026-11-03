namespace MadClean.Core;

public enum StructuredWingetStage { NotStarted, Activation, SourceValidation, Connect, Find, Projection, Completed, ActivationClass, ActivationProjection }
public enum StructuredWingetOutcome { NotStarted, Completed, Partial, Unavailable, Unsupported, Failed, Cancelled, TimedOut, Busy }
public sealed record StructuredWingetDiagnostic(
    StructuredWingetStage Stage, StructuredWingetOutcome Outcome, int? HResult,
    bool Attempted, DateTimeOffset? StartedUtc, DateTimeOffset? CompletedUtc,
    int ProjectedCount, int PropertyFailureCount, bool? SourceMatched, bool? AgreementsDisabled,
    bool? AuthenticationNone, bool? ConnectSucceeded, bool? FindSucceeded,
    bool? NativeLimitExceeded, bool? ProjectionLimitExceeded);
public sealed record StructuredWingetObservation(CheckResult<AppUpdate> Result, StructuredWingetDiagnostic Diagnostic);

// These are observed primitive facts, never activation or installation capabilities.
public sealed record StructuredWingetCatalogFacts(string? Name, string? Type, string? Argument,
    string? Identifier, bool? IsComposite, bool? AgreementsDisabled, bool? AuthenticationNone);
public sealed record StructuredWingetPackageFacts(string? Name, string? Id, string? InstalledVersion,
    string? OfferedVersion, bool? ReportsNewerVersion, bool? OfferedSourceMatched);

public static class StructuredWingetPolicy
{
    public const int ResultLimit = 256;
    public const int TextLimit = 512;
    public const string SourceName = "winget";
    public const string SourceType = "Microsoft.PreIndexed.Package";
    public const string SourceArgument = "https://cdn.winget.microsoft.com/cache";
    public const string SourceIdentifier = "Microsoft.Winget.Source_8wekyb3d8bbwe";

    public static bool MatchesSource(StructuredWingetCatalogFacts facts) =>
        facts.Name == SourceName && facts.Type == SourceType && facts.Argument == SourceArgument &&
        facts.Identifier == SourceIdentifier;

    public static bool CanConnect(StructuredWingetCatalogFacts facts) => MatchesSource(facts) &&
        facts.IsComposite == false && facts.AgreementsDisabled == true && facts.AuthenticationNone == true;

    public static bool IsKnownText(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= TextLimit && !value.Any(char.IsControl);

    public static CheckResult<AppUpdate> Project(IReadOnlyList<StructuredWingetPackageFacts> packages,
        bool? nativeLimitExceeded, bool? projectionLimitExceeded, int propertyFailures)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (propertyFailures < 0) throw new ArgumentOutOfRangeException(nameof(propertyFailures));
        var rows = new List<AppUpdate>();
        bool partial = nativeLimitExceeded != false || projectionLimitExceeded != false || propertyFailures > 0 || packages.Count > ResultLimit;
        var duplicateIds = packages.Take(ResultLimit).Where(p => IsKnownText(p.Id))
            .GroupBy(p => p.Id!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.Take(ResultLimit))
        {
            bool identityKnown = IsKnownText(package.Name) && IsKnownText(package.Id);
            bool versionsKnown = IsKnownText(package.InstalledVersion) && IsKnownText(package.OfferedVersion) &&
                !IsUnknownVersion(package.InstalledVersion!) && !IsUnknownVersion(package.OfferedVersion!);
            bool ambiguous = package.Id is not null && duplicateIds.Contains(package.Id);
            if (!identityKnown || !versionsKnown || package.ReportsNewerVersion is null || package.OfferedSourceMatched != true || ambiguous)
                partial = true;
            bool newer = identityKnown && versionsKnown && !ambiguous && package.ReportsNewerVersion == true && package.OfferedSourceMatched == true;
            rows.Add(new AppUpdate(KnownOrUnknown(package.Name), KnownOrUnknown(package.Id),
                KnownOrUnknown(package.InstalledVersion), KnownOrUnknown(package.OfferedVersion), SourceName,
                newer ? CheckState.Available : CheckState.Unknown));
        }
        return new(partial ? CheckState.Partial : CheckState.Available,
            "WinGet catalog observations; Available means WinGet reports a newer version, not verified compatibility or installation permission. Other rows remain Unknown. Empty results do not establish all software is up to date. Catalog data age is unknown.",
            rows.AsReadOnly());
    }

    private static bool IsUnknownVersion(string value) => value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || value.Trim() == "-";
    private static string KnownOrUnknown(string? value) => IsKnownText(value) ? value! : "Unknown";
}
