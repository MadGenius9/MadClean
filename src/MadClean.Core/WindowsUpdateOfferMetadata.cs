using System.Text.Json;

namespace MadClean.Core;

public enum WindowsUpdateInstallationImpact { Normal = 0, Minor = 1, RequiresExclusiveHandling = 2 }
public enum WindowsUpdateRebootBehavior { NeverReboots = 0, AlwaysRequiresReboot = 1, CanRequestReboot = 2 }
public sealed record WindowsUpdateTextField(CheckState State, string? Value);
public sealed record WindowsUpdateBooleanField(CheckState State, bool? Value);
public sealed record WindowsUpdateImpactField(CheckState State, WindowsUpdateInstallationImpact? Value);
public sealed record WindowsUpdateRebootField(CheckState State, WindowsUpdateRebootBehavior? Value);

/// <summary>Provider characteristics only, never complete prerequisites, compatibility or action permission.</summary>
public sealed record WindowsUpdateOfferMetadata(CheckState State, WindowsUpdateTextField Manufacturer,
    WindowsUpdateTextField Provider, WindowsUpdateTextField Model, WindowsUpdateBooleanField CanRequestUserInput,
    WindowsUpdateBooleanField RequiresNetworkConnectivity, WindowsUpdateImpactField Impact, WindowsUpdateRebootField RebootBehavior)
{
    internal bool HasIssues { get; init; }
}

