namespace MadClean.Core;

public sealed record HardwareCompatibilityIdentityReference(string Manufacturer, string Model, string Sku,
    string ProductLabel, Uri SourceUri, DateOnly DevelopmentReviewDate, string Scope);

/// <summary>A tiny bundled developer-reviewed model association, not a live source check or upgrade catalog.</summary>
public static class HardwareCompatibilityIdentityCatalog
{
    private const string IdentityOnly = "Bundled identity reference only, reviewed during development on 2026-10-26 (America/Chicago). MadClean has not retrieved the page in this session. This supplies no RAM limit, CPU support, upgradeability, BIOS-version approval or compatibility verdict.";
    public static IReadOnlyList<HardwareCompatibilityIdentityReference> References { get; } = Array.AsReadOnly(new[]
    {
        new HardwareCompatibilityIdentityReference("Microsoft Corporation", "Surface Laptop 6 for Business", "Surface_Laptop_6_for_Business_2033", "Surface Laptop 6 for Business 13.5-inch", new("https://learn.microsoft.com/en-us/surface/surface-system-sku-reference"), new(2026, 10, 26), IdentityOnly),
        new HardwareCompatibilityIdentityReference("Microsoft Corporation", "Surface Laptop 6 for Business", "Surface_Laptop_6_for_Business_2035", "Surface Laptop 6 for Business 15-inch", new("https://learn.microsoft.com/en-us/surface/surface-system-sku-reference"), new(2026, 10, 26), IdentityOnly)
    });
    public static HardwareCompatibilityIdentityReference? Find(string? manufacturer, string? model, string? sku) =>
        References.FirstOrDefault(reference => HardwareCompatibilityPolicy.Same(reference.Manufacturer, manufacturer) &&
            HardwareCompatibilityPolicy.Same(reference.Model, model) && HardwareCompatibilityPolicy.Same(reference.Sku, sku));
}
