using System.Buffers.Binary;
using System.Text;

namespace MadClean.Core;

public enum DeviceDriverFieldState { Available, Unknown, Missing, AccessDenied, Oversized, Invalid, Failed }
public sealed record DeviceDriverText(DeviceDriverFieldState State, string? Value, int? NativeError);
public sealed record DeviceDriverDate(DeviceDriverFieldState State, DateOnly? Value, int? NativeError);
public sealed record DeviceDriverObservation(int ObservationNumber, DeviceDriverText Name, DeviceDriverText Description,
    DeviceDriverText Provider, DeviceDriverText InstalledVersion, DeviceDriverDate DriverDate, DateTimeOffset CheckedUtc);
public interface IDeviceDriverInventory
{
    Task<CheckResult<DeviceDriverObservation>> ReadAsync(CancellationToken token);
}

// Pure property decoding only. Enumeration numbers are not device identities, provider
// version text has no ordering semantics, and reported dates are not installation times.
public static class DeviceDriverPropertyDecoder
{
    public const int MaximumTextCharacters = 256; // UTF16 code units, not graphemes/scalars.
    public const int MaximumTextBytes = 2 * (MaximumTextCharacters + 1);
    public const int MaximumObservations = 512;
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);
    private static readonly DateOnly EarliestFileTimeDate = new(1601, 1, 1);
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unknown", "N/A", "Not available", "None", "Null", "(null)", "Not specified", "Unspecified",
        "To be filled by O.E.M.", "To be filled by OEM", "-", "—", "?"
    };

    public static DeviceDriverText Text(uint actualType, ReadOnlySpan<byte> bytes)
    {
        // DEVPROP_TYPE_STRING exactly. ARRAY/LIST modifiers and other types are not strings.
        if (actualType != 0x12) return NoText(DeviceDriverFieldState.Invalid);
        if (bytes.Length > MaximumTextBytes) return NoText(DeviceDriverFieldState.Oversized);
        if (bytes.Length < 2 || (bytes.Length & 1) != 0 || bytes[^2] != 0 || bytes[^1] != 0)
            return NoText(DeviceDriverFieldState.Invalid);
        string value;
        try { value = StrictUtf16.GetString(bytes[..^2]); }
        catch (DecoderFallbackException) { return NoText(DeviceDriverFieldState.Invalid); }
        if (value.IndexOf('\0') >= 0 || !ShortcutPathPolicy.HasSafeUnicode(value)) return NoText(DeviceDriverFieldState.Invalid);
        // Trim for display only; no parsing, environment expansion, version ordering or inference.
        value = value.Trim();
        return value.Length == 0 || Placeholders.Contains(value) ? NoText(DeviceDriverFieldState.Unknown)
            : new(DeviceDriverFieldState.Available, value, null);
    }

    public static DeviceDriverDate Date(uint actualType, ReadOnlySpan<byte> bytes)
    {
        if (actualType != 0x10 || bytes.Length != 8) return NoDate(DeviceDriverFieldState.Invalid);
        ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if (raw == 0) return NoDate(DeviceDriverFieldState.Unknown);
        if (raw > long.MaxValue) return NoDate(DeviceDriverFieldState.Invalid);
        try { return new(DeviceDriverFieldState.Available, DateOnly.FromDateTime(DateTime.FromFileTimeUtc((long)raw)), null); }
        catch (ArgumentOutOfRangeException) { return NoDate(DeviceDriverFieldState.Invalid); }
    }

    public static bool IsConsistent(DeviceDriverObservation? observation, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        return observation != null && now != default && now.Offset == TimeSpan.Zero &&
            observation.ObservationNumber is >= 1 and <= MaximumObservations && observation.CheckedUtc != default &&
            observation.CheckedUtc.Offset == TimeSpan.Zero && observation.CheckedUtc <= now &&
            TextField(observation.Name) && TextField(observation.Description) && TextField(observation.Provider) &&
            TextField(observation.InstalledVersion) && DateField(observation.DriverDate);
    }
    // Win32/SetupAPI last-error values are DWORD bit patterns. Their signed Int32
    // representation may be negative; only an Available field forbids a native error.
    private static bool TextField(DeviceDriverText? field)
    {
        if (field == null || !Enum.IsDefined(field.State)) return false;
        if (field.State != DeviceDriverFieldState.Available) return field.Value == null;
        return field.NativeError == null && field.Value is { Length: > 0 and <= MaximumTextCharacters } value &&
            value == value.Trim() && !Placeholders.Contains(value) && value.IndexOf('\0') < 0 && ShortcutPathPolicy.HasSafeUnicode(value);
    }
    private static bool DateField(DeviceDriverDate? field)
    {
        if (field == null || !Enum.IsDefined(field.State)) return false;
        if (field.State != DeviceDriverFieldState.Available) return field.Value == null;
        // Future dates remain valid reported metadata. They prove no release/installation
        // event, recency, compatibility, update ordering or authenticity.
        return field.NativeError == null && field.Value is { } date && date >= EarliestFileTimeDate;
    }
    private static DeviceDriverText NoText(DeviceDriverFieldState state) => new(state, null, null);
    private static DeviceDriverDate NoDate(DeviceDriverFieldState state) => new(state, null, null);
}
