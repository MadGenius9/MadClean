using System.Globalization;
using System.Text.Json;

namespace MadClean.Core;

/// <summary>Parses minimized Event Log metadata into bounded observations, never diagnoses or repairs.</summary>
public static class DiagnosticEvidenceParser
{
    private const int EventLimit = 1000;
    private const int GroupLimit = 256;
    private const int ResponseLimit = 2_097_152;
    private static readonly string[] Channels = ["Application", "System"];
    private static readonly string[] DateFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
    private static readonly HashSet<string> RootFields = new(["Version", "StartUtc", "EndUtc", "Channels"], StringComparer.Ordinal);
    private static readonly HashSet<string> ChannelFields = new(["Name", "Status", "Truncated", "RecordCount", "OldestRecordId", "OldestUtc", "NewestUtc", "BoundsStatus", "Records"], StringComparer.Ordinal);
    private static readonly HashSet<string> EventFields = new(["Channel", "Provider", "EventId", "Version", "Level", "RecordId", "TimeUtc"], StringComparer.Ordinal);
    private sealed record Row(string Channel, string Provider, int EventId, int Version, int Level, long RecordId, DateTimeOffset Time);
    private sealed record GroupKey(string Channel, string Provider, int EventId, int Version);

    public static CheckResult<DiagnosticReport> Parse(string json, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (json is null || json.Length > ResponseLimit || string.IsNullOrWhiteSpace(json)) return Invalid(token);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (!Shape(root, RootFields) || root.GetProperty("Version").GetInt32() != 1 ||
                !Date(root.GetProperty("StartUtc"), out var start) || !Date(root.GetProperty("EndUtc"), out var end) ||
                !new[] { TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30) }.Contains(end - start)) return Invalid(token);
            var channelArray = root.GetProperty("Channels");
            if (channelArray.ValueKind != JsonValueKind.Array || channelArray.GetArrayLength() > 4) return Invalid(token);
            var coverage = new List<string>
            {
                $"Requested local Application and System only, fixed UTC interval [{start:O}, {end:O}). Filter: Windows-reported levels 1/2/3 plus Application / Windows Error Reporting / event 1001. At most {EventLimit} records per channel and {GroupLimit} grouped findings.",
                "Counts are event records, not guessed distinct crashes. No matching records is not a health verdict. Logs can be cleared, overwritten or unavailable; this query cannot recreate missing history. Event messages, payload properties, user identities, application names, paths and dump contents were not collected."
            };
            bool partial = false;
            int successful = 0, failed = 0, queryFailures = 0;
            var rows = new List<Row>();
            var inputs = Channels.ToDictionary(name => name, _ => new List<JsonElement>(), StringComparer.Ordinal);
            foreach (var channel in channelArray.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (channel.ValueKind != JsonValueKind.Object || !channel.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !inputs.TryGetValue(name.GetString()!, out var matches))
                { partial = true; coverage.Add("An unrecognized channel response was omitted; Security/remote/custom channels are outside this report."); continue; }
                matches.Add(channel);
            }
            foreach (string name in Channels)
            {
                token.ThrowIfCancellationRequested();
                var matches = inputs[name];
                if (matches.Count != 1)
                { partial = true; coverage.Add($"{name}: {(matches.Count == 0 ? "missing channel response" : "duplicate/ambiguous channel responses")}; no records trusted."); continue; }
                var source = matches[0];
                if (!Shape(source, ChannelFields) || source.GetProperty("Status").ValueKind != JsonValueKind.String)
                { partial = true; coverage.Add($"{name}: malformed channel response; coverage unknown."); continue; }
                string? status = source.GetProperty("Status").GetString();
                if (status != "Available")
                {
                    partial = true;
                    if (status is "Denied" or "Unavailable" or "Disabled") failed++;
                    if (status == "Failed") queryFailures++;
                    coverage.Add($"{name}: " + (status switch
                    {
                        "Denied" => "access denied; check existing Event Log read permissions. No elevation was requested.",
                        "Unavailable" => "channel/provider unavailable; no matching-history claim is possible.",
                        "Disabled" => "channel is disabled; it was not enabled or modified.",
                        "Failed" => "query failed; check Windows Event Log service and local access before retrying.",
                        _ => "unrecognized query status; no records trusted."
                    }));
                    continue;
                }
                successful++;
                if (source.GetProperty("Truncated").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    source.GetProperty("Records").ValueKind != JsonValueKind.Array)
                { partial = true; coverage.Add($"{name}: malformed count-limit/record metadata; no records trusted."); continue; }
                var records = source.GetProperty("Records");
                bool limited = source.GetProperty("Truncated").GetBoolean() || records.GetArrayLength() > EventLimit;
                if (limited) partial = true;
                long? count = NullablePositiveLong(source.GetProperty("RecordCount"), allowZero: true);
                long? oldestId = NullablePositiveLong(source.GetProperty("OldestRecordId"), allowZero: false);
                string? bounds = source.GetProperty("BoundsStatus").ValueKind == JsonValueKind.String ? source.GetProperty("BoundsStatus").GetString() : null;
                bool oldestKnown = Date(source.GetProperty("OldestUtc"), out var oldest);
                bool newestKnown = Date(source.GetProperty("NewestUtc"), out var newest);
                if (bounds == "Available" && (!oldestKnown || !newestKnown || oldest > newest || oldestId == null)) { bounds = "Unknown"; partial = true; }
                if (bounds is not ("Available" or "Empty")) { bounds = "Unknown"; partial = true; }
                if (bounds == "Empty" && (count != 0 || oldestKnown || newestKnown)) { bounds = "Unknown"; partial = true; }
                if (count == null) partial = true;
                string retention = bounds == "Available"
                    ? $"oldest/newest currently retained event times {oldest:O} / {newest:O} (whole-log probes, not matching-event or continuous-coverage proof)"
                    : bounds == "Empty" ? "whole-log metadata/probes indicated an empty log" : "oldest/newest retention bounds unknown or failed";
                if (bounds == "Available" && oldest > start)
                { partial = true; retention += "; oldest retained event is later than requested start: earlier history may be absent or overwritten"; }

                int invalid = 0, duplicates = 0;
                var accepted = new Dictionary<long, Row>();
                var ambiguous = new HashSet<long>();
                foreach (var item in records.EnumerateArray().Take(EventLimit))
                {
                    token.ThrowIfCancellationRequested();
                    if (!TryRow(item, name, start, end, out var row)) { invalid++; partial = true; continue; }
                    var valid = row!;
                    if (ambiguous.Contains(valid.RecordId)) { duplicates++; continue; }
                    if (accepted.TryGetValue(valid.RecordId, out var previous))
                    {
                        duplicates++; partial = true;
                        if (previous != valid) { accepted.Remove(valid.RecordId); ambiguous.Add(valid.RecordId); }
                        continue;
                    }
                    accepted.Add(valid.RecordId, valid);
                }
                if (count.HasValue && count.Value < accepted.Count || oldestId.HasValue && accepted.Keys.Any(id => id < oldestId.Value))
                { partial = true; retention += "; log metadata and selected records changed or conflict; snapshot is not atomic"; }
                rows.AddRange(accepted.Values);
                string observed = accepted.Count == 0 ? "no matching usable records returned; not a healthy-system determination"
                    : $"{accepted.Count} usable matching event records; matching times {accepted.Values.Min(row => row.Time):O} / {accepted.Values.Max(row => row.Time):O}";
                coverage.Add($"{name}: queried; {observed}; current whole-log record count {(count?.ToString(CultureInfo.InvariantCulture) ?? "Unknown")}; {retention}" +
                    (limited ? "; TRUNCATED: count limit reached, older matching records may be missing" : "") +
                    (invalid > 0 ? $"; {invalid} records rejected for missing/invalid/private fields, channel/filter/time mismatch" : "") +
                    (duplicates > 0 ? $"; {duplicates} duplicate/ambiguous record references omitted; counts do not double-count identical references" : "") + ".");
            }
            var grouped = rows.GroupBy(row => new GroupKey(row.Channel, row.Provider, row.EventId, row.Version))
                .OrderByDescending(group => group.Max(row => row.Time)).ToArray();
            if (grouped.Length > GroupLimit) { partial = true; coverage.Add("Finding-group limit reached; additional groups were omitted. This report is incomplete."); }
            var findings = new List<DiagnosticFinding>();
            foreach (var group in grouped.Take(GroupLimit))
            {
                token.ThrowIfCancellationRequested();
                var key = group.Key;
                var annotation = DiagnosticEvidenceAnnotations.For(key.Channel, key.Provider, key.EventId);
                var levels = group.Select(row => row.Level).Distinct().ToArray();
                findings.Add(new(key.Channel, key.Provider, key.EventId, key.Version,
                    levels.Length == 1 ? Level(levels[0]) : "Mixed Windows-reported levels", group.Count(),
                    group.Min(row => row.Time), group.Max(row => row.Time), group.Select(row => row.RecordId).Order().ToArray(),
                    annotation.Observation, annotation.Uncertainty + $" Schema version {key.Version} is preserved; event payloads were not inspected.", annotation.Impact, annotation.NextStep));
            }
            token.ThrowIfCancellationRequested();
            var report = new DiagnosticReport(start, end, findings, coverage);
            var state = successful == 0
                ? queryFailures > 0 ? CheckState.Failed : failed == 2 ? CheckState.Unavailable : CheckState.Unknown
                : partial ? CheckState.Partial : CheckState.Available;
            return new(state, "Passive local Event Log evidence only. Review per-channel coverage and uncertainty; no integrity checks, diagnosis or repair were performed.", [report]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        { return Invalid(token); }
    }

