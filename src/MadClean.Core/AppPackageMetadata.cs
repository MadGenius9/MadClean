using System.Globalization;
using System.Text;

namespace MadClean.Core;

/// <summary>A short-lived read selector, never source authentication or installation permission.</summary>
public sealed record AppPackageMetadataRequest(Guid RequestId, string PackageId, string InstalledVersion,
    string OfferedVersion, DateTimeOffset PreparedUtc, DateTimeOffset ExpiresUtc);
public sealed record AppPackageMetadataField(CheckState State, string? Value);
public sealed record AppPackageMetadataReport(Guid RequestId, string PackageId, string InstalledVersion,
    string OfferedVersion, DateTimeOffset CheckedUtc, AppPackageMetadataField Publisher,
    AppPackageMetadataField Description, AppPackageMetadataField License, AppPackageMetadataField ReleaseNotes)
{
    public bool CanInstall => false;
}
public interface IAppPackageMetadataInventory
{
    bool HasPendingNativeRead { get; }
    Task<CheckResult<AppPackageMetadataReport>> ReadPackageMetadataAsync(AppPackageMetadataRequest request,
        CancellationToken token);
}

/// <summary>Pure selection and bounded plain-text projection; performs no IO or package operation.</summary>
public static class AppPackageMetadataPolicy
{
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(2);
    public const int PublisherLimit = 512, DescriptionLimit = 8192, LicenseLimit = 1024, ReleaseNotesLimit = 8192;
    private const string Limits = "Catalog manifest claims only, not verified publisher identity, official release-note authenticity, compatibility or installation readiness. No URL, installer, package download, agreement acceptance or restart is authorized.";

