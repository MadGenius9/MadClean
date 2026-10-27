using System.Text;

namespace MadClean.Core;

/// <summary>Interprets only the pinned articles' authored identity, service-parts and rSSD procedure context.</summary>
public static class ManufacturerServiceFactsParser
{
    private const string FamilyScope = "Official Surface Laptop 6 for Business family service documentation. This is not approval of a particular replacement part, revision or upgrade.";
    public static CheckResult<ManufacturerServiceFactsReport> Parse(string manufacturer, string model, string sku,
        IReadOnlyList<ManufacturerServiceDocument> documents, DateTimeOffset checkedUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var reference = HardwareCompatibilityIdentityCatalog.Find(manufacturer, model, sku);
        if (reference == null) return CheckResult<ManufacturerServiceFactsReport>.Empty(CheckState.Unsupported,
            "This reported manufacturer/model/SKU is outside the two supported exact identity pairs; no service fact is guessed.");
        if (documents == null || documents.Count != 3) return Invalid("All three pinned official article responses are required.");
        try
        {
            var byUri = new Dictionary<string, ManufacturerServiceDocument>(StringComparer.Ordinal);
            foreach (var document in documents)
            {
                token.ThrowIfCancellationRequested();
                if (document == null || !ManufacturerServiceFactsSources.IsPinnedUri(document.SourceUri) ||
                    document.Html == null || document.Html.Length > ManufacturerServiceFactsSources.MaximumDocumentBytes ||
                    Encoding.UTF8.GetByteCount(document.Html) > ManufacturerServiceFactsSources.MaximumDocumentBytes ||
                    document.RetrievedUtc > checkedUtc || checkedUtc - document.RetrievedUtc > TimeSpan.FromMinutes(2) ||
                    !byUri.TryAdd(document.SourceUri.AbsoluteUri, document))
                    return Invalid("Source origin, response bound, retrieval time or duplicate article identity was invalid.");
            }
            var identityDocument = byUri[ManufacturerServiceFactsSources.Identity.AbsoluteUri];
            var partsDocument = byUri[ManufacturerServiceFactsSources.Parts.AbsoluteUri];
            var procedureDocument = byUri[ManufacturerServiceFactsSources.Procedure.AbsoluteUri];
            var identity = ParseArticle(identityDocument, token, "surface-system-sku-reference", "Surface System SKU reference");
            var identityBody = UniqueBody(identity, element => element.Children.OfType<ManufacturerServiceFactsHtml.Element>().Any(child => child.Name == "table"));
            var identityTables = identityBody.Children.OfType<ManufacturerServiceFactsHtml.Element>().Where(element => element.Name == "table");
            var identityTable = Unique(identityTables.Where(table => Header(table).SequenceEqual(new[] { "Device", "System Model", "System SKU" })), "Identity table changed.");
            var identityRows = Rows(identityTable).Select(Cells).Where(cells => cells.Count == 3 && cells[2] == reference.Sku).ToArray();
            if (identityRows.Length != 1 || identityRows[0][0] != reference.ProductLabel || identityRows[0][1] != reference.Model)
                return Invalid("The live official System Model/System SKU row did not exactly confirm the supported product label.");

            var parts = ParseArticle(partsDocument, token, "surface-laptop-6th-for-business-device-information-and-service-parts",
                "Surface Laptop 6th for Business Device Information and Service Parts");
            var partsBody = UniqueBody(parts, element => element.Children.OfType<ManufacturerServiceFactsHtml.Element>().Any(child => child.Name == "h2" && child.HasId("device-identity-information")));
            var partsSection = Section(partsBody, "illustrated-service-parts-list", "Illustrated Service Parts List");
            var partsTable = Unique(partsSection.Where(element => element.Name == "table" &&
                Header(element).SequenceEqual(new[] { "Item", "Component", "SKU Part No.", "Substitute SKU" })), "Specific service-parts table changed.");
            var partRows = Rows(partsTable).Select(Cells).ToArray();
            ValidatePartsSection(partRows, "4", "Removable Solid-State Drive", text => text.EndsWith(" rSSD", StringComparison.Ordinal));
            ValidatePartsSection(partRows, "7", "Motherboard Module ( includes main processor and main memory)", text => text.Contains("Intel", StringComparison.Ordinal) && text.EndsWith("GB", StringComparison.Ordinal));

            var procedure = ParseArticle(procedureDocument, token, "surface-laptop-6-for-business-component-disassembly-and-reassembly",
                "Surface Laptop 6 for Business Component Disassembly and Reassembly");
            var procedureBody = UniqueBody(procedure, element => element.Children.OfType<ManufacturerServiceFactsHtml.Element>().Any(child => child.Name == "h2" && child.HasId("prerequisite-steps")));
            var storage = Section(procedureBody, "storage-rssd-replacement-process", "Storage (rSSD) Replacement Process");
            _ = Unique(storage.Where(element => element.Name == "h3" && element.Text() == "Procedure - Removal (rSSD)"), "rSSD removal section changed.");
            var installation = Unique(storage.Where(element => element.Name == "h3" && element.Text() == "Procedure - Installation (rSSD)"), "rSSD installation section changed.");
            var toolsHeading = Unique(storage.Where(element => element.Name == "h3" && element.Text() == "Required Tools"), "rSSD tools section changed.");
            string tools = SectionTextAfter(storage, toolsHeading);
            string installationText = SectionTextAfter(storage, installation);
            if (!tools.Contains("Surface Battery Cover (M1214771-001)", StringComparison.Ordinal) ||
                !tools.Contains("USB Drive with SDT and BMR Image", StringComparison.Ordinal) ||
                !installationText.Contains("Reinstall the operating system and all drivers/firmware", StringComparison.Ordinal) ||
                !installationText.Contains("BMR for your model", StringComparison.Ordinal))
                return Invalid("The scoped rSSD prerequisite/imaging qualifications were not confirmed; a generic storage mention is insufficient.");

            string ambiguity = reference.Sku.EndsWith("2033", StringComparison.Ordinal)
                ? "The identity table calls this device 13.5-inch while the service list uses 13-inch. No numerical rounding or exact-part equivalence is established; verify the precise hardware and approved part with Microsoft."
                : "The identity and service documents both use a 15-inch label, but that alone does not establish exact part/revision compatibility.";
            ManufacturerServiceFact[] facts =
            [
                new("Published SSD service category", "The official service-parts table labels item 4 ‘Removable Solid-State Drive’ and lists 13-inch and 15-inch service variants.", FamilyScope,
                    "This confirms the documented family service category only. SSD form factor, protocol, maximum capacity, free slots and any particular replacement part remain Unknown. " + ambiguity,
                    partsDocument.SourceUri, "Illustrated Service Parts List · item 4", partsDocument.RetrievedUtc),
                new("Published motherboard assembly", "The official service-parts table labels item 7 ‘Motherboard Module ( includes main processor and main memory)’ and lists both size variants.", FamilyScope,
                    "This assembly description does not establish RAM type/maximum, soldering, individual memory or CPU upgradeability, a supported-CPU list or a required-BIOS rule.",
                    partsDocument.SourceUri, "Illustrated Service Parts List · item 7", partsDocument.RetrievedUtc),
                new("Published SSD service procedure", "The official guide contains a Storage (rSSD) Replacement Process with removal/installation sections. Its tools list includes a Surface Battery Cover and USB drive with SDT/BMR image; its installation workflow includes model-specific BMR imaging.", FamilyScope,
                    "These are limited observations, not a complete safety/prerequisite checklist, permission to disassemble, a recovery guarantee or an upgrade recommendation. Read the entire current official guide and obtain qualified help as needed. MadClean downloads no image/firmware and performs no repair or reimaging.",
                    procedureDocument.SourceUri, "Storage (rSSD) Replacement Process · required tools and installation", procedureDocument.RetrievedUtc)
            ];
            string[] coverage =
            [
                "The live official identity row exactly confirmed the supported PC model and SKU. Requests contained only three fixed documentation URLs, never the PC identity or a serial/UUID/service tag.",
                "The parts/procedure observations are scoped to the Surface Laptop 6 for Business family; exact-part/revision approval is not established. " + ambiguity,
                "Unknown: RAM type/upgrade maximum/soldered design; CPU upgrades/required BIOS; SSD form factor/protocol/maximum; free physical slots; PSU/connectors and clearance. No budget, price or purchase advice is generated.",
                "Only these three authored HTML articles were checked. Linked videos, images, tools, recovery media, firmware, PDFs and partner/shop pages were not fetched. All facts require user review."
            ];
            return new(CheckState.Available, "Three official sources confirmed these narrowly scoped published service observations. This is not a complete hardware-compatibility, repair-suitability or firmware check.",
                [new(reference.ProductLabel, facts, coverage, checkedUtc)]);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return Invalid("The exact authored article/section/table structure changed or could not be interpreted. No service fact or compatibility verdict was inferred.");
        }
    }