public static class WindowsUpdateOfferMetadataParser
{
    public const int MaximumTextCharacters = 512;
    private static readonly string[] Names = ["Version", "Manufacturer", "Provider", "Model", "CanRequestUserInput",
        "RequiresNetworkConnectivity", "Impact", "RebootBehavior"];
    private static WindowsUpdateTextField UnknownText => new(CheckState.Unknown, null);
    private static WindowsUpdateBooleanField UnknownBoolean => new(CheckState.Unknown, null);
    public static WindowsUpdateOfferMetadata Parse(JsonElement value, int updateType)
    {
        if (!Fields(value, Names) || value.GetProperty("Version").ValueKind != JsonValueKind.Number ||
            !value.GetProperty("Version").TryGetInt32(out int version) || version != 1 || updateType is not (1 or 2))
            return Unknown();
        bool issues = false;
        WindowsUpdateTextField Text(string name)
        {
            if (!Field(value.GetProperty(name), out var state, out var data)) { issues = true; return UnknownText; }
            if (updateType == 1)
            {
                if (state == CheckState.Unsupported) return new(state, null);
                issues = true; return UnknownText;
            }
            if (state != CheckState.Available) return new(state, null);
            if (data.ValueKind != JsonValueKind.String) { issues = true; return UnknownText; }
            string? text;
            try { text = data.GetString(); }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException) { issues = true; return UnknownText; }
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextCharacters || Placeholder(text) ||
                !ShortcutPathPolicy.HasSafeUnicode(text)) { issues = true; return UnknownText; }
            return new(state, text);
        }
        WindowsUpdateBooleanField Boolean(string name)
        {
            if (!Field(value.GetProperty(name), out var state, out var data)) { issues = true; return UnknownBoolean; }
            if (state != CheckState.Available) return new(state, null);
            if (data.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { issues = true; return UnknownBoolean; }
            return new(state, data.GetBoolean());
        }
        (CheckState State, int? Value) Number(string name)
        {
            if (!Field(value.GetProperty(name), out var state, out var data)) { issues = true; return (CheckState.Unknown, null); }
            if (state != CheckState.Available) return (state, null);
            if (data.ValueKind != JsonValueKind.Number || !data.TryGetInt32(out int number) || number is < 0 or > 2)
            { issues = true; return (CheckState.Unknown, null); }
            return (state, number);
        }
        var manufacturer = Text("Manufacturer"); var provider = Text("Provider"); var model = Text("Model");
        var input = Boolean("CanRequestUserInput"); var network = Boolean("RequiresNetworkConnectivity");
        var impact = Number("Impact"); var reboot = Number("RebootBehavior");
        CheckState[] applicable = updateType == 1 ? [input.State, network.State, impact.State, reboot.State]
            : [manufacturer.State, provider.State, model.State, input.State, network.State, impact.State, reboot.State];
        int known = applicable.Count(state => state == CheckState.Available);
        var overall = known == 0 ? CheckState.Unknown : known == applicable.Length && !issues ? CheckState.Available : CheckState.Partial;
        return new(overall, manufacturer, provider, model, input, network,
            new(impact.State, impact.Value.HasValue ? (WindowsUpdateInstallationImpact)impact.Value.Value : null),
            new(reboot.State, reboot.Value.HasValue ? (WindowsUpdateRebootBehavior)reboot.Value.Value : null))
            { HasIssues = issues || known != applicable.Length };
    }
    public static WindowsUpdateOfferMetadata Unknown() => new(CheckState.Unknown, UnknownText, UnknownText, UnknownText,
        UnknownBoolean, UnknownBoolean, new(CheckState.Unknown, null), new(CheckState.Unknown, null)) { HasIssues = true };
    public static bool IsConsistent(WindowsUpdateOfferMetadata? metadata, int updateType)
    {
        if (metadata == null || updateType is not (1 or 2) || !TextValid(metadata.Manufacturer) || !TextValid(metadata.Provider) || !TextValid(metadata.Model) ||
            !BooleanValid(metadata.CanRequestUserInput) || !BooleanValid(metadata.RequiresNetworkConnectivity) || metadata.Impact == null || metadata.RebootBehavior == null ||
            !Allowed(metadata.Impact.State) || !Allowed(metadata.RebootBehavior.State) ||
            (metadata.Impact.State == CheckState.Available ? !metadata.Impact.Value.HasValue || !Enum.IsDefined(metadata.Impact.Value.Value) : metadata.Impact.Value.HasValue) ||
            (metadata.RebootBehavior.State == CheckState.Available ? !metadata.RebootBehavior.Value.HasValue || !Enum.IsDefined(metadata.RebootBehavior.Value.Value) : metadata.RebootBehavior.Value.HasValue)) return false;
        CheckState[] texts = [metadata.Manufacturer.State, metadata.Provider.State, metadata.Model.State];
        if (updateType == 1 && texts.Any(state => state is not (CheckState.Unsupported or CheckState.Unknown))) return false;
        CheckState[] common = [metadata.CanRequestUserInput.State, metadata.RequiresNetworkConnectivity.State, metadata.Impact.State, metadata.RebootBehavior.State];
        var applicable = updateType == 1 ? common : texts.Concat(common).ToArray();
        int known = applicable.Count(state => state == CheckState.Available);
        bool issues = metadata.HasIssues || updateType == 1 && texts.Any(state => state != CheckState.Unsupported);
        var expected = known == 0 ? CheckState.Unknown : known == applicable.Length && !issues ? CheckState.Available : CheckState.Partial;
        return metadata.State == expected;
    }
    private static bool Allowed(CheckState state) => state is CheckState.Available or CheckState.Unknown or CheckState.Unsupported or CheckState.Failed;
    private static bool TextValid(WindowsUpdateTextField? field) => field != null && Allowed(field.State) &&
        (field.State == CheckState.Available ? !string.IsNullOrWhiteSpace(field.Value) && field.Value.Length <= MaximumTextCharacters && !Placeholder(field.Value) && ShortcutPathPolicy.HasSafeUnicode(field.Value) : field.Value == null);
    private static bool BooleanValid(WindowsUpdateBooleanField? field) => field != null && Allowed(field.State) &&
        (field.State == CheckState.Available ? field.Value.HasValue : field.Value == null);
    private static bool Placeholder(string value) => value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase);
    private static bool Field(JsonElement field, out CheckState state, out JsonElement value)
    {
        state = CheckState.Unknown; value = default;
        if (!Fields(field, ["State", "Value"])) return false;
        var status = field.GetProperty("State"); value = field.GetProperty("Value");
        if (status.ValueKind != JsonValueKind.String) return false;
        string? text;
        try { text = status.GetString(); }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException) { return false; }
        if (!Enum.TryParse(text, false, out state) ||
            state is not (CheckState.Available or CheckState.Unknown or CheckState.Unsupported or CheckState.Failed) || state.ToString() != text) return false;
        return state == CheckState.Available || value.ValueKind == JsonValueKind.Null;
    }
    private static bool Fields(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var property in element.EnumerateObject()) if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException) { return false; }
        return seen.Count == names.Length;
    }
}
