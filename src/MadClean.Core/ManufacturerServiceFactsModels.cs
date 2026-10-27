namespace MadClean.Core;

public sealed record ManufacturerServiceFact(string Topic, string Observation, string Scope, string Uncertainty,
    Uri SourceUri, string SourceSection, DateTimeOffset RetrievedUtc);
public sealed record ManufacturerServiceFactsReport(string ProductLabel, IReadOnlyList<ManufacturerServiceFact> Facts,
    IReadOnlyList<string> Coverage, DateTimeOffset CheckedUtc);
public sealed record ManufacturerServiceDocument(Uri SourceUri, string Html, DateTimeOffset RetrievedUtc);
public interface IManufacturerServiceFactsInventory
{
    Task<CheckResult<ManufacturerServiceFactsReport>> ReadAsync(string manufacturer, string model, string sku, CancellationToken token);
}
public static class ManufacturerServiceFactsSources
{
    public const int MaximumDocumentBytes = 524_288;
    public static Uri Identity { get; } = new("https://learn.microsoft.com/en-us/surface/surface-system-sku-reference");
    public static Uri Parts { get; } = new("https://learn.microsoft.com/en-us/surface/service-guides/surface-laptop-6/surface-laptop-6-business-device-information-and-service-parts");
    public static Uri Procedure { get; } = new("https://learn.microsoft.com/en-us/surface/service-guides/surface-laptop-6/surface-laptop-6-business-component-disassembly-and-reassembly");
    public static IReadOnlyList<Uri> All { get; } = Array.AsReadOnly(new[] { Identity, Parts, Procedure });
    public static bool IsPinnedUri(Uri? uri) => uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        !uri.OriginalString.Any(character => character is '%' or '\\' || char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format) &&
        All.Any(expected => uri.AbsoluteUri.Equals(expected.AbsoluteUri, StringComparison.Ordinal));
}
