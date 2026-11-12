using System.Globalization;
using System.Text.Json;

namespace MadClean.Core;

public enum UpdateHistoryFieldState { Unknown, Known, Failed }
public sealed record UpdateHistoryText(UpdateHistoryFieldState State, string? Value);
public sealed record UpdateHistoryValue<T>(UpdateHistoryFieldState State, T? Value) where T : struct;

/// <summary>Provider history, not an installed-state, applicability or MadClean-operation observation.</summary>
public sealed record WindowsUpdateHistoryEntry(int Index, UpdateHistoryText Title,
    UpdateHistoryValue<DateTimeOffset> DateUtc, UpdateHistoryValue<int> Operation,
    UpdateHistoryValue<int> ResultCode, UpdateHistoryValue<int> HResult,
    UpdateHistoryValue<Guid> UpdateId, UpdateHistoryValue<int> Revision);

public sealed record WindowsUpdateHistoryReport
{
    public WindowsUpdateHistoryReport(DateTimeOffset QueryStartedUtc, DateTimeOffset CheckedUtc,
        int TotalBefore, int RequestedCount, IReadOnlyList<WindowsUpdateHistoryEntry> Entries)
    {
        this.QueryStartedUtc = QueryStartedUtc; this.CheckedUtc = CheckedUtc;
        this.TotalBefore = TotalBefore; this.RequestedCount = RequestedCount;
        this.Entries = Array.AsReadOnly(Entries.ToArray());
    }
    public DateTimeOffset QueryStartedUtc { get; }
    public DateTimeOffset CheckedUtc { get; }
    public int TotalBefore { get; }
    public int RequestedCount { get; }
    public IReadOnlyList<WindowsUpdateHistoryEntry> Entries { get; }
}

public interface IWindowsUpdateHistoryInventory
{
    Task<CheckResult<WindowsUpdateHistoryReport>> ReadAsync(CancellationToken token);
}

public static class WindowsUpdateHistoryPolicy
{
    public const int MaximumEntries = 128;
    public const int MaximumCharacters = 1_048_576;
    public const string Limits = "Recorded Windows Update history only; this bounded, non-atomic read may omit events. " +
        "Historical results do not establish current installation, applicability, available updates or MadClean ownership. " +
        "Revision is update identity metadata, not a software version. No update, removal or restart was requested.";
    private static readonly string[] RootFields = ["FormatVersion", "QueryStartedUtc", "CheckedUtc", "TotalBefore", "RequestedCount", "Entries"];
    private static readonly string[] RowFields = ["Index", "Title", "DateUtc", "Operation", "ResultCode", "HResult", "UpdateId", "Revision"];

