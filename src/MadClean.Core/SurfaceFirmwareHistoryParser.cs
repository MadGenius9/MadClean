using System.Globalization;
using System.Text;
using Element = MadClean.Core.ManufacturerServiceFactsHtml.Element;

namespace MadClean.Core;

/// <summary>Published family history, never an applicability decision or installed-version comparison.</summary>
public static class SurfaceFirmwareHistoryParser
{
    public static CheckResult<SurfaceFirmwareHistoryReport> Parse(FirmwareIdentity identity,
        IReadOnlyList<SurfaceFirmwareHistoryDocument> documents, DateTimeOffset checkedUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var rejection = SurfaceFirmwareHistorySources.RejectIdentity(identity);
        if (rejection != null) return rejection;
        try
        {
            if (documents == null || documents.Count != 2) throw new FormatException();
            var sources = new Dictionary<string, SurfaceFirmwareHistoryDocument>(StringComparer.Ordinal);
            foreach (var document in documents)
            {
                token.ThrowIfCancellationRequested();
                if (document == null || !SurfaceFirmwareHistorySources.IsPinnedUri(document.SourceUri) || document.Html == null ||
                    document.Html.Length > SurfaceFirmwareHistorySources.MaximumDocumentBytes ||
                    Encoding.UTF8.GetByteCount(document.Html) > SurfaceFirmwareHistorySources.MaximumDocumentBytes ||
                    document.RetrievedUtc > checkedUtc || checkedUtc - document.RetrievedUtc > TimeSpan.FromMinutes(2) ||
                    !sources.TryAdd(document.SourceUri.AbsoluteUri, document)) throw new FormatException();
            }
            var reference = HardwareCompatibilityIdentityCatalog.Find(identity.PcManufacturer, identity.PcModel, identity.PcSku)!;
            var identityArticle = Article(sources[SurfaceFirmwareHistorySources.Identity.AbsoluteUri], token,
                "surface-system-sku-reference", "Surface System SKU reference");
            var identityBody = One(identityArticle.Main.Descendants("div").Where(element => element.HasClass("content") &&
                element.Children.OfType<Element>().Any(child => child.Name == "table")));
            var identityTable = One(identityBody.Children.OfType<Element>().Where(element => element.Name == "table" &&
                Rows(element).Select(Cells).FirstOrDefault()?.SequenceEqual(new[] { "Device", "System Model", "System SKU" }) == true));
            var identityRow = One(Rows(identityTable).Select(Cells).Where(cells => cells.Count == 3 && cells[2] == reference.Sku));
            if (identityRow[0] != reference.ProductLabel || identityRow[1] != reference.Model) throw new FormatException();

            var historyDocument = sources[SurfaceFirmwareHistorySources.History.AbsoluteUri];
            var article = Article(historyDocument, token, "surface-laptop-6-for-business-update-history", "Surface Laptop 6 for Business update history");
            if (!article.Main.HasId("supMainContent")) throw new FormatException();
            var applies = One(article.Main.Children.OfType<Element>().Where(element => element.Name == "section" && element.HasClass("supAppliesToSection")));
            var applicability = applies.Descendants("span").Where(element => element.HasClass("appliesToItem")).Select(element => element.Text()).ToArray();
            if (!applicability.SequenceEqual(new[] { "Surface Laptop 6 - 13 inch", "Surface Laptop 6 - 15 inch" })) throw new FormatException();
            var body = One(article.Main.Descendants("div").Where(element => element.HasClass("learnArticleContent")));
            var children = body.Children.OfType<Element>().Where(element => !element.Suppressed).ToArray();
            var historyHeading = One(children.Where(element => element.Name == "h2" && element.HasId("improvements-and-fixes-in-the-updates") &&
                element.Text() == "Improvements and fixes in the updates"));
            var section = children.Skip(Array.IndexOf(children, historyHeading) + 1).TakeWhile(element => element.Name != "h2").ToArray();
            if (section.Length == 0 || section[0].Name != "h3") throw new FormatException();
            List<SurfaceFirmwareReleaseObservation> releases = [];
            DateOnly? previous = null;
            for (int index = 0; index < section.Length;)
            {
                token.ThrowIfCancellationRequested();
                var monthHeading = section[index++];
                if (monthHeading.Name != "h3" || !DateTime.TryParseExact(monthHeading.Text(), "MMMM yyyy 'updates'", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var month) || monthHeading.Attributes.GetValueOrDefault("id") != month.ToString("MMMM-yyyy", CultureInfo.InvariantCulture).ToLowerInvariant() + "-updates") throw new FormatException();
                int monthEnd = index;
                while (monthEnd < section.Length && section[monthEnd].Name != "h3") monthEnd++;
                bool any = false;
                while (index < monthEnd)
                {
                    var releaseHeading = section[index++];
                    string label = releaseHeading.Text();
                    if (releaseHeading.Name != "p" || !releaseHeading.Children.OfType<Element>().Any(child => child.Name == "strong" && child.Text() == label) ||
                        !DateTime.TryParseExact(label + " " + month.Year, "MMMM d 'release' yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var published) ||
                        published.Month != month.Month) throw new FormatException();
                    var date = DateOnly.FromDateTime(published);
                    if (date > DateOnly.FromDateTime(checkedUtc.UtcDateTime) || date.Year < 2024 || previous.HasValue && date >= previous.Value) throw new FormatException();
                    int end = index;
                    while (end < monthEnd && !IsReleaseHeading(section[end])) end++;
                    var release = section[index..end]; index = end;
                    if (release.Length == 0 || release[0].Name != "p") throw new FormatException();
                    string publishedPrerequisites = release[0].Text();
                    if (!SupportedApplicability(publishedPrerequisites)) throw new FormatException();
                    var table = One(release.Where(element => element.Name == "table"));
                    var tableHeader = Rows(table).Select(Cells).FirstOrDefault();
                    if (tableHeader == null || !tableHeader.SequenceEqual(new[] { "Windows Update Name", "Device manager" }) &&
                        !tableHeader.SequenceEqual(new[] { "Windows Update Name", "Device Manager" })) throw new FormatException();
                    var componentRows = Rows(table).Skip(1).Select(Cells).ToArray();
                    if (componentRows.Length is < 1 or > 128 || componentRows.Any(row => row.Count != 2 || row.Any(cell => cell.Length is < 1 or > 512))) throw new FormatException();
                    var uefi = componentRows.Where(row => row[1] == "Surface UEFI - Firmware").ToArray();
                    if (uefi.Length > 1) throw new FormatException();
                    string? version = uefi.Length == 0 ? null : UefiVersion(uefi[0][0]);
                    string notes = string.Join(" ", release.Where(element => element.Name != "table").Select(element => element.Text()));
                    if (notes.Length is < 1 or > 4096 || releases.Count >= 64) throw new FormatException();
                    releases.Add(new(date, monthHeading.Text() + " · " + label, notes, publishedPrerequisites, version, historyDocument.SourceUri,
                        "Improvements and fixes in the updates · " + monthHeading.Text() + " · " + label, historyDocument.RetrievedUtc));
                    previous = date; any = true;
                }
                if (!any) throw new FormatException();
            }
            if (releases.Count == 0) throw new FormatException();
            string[] coverage =
            [
                "The live official System Model/System SKU row and the history's 15-inch Applies To entry qualify the supported 2035 identity. These are published family releases, not exact configuration/revision approval.",
                "A release may contain drivers only. A blank UEFI version means no explicit Surface UEFI table row in that release; it does not mean no firmware exists or that the installed BIOS is current. Published UEFI package versions are not compared with the installed BIOS string.",
                "Windows version, staged rollout, device configuration and Windows Update applicability have not been assessed by this history check. No AvailableVersion, Up to date or update-required verdict is produced.",
                "Before any separately reviewed firmware update, read the current official prerequisites, ensure required power, back up important data and verify recovery-key availability when device encryption/BitLocker applies. Exact vendor prerequisites remain unverified here. Firmware rollback may be unavailable; a restart may be required. This check performs no installation, encryption change or restart.",
                "Only the two fixed authored HTML articles were fetched. No identity/serial was sent, no links/assets were followed and no firmware package was downloaded."
            ];
            token.ThrowIfCancellationRequested();
            return new(CheckState.Available, "Official published release history retrieved. Installed-version comparison and update applicability remain Unknown.",
                [new(reference.ProductLabel, releases, checkedUtc, coverage)]);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return CheckResult<SurfaceFirmwareHistoryReport>.Empty(CheckState.Unknown, "The exact official identity, applicability or dated release-table structure could not be confirmed. No partial history or BIOS status was inferred."); }
    }
    private static bool SupportedApplicability(string text) => new[]
    {
        "The following update is available for Surface Laptop 6 for Business devices running Windows 11, Version 23H2 or greater.",
        "The following update is available for Surface Laptop 6 for Business running Windows 11, Version 23H2 or greater.",
        "The following update is available for Surface Laptop 6 for Business running Windows 10, Version 22H2 or greater.",
        "The first update is available for Surface Laptop 6 for Business running Windows 10, Version 22H2 or greater."
    }.Contains(text, StringComparer.Ordinal);
    private static bool IsReleaseHeading(Element element) => element.Name == "p" && element.Text().EndsWith(" release", StringComparison.Ordinal);
    private static string UefiVersion(string text)
    {
        string value = text.StartsWith("Surface Driver Update (", StringComparison.Ordinal) && text.EndsWith(')')
            ? text[23..^1] : text.StartsWith("Surface - Firmware - ", StringComparison.Ordinal) ? text[21..] : throw new FormatException();
        var pieces = value.Split('.');
        if (value.Length > 64 || pieces.Length != 4 || pieces.Any(piece => piece.Length is < 1 or > 10 || !piece.All(char.IsAsciiDigit))) throw new FormatException();
        return value;
    }
    private static ManufacturerServiceFactsHtml Article(SurfaceFirmwareHistoryDocument document, CancellationToken token, string id, string title)
    {
        var article = ManufacturerServiceFactsHtml.Parse(document.Html, token);
        var heading = One(article.Main.Descendants("h1"));
        if (!heading.HasId(id) || heading.Text() != title) throw new FormatException();
        return article;
    }
    private static T One<T>(IEnumerable<T> values)
    {
        var matches = values.Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new FormatException();
    }
    private static IEnumerable<Element> Rows(Element table)
    {
        if (table.Descendants("table").Any()) throw new FormatException();
        return table.Descendants("tr");
    }
    private static IReadOnlyList<string> Cells(Element row) => row.Children.OfType<Element>()
        .Where(element => element.Name is "td" or "th").Select(element => element.Text()).ToArray();
}