    private static ManufacturerServiceFactsHtml ParseArticle(ManufacturerServiceDocument document, CancellationToken token, string id, string title)
    {
        var article = ManufacturerServiceFactsHtml.Parse(document.Html, token);
        var heading = Unique(article.Main.Descendants("h1"), "The article title is missing or ambiguous.");
        if (!heading.HasId(id) || heading.Text() != title) throw new FormatException("The exact product article title changed.");
        return article;
    }
    private static ManufacturerServiceFactsHtml.Element UniqueBody(ManufacturerServiceFactsHtml article, Func<ManufacturerServiceFactsHtml.Element, bool> predicate) =>
        Unique(article.Main.Descendants("div").Where(element => element.HasClass("content") && predicate(element)), "A unique authored content body is required.");
    private static T Unique<T>(IEnumerable<T> values, string error)
    {
        var matches = values.Take(2).ToArray();
        if (matches.Length != 1) throw new FormatException(error);
        return matches[0];
    }
    private static IReadOnlyList<string> Header(ManufacturerServiceFactsHtml.Element table) =>
        Rows(table).Select(Cells).FirstOrDefault() ?? [];
    private static IEnumerable<ManufacturerServiceFactsHtml.Element> Rows(ManufacturerServiceFactsHtml.Element table)
    {
        if (table.Descendants("table").Any()) throw new FormatException("Nested evidence tables are unsupported.");
        return table.Descendants("tr");
    }
    private static IReadOnlyList<string> Cells(ManufacturerServiceFactsHtml.Element row) =>
        row.Children.OfType<ManufacturerServiceFactsHtml.Element>().Where(element => element.Name is "td" or "th").Select(element => element.Text()).ToArray();
    private static void ValidatePartsSection(IReadOnlyList<string>[] rows, string item, string title, Func<string, bool> relevant)
    {
        var starts = rows.Select((cells, index) => (cells, index)).Where(pair => pair.cells.Count == 4 && pair.cells[0] == item && pair.cells[1] == title).Select(pair => pair.index).Take(2).ToArray();
        if (starts.Length != 1) throw new FormatException("The exact service category is missing or ambiguous.");
        bool small = false, large = false;
        foreach (var cells in rows.Skip(starts[0] + 1))
        {
            if (cells.Count != 4) throw new FormatException("The service category row shape changed.");
            if (cells[0].Length != 0) break;
            if (!relevant(cells[1])) continue;
            small |= cells[1].StartsWith("13-inch ", StringComparison.Ordinal);
            large |= cells[1].StartsWith("15-inch ", StringComparison.Ordinal);
        }
        if (!small || !large) throw new FormatException("Both documented service variants are required for the family-scoped observation.");
    }
    private static IReadOnlyList<ManufacturerServiceFactsHtml.Element> Section(ManufacturerServiceFactsHtml.Element body, string id, string title)
    {
        var children = body.Children.OfType<ManufacturerServiceFactsHtml.Element>().Where(element => !element.Suppressed).ToArray();
        var start = Unique(children.Select((element, index) => (element, index)).Where(pair => pair.element.Name == "h2" && pair.element.HasId(id) && pair.element.Text() == title), "The exact service section is missing or ambiguous.").index;
        return children.Skip(start).TakeWhile((element, index) => index == 0 || element.Name != "h2").ToArray();
    }
    private static string SectionTextAfter(IReadOnlyList<ManufacturerServiceFactsHtml.Element> section, ManufacturerServiceFactsHtml.Element heading)
    {
        int start = section.ToList().IndexOf(heading);
        return string.Join(" ", section.Skip(start + 1).TakeWhile(element => element.Name != "h3").Select(element => element.Text()));
    }
    private static CheckResult<ManufacturerServiceFactsReport> Invalid(string reason) =>
        CheckResult<ManufacturerServiceFactsReport>.Empty(CheckState.Unknown, reason + " RAM/CPU/BIOS/slot/fit limits and upgrade suitability remain Unknown.");
}
