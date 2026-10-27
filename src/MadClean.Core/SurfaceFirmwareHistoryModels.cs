namespace MadClean.Core;

public sealed record SurfaceFirmwareReleaseObservation(DateOnly PublishedDate, string Heading, string Summary,
    string? PublishedUefiVersion, Uri SourceUri, string SourceSection, DateTimeOffset RetrievedUtc);
public sealed record SurfaceFirmwareHistoryReport(string ProductLabel,
    IReadOnlyList<SurfaceFirmwareReleaseObservation> Releases, DateTimeOffset CheckedUtc, IReadOnlyList<string> Coverage);
public sealed record SurfaceFirmwareHistoryDocument(Uri SourceUri, string Html, DateTimeOffset RetrievedUtc);
public interface IFirmwareReleaseHistory
{
    Task<CheckResult<SurfaceFirmwareHistoryReport>> CheckAsync(FirmwareIdentity identity, CancellationToken token);
}
public static class SurfaceFirmwareHistorySources
{
    public const int MaximumDocumentBytes = 524_288;
    public static Uri Identity { get; } = new("https://learn.microsoft.com/en-us/surface/surface-system-sku-reference");
    public static Uri History { get; } = new("https://support.microsoft.com/en-us/surface/updates/surface-laptop-6-for-business-update-history");
    public static IReadOnlyList<Uri> All { get; } = Array.AsReadOnly(new[] { Identity, History });
    public static bool IsTrustedHistoryUri(Uri? uri) => IsExactUri(uri, History);
    internal static bool IsPinnedUri(Uri? uri) => All.Any(expected => IsExactUri(uri, expected));
    private static bool IsExactUri(Uri? uri, Uri expected) => uri != null && uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        !uri.OriginalString.Any(character => character is '%' or '\\' || char.IsControl(character) ||
            char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format) &&
        uri.OriginalString.Equals(expected.AbsoluteUri, StringComparison.Ordinal) &&
        uri.AbsoluteUri.Equals(expected.AbsoluteUri, StringComparison.Ordinal);
    internal static CheckResult<SurfaceFirmwareHistoryReport>? RejectIdentity(FirmwareIdentity? identity)
    {
        var reference = identity == null ? null : HardwareCompatibilityIdentityCatalog.Find(identity.PcManufacturer, identity.PcModel, identity.PcSku);
        if (reference == null) return CheckResult<SurfaceFirmwareHistoryReport>.Empty(CheckState.Unsupported,
            "This exact manufacturer/model/SKU has no supported official firmware-history adapter. No request or model substitution was made.");
        if (!reference.Sku.EndsWith("2035", StringComparison.Ordinal))
            return CheckResult<SurfaceFirmwareHistoryReport>.Empty(CheckState.Unknown,
                "The SKU reference calls 2033 13.5-inch, but the history's applicability list says 13 inch. Exact variant mapping is unconfirmed; no request, version comparison or current-status claim was made.");
        return null;
    }
}
