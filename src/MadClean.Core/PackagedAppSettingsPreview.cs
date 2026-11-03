namespace MadClean.Core;

/// <summary>A family-level Settings navigation preview, never a removal or repair permission.</summary>
public sealed class PackagedAppSettingsPreview
{
    internal PackagedAppSettingsPreview(CheckResult<PackagedApplication> source, PackagedApplication selected,
        DateTimeOffset completedUtc, DateTimeOffset createdUtc, Uri target)
    { Source = source; Selected = selected; SourceCompletedUtc = completedUtc; CreatedUtc = createdUtc; TargetUri = target; }
    internal CheckResult<PackagedApplication> Source { get; }
    public PackagedApplication Selected { get; }
    public DateTimeOffset SourceCompletedUtc { get; }
    public DateTimeOffset CreatedUtc { get; }
    public CheckState SourceCoverage => Source.State;
    public Uri TargetUri { get; }
    public const string OfficialReference = "https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings";
    public const string Effect = "Request Windows' App features / Advanced options page for this package family only. No uninstall, reset, repair, termination or package deployment is requested by MadClean.";
    public const string Limits = "The destination identifies a package family, not this exact version or a particular window. Windows controls page availability and any later selected actions; the documented page does not guarantee an uninstall control. A returned shell request does not prove the page appeared or that an operation completed. Package metadata and non-removable/signing flags are not action permission or independent trust verification.";
}

/// <summary>Fresh membership-bound navigation only; no Windows operation is authorized.</summary>
public sealed class PackagedAppSettingsHandoff
{
    internal PackagedAppSettingsHandoff(PackagedAppSettingsPreview preview, CheckResult<PackagedApplication> current, DateTimeOffset receivedUtc)
    { Preview = preview; Current = current; CurrentReceivedUtc = receivedUtc; }
    public PackagedAppSettingsPreview Preview { get; }
    internal CheckResult<PackagedApplication> Current { get; }
    public DateTimeOffset CurrentReceivedUtc { get; }
    public Uri TargetUri => Preview.TargetUri;
}

public static class PackagedAppSettingsPolicy
{
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(5);
    private const string Prefix = "ms-settings:appsfeatures-app?";

    public static PackagedAppSettingsPreview? Create(CheckResult<PackagedApplication> source, PackagedApplication selected,
        DateTimeOffset sourceCompletedUtc, DateTimeOffset nowUtc)
    {
        if (!Fresh(sourceCompletedUtc, nowUtc) || !SourceMatches(source, selected, exactReference: true)) return null;
        // The documented parameter is the bare PFN, not a guessed "PFN=" named parameter.
        var target = new Uri(Prefix + selected.FamilyName, UriKind.Absolute);
        return new(source, selected, sourceCompletedUtc, nowUtc, target);
    }

    public static bool IsAllowed(PackagedAppSettingsPreview? preview, DateTimeOffset nowUtc) =>
        preview != null && Fresh(preview.CreatedUtc, nowUtc) && Fresh(preview.SourceCompletedUtc, nowUtc) &&
        preview.CreatedUtc >= preview.SourceCompletedUtc && SourceMatches(preview.Source, preview.Selected, exactReference: true) &&
        preview.TargetUri.IsAbsoluteUri && preview.TargetUri.OriginalString == Prefix + preview.Selected.FamilyName &&
        preview.TargetUri.UserInfo.Length == 0 && preview.TargetUri.Fragment.Length == 0;

    public static bool MatchesCurrent(PackagedAppSettingsPreview? preview, CheckResult<PackagedApplication> current,
        DateTimeOffset currentReceivedUtc, DateTimeOffset nowUtc) => IsAllowed(preview, nowUtc) &&
        Fresh(currentReceivedUtc, nowUtc) && currentReceivedUtc >= preview!.CreatedUtc &&
        SourceMatches(current, preview.Selected, exactReference: false);

    public static PackagedAppSettingsHandoff? PrepareHandoff(PackagedAppSettingsPreview preview,
        CheckResult<PackagedApplication> current, DateTimeOffset receivedUtc, DateTimeOffset nowUtc) =>
        MatchesCurrent(preview, current, receivedUtc, nowUtc) && nowUtc - receivedUtc <= TimeSpan.FromSeconds(30)
            ? new(preview, current, receivedUtc) : null;

    public static bool IsAllowed(PackagedAppSettingsHandoff? handoff, DateTimeOffset nowUtc) => handoff != null &&
        MatchesCurrent(handoff.Preview, handoff.Current, handoff.CurrentReceivedUtc, nowUtc) &&
        nowUtc - handoff.CurrentReceivedUtc <= TimeSpan.FromSeconds(30);

    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now) => observed != default && observed <= now && now - observed <= ReviewLifetime;
    private static bool SourceMatches(CheckResult<PackagedApplication>? source, PackagedApplication? selected, bool exactReference)
    {
        if (source == null || selected == null || source.State is not (CheckState.Available or CheckState.Partial) ||
            source.Items == null || source.Items.Count is 0 or > PackagedApplicationParser.MaximumRecords || !SupportedIdentity(selected)) return false;
        int matches = 0;
        foreach (var row in source.Items)
        {
            if (row == null) return false;
            if (string.Equals(row.FullName, selected.FullName, StringComparison.OrdinalIgnoreCase))
            {
                if (row != selected || exactReference && !ReferenceEquals(row, selected)) return false;
                matches++;
            }
            if (string.Equals(row.FamilyName, selected.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                (!SupportedIdentity(row) || row.Name != selected.Name || row.FamilyName != selected.FamilyName)) return false;
        }
        return matches == 1;
    }
    private static bool SupportedIdentity(PackagedApplication package)
    {
        // Conservative navigation allowlist, not a complete Windows package-identity grammar.
        if (!Token(package.Name, 3, 50) || package.FamilyName == null || package.FullName == null || package.FullName.Length > 256 || package.Version == null ||
            package.FamilyName.Length != package.Name.Length + 14 ||
            !package.FamilyName.StartsWith(package.Name + "_", StringComparison.Ordinal) ||
            !package.FamilyName.AsSpan(package.Name.Length + 1).ToArray().All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9') ||
            package.IsFramework != false || package.IsResourcePackage != false || package.PackageType is not ("Main" or "Bundle")) return false;
        string[] parts = package.FullName.Split('_');
        if (parts.Length != 5 || parts[0] != package.Name || parts[1] != package.Version ||
            package.FamilyName != parts[0] + "_" + parts[4] ||
            parts[2] is not ("x86" or "x64" or "arm" or "arm64" or "neutral" or "x86onarm64") ||
            !string.Equals(parts[2], package.Architecture, StringComparison.OrdinalIgnoreCase) ||
            !(parts[3].Length == 0 || parts[3] == "~" || Token(parts[3], 1, 30))) return false;
        string[] numbers = package.Version.Split('.');
        return numbers.Length == 4 && numbers.All(part => ushort.TryParse(part, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out ushort number) && part == number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static bool Token(string? text, int minimum, int maximum) => text != null && text.Length >= minimum && text.Length <= maximum &&
        text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-');
}