    /// <summary>OLE DATE carries no zone. WUA history defines its fields as UTC, so never apply local conversion.</summary>
    public static DateTimeOffset? FromOleDateUtc(double value)
    {
        if (!double.IsFinite(value)) return null;
        try { return new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(value), DateTimeKind.Utc)); }
        catch (ArgumentException) { return null; }
    }

    public static CheckResult<WindowsUpdateHistoryReport> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumCharacters) return Invalid();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            if (!Shape(root, RootFields, requireAll: true) ||
                !Integer(root.GetProperty("FormatVersion"), out int version) || version != 1 ||
                !UtcDate(root.GetProperty("QueryStartedUtc"), out var started) ||
                !UtcDate(root.GetProperty("CheckedUtc"), out var checkedUtc) || started > checkedUtc ||
                !Integer(root.GetProperty("TotalBefore"), out int total) || total < 0 ||
                !Integer(root.GetProperty("RequestedCount"), out int requested) || requested != Math.Min(total, MaximumEntries)) return Invalid();
            var array = root.GetProperty("Entries");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > requested) return Invalid();
            List<WindowsUpdateHistoryEntry> entries = [];
            foreach (var row in array.EnumerateArray())
            {
                if (!Shape(row, RowFields, requireAll: false) || !row.TryGetProperty("Index", out var indexValue) ||
                    !Integer(indexValue, out int index) || index != entries.Count) return Invalid();
                entries.Add(new(index, Text(row, "Title"), Value(row, "DateUtc", OleDate),
                    Number(row, "Operation", value => value is 1 or 2), Number(row, "ResultCode", value => value is >= 0 and <= 5),
                    Number(row, "HResult", _ => true), Value(row, "UpdateId", Identity), Number(row, "Revision", value => value >= 0)));
            }
            var report = new WindowsUpdateHistoryReport(started, checkedUtc, total, requested, entries);
            return new(CheckState.Partial, "Windows Update history metadata returned. " + Limits, Array.AsReadOnly(new[] { report }));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException)
        { return Invalid(); }
    }

    /// <summary>Positive report admission only. Negative provider responses must be displayed as their separate status.</summary>
    public static bool IsConsistent(CheckResult<WindowsUpdateHistoryReport>? result, DateTimeOffset receivedUtc)
    {
        if (!Utc(receivedUtc) || result is not { State: CheckState.Partial, Items: { Count: 1 } } || result.Items[0] is not { } report ||
            !Utc(report.QueryStartedUtc) || !Utc(report.CheckedUtc) || report.QueryStartedUtc > report.CheckedUtc || report.CheckedUtc > receivedUtc ||
            report.TotalBefore < 0 || report.RequestedCount != Math.Min(report.TotalBefore, MaximumEntries) || report.Entries.Count > report.RequestedCount)
            return false;
        for (int index = 0; index < report.Entries.Count; index++)
        {
            var row = report.Entries[index];
            if (row == null || row.Index != index || !Valid(row.Title) || !Valid(row.DateUtc, Utc) ||
                !Valid(row.Operation, value => value is 1 or 2) || !Valid(row.ResultCode, value => value is >= 0 and <= 5) ||
                !Valid(row.HResult, _ => true) || !Valid(row.UpdateId, value => value != Guid.Empty) || !Valid(row.Revision, value => value >= 0)) return false;
        }
        return true;
    }

    private static UpdateHistoryText Text(JsonElement row, string name)
    {
        if (!Field(row, name, out var state, out var value)) return new(UpdateHistoryFieldState.Failed, null);
        if (state != UpdateHistoryFieldState.Known) return new(state, null);
        return value.ValueKind == JsonValueKind.String && SafeText(value.GetString())
            ? new(state, value.GetString()) : new(UpdateHistoryFieldState.Failed, null);
    }
    private static UpdateHistoryValue<int> Number(JsonElement row, string name, Func<int, bool> allowed)
    {
        if (!Field(row, name, out var state, out var value)) return new(UpdateHistoryFieldState.Failed, null);
        if (state != UpdateHistoryFieldState.Known) return new(state, null);
        if (!Integer(value, out int number)) return new(UpdateHistoryFieldState.Failed, null);
        return allowed(number) ? new(state, number) : new(UpdateHistoryFieldState.Unknown, null);
    }
    private static UpdateHistoryValue<T> Value<T>(JsonElement row, string name, Func<JsonElement, T?> convert) where T : struct
    {
        if (!Field(row, name, out var state, out var value)) return new(UpdateHistoryFieldState.Failed, null);
        if (state != UpdateHistoryFieldState.Known) return new(state, null);
        var converted = convert(value);
        return converted.HasValue ? new(state, converted) : new(UpdateHistoryFieldState.Failed, null);
    }
    private static bool Field(JsonElement row, string name, out UpdateHistoryFieldState state, out JsonElement value)
    {
        state = UpdateHistoryFieldState.Failed; value = default;
        if (!row.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Object) return false;
        // Duplicate/extra keys make the schema ambiguous; never choose an arbitrary duplicate value.
        if (!Shape(field, ["State", "Value"], requireAll: false)) throw new InvalidDataException();
        if (!field.TryGetProperty("State", out var stateValue) || !field.TryGetProperty("Value", out value) || stateValue.ValueKind != JsonValueKind.String)
            return false;
        state = stateValue.GetString() switch { "Known" => UpdateHistoryFieldState.Known, "Unknown" => UpdateHistoryFieldState.Unknown,
            "Failed" => UpdateHistoryFieldState.Failed, _ => (UpdateHistoryFieldState)(-1) };
        return Enum.IsDefined(state) && (state == UpdateHistoryFieldState.Known || value.ValueKind == JsonValueKind.Null);
    }
    private static DateTimeOffset? OleDate(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
        ? FromOleDateUtc(number) : null;
    private static Guid? Identity(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        string? text = value.GetString();
        return Guid.TryParseExact(text, "D", out var guid) && guid != Guid.Empty && guid.ToString("D") == text ? guid : null;
    }
    private static bool Shape(JsonElement value, string[] names, bool requireAll)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name)) return false;
        return !requireAll || found.Count == names.Length;
    }
    private static bool Integer(JsonElement value, out int number)
    { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number); }
    private static bool UtcDate(JsonElement value, out DateTimeOffset date)
    {
        date = default;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text ||
            !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))) return false;
        return value.TryGetDateTimeOffset(out date) && Utc(date);
    }
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool SafeText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 &&
        !value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format);
    private static bool Valid(UpdateHistoryText? value) => value != null && Enum.IsDefined(value.State) &&
        (value.State == UpdateHistoryFieldState.Known ? SafeText(value.Value) : value.Value == null);
    private static bool Valid<T>(UpdateHistoryValue<T>? value, Func<T, bool> allowed) where T : struct =>
        value != null && Enum.IsDefined(value.State) && (value.State == UpdateHistoryFieldState.Known
            ? value.Value is T actual && allowed(actual) : value.Value == null);
    private static CheckResult<WindowsUpdateHistoryReport> Invalid() => CheckResult<WindowsUpdateHistoryReport>.Empty(CheckState.Unknown,
        "No consistent bounded Windows Update history response was accepted. " + Limits);
}
