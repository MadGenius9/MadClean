using System.Text.Json;

namespace MadClean.Core;

public sealed record WindowsUpdateRevisionField(CheckState State, int? Value);

/// <summary>Provider identity declarations, not installed versions, device matching or action authority.</summary>
public sealed record WindowsUpdateIdentityMetadata(CheckState State, WindowsUpdateRevisionField RevisionNumber,
    WindowsUpdateTextField DriverHardwareId);

public static class WindowsUpdateIdentityMetadataParser
{
    public const int MaximumHardwareIdCharacters = 512;
    public static WindowsUpdateIdentityMetadata Unknown() => new(CheckState.Unknown,
        new(CheckState.Unknown, null), new(CheckState.Unknown, null));

    public static WindowsUpdateIdentityMetadata Parse(JsonElement value, int updateType)
    {
        if (updateType is not (1 or 2) || value.ValueKind != JsonValueKind.Object) return Unknown();
        // Optional duplicate fields poison only themselves; schema/version ambiguity poisons the envelope.
        foreach (var property in value.EnumerateObject())
            if (property.Name is not ("Version" or "RevisionNumber" or "DriverHardwareID")) return Unknown();
        if (!Unique(value, "Version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int number) || number != 1) return Unknown();
        var revision = new WindowsUpdateRevisionField(CheckState.Unknown, null);
        if (Unique(value, "RevisionNumber", out var revisionJson) && Field(revisionJson, out var revisionState, out var revisionValue))
        {
            if (revisionState != CheckState.Available) revision = new(revisionState, null);
            else if (revisionValue.ValueKind == JsonValueKind.Number && revisionValue.TryGetInt32(out int parsed) && parsed >= 0)
                revision = new(CheckState.Available, parsed);
        }
        var hardware = new WindowsUpdateTextField(CheckState.Unknown, null);
        if (Unique(value, "DriverHardwareID", out var hardwareJson) && Field(hardwareJson, out var hardwareState, out var hardwareValue))
        {
            if (updateType == 1)
            {
                if (hardwareState == CheckState.Unsupported) hardware = new(CheckState.Unsupported, null);
            }
            else if (hardwareState != CheckState.Available) hardware = new(hardwareState, null);
            else if (hardwareValue.ValueKind == JsonValueKind.String)
            {
                string? text;
                try { text = hardwareValue.GetString(); }
                catch (Exception exception) when (exception is InvalidOperationException or JsonException) { text = null; }
                if (SafeHardware(text)) hardware = new(CheckState.Available, text);
            }
        }
        return new(Coverage(revision, hardware, updateType), revision, hardware);
    }

    public static bool IsConsistent(WindowsUpdateIdentityMetadata? metadata, int updateType)
    {
        if (metadata is null || updateType is not (1 or 2) || metadata.RevisionNumber is not { } revision ||
            metadata.DriverHardwareId is not { } hardware || !Allowed(revision.State) || !Allowed(hardware.State) ||
            (revision.State == CheckState.Available ? revision.Value is not >= 0 : revision.Value.HasValue) ||
            (hardware.State == CheckState.Available ? !SafeHardware(hardware.Value) : hardware.Value is not null) ||
            updateType == 1 && hardware.State is not (CheckState.Unknown or CheckState.Unsupported)) return false;
        return metadata.State == Coverage(revision, hardware, updateType);
    }

    private static CheckState Coverage(WindowsUpdateRevisionField revision, WindowsUpdateTextField hardware, int type)
    {
        int known = (revision.State == CheckState.Available ? 1 : 0) + (type == 2 && hardware.State == CheckState.Available ? 1 : 0);
        bool complete = revision.State == CheckState.Available && (type == 1 ? hardware.State == CheckState.Unsupported : hardware.State == CheckState.Available);
        return complete ? CheckState.Available : known > 0 ? CheckState.Partial : CheckState.Unknown;
    }
    private static bool SafeHardware(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumHardwareIdCharacters && ShortcutPathPolicy.HasSafeUnicode(value) &&
        !value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !value.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase);
    private static bool Allowed(CheckState state) => state is CheckState.Available or CheckState.Unknown or CheckState.Failed or CheckState.Unsupported;
    private static bool Unique(JsonElement value, string name, out JsonElement result)
    {
        result = default; int count = 0;
        foreach (var property in value.EnumerateObject())
            if (property.Name == name) { result = property.Value; count++; }
        return count == 1;
    }
    private static bool Field(JsonElement value, out CheckState state, out JsonElement data)
    {
        state = CheckState.Unknown; data = default;
        if (value.ValueKind != JsonValueKind.Object || !Unique(value, "State", out var status) || !Unique(value, "Value", out data)) return false;
        foreach (var property in value.EnumerateObject()) if (property.Name is not ("State" or "Value")) return false;
        if (status.ValueKind != JsonValueKind.String) return false;
        string? text;
        try { text = status.GetString(); }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException) { return false; }
        return Enum.TryParse(text, false, out state) && Allowed(state) && state.ToString() == text &&
            (state == CheckState.Available || data.ValueKind == JsonValueKind.Null);
    }
}
