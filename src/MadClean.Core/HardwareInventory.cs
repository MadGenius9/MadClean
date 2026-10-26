using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MadClean.Core;

public record HardwareFact(string Category, string Component, string Property, string Value, string Source);
public interface IHardwareInventory
{
    Task<CheckResult<HardwareFact>> ReadAsync(CancellationToken token);
}

/// <summary>Bounded parsing of reported hardware observations, never compatibility or health conclusions.</summary>
public static class HardwareInventoryParser
{
    private const int RecordLimit = 128;
    private const int ResponseLimit = 1_048_576;
    private const string Unknown = "Unknown";
    private const string Caveat = "Detected/provider-reported observations only; not manufacturer-verified compatibility or hardware health. " +
        "Raw memory codes are not decoded compatibility specifications. Counts never establish free slots, replaceability, PSU/connectors or case clearance. " +
        "Disk interface reports do not establish physical connectors; drive firmware is not the system BIOS. No serial/UUID/device-instance identifiers were projected.";
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "unknown", "none", "null", "na", "notavailable", "notapplicable", "notspecified", "unspecified", "defaultstring",
        "tobefilledbyoem", "systemproductname", "systemmanufacturer", "systemversion", "systemfamily", "systemsku",
        "baseboardproductname", "baseboardmanufacturer", "baseboardversion", "oem", "invalid"
    };
    private enum Kind { Text, Bytes, PositiveNumber, FormFactor, MemoryType }
    private sealed record Field(string Name, Kind Kind = Kind.Text, string? Label = null);
    private sealed record Definition(string Class, string Category, string Component, Field[] Fields);
    private static readonly Definition[] Definitions =
    [
        new("Win32_ComputerSystem", "PC", "PC record", [new("Manufacturer"), new("Model"), new("SystemFamily"), new("SystemSKUNumber"), new("SystemType")]),
        new("Win32_BaseBoard", "Baseboard", "Baseboard record", [new("Manufacturer"), new("Product"), new("Version", Label: "Version (reported; not verified PCB revision)")]),
        new("Win32_Processor", "CPU", "Processor", [new("Manufacturer"), new("Name"), new("NumberOfCores", Kind.PositiveNumber), new("NumberOfLogicalProcessors", Kind.PositiveNumber), new("MaxClockSpeed", Kind.PositiveNumber, "MaxClockSpeed (reported MHz)"), new("SocketDesignation")]),
        new("Win32_PhysicalMemory", "RAM", "Memory device", [new("Manufacturer"), new("PartNumber"), new("Capacity", Kind.Bytes, "Capacity (bytes)"), new("SMBIOSMemoryType", Kind.MemoryType, "SMBIOSMemoryType (raw code; not decoded)"), new("FormFactor", Kind.FormFactor, "FormFactor (raw code; not replaceability)"), new("ConfiguredClockSpeed", Kind.PositiveNumber, "ConfiguredClockSpeed (reported MHz; not marketing transfer rate)")]),
        new("Win32_VideoController", "GPU", "Video controller", [new("Name"), new("VideoProcessor"), new("DriverVersion")]),
        new("Win32_DiskDrive", "Storage", "Disk", [new("Manufacturer"), new("Model"), new("MediaType"), new("InterfaceType"), new("Size", Kind.Bytes, "Size (bytes)"), new("FirmwareRevision", Label: "FirmwareRevision (drive firmware; not BIOS)")])
    ];

    // Version=1, Sources=[{Class,Status=Available|Failed,Truncated:bool,Records:[projected objects]}].
    public static CheckResult<HardwareFact> Parse(string json)
    {
        if (json is null || json.Length > ResponseLimit || string.IsNullOrWhiteSpace(json)) return Invalid();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Duplicates(root) ||
                !root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int number) || number != 1 ||
                !root.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > 12)
                return Invalid();
            List<HardwareFact> facts = [];
            List<string> coverage = [];
            bool partial = false;
            int recognizedSources = 0, failedSources = 0;
            bool unexpectedSource = false;
            var grouped = Definitions.ToDictionary(d => d.Class, _ => new List<JsonElement>(), StringComparer.Ordinal);
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("Class", out var name) || name.ValueKind != JsonValueKind.String ||
                    !grouped.TryGetValue(name.GetString()!, out var matches))
                { partial = true; unexpectedSource = true; continue; }
                matches.Add(source);
            }
            if (unexpectedSource) coverage.Add("An unexpected or unrecognized source envelope was omitted; its coverage is untrusted.");
            foreach (var definition in Definitions)
            {
                var matches = grouped[definition.Class];
                if (matches.Count != 1)
                {
                    partial = true;
                    coverage.Add($"{definition.Class}: {(matches.Count == 0 ? "missing source; retry the local provider" : "duplicate/ambiguous sources; records not trusted")}.");
                    continue;
                }
                recognizedSources++;
                var source = matches[0];
                if (Duplicates(source) || !source.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.String ||
                    !source.TryGetProperty("Truncated", out var truncated) || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !source.TryGetProperty("Records", out var records) || records.ValueKind != JsonValueKind.Array)
                {
                    partial = true; coverage.Add($"{definition.Class}: malformed source response; no observations trusted."); continue;
                }
                if (status.GetString() == "Failed")
                {
                    failedSources++;
                    partial = true; coverage.Add($"{definition.Class}: query failed or access denied; check local CIM provider/access policy and retry."); continue;
                }
                if (status.GetString() != "Available")
                { partial = true; coverage.Add($"{definition.Class}: unrecognized query status; no observations trusted."); continue; }
                int reported = records.GetArrayLength();
                if (reported == 0)
                {
                    partial = true; coverage.Add($"{definition.Class}: empty response; absence of components or unused slots cannot be inferred."); continue;
                }
                bool limited = truncated.GetBoolean() || reported > RecordLimit;
                if (limited) partial = true;
                var unknownFields = new HashSet<string>(StringComparer.Ordinal);
                int malformed = 0, parsed = 0;
                foreach (var record in records.EnumerateArray().Take(RecordLimit))
                {
                    parsed++;
                    if (record.ValueKind != JsonValueKind.Object || Duplicates(record)) { malformed++; partial = true; continue; }
                    foreach (var field in definition.Fields)
                    {
                        string value = Value(record, field, out bool known);
                        if (!known) { partial = true; unknownFields.Add(field.Name); }
                        facts.Add(new(definition.Category, $"{definition.Component} {parsed}", field.Label ?? field.Name, value,
                            $"root/cimv2:{definition.Class}.{field.Name}"));
                    }
                }
                coverage.Add($"{definition.Class}: {parsed - malformed} reported record(s)" +
                    (limited ? "; TRUNCATED at 128; further records unknown" : "") +
                    (malformed > 0 ? $"; {malformed} malformed record(s) omitted" : "") +
                    (unknownFields.Count > 0 ? "; unknown/uninterpreted/invalid fields: " + string.Join(", ", unknownFields.Order(StringComparer.Ordinal)) : "") + ".");
            }
            if (recognizedSources == 0) return new(CheckState.Unknown, "No unique allowlisted hardware source was returned. " + string.Join(" ", coverage) + " " + Caveat, facts);
            return new(failedSources == Definitions.Length ? CheckState.Failed : partial ? CheckState.Partial : CheckState.Available, string.Join(" ", coverage) + " " + Caveat, facts);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { return Invalid(); }
    }

    private static string Value(JsonElement record, Field field, out bool known)
    {
        known = false;
        if (!record.TryGetProperty(field.Name, out var value) || value.ValueKind == JsonValueKind.Null) return Unknown;
        if (field.Kind == Kind.Text)
        {
            if (value.ValueKind != JsonValueKind.String) return Unknown;
            string text = value.GetString()!.Trim();
            if (text.Length is 0 or > 512 || text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)) return Unknown;
            string key = string.Concat(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
            if (key.Length == 0 || Placeholders.Contains(key)) return Unknown;
            known = true; return text;
        }
        ulong number;
        if (value.ValueKind == JsonValueKind.Number)
        { if (!value.TryGetUInt64(out number)) return Unknown; }
        else if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            if (text.Length is 0 or > 20 || text.Any(c => c is < '0' or > '9') ||
                !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)) return Unknown;
        }
        else return Unknown;
        if (field.Kind != Kind.Bytes && number > uint.MaxValue) return Unknown;
        if (field.Kind == Kind.MemoryType)
            return $"Unknown meaning (reported raw SMBIOS code {number.ToString(CultureInfo.InvariantCulture)})";
        if (field.Kind == Kind.FormFactor)
        {
            if (number == 0 || number > 23)
                return $"Unknown (reported raw code {number.ToString(CultureInfo.InvariantCulture)})";
        }
        else if (number == 0) return Unknown;
        known = true; return number.ToString(CultureInfo.InvariantCulture);
    }
    private static bool Duplicates(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !names.Add(property.Name));
    }
    private static CheckResult<HardwareFact> Invalid() => CheckResult<HardwareFact>.Empty(CheckState.Unknown,
        "Hardware response was malformed, oversized or used an unsupported schema; no specifications were inferred. Retry/check provider compatibility. " + Caveat);
}