    private static bool TryRow(JsonElement item, string channel, DateTimeOffset start, DateTimeOffset end, out Row? row)
    {
        row = null;
        if (!Shape(item, EventFields) || item.GetProperty("Channel").ValueKind != JsonValueKind.String || item.GetProperty("Channel").GetString() != channel ||
            item.GetProperty("Provider").ValueKind != JsonValueKind.String) return false;
        string provider = item.GetProperty("Provider").GetString()!;
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 256 || provider.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)) return false;
        if (!Integer(item.GetProperty("EventId"), 0, 65535, out int id) || !Integer(item.GetProperty("Version"), 0, 255, out int version) ||
            !Integer(item.GetProperty("Level"), 0, 5, out int level) || item.GetProperty("RecordId").ValueKind != JsonValueKind.Number || !item.GetProperty("RecordId").TryGetInt64(out long record) || record <= 0 ||
            !Date(item.GetProperty("TimeUtc"), out var time) || time < start || time >= end) return false;
        if (level is not (1 or 2 or 3) && !(channel == "Application" && provider == "Windows Error Reporting" && id == 1001)) return false;
        row = new(channel, provider, id, version, level, record, time);
        return true;
    }
    private static bool Shape(JsonElement value, HashSet<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name) || !names.Add(property.Name)) return false;
        return names.SetEquals(fields);
    }
    private static bool Integer(JsonElement value, int min, int max, out int number)
    { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number) && number >= min && number <= max; }
    private static long? NullablePositiveLong(JsonElement value, bool allowZero) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && (allowZero ? number >= 0 : number > 0) ? number : null;
    private static bool Date(JsonElement value, out DateTimeOffset date)
    {
        date = default;
        if (value.ValueKind != JsonValueKind.String) return false;
        string text = value.GetString()!;
        return text.Length <= 40 && (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) &&
            DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }
    private static string Level(int value) => value switch
    { 0 => "LogAlways (Windows-reported)", 1 => "Critical (Windows-reported)", 2 => "Error (Windows-reported)", 3 => "Warning (Windows-reported)", 4 => "Information (Windows-reported)", _ => "Verbose (Windows-reported)" };
    private static CheckResult<DiagnosticReport> Invalid(CancellationToken token)
    {
        // Cancellation during parsing/validation takes precedence over an unknown-envelope result.
        token.ThrowIfCancellationRequested();
        return CheckResult<DiagnosticReport>.Empty(CheckState.Unknown,
            "Diagnostic evidence response was malformed, oversized or used an unsupported time window/schema. No findings or health verdict were inferred; retry/check provider compatibility.");
    }
}
