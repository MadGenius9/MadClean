using System.Globalization;

namespace MadClean.Core;

public enum HardwareCompatibilityPlatform { Unknown, OemSystem, RetailBoard }
public enum HardwareCompatibilityClaimKind { Identity, Memory, Cpu, Expansion, Storage }
public enum HardwareCompatibilityMatchState { Unknown, ScopeMatched }
public enum HardwareCompatibilityAdviceState { Unknown, ReadyForReview }
public enum HardwareCompatibilityUpgradeArea { Memory, Cpu, Gpu, Storage }
public enum HardwareCompatibilityCheckKind
{
    ManufacturerSpecification, MemoryTypeLimitsAndPopulation, ReplaceabilityOrSolderedDesign,
    PhysicalOccupancy, SupportedCpuExactSku, RequiredFirmware, PowerAndCooling,
    ExpansionLanes, PsuCapacityAndConnectors, CaseAndGpuClearance, StorageProtocolAndSocket,
    LaneSharing, MountingAndCooling
}

public sealed record HardwareCompatibilityIdentity(HardwareCompatibilityPlatform Platform,
    string Manufacturer, string Model, string Sku, string BoardManufacturer, string BoardProduct,
    string BoardRevision, bool BoardRevisionVerified, string FirmwareVersion, string FirmwareNamespace);
public sealed record HardwareCompatibilityScope(HardwareCompatibilityPlatform Platform,
    HardwareCompatibilityClaimKind ClaimKind, string Manufacturer, string Model, string Sku,
    string BoardManufacturer, string BoardProduct, string BoardRevision,
    bool RequiresFirmwarePrerequisite = false, string FirmwareNamespace = "", string FirmwareRequirement = "");
public sealed record HardwareCompatibilitySource(Uri Uri, string Issuer, string Title,
    CheckState RetrievalState, DateTimeOffset? RetrievedUtc, bool ExactProductScope,
    bool HasConflictingEvidence = false);
public sealed record HardwareCompatibilityCheck(HardwareCompatibilityCheckKind Kind, bool Verified,
    string EvidenceReference);
public sealed record HardwareCompatibilityFirmwareEvidence(bool RequirementSatisfied,
    string ObservedVersion, string Namespace, string EvidenceReference = "");
public sealed record HardwareCompatibilityMatch(HardwareCompatibilityMatchState State,
    IReadOnlyList<string> Reasons, HardwareCompatibilityClaimKind? ClaimKind = null);
public sealed record HardwareCompatibilityPreferences(decimal? Budget, string Currency, string Region,
    string UseCase);