    public static CheckResult<AppPackageMetadataRequest> Prepare(CheckResult<AppUpdate>? snapshot,
        AppUpdate? selected, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Utc(nowUtc) || nowUtc > DateTimeOffset.UtcNow || nowUtc > DateTimeOffset.MaxValue - RequestLifetime ||
            snapshot == null || snapshot.State is not (CheckState.Available or CheckState.Partial) ||
            snapshot.Items == null || snapshot.Items.Count > StructuredWingetPolicy.ResultLimit || selected == null ||
            selected.Status != CheckState.Available || selected.Source != StructuredWingetPolicy.SourceName ||
            !Key(selected.Id) || !Key(selected.InstalledVersion) || !Key(selected.AvailableVersion))
            return BlockRequest();
        int references = 0, identities = 0;
        foreach (var row in snapshot.Items)
        {
            token.ThrowIfCancellationRequested();
            if (row == null) return BlockRequest();
            if (ReferenceEquals(row, selected)) references++;
            if (string.Equals(row.Id, selected.Id, StringComparison.OrdinalIgnoreCase)) identities++;
        }
        if (references != 1 || identities != 1) return BlockRequest();
        return new(CheckState.Available,
            "Selected matched package qualified for an explicit metadata read only. Its ID, installed/offered versions and pinned source must be rechecked by the actual reader. " + Limits,
            [new(Guid.NewGuid(), selected.Id, selected.InstalledVersion, selected.AvailableVersion,
                nowUtc, nowUtc + RequestLifetime)]);
    }

    public static bool IsRequestConsistent(AppPackageMetadataRequest? request, DateTimeOffset nowUtc) =>
        RequestShape(request) && Utc(nowUtc) && nowUtc <= DateTimeOffset.UtcNow &&
        nowUtc >= request!.PreparedUtc && nowUtc < request.ExpiresUtc;

    public static CheckResult<AppPackageMetadataReport> Project(AppPackageMetadataRequest? request,
        string? publisher, string? description, string? license, string? releaseNotes, DateTimeOffset checkedUtc)
    {
        // Expiry bounds admission, not an already-owned synchronous getter's return time.
        if (request == null || !RequestShape(request) || !Utc(checkedUtc) || checkedUtc < request!.PreparedUtc || checkedUtc > DateTimeOffset.UtcNow)
            return BlockReport();
        var publisherField = Field(publisher, PublisherLimit, multiline: false);
        var descriptionField = Field(description, DescriptionLimit, multiline: true);
        var licenseField = Field(license, LicenseLimit, multiline: false);
        var notesField = Field(releaseNotes, ReleaseNotesLimit, multiline: true);
        int known = new[] { publisherField, descriptionField, licenseField, notesField }.Count(field => field.State == CheckState.Available);
        if (known == 0) return CheckResult<AppPackageMetadataReport>.Empty(CheckState.Unknown,
            "No bounded catalog metadata fields were established for the selected version. Missing or malformed fields remain Unknown. " + Limits);
        return new(known == 4 ? CheckState.Available : CheckState.Partial,
            $"{known} of 4 bounded catalog fields observed; missing or malformed fields remain Unknown. " + Limits,
            [new(request.RequestId, request.PackageId, request.InstalledVersion, request.OfferedVersion, checkedUtc,
                publisherField, descriptionField, licenseField, notesField)]);
    }

    public static bool IsResultConsistent(AppPackageMetadataRequest? request,
        CheckResult<AppPackageMetadataReport>? result, DateTimeOffset receivedUtc)
    {
        if (request == null || !RequestShape(request) || result == null || result.State is not (CheckState.Available or CheckState.Partial) ||
            result.Items is not { Count: 1 } || result.Items[0] is not { } report ||
            !Utc(receivedUtc) || receivedUtc > DateTimeOffset.UtcNow || receivedUtc < request!.PreparedUtc ||
            report.RequestId != request.RequestId || report.PackageId != request.PackageId ||
            report.InstalledVersion != request.InstalledVersion || report.OfferedVersion != request.OfferedVersion ||
            !Utc(report.CheckedUtc) || report.CheckedUtc < request.PreparedUtc || report.CheckedUtc > receivedUtc ||
            !ConsistentField(report.Publisher, PublisherLimit, multiline: false) || !ConsistentField(report.Description, DescriptionLimit, multiline: true) ||
            !ConsistentField(report.License, LicenseLimit, multiline: false) || !ConsistentField(report.ReleaseNotes, ReleaseNotesLimit, multiline: true))
            return false;
        int known = new[] { report.Publisher, report.Description, report.License, report.ReleaseNotes }.Count(field => field.State == CheckState.Available);
        return known > 0 && result.State == (known == 4 ? CheckState.Available : CheckState.Partial);
    }

    private static bool RequestShape(AppPackageMetadataRequest? request) => request != null &&
        request.RequestId != Guid.Empty && Key(request.PackageId) && Key(request.InstalledVersion) && Key(request.OfferedVersion) &&
        Utc(request.PreparedUtc) && Utc(request.ExpiresUtc) && request.ExpiresUtc - request.PreparedUtc == RequestLifetime;
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Key(string? value) => value is { Length: > 0 and <= StructuredWingetPolicy.TextLimit } &&
        value == value.Trim() && !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && value != "-" &&
        Plain(value, multiline: false);
    private static AppPackageMetadataField Field(string? value, int limit, bool multiline)
    {
        if (value == null || value.Length > limit || !Plain(value, multiline)) return new(CheckState.Unknown, null);
        string text = value.Trim();
        return text.Length == 0 ? new(CheckState.Unknown, null) : new(CheckState.Available, text);
    }
    private static bool ConsistentField(AppPackageMetadataField? field, int limit, bool multiline) => field != null &&
        (field.State == CheckState.Unknown ? field.Value == null : field.State == CheckState.Available &&
            field.Value is { Length: > 0 } value && value.Length <= limit && value == value.Trim() && Plain(value, multiline));
    private static bool Plain(string text, bool multiline)
    {
        for (int index = 0; index < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, index, out var rune)) return false;
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format || category == UnicodeCategory.Control &&
                !(multiline && (rune.Value is 9 or 10 or 13))) return false;
            index += rune.Utf16SequenceLength;
        }
        return true;
    }
    private static CheckResult<AppPackageMetadataRequest> BlockRequest() => CheckResult<AppPackageMetadataRequest>.Empty(CheckState.Unknown,
        "Select one current unambiguous Available package from the trusted WinGet observations with known installed/offered versions. No metadata request was prepared. " + Limits);
    private static CheckResult<AppPackageMetadataReport> BlockReport() => CheckResult<AppPackageMetadataReport>.Empty(CheckState.Unknown,
        "The metadata request or observation time was inconsistent; no catalog fields were accepted. " + Limits);
}
