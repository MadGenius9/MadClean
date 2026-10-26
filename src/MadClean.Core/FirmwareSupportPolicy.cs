using System.Globalization;

namespace MadClean.Core;

public sealed record FirmwareSupportGuide(string Vendor, string Scope, string MatchStatus, Uri SupportUri,
    string Precautions, Uri PrecautionsUri);

/// <summary>
/// A deliberately narrow, offline catalog of verified official generic guidance. It does not discover
/// an available firmware version, fetch any package, validate hardware revisions, or authorize an update.
/// </summary>
public static class FirmwareSupportPolicy
{
    private static readonly Uri SurfaceSupportUri = new(
        "https://learn.microsoft.com/en-us/surface/manage-surface-driver-and-firmware-updates");
    private static readonly Uri MicrosoftPrecautionsUri = new(
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/user-experience-for-uefi-firmware-updates");

    private static readonly FirmwareSupportGuide SurfaceGuide = new(
        "Microsoft Surface",
        "Generic official Surface driver/firmware guidance only. This is not an exact-model, SKU or motherboard-revision selection and is not an available-update check.",
        "Not exact-model/revision verified; available version, release notes and prerequisites are unknown until you verify the exact model and applicable manufacturer instructions.",
        SurfaceSupportUri,
        "For a Surface PC, verify Microsoft's AC-power and battery prerequisites for the exact model/SKU before considering any manually reviewed firmware action; those model-specific requirements have not been verified here. " +
        "Microsoft's UEFI guidance says not to disturb the system or unplug power during the process. The Surface guide links consumer support instructions; those linked support-page contents were not fetched by MadClean. " +
        "Ensure you can securely access the applicable Windows device-encryption/BitLocker recovery key, and follow manufacturer and organizational policy. MadClean does not collect keys, change encryption settings, install firmware or restart the PC.",
        MicrosoftPrecautionsUri);

    private static readonly HashSet<string> PlaceholderModels = new(StringComparer.Ordinal)
    {
        "UNKNOWN", "DEFAULT STRING", "SYSTEM PRODUCT NAME", "SYSTEM MODEL", "TO BE FILLED BY O.E.M.",
        "TO BE FILLED BY OEM", "NOT SPECIFIED", "NOT APPLICABLE", "NONE", "N/A", "OEM", "O.E.M.",
        "INVALID", "MODEL", "PC", "COMPUTER", "DESKTOP", "NOTEBOOK", "LAPTOP", "SURFACE"
    };
    private static readonly string[] SupportedSurfaceFamilies =
        ["SURFACE PRO", "SURFACE LAPTOP", "SURFACE BOOK", "SURFACE GO", "SURFACE STUDIO"];

    public static FirmwareSupportGuide? FindGuide(FirmwareIdentity identity)
    {
        if (identity is null) return null;
        string? manufacturer = Normalize(identity.PcManufacturer);
        string? model = Normalize(identity.PcModel);
        if (manufacturer != "MICROSOFT CORPORATION" || model == null || PlaceholderModels.Contains(model)) return null;
        if (!SupportedSurfaceFamilies.Any(family => model == family || model.StartsWith(family + " ", StringComparison.Ordinal)))
            return null;

        // Only computer OEM identity is considered. A retail baseboard name, BIOS manufacturer or
        // another component cannot replace the PC OEM or imply firmware compatibility.
        return SurfaceGuide;
    }

    public static bool IsTrustedGuideUri(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || !uri.IsWellFormedOriginalString() ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.OriginalString.Contains('%') || uri.OriginalString.Contains('\\') ||
            uri.OriginalString.Split('/').Any(segment => segment is "." or "..")) return false;

        // Exact catalog URIs only: an official-looking hostname is not enough to permit arbitrary
        // routes, redirects, download endpoints, query strings or third-party URLs.
        return uri == SurfaceSupportUri || uri == MicrosoftPrecautionsUri;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format))
            return null;
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}
