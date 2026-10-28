using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MadClean.Core;

/// <summary>Strict bounded metadata projection; no package paths, manifests, users or deployment operations.</summary>
public static class PackagedApplicationParser
{
    public const int MaximumRecords = 1000;
    public const int ResponseLimit = 4_194_304;
    private static readonly HashSet<string> EnvelopeFields = new(StringComparer.Ordinal)
        { "Version", "Scope", "PackageTypes", "Status", "Truncated", "Packages" };
    private static readonly HashSet<string> RowFields = new(StringComparer.Ordinal)
        { "Name", "PackageFullName", "PackageFamilyName", "Version", "Publisher", "Architecture", "IsBundle", "NonRemovable", "IsFramework", "IsResourcePackage", "SignatureKind" };
    private static readonly string[] Architectures = ["X86", "Arm", "X64", "Neutral", "Arm64", "X86OnArm64"];
    private static readonly string[] Signatures = ["None", "Developer", "Enterprise", "Store", "System"];
    private const string Scope = "Current-user Main/Bundle package rows only; bundles and main packages are not unique app counts. Other users, dependency/resource/optional packages and unregistered software are outside coverage. No update, removal eligibility or package integrity was checked; signature kind is reported metadata only.";

    public static CheckResult<PackagedApplication> Parse(string json, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        CheckResult<PackagedApplication> Invalid()
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<PackagedApplication>.Empty(CheckState.Unknown, "The package metadata response was malformed or outside supported bounds. " + Scope);
        }
        if (string.IsNullOrWhiteSpace(json) || json.Length > ResponseLimit) return Invalid();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            if (!Shape(root, EnvelopeFields) || !root.GetProperty("Version").TryGetInt32(out int version) || version != 1 ||
                root.GetProperty("Scope").ValueKind != JsonValueKind.String || root.GetProperty("Scope").GetString() != "CurrentUser" ||
                root.GetProperty("Status").ValueKind != JsonValueKind.String ||
                root.GetProperty("Truncated").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid();
            var types = root.GetProperty("PackageTypes");
            if (types.ValueKind != JsonValueKind.Array || types.GetArrayLength() != 2 ||
                types[0].ValueKind != JsonValueKind.String || types[0].GetString() != "Main" ||
                types[1].ValueKind != JsonValueKind.String || types[1].GetString() != "Bundle") return Invalid();
            var rows = root.GetProperty("Packages");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumRecords) return Invalid();
            string status = root.GetProperty("Status").GetString()!;
            if (status is "Unavailable" or "Failed")
            {
                if (rows.GetArrayLength() != 0 || root.GetProperty("Truncated").GetBoolean()) return Invalid();
                token.ThrowIfCancellationRequested();
                return CheckResult<PackagedApplication>.Empty(status == "Unavailable" ? CheckState.Unavailable : CheckState.Failed,
                    status == "Unavailable" ? "The current-user package provider is unavailable. " + Scope : "The current-user package metadata check failed. " + Scope);
            }
            if (status is not ("Available" or "Partial")) return Invalid();
            bool partial = status == "Partial" || root.GetProperty("Truncated").GetBoolean();
            int omitted = 0;
            var accepted = new Dictionary<string, PackagedApplication>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                // Track a usable identity before validating other fields: an invalid duplicate
                // must not leave an apparently unambiguous earlier row trusted.
                bool duplicate = false;
                if (row.ValueKind == JsonValueKind.Object)
                    foreach (var property in row.EnumerateObject())
                    {
                        token.ThrowIfCancellationRequested();
                        if (property.Name != "PackageFullName") continue;
                        string? identity = Text(property.Value, 256);
                        if (identity != null && !seen.Add(identity))
                        { accepted.Remove(identity); ambiguous.Add(identity); duplicate = true; }
                    }
                if (duplicate || !Shape(row, RowFields)) { partial = true; omitted++; continue; }
                string? full = Text(row.GetProperty("PackageFullName"), 256);
                string? name = Text(row.GetProperty("Name"), 256), family = Text(row.GetProperty("PackageFamilyName"), 128);
                if (name == null || full == null || family == null) { partial = true; omitted++; continue; }
                bool? framework = Flag(row.GetProperty("IsFramework")), resource = Flag(row.GetProperty("IsResourcePackage"));
                if (framework == true || resource == true) { partial = true; omitted++; continue; }
                bool? bundle = Flag(row.GetProperty("IsBundle")), nonRemovable = Flag(row.GetProperty("NonRemovable"));
                string? publisher = Text(row.GetProperty("Publisher"), 1024);
                string? packageVersion = CanonicalVersion(row.GetProperty("Version"));
                string? architecture = Known(row.GetProperty("Architecture"), 32, Architectures);
                string? signature = Known(row.GetProperty("SignatureKind"), 32, Signatures);
                if (publisher == null || packageVersion == null || architecture == null || signature == null ||
                    bundle == null || nonRemovable == null || framework == null || resource == null) partial = true;
                if (!ambiguous.Contains(full)) accepted.Add(full, new(name, full, family, packageVersion ?? "Unknown", publisher ?? "Unknown",
                    architecture ?? "Unknown", bundle.HasValue ? bundle.Value ? "Bundle" : "Main" : "Unknown",
                    nonRemovable, framework, resource, signature ?? "Unknown"));
            }
            token.ThrowIfCancellationRequested();
            omitted = rows.GetArrayLength() - accepted.Count;
            return new(partial ? CheckState.Partial : CheckState.Available,
                $"{accepted.Count} current-user package rows observed; {omitted} unusable or ambiguous rows omitted." +
                (status == "Partial" ? " The provider reported incomplete coverage; missing package rows remain unknown." : "") +
                (root.GetProperty("Truncated").GetBoolean() ? " The record limit truncated this inventory." : "") + " " + Scope,
                accepted.Values.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ThenBy(app => app.FullName, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { return Invalid(); }
    }

    private static bool Shape(JsonElement value, HashSet<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !names.Add(property.Name)) return false;
        return names.SetEquals(expected);
    }
    private static bool? Flag(JsonElement value) => value.ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static string? Known(JsonElement value, int maximum, string[] choices)
    {
        string? text = Text(value, maximum);
        return text == null ? null : choices.FirstOrDefault(choice => string.Equals(choice, text, StringComparison.OrdinalIgnoreCase));
    }
    private static string? CanonicalVersion(JsonElement value)
    {
        string? text = Text(value, 64);
        if (text == null) return null;
        string[] parts = text.Split('.');
        if (parts.Length != 4) return null;
        foreach (string part in parts)
            if (!ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out ushort number) ||
                number.ToString(CultureInfo.InvariantCulture) != part) return null;
        return text;
    }
    private static string? Text(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        string text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text != text.Trim() || text == "Unknown") return null;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return null;
            remaining = remaining[consumed..];
        }
        return text;
    }
}
