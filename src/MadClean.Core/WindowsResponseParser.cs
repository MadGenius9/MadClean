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
            Scalar(value, "AntivirusSignatureVersion"), Scalar(value, "SignatureUpdated"), history, findings);
        bool partial = history == "Failed" || antivirus == null || realtime == null || service == null;
        return new CheckResult<DefenderInfo>(partial ? CheckState.Partial : CheckState.Available,
            "Provider status and recorded Defender detections only; no new malware scan was run. Empty history does not establish a clean PC." +
            (history == "Failed" ? " Detection history could not be read; check Windows Security access." : ""), [info]);
    }, "Defender");

    public static CheckResult<SystemUpdate> SystemUpdates(string json) => Parse(json, value =>
    {
        int code = value.GetProperty("ResultCode").GetInt32();
        if (code is not (2 or 3)) return CheckResult<SystemUpdate>.Empty(CheckState.Failed, $"Windows Update search did not succeed (result {code}). Retry from Windows Settings.");
        var updates = value.GetProperty("Updates").EnumerateArray().Select(u => new SystemUpdate(
            Scalar(u, "Title"), u.GetProperty("Type").GetInt32() switch { 1 => "Software", 2 => "Driver", _ => "Unknown" },
            Scalar(u, "Id"), u.GetProperty("RebootRequired").GetBoolean())).ToArray();
        return new CheckResult<SystemUpdate>(code == 3 ? CheckState.Partial : CheckState.Available,
            "Offered, non-hidden updates from your configured Windows Update service. Driver coverage depends on that service and policy; no manufacturer-wide check or installation performed.", updates);
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
