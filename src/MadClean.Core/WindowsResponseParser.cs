using System.Text.Json;

namespace MadClean.Core;

// Portable contract validation: malformed provider data must never become a healthy result.
public static class WindowsResponseParser
{
    public static CheckResult<DefenderInfo> Defender(string json) => Parse(json, value =>
    {
        bool? antivirus = Flag(value, "AntivirusEnabled"), realtime = Flag(value, "RealTimeProtectionEnabled"), service = Flag(value, "AMServiceEnabled");
        var findings = value.GetProperty("Findings").EnumerateArray().Select(f => new DefenderFinding(
            Scalar(f, "ThreatID"), Scalar(f, "ActionSuccess"), Scalar(f, "LastThreatStatusChangeTime"))).ToArray();
        string history = Scalar(value, "FindingsState");
        if (history is not ("Available" or "Failed")) throw new JsonException();
        var info = new DefenderInfo(antivirus, realtime, service, Scalar(value, "AMRunningMode"),
            Scalar(value, "AntivirusSignatureVersion"), Scalar(value, "SignatureUpdated"), history, findings)
            { ScanHistory = DefenderScanHistoryParser.Read(value, DateTimeOffset.UtcNow) };
        bool partial = history == "Failed" || antivirus == null || realtime == null || service == null;
        return new CheckResult<DefenderInfo>(partial ? CheckState.Partial : CheckState.Available,
            "Provider status and recorded Defender detections only; no new malware scan was run. Empty history does not establish a clean PC." +
            (history == "Failed" ? " Detection history could not be read; check Windows Security access." : ""), [info]);
    }, "Defender");

    public static CheckResult<SystemUpdate> SystemUpdates(string json) => Parse(json, value =>
    {
        int code = value.GetProperty("ResultCode").GetInt32();
        if (code == 5) return CheckResult<SystemUpdate>.Empty(CheckState.Cancelled, "Windows Update search was aborted; no completed check is claimed.");
        if (code is 0 or 1) return CheckResult<SystemUpdate>.Empty(CheckState.Unknown, "Windows Update search has not completed; retry from Windows Settings.");
        if (code is not (2 or 3)) return CheckResult<SystemUpdate>.Empty(CheckState.Failed, $"Windows Update search did not succeed (result {code}). Retry from Windows Settings.");
        var truncationFields = value.EnumerateObject().Where(field => field.Name == "Truncated").ToArray();
        if (truncationFields.Length > 1 || truncationFields.Length == 1 && truncationFields[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return CheckResult<SystemUpdate>.Empty(CheckState.Unknown, "Windows Update projection coverage is malformed; no complete check was inferred.");
        bool truncated = truncationFields.Length == 1 && truncationFields[0].Value.GetBoolean();
        var rows = value.GetProperty("Updates");
        if (rows.GetArrayLength() > 256)
            return CheckResult<SystemUpdate>.Empty(CheckState.Unknown, "Windows Update returned more than the supported 256 projected offers; unchecked or malformed rows were not silently accepted.");
        var updates = rows.EnumerateArray().Select(u =>
        {
            int type = u.GetProperty("Type").GetInt32();
            string driverClass = type == 2 ? OptionalText(u, "DriverClass", 128) : "Unknown";
            string kind = type switch { 1 => "Software", 2 when driverClass.Equals("Firmware", StringComparison.OrdinalIgnoreCase) => "Firmware", 2 => "Driver", _ => "Unknown" };
            var metadata = u.EnumerateObject().Where(field => field.Name == "OfferMetadata").ToArray();
            var identity = u.EnumerateObject().Where(field => field.Name == "IdentityMetadata").ToArray();
            return new SystemUpdate(RequiredText(u, "Title"), kind, RequiredText(u, "Id"), u.GetProperty("RebootRequired").GetBoolean(),
                driverClass, OptionalDate(u, "DriverVerDate"), OptionalText(u, "Description", 2300))
            { OfferMetadata = metadata.Length == 0 ? null : metadata.Length != 1 ? WindowsUpdateOfferMetadataParser.Unknown()
                : WindowsUpdateOfferMetadataParser.Parse(metadata[0].Value, type),
                IdentityMetadata = identity.Length == 0 ? null : identity.Length != 1 ? WindowsUpdateIdentityMetadataParser.Unknown()
                    : WindowsUpdateIdentityMetadataParser.Parse(identity[0].Value, type) };
        }).ToArray();
        bool incomplete = code == 3 || truncated || updates.Any(u => u.Kind == "Unknown" || u.Kind == "Driver" && u.DriverClass == "Unknown");
        return new CheckResult<SystemUpdate>(incomplete ? CheckState.Partial : CheckState.Available,
            "Offered, non-hidden updates from your configured Windows Update service. Driver/firmware coverage depends on this service, classification metadata and policy. " +
            "Firmware class can refer to system or device firmware; it does not verify BIOS/model/revision compatibility. No manufacturer-wide check or installation performed." +
            (truncated ? " Projection limited to at most 256 offers; additional offers remain unknown." : "") +
            " Optional driver and installation metadata has independent coverage; it does not establish complete prerequisites, compatibility or installation readiness.", updates);
    }, "Windows Update");

    private static CheckResult<T> Parse<T>(string json, Func<JsonElement, CheckResult<T>> read, string provider)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return read(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return CheckResult<T>.Empty(CheckState.Unknown, $"{provider} returned an unexpected response. Check provider compatibility; no successful check is claimed."); }
    }
    private static bool? Flag(JsonElement value, string name) => value.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new JsonException()
    };
    private static string OptionalText(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return "Unknown";
        if (field.ValueKind != JsonValueKind.String || field.GetString()!.Length > maximum) throw new JsonException();
        return string.IsNullOrWhiteSpace(field.GetString()) || field.GetString()!.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "Unknown" : field.GetString()!;
    }
    private static string OptionalDate(JsonElement value, string name)
    {
        string date = OptionalText(value, name, 32);
        if (date == "Unknown") return date;
        return DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "Unknown";
    }
    private static string RequiredText(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString())) throw new JsonException();
        return field.GetString()!;
    }
    private static string Scalar(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        return field.ValueKind switch
        {
            JsonValueKind.Null => "Unknown",
            JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => field.ToString(),
            _ => throw new JsonException()
        };
    }
}
