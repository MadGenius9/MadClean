using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MadClean.Core;

public enum SystemToolJournalPhase { DispatchIntent, ClientObserved, ClientExited, ObservationLost, LaunchDenied }
public enum SystemToolJournalState { Unknown, Unresolved, RecordedClientExit, RecordedLaunchDenied }
public sealed record SystemToolJournalEntry(Guid ExecutionId, SystemToolOperation Operation, int Sequence,
    DateTimeOffset TimestampUtc, SystemToolJournalPhase Phase, int? ProcessId, DateTimeOffset? ClientStartUtc,
    int? ExitCode, int? NativeError);
public sealed record SystemToolJournalReview(SystemToolJournalState State, IReadOnlyList<SystemToolJournalEntry> Entries, string Message);

/// <summary>Strict bounded record review. Neither valid bytes nor terminal observations prove authorship, health or permission.</summary>
public static class SystemToolJournal
{
    public const int MaximumUtf8Bytes = 4096;
    public const int MaximumEntries = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] EntryFields =
        ["ExecutionId", "Operation", "Sequence", "TimestampUtc", "Phase", "ProcessId", "ClientStartUtc", "ExitCode", "NativeError"];

    public static SystemToolJournalReview Review(IReadOnlyList<SystemToolJournalEntry>? entries, DateTimeOffset nowUtc)
    {
        try
        {
            if (!Utc(nowUtc) || entries == null || entries.Count is < 1 or > MaximumEntries) return Unknown();
            int count = entries.Count;
            var copy = new SystemToolJournalEntry[count];
            for (int i = 0; i < count; i++)
            {
                var row = entries[i];
                if (row == null || row.ExecutionId == Guid.Empty || SystemToolActionsCatalog.Find(row.Operation) == null ||
                    !Enum.IsDefined(row.Phase) || row.Sequence != i + 1 || !Utc(row.TimestampUtc) || row.TimestampUtc > nowUtc ||
                    row.ProcessId is <= 0 || row.ClientStartUtc is { } start && (!Utc(start) || start > row.TimestampUtc) ||
                    i > 0 && (row.ExecutionId != copy[0].ExecutionId || row.Operation != copy[0].Operation || row.TimestampUtc < copy[i - 1].TimestampUtc))
                    return Unknown();
                copy[i] = row with { };
            }
            if (entries.Count != count) return Unknown();
            var first = copy[0];
            if (first.Phase != SystemToolJournalPhase.DispatchIntent || first.ProcessId.HasValue || first.ClientStartUtc.HasValue ||
                first.ExitCode.HasValue || first.NativeError.HasValue) return Unknown();
            var state = SystemToolJournalState.Unresolved;
            if (count >= 2)
            {
                var second = copy[1];
                switch (second.Phase)
                {
                    case SystemToolJournalPhase.ClientObserved:
                        if (!second.ProcessId.HasValue || second.ExitCode.HasValue || second.NativeError.HasValue) return Unknown();
                        break;
                    case SystemToolJournalPhase.ObservationLost:
                        if (count != 2 || second.ExitCode.HasValue || !second.ProcessId.HasValue && second.ClientStartUtc.HasValue) return Unknown();
                        break;
                    case SystemToolJournalPhase.LaunchDenied:
                        if (count != 2 || second.ProcessId.HasValue || second.ClientStartUtc.HasValue || second.ExitCode.HasValue || second.NativeError != 1223) return Unknown();
                        state = SystemToolJournalState.RecordedLaunchDenied;
                        break;
                    default: return Unknown();
                }
            }
            if (count == 3)
            {
                var observed = copy[1]; var terminal = copy[2];
                if (observed.Phase != SystemToolJournalPhase.ClientObserved || terminal.ProcessId != observed.ProcessId ||
                    terminal.ClientStartUtc != observed.ClientStartUtc) return Unknown();
                if (terminal.Phase == SystemToolJournalPhase.ClientExited)
                {
                    if (!terminal.ExitCode.HasValue || terminal.NativeError.HasValue) return Unknown();
                    state = SystemToolJournalState.RecordedClientExit;
                }
                else if (terminal.Phase != SystemToolJournalPhase.ObservationLost || terminal.ExitCode.HasValue) return Unknown();
            }
            return new(state, Array.AsReadOnly(copy), state switch
            {
                SystemToolJournalState.RecordedClientExit => "A client exit was recorded; its code is not an integrity, health or repair verdict. Record validity does not prove authorship or action eligibility.",
                SystemToolJournalState.RecordedLaunchDenied => "Windows launch denial was recorded with native code 1223; journal validity does not authenticate the record or grant action eligibility.",
                _ => "The recorded dispatch/observation remains unresolved. This is not proof that a process is running or stopped, and grants no action eligibility."
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        { return Unknown(); }
    }

    public static SystemToolJournalReview Decode(ReadOnlySpan<byte> utf8, DateTimeOffset nowUtc)
    {
        if (utf8.Length is 0 or > MaximumUtf8Bytes) return Unknown();
        try
        {
            using var document = JsonDocument.Parse(StrictUtf8.GetString(utf8), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!Fields(root, ["Version", "Entries"]) || !Integer(root.GetProperty("Version"), out var version) || version != 1 ||
                root.GetProperty("Entries").ValueKind != JsonValueKind.Array) return Unknown();
            var array = root.GetProperty("Entries");
            if (array.GetArrayLength() is < 1 or > MaximumEntries) return Unknown();
            List<SystemToolJournalEntry> entries = [];
            foreach (var row in array.EnumerateArray())
            {
                if (!Fields(row, EntryFields) || !Text(row.GetProperty("ExecutionId"), out var idText) ||
                    !Guid.TryParseExact(idText, "D", out var id) || id.ToString("D") != idText ||
                    !Named(row.GetProperty("Operation"), out SystemToolOperation operation) ||
                    !Integer(row.GetProperty("Sequence"), out var sequence) || !Date(row.GetProperty("TimestampUtc"), out var timestamp) ||
                    !Named(row.GetProperty("Phase"), out SystemToolJournalPhase phase) ||
                    !NullableInteger(row.GetProperty("ProcessId"), out var pid) ||
                    !NullableDate(row.GetProperty("ClientStartUtc"), out var clientStart) ||
                    !NullableInteger(row.GetProperty("ExitCode"), out var exit) || !NullableInteger(row.GetProperty("NativeError"), out var error))
                    return Unknown();
                entries.Add(new(id, operation, sequence, timestamp, phase, pid, clientStart, exit, error));
            }
            return Review(entries, nowUtc);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException or InvalidOperationException or FormatException)
        { return Unknown(); }
    }

    public static bool TryEncode(IReadOnlyList<SystemToolJournalEntry>? entries, DateTimeOffset nowUtc, out byte[]? utf8)
    {
        utf8 = null;
        var review = Review(entries, nowUtc);
        if (review.State == SystemToolJournalState.Unknown) return false;
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject(); writer.WriteNumber("Version", 1); writer.WriteStartArray("Entries");
            foreach (var row in review.Entries)
            {
                writer.WriteStartObject(); writer.WriteString("ExecutionId", row.ExecutionId.ToString("D"));
                writer.WriteString("Operation", row.Operation.ToString()); writer.WriteNumber("Sequence", row.Sequence);
                writer.WriteString("TimestampUtc", Stamp(row.TimestampUtc)); writer.WriteString("Phase", row.Phase.ToString());
                Write(writer, "ProcessId", row.ProcessId);
                if (row.ClientStartUtc is { } start) writer.WriteString("ClientStartUtc", Stamp(start)); else writer.WriteNull("ClientStartUtc");
                Write(writer, "ExitCode", row.ExitCode); Write(writer, "NativeError", row.NativeError); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        if (memory.Length > MaximumUtf8Bytes) return false;
        utf8 = memory.ToArray(); return true;
    }

    /// <summary>Validates the entire append-only snapshot history, including its final line and immutable prefixes.</summary>
    public static SystemToolJournalReview DecodeSnapshots(ReadOnlySpan<byte> utf8, DateTimeOffset nowUtc)
    {
        if (utf8.Length is 0 or > MaximumUtf8Bytes || utf8[^1] != (byte)'\n') return Unknown();
        SystemToolJournalReview? previous = null;
        int offset = 0, lines = 0;
        while (offset < utf8.Length)
        {
            if (++lines > MaximumEntries) return Unknown();
            int length = utf8[offset..].IndexOf((byte)'\n');
            if (length <= 0) return Unknown();
            var current = Decode(utf8.Slice(offset, length), nowUtc);
            if (current.State == SystemToolJournalState.Unknown || current.Entries.Count != lines) return Unknown();
            if (previous != null)
            {
                for (int i = 0; i < previous.Entries.Count; i++)
                    if (previous.Entries[i] != current.Entries[i]) return Unknown();
            }
            previous = current;
            offset += length + 1;
        }
        return previous ?? Unknown();
    }

    private static SystemToolJournalReview Unknown() => new(SystemToolJournalState.Unknown,
        Array.AsReadOnly(Array.Empty<SystemToolJournalEntry>()), "Journal content is missing, unsupported, incomplete or invalid. No absence, completion, process-state or action-eligibility conclusion is established.");
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static bool Fields(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (!expected.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) return false;
        return seen.Count == expected.Length;
    }
    private static bool Text(JsonElement value, out string text)
    { text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""; return value.ValueKind == JsonValueKind.String; }
    private static bool Integer(JsonElement value, out int number)
    { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number); }
    private static bool NullableInteger(JsonElement value, out int? number)
    { number = null; if (value.ValueKind == JsonValueKind.Null) return true; if (!Integer(value, out var parsed)) return false; number = parsed; return true; }
    private static bool Named<T>(JsonElement value, out T named) where T : struct, Enum
    { named = default; return Text(value, out var text) && Enum.TryParse(text, false, out named) && Enum.IsDefined(named) && named.ToString() == text; }
    private static bool Date(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return Text(value, out var text) && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp) &&
            Utc(timestamp) && Stamp(timestamp) == text;
    }
    private static bool NullableDate(JsonElement value, out DateTimeOffset? timestamp)
    { timestamp = null; if (value.ValueKind == JsonValueKind.Null) return true; if (!Date(value, out var parsed)) return false; timestamp = parsed; return true; }
    private static void Write(Utf8JsonWriter writer, string property, int? value)
    { if (value.HasValue) writer.WriteNumber(property, value.Value); else writer.WriteNull(property); }
}