public sealed record HardwareCompatibilityAdvice(HardwareCompatibilityAdviceState State,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Eliminates scope mismatches. ScopeMatched is NOT source authentication, a verified specification,
/// a compatible candidate, a recommendation or permission to buy/install/flash anything.
/// A future trusted source adapter must still retrieve and validate the actual specific claim.
/// </summary>
public static class HardwareCompatibilityPolicy
{
    public static readonly TimeSpan RetrievalReviewLimit = TimeSpan.FromDays(30);
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "unknown", "none", "na", "notspecified", "notapplicable", "defaultstring", "tobefilledbyoem",
        "oem", "systemsku", "systemproductname", "baseboardproductname", "systemmanufacturer"
    };
    private static readonly IReadOnlyDictionary<string, string[]> OfficialHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft Corporation"] = ["learn.microsoft.com", "support.microsoft.com", "www.microsoft.com"],
        ["Dell Inc."] = ["www.dell.com"],
        ["HP"] = ["support.hp.com"],
        ["LENOVO"] = ["psref.lenovo.com", "support.lenovo.com", "pcsupport.lenovo.com"],
        ["Framework"] = ["frame.work", "knowledgebase.frame.work"],
        ["ASUSTeK COMPUTER INC."] = ["www.asus.com"],
        ["Gigabyte Technology Co., Ltd."] = ["www.gigabyte.com"],
        ["Micro-Star International Co., Ltd."] = ["www.msi.com"],
        ["ASRock"] = ["www.asrock.com"]
    };
    private static readonly HashSet<string> SupportedCurrencies = new(StringComparer.Ordinal)
    { "USD", "EUR", "GBP", "CAD", "AUD", "NZD", "JPY", "CHF", "SEK", "NOK", "DKK", "INR", "SGD", "BRL", "MXN", "CNY", "KRW", "ZAR", "PLN", "CZK", "HKD", "TWD" };

    public static HardwareCompatibilityMatch MatchTarget(HardwareCompatibilityIdentity identity,
        HardwareCompatibilityScope scope, HardwareCompatibilitySource source,
        DateTimeOffset nowUtc, HardwareCompatibilityFirmwareEvidence? firmware = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(source);
        List<string> reasons = [];
        if (!Enum.IsDefined(identity.Platform) || identity.Platform == HardwareCompatibilityPlatform.Unknown ||
            !Enum.IsDefined(scope.Platform) || scope.Platform == HardwareCompatibilityPlatform.Unknown || identity.Platform != scope.Platform)
            reasons.Add("OEM-versus-retail scope is unknown or mismatched; a retail board cannot substitute for an OEM system.");
        if (!Enum.IsDefined(scope.ClaimKind)) reasons.Add("The specification claim kind is unsupported.");
        string issuer = "";
        if (identity.Platform == HardwareCompatibilityPlatform.OemSystem && scope.Platform == HardwareCompatibilityPlatform.OemSystem)
        {
            issuer = identity.Manufacturer?.Trim() ?? "";
            if (!Same(identity.Manufacturer, scope.Manufacturer) || !Same(identity.Model, scope.Model) || !Same(identity.Sku, scope.Sku))
                reasons.Add("Exact OEM manufacturer, model and SKU evidence is missing or does not match. Similar names and board strings do not establish applicability.");
        }
        if (identity.Platform == HardwareCompatibilityPlatform.RetailBoard && scope.Platform == HardwareCompatibilityPlatform.RetailBoard)
        {
            issuer = identity.BoardManufacturer?.Trim() ?? "";
            if (!Same(identity.BoardManufacturer, scope.BoardManufacturer) || !Same(identity.BoardProduct, scope.BoardProduct) ||
                !Same(identity.BoardRevision, scope.BoardRevision) || !identity.BoardRevisionVerified)
                reasons.Add("Exact retail board manufacturer/product and independently verified hardware revision are required; reported Version alone is insufficient.");
        }
        if (!Same(source.Issuer, issuer) || !IsOfficialSourceUri(issuer, source.Uri))
            reasons.Add("The source origin/issuer is unsupported, ambiguous or does not belong to the matched manufacturer.");
        if (!ValidText(source.Title, 512) || !source.ExactProductScope)
            reasons.Add("A specific exact-product source title and independently established document scope are required; a generic support page is insufficient.");
        if (source.RetrievalState != CheckState.Available || source.RetrievedUtc is not { } retrieved)
            reasons.Add("The official source was not successfully retrieved; offline, partial and failed evidence cannot establish a match.");
        else if (retrieved > nowUtc || nowUtc - retrieved > RetrievalReviewLimit)
            reasons.Add("Source retrieval time is future, unknown or outside MadClean's 30-day review limit. This limit is a product policy, not a manufacturer document expiry.");
        if (source.HasConflictingEvidence) reasons.Add("Conflicting source/configuration evidence remains unresolved.");
        if (source.Uri != null && IsSurfaceIdentityReference(source.Uri))
        {
            if (scope.ClaimKind != HardwareCompatibilityClaimKind.Identity)
                reasons.Add("The Surface SKU reference maps identity only; it provides no RAM/CPU/expansion/storage upgrade specification.");
            else if (HardwareCompatibilityIdentityCatalog.Find(identity.Manufacturer, identity.Model, identity.Sku) == null)
                reasons.Add("This identity-reference pair is outside the bounded developer-reviewed catalogue; no table row is guessed.");
        }
        if (scope.RequiresFirmwarePrerequisite && (!ValidText(scope.FirmwareRequirement, 512) || firmware == null || !firmware.RequirementSatisfied ||
            !ValidText(firmware.EvidenceReference, 1024) || !Exact(scope.FirmwareNamespace, firmware.Namespace) || !Exact(identity.FirmwareNamespace, firmware.Namespace) ||
            !Exact(identity.FirmwareVersion, firmware.ObservedVersion)))
            reasons.Add("The relevant vendor firmware prerequisite has not been verified against the current exact version/namespace. No generic version ordering is performed.");
        return new(reasons.Count == 0 ? HardwareCompatibilityMatchState.ScopeMatched : HardwareCompatibilityMatchState.Unknown,
            reasons.Count == 0 ? ["Exact scope gate matched only. Actual document authenticity/content, individual specification claims and upgrade compatibility still require independent verification."] : reasons,
            Enum.IsDefined(scope.ClaimKind) ? scope.ClaimKind : null);
    }

    public static bool IsOfficialSourceUri(string issuer, Uri? uri)
    {
        if (!ValidText(issuer, 512) || uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.HostNameType != UriHostNameType.Dns || uri.Host.EndsWith('.') || uri.OriginalString.Length > 2048 ||
            uri.OriginalString.Any(c => c is '%' or '\\' || char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) ||
            !OfficialHosts.TryGetValue(issuer.Trim(), out var hosts) || !hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            return false;
        // A known origin is not enough: reject generic landing pages rather than calling them a product document.
        string path = uri.AbsolutePath.TrimEnd('/');
        return path.Length > 1 && !path.Equals("/support/home/en-us", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("/us-en", StringComparison.OrdinalIgnoreCase);
    }

    public static HardwareCompatibilityAdvice AssessAdvice(HardwareCompatibilityUpgradeArea area,
        HardwareCompatibilityMatch scopeMatch, HardwareCompatibilityPreferences preferences,
        IReadOnlyList<HardwareCompatibilityCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(scopeMatch);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(checks);
        List<string> reasons = [];
        if (!Enum.IsDefined(area)) reasons.Add("The upgrade area is unsupported.");
        if (scopeMatch.State != HardwareCompatibilityMatchState.ScopeMatched) reasons.Add("Exact-model scope remains unknown.");
        var requiredClaim = area switch
        {
            HardwareCompatibilityUpgradeArea.Memory => HardwareCompatibilityClaimKind.Memory,
            HardwareCompatibilityUpgradeArea.Cpu => HardwareCompatibilityClaimKind.Cpu,
            HardwareCompatibilityUpgradeArea.Gpu => HardwareCompatibilityClaimKind.Expansion,
            HardwareCompatibilityUpgradeArea.Storage => HardwareCompatibilityClaimKind.Storage,
            _ => (HardwareCompatibilityClaimKind?)null
        };
        if (requiredClaim == null || scopeMatch.ClaimKind != requiredClaim)
            reasons.Add("The matched source scope does not cover this upgrade area; identity-only or another component's evidence cannot substitute.");
        if (preferences.Budget is not > 0 || !SupportedCurrencies.Contains(preferences.Currency ?? "") ||
            !ValidRegion(preferences.Region) || !ValidText(preferences.UseCase, 1024))
            reasons.Add("A positive user-supplied budget, supported currency, region and use case are required; none is inferred from hardware.");
        if (checks.Count > 32 || checks.Any(check => check == null || !Enum.IsDefined(check.Kind)) ||
            checks.GroupBy(check => check.Kind).Any(group => group.Count() != 1))
            reasons.Add("Compatibility-check evidence is excessive, unsupported or ambiguous.");
        else
        {
            foreach (var kind in RequiredChecks(area))
            {
                var check = checks.FirstOrDefault(item => item.Kind == kind);
                if (check == null || !check.Verified || !ValidText(check.EvidenceReference, 1024))
                    reasons.Add("Required evidence remains unknown: " + kind + ". A detected count or reported socket does not satisfy this check.");
            }
        }
        return new(reasons.Count == 0 ? HardwareCompatibilityAdviceState.ReadyForReview : HardwareCompatibilityAdviceState.Unknown,
            reasons.Count == 0 ? ["Inputs and required evidence are present for manual review only. No product, price, compatibility guarantee, purchase, firmware update or installation is authorized."] : reasons);
    }

    public static IReadOnlyList<HardwareCompatibilityCheckKind> RequiredChecks(HardwareCompatibilityUpgradeArea area) => area switch
    {
        HardwareCompatibilityUpgradeArea.Memory => [HardwareCompatibilityCheckKind.ManufacturerSpecification, HardwareCompatibilityCheckKind.MemoryTypeLimitsAndPopulation, HardwareCompatibilityCheckKind.ReplaceabilityOrSolderedDesign, HardwareCompatibilityCheckKind.PhysicalOccupancy],
        HardwareCompatibilityUpgradeArea.Cpu => [HardwareCompatibilityCheckKind.ManufacturerSpecification, HardwareCompatibilityCheckKind.SupportedCpuExactSku, HardwareCompatibilityCheckKind.RequiredFirmware, HardwareCompatibilityCheckKind.PowerAndCooling, HardwareCompatibilityCheckKind.ReplaceabilityOrSolderedDesign],
        HardwareCompatibilityUpgradeArea.Gpu => [HardwareCompatibilityCheckKind.ManufacturerSpecification, HardwareCompatibilityCheckKind.ExpansionLanes, HardwareCompatibilityCheckKind.PsuCapacityAndConnectors, HardwareCompatibilityCheckKind.CaseAndGpuClearance, HardwareCompatibilityCheckKind.PhysicalOccupancy],
        HardwareCompatibilityUpgradeArea.Storage => [HardwareCompatibilityCheckKind.ManufacturerSpecification, HardwareCompatibilityCheckKind.StorageProtocolAndSocket, HardwareCompatibilityCheckKind.LaneSharing, HardwareCompatibilityCheckKind.PhysicalOccupancy, HardwareCompatibilityCheckKind.MountingAndCooling],
        _ => []
    };

    internal static bool Same(string? left, string? right) => ValidText(left, 512) && ValidText(right, 512) &&
        string.Equals(left!.Trim(), right!.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool Exact(string? left, string? right) => ValidText(left, 512) && ValidText(right, 512) &&
        string.Equals(left!.Trim(), right!.Trim(), StringComparison.Ordinal);
    private static bool ValidText(string? value, int limit)
    {
        if (value == null || value.Length > limit || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)) return false;
        string text = value.Trim();
        if (text.Length == 0) return false;
        string key = string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return key.Length > 0 && !Placeholders.Contains(key);
    }
    private static bool ValidRegion(string? value)
    {
        if (value == null || value.Length != 2 || value.Any(c => c is < 'A' or > 'Z')) return false;
        try { return new RegionInfo(value).TwoLetterISORegionName == value; }
        catch (ArgumentException) { return false; }
    }
    private static bool IsSurfaceIdentityReference(Uri uri) => uri.IsAbsoluteUri && uri.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.TrimEnd('/').EndsWith("/surface/surface-system-sku-reference", StringComparison.OrdinalIgnoreCase);
}
