namespace MadClean.Core;

public sealed record AppInstallerReviewReport(Guid RequestId, string PackageId, string InstalledVersion,
    string OfferedVersion, DateTimeOffset CheckedUtc, bool ApplicableInstaller,
    AppPackageMetadataField InstallerType, AppPackageMetadataField NestedInstallerType,
    AppPackageMetadataField Architecture, AppPackageMetadataField Scope, AppPackageMetadataField Locale,
    AppPackageMetadataField Elevation, AppPackageMetadataField Authentication)
{
    public bool CanInstall => false;
}

public interface IAppInstallerReviewInventory
{
    bool HasPendingNativeRead { get; }
    Task<CheckResult<AppInstallerReviewReport>> ReviewInstallerAsync(AppPackageMetadataRequest request, CancellationToken token);
}

/// <summary>Fixed User/Interactive installer-profile observations only, never an installation capability.</summary>
public static class AppInstallerReviewPolicy
{
    private const string Limits = "WinGet manifest declarations for the fixed User/Interactive review profile only. " +
        "Upgrade readiness, dependency changes, authentication behavior and restart behavior remain unknown. " +
        "No installer download, installation, agreement acceptance, elevation or restart is authorized.";
    private static readonly string[] InstallerTypes = ["Inno", "Wix", "Msi", "Nullsoft", "Zip", "Msix", "Exe", "Burn", "MSStore", "Portable"];
    private static readonly string[] Architectures = ["X86", "Arm", "X64", "Neutral", "Arm64", "X86OnArm64"];
    private static readonly string[] Scopes = ["User", "System"];
    private static readonly string[] Elevations = ["ElevationRequired", "ElevationProhibited", "ElevatesSelf"];
    private static readonly string[] Authentications = ["None", "MicrosoftEntraId", "MicrosoftEntraIdForAzureBlobStorage"];

    public static CheckResult<AppPackageMetadataRequest> Prepare(CheckResult<AppUpdate>? snapshot,
        AppUpdate? selected, DateTimeOffset now, CancellationToken token = default) =>
        AppPackageMetadataPolicy.Prepare(snapshot, selected, now, token);

    public static CheckResult<AppInstallerReviewReport> Project(AppPackageMetadataRequest? request, bool applicable,
        string? type, string? nested, string? architecture, string? scope, string? locale, string? elevation,
        string? authentication, DateTimeOffset checkedUtc)
    {
        if (request == null || !RequestShape(request) || !Utc(checkedUtc) || checkedUtc < request.PreparedUtc || checkedUtc > DateTimeOffset.UtcNow)
            return Reject();
        if (!applicable && new[] { type, nested, architecture, scope, locale, elevation, authentication }
            .Any(value => value != null && (value.Length > 85 || !string.IsNullOrWhiteSpace(value)))) return Reject();
        return new(applicable ? CheckState.Partial : CheckState.Unavailable,
            (applicable ? "An applicable installer was reported for this profile; missing or unsupported declarations remain Unknown. " :
                "No applicable installer was reported for this fixed profile. This does not mean no update exists. ") + Limits,
            [new(request!.RequestId, request.PackageId, request.InstalledVersion, request.OfferedVersion, checkedUtc, applicable,
                EnumField(type, InstallerTypes), EnumField(nested, InstallerTypes), EnumField(architecture, Architectures),
                EnumField(scope, Scopes), LocaleField(locale), EnumField(elevation, Elevations), EnumField(authentication, Authentications))]);
    }

    public static bool IsResultConsistent(AppPackageMetadataRequest? request,
        CheckResult<AppInstallerReviewReport>? result, DateTimeOffset receivedUtc)
    {
        if (request == null || !RequestShape(request) || result?.Items is not { Count: 1 } || result.Items[0] is not { } report ||
            !Utc(receivedUtc) || receivedUtc < request.PreparedUtc || receivedUtc > DateTimeOffset.UtcNow ||
            report.RequestId != request.RequestId || report.PackageId != request.PackageId ||
            report.InstalledVersion != request.InstalledVersion || report.OfferedVersion != request.OfferedVersion ||
            !Utc(report.CheckedUtc) || report.CheckedUtc < request.PreparedUtc || report.CheckedUtc > receivedUtc ||
            !ConsistentEnum(report.InstallerType, InstallerTypes) || !ConsistentEnum(report.NestedInstallerType, InstallerTypes) ||
            !ConsistentEnum(report.Architecture, Architectures) || !ConsistentEnum(report.Scope, Scopes) ||
            !ConsistentLocale(report.Locale) || !ConsistentEnum(report.Elevation, Elevations) ||
            !ConsistentEnum(report.Authentication, Authentications)) return false;
        if (report.ApplicableInstaller) return result.State == CheckState.Partial;
        return result.State == CheckState.Unavailable && new[] { report.InstallerType, report.NestedInstallerType,
            report.Architecture, report.Scope, report.Locale, report.Elevation, report.Authentication }
            .All(field => field.State == CheckState.Unknown && field.Value == null);
    }

    // Expiration bounds native admission, not completion of an already-owned getter.
    private static bool RequestShape(AppPackageMetadataRequest? request) => request != null &&
        AppPackageMetadataPolicy.IsRequestConsistent(request, request.PreparedUtc);
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static AppPackageMetadataField Unknown() => new(CheckState.Unknown, null);
    private static AppPackageMetadataField EnumField(string? value, string[] known) =>
        value != null && known.Contains(value, StringComparer.Ordinal) ? new(CheckState.Available, value) : Unknown();
    private static bool ConsistentEnum(AppPackageMetadataField? field, string[] known) => field != null &&
        (field.State == CheckState.Unknown ? field.Value == null : field.State == CheckState.Available &&
            field.Value != null && known.Contains(field.Value, StringComparer.Ordinal));
    private static AppPackageMetadataField LocaleField(string? value) => ValidLocale(value) ? new(CheckState.Available, value) : Unknown();
    private static bool ConsistentLocale(AppPackageMetadataField? field) => field != null &&
        (field.State == CheckState.Unknown ? field.Value == null : field.State == CheckState.Available && ValidLocale(field.Value));
    private static bool ValidLocale(string? value) => value is { Length: > 0 and <= 85 } &&
        !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && value[0] != '-' && value[^1] != '-' &&
        !value.Contains("--", StringComparison.Ordinal) && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    private static CheckResult<AppInstallerReviewReport> Reject() => CheckResult<AppInstallerReviewReport>.Empty(CheckState.Unknown,
        "The installer review request, time or applicability declarations were inconsistent; no fields were accepted. " + Limits);
}
