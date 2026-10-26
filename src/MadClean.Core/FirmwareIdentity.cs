using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MadClean.Core;

public sealed record FirmwareIdentity(
    string PcManufacturer, string PcModel, string PcSku,
    string BoardManufacturer, string BoardProduct, string BoardRevision,
    string BiosManufacturer, string BiosVersion, string BiosReleaseDate);

public interface IFirmwareIdentityInventory
{
    Task<CheckResult<FirmwareIdentity>> ReadAsync(CancellationToken token);
}

/// <summary>Parses an explicit projection of firmware identity, not an update/compatibility verdict.</summary>
public static class FirmwareIdentityParser
{
    private const string Unknown = "Unknown";
    private const int MaximumResponseCharacters = 65_536;
    private const string Provenance = "Firmware-reported identity only; not manufacturer-verified compatibility. " +
        "No serial numbers, UUIDs or service tags were collected. No firmware update check or flash was performed.";
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "unknown", "none", "null", "na", "notapplicable", "notavailable", "notspecified", "unspecified",
        "defaultstring", "tobefilledbyoem", "systemproductname", "systemmanufacturer", "systemversion",
        "systemsku", "baseboardproductname", "baseboardmanufacturer", "baseboardversion", "systembiosversion",
        "oem", "invalid"
    };

    // SchemaVersion=1; each source has Status=Available|Failed and an explicitly projected Records array.
    // Exactly one successful record is required per source. Other sources are not used as substitutes.
    public static CheckResult<FirmwareIdentity> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseCharacters)
            return InvalidEnvelope();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                !root.TryGetProperty("SchemaVersion", out var version) || !version.TryGetInt32(out int schema) || schema != 1)
                return InvalidEnvelope();
            if (!root.TryGetProperty("ComputerSystem", out _) && !root.TryGetProperty("BaseBoard", out _) && !root.TryGetProperty("Bios", out _))
                return InvalidEnvelope();

            List<string> issues = [];
            JsonElement? computer = Source(root, "ComputerSystem", issues);
            JsonElement? board = Source(root, "BaseBoard", issues);
            JsonElement? bios = Source(root, "Bios", issues);
            string date = Text(bios, "ReleaseDate", "Bios", issues);
            if (date != Unknown)
            {
                string normalized = FirmwareDate(date);
                if (normalized == Unknown) issues.Add("Bios.ReleaseDate was not a supported firmware date; the date remains Unknown.");
                date = normalized;
            }
            var identity = new FirmwareIdentity(
                Text(computer, "Manufacturer", "ComputerSystem", issues),
                Text(computer, "Model", "ComputerSystem", issues),
                Text(computer, "SystemSKUNumber", "ComputerSystem", issues),
                Text(board, "Manufacturer", "BaseBoard", issues),
                Text(board, "Product", "BaseBoard", issues),
                Text(board, "Version", "BaseBoard", issues),
                Text(bios, "Manufacturer", "Bios", issues),
                Text(bios, "SMBIOSBIOSVersion", "Bios", issues), date);
            bool incomplete = new[]
            {
                identity.PcManufacturer, identity.PcModel, identity.PcSku,
                identity.BoardManufacturer, identity.BoardProduct, identity.BoardRevision,
                identity.BiosManufacturer, identity.BiosVersion, identity.BiosReleaseDate
            }.Any(value => value == Unknown);
            if (incomplete) issues.Add("Unavailable, blank or placeholder fields remain Unknown; no sole model or compatibility has been inferred.");
            return new(issues.Count == 0 ? CheckState.Available : CheckState.Partial,
                Provenance + (issues.Count == 0 ? "" : " " + string.Join(" ", issues)), [identity]);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            return InvalidEnvelope();
        }
    }

    private static JsonElement? Source(JsonElement root, string name, List<string> issues)
    {
        if (!root.TryGetProperty(name, out var source) || source.ValueKind != JsonValueKind.Object || HasDuplicateProperties(source) ||
            !source.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.String ||
            !source.TryGetProperty("Records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            issues.Add($"Parse error: {name} did not return the expected source envelope; its identity fields remain Unknown.");
            return null;
        }
        string? state = status.GetString();
        if (state == "Failed")
        {
            issues.Add(records.GetArrayLength() == 0
                ? $"{name} query failed or access was denied; its identity fields remain Unknown. Check the local CIM provider and access policy before retrying."
                : $"Parse error: {name} returned records together with a failed query; those records were not trusted.");
            return null;
        }
        if (state != "Available")
        {
            issues.Add($"Parse error: {name} returned an unrecognized query status; its identity fields remain Unknown.");
            return null;
        }
        int count = records.GetArrayLength();
        if (count != 1)
        {
            issues.Add(count == 0
                ? $"{name} returned no record; its identity fields remain Unknown."
                : $"{name} returned multiple records; no sole identity was chosen and its fields remain Unknown.");
            return null;
        }
        var record = records[0];
        if (record.ValueKind != JsonValueKind.Object || HasDuplicateProperties(record))
        {
            issues.Add($"Parse error: {name} returned an invalid or ambiguous record; its identity fields remain Unknown.");
            return null;
        }
        return record;
    }

    private static string Text(JsonElement? record, string property, string source, List<string> issues)
    {
        if (record == null) return Unknown;
        if (!record.Value.TryGetProperty(property, out var field) || field.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            issues.Add($"Parse error: {source}.{property} was missing or not text/null; this field remains Unknown.");
            return Unknown;
        }
        if (field.ValueKind == JsonValueKind.Null) return Unknown;
        string value = field.GetString()!.Trim();
        if (value.Length == 0) return Unknown;
        if (value.Length > 512 || value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            issues.Add($"Parse error: {source}.{property} contained unsupported text; this field remains Unknown.");
            return Unknown;
        }
        string key = string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return Placeholders.Contains(key) ? Unknown : value;
    }

    private static string FirmwareDate(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // A raw CIM datetime is also accepted. Preserve its reported calendar date without timezone conversion.
        if (value.Length == 25 && value[14] == '.' && value[21] is '+' or '-' &&
            value.Where((_, index) => index is not (14 or 21)).All(character => character is >= '0' and <= '9') &&
            DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
            int.Parse(value.AsSpan(8, 2), CultureInfo.InvariantCulture) <= 23 &&
            int.Parse(value.AsSpan(10, 2), CultureInfo.InvariantCulture) <= 59 &&
            int.Parse(value.AsSpan(12, 2), CultureInfo.InvariantCulture) <= 59)
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Unknown;
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !names.Add(property.Name));
    }
    private static CheckResult<FirmwareIdentity> InvalidEnvelope() => CheckResult<FirmwareIdentity>.Empty(CheckState.Unknown,
        "Firmware identity response could not be parsed or used an unsupported schema. No identity was inferred; retry or report provider compatibility. " + Provenance);
}
