using System.Runtime.InteropServices;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

internal sealed record BoundedRegistrationKeyName(bool End, string? Name, int NativeError, string Issue);
internal sealed record BoundedRegistrationText(RegistrationMetadataState State, string? Value, string Issue, int NativeError);
internal sealed record BoundedRegistrationFlag(RegistrationMetadataState State, bool? Value, string Issue, int NativeError);
internal sealed record BoundedRegistrationPresence(bool? Exists, int NativeError, string Issue);

/// <summary>Fixed-capacity registry metadata only. Never reads uninstall command contents.</summary>
internal static class BoundedRegistrationMetadata
{
    private static readonly IReadOnlyDictionary<string, int> TextFields = new Dictionary<string, int>(StringComparer.Ordinal)
    { ["DisplayName"] = RegistrationMetadataDecoder.NameCharacters, ["DisplayVersion"] = RegistrationMetadataDecoder.VersionCharacters,
      ["Publisher"] = RegistrationMetadataDecoder.PublisherCharacters, ["InstallLocation"] = RegistrationMetadataDecoder.LocationCharacters };
    private static readonly HashSet<string> FlagFields = new(StringComparer.Ordinal)
    { "SystemComponent", "WindowsInstaller", "NoRemove" };

    internal static BoundedRegistrationKeyName EnumerateKey(SafeRegistryHandle key, uint index, Action check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (index > 20_000) throw new ArgumentOutOfRangeException(nameof(index));
        char[] buffer = new char[256];
        uint count = (uint)buffer.Length;
        try
        {
            check();
            int code = RegEnumKeyExW(key, index, buffer, ref count, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            check();
            if (code == 259) return new(true, null, code, ""); // ERROR_NO_MORE_ITEMS
            if (code != 0) return new(false, null, code, "Registry subkey enumeration failed or exceeded its fixed name bound; source coverage is incomplete.");
            if (count == 0 || count >= buffer.Length || buffer[(int)count] != '\0')
                return new(false, null, 0, "Registry subkey name metadata was malformed; entry omitted.");
            string name = new(buffer, 0, (int)count);
            if (name is "." or ".." || name.IndexOfAny(['\\', '/']) >= 0 || name.Any(c => char.IsControl(c) ||
                char.IsSurrogate(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
                return new(false, null, 0, "Registry subkey name metadata was unsupported; entry omitted.");
            return new(false, name, 0, "");
        }
        finally { Array.Clear(buffer); }
    }

    internal static BoundedRegistrationText ReadText(SafeRegistryHandle key, string field, Action check, CancellationToken token)
    {
        if (!TextFields.TryGetValue(field, out int maximum)) throw new ArgumentException("Field is outside the metadata whitelist.", nameof(field));
        var raw = ReadRaw(key, field, checked(2 * (maximum + 1)), false, check);
        if (raw.Data == null)
            return new(raw.Missing ? RegistrationMetadataState.Missing : RegistrationMetadataState.Invalid, null, raw.Issue, raw.NativeError);
        try
        {
            var decoded = RegistrationMetadataDecoder.DecodeText(raw.Type, raw.Count, raw.Data.AsSpan(0, (int)raw.Count), maximum, token);
            return new(decoded.State, decoded.Value, decoded.Issue, raw.NativeError);
        }
        finally { Array.Clear(raw.Data); }
    }

    internal static BoundedRegistrationFlag ReadFlag(SafeRegistryHandle key, string field, Action check, CancellationToken token)
    {
        if (!FlagFields.Contains(field)) throw new ArgumentException("Field is outside the metadata whitelist.", nameof(field));
        var raw = ReadRaw(key, field, RegistrationMetadataDecoder.FlagBytes, true, check);
        if (raw.Data == null)
            return new(raw.Missing ? RegistrationMetadataState.Missing : RegistrationMetadataState.Invalid, null, raw.Issue, raw.NativeError);
        try
        {
            var decoded = RegistrationMetadataDecoder.DecodeFlag(raw.Type, raw.Count, raw.Data.AsSpan(0, (int)raw.Count), token);
            return new(decoded.State, decoded.Value, decoded.Issue, raw.NativeError);
        }
        finally { Array.Clear(raw.Data); }
    }

    internal static BoundedRegistrationPresence ProbeUninstallerName(SafeRegistryHandle key, string field, Action check)
    {
        if (field is not ("UninstallString" or "QuietUninstallString")) throw new ArgumentException("Only uninstall name-presence metadata is supported.", nameof(field));
        uint count = 0;
        check();
        // lpData is always NULL, including arbitrarily large or unexpected registry value types.
        int code = RegQueryValueExW(key, field, IntPtr.Zero, out _, null, ref count);
        check();
        return code switch
        {
            0 => new(true, code, ""),
            2 => new(false, code, ""),
            _ => new(null, code, "Uninstaller value-name presence could not be established; no command data was read.")
        };
    }

    private sealed record Raw(byte[]? Data, uint Type, uint Count, bool Missing, int NativeError, string Issue);
    private static Raw ReadRaw(SafeRegistryHandle key, string field, int capacity, bool flag, Action check)
    {
        uint count = 0;
        check();
        int code = RegQueryValueExW(key, field, IntPtr.Zero, out uint type, null, ref count);
        check();
        if (code == 2) return new(null, type, count, true, code, "");
        if (code != 0) return new(null, type, count, false, code, "Metadata type/size query failed; value remains unknown.");
        if ((flag ? type != 4 : type is not (1 or 2)) || count == 0 || count > capacity || (flag && count != 4))
            return new(null, type, count, false, 0, "Metadata type/size is outside its fixed field bound; value remains unknown.");
        byte[]? buffer = new byte[capacity];
        try
        {
            uint received = (uint)buffer.Length;
            check();
            code = RegQueryValueExW(key, field, IntPtr.Zero, out uint receivedType, buffer, ref received);
            check();
            if (code != 0) return new(null, receivedType, received, false, code, "Metadata data query failed or changed beyond its fixed bound; no buffer contents were accepted.");
            if (received != count || receivedType != type || received > buffer.Length)
                return new(null, receivedType, received, false, 0, "Metadata type/size changed between queries; value remains unknown.");
            var result = new Raw(buffer, type, received, false, 0, "");
            buffer = null; // Ownership transfers only to ReadText/ReadFlag, which always zero it.
            return result;
        }
        finally { if (buffer != null) Array.Clear(buffer); }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegEnumKeyExW(SafeRegistryHandle key, uint index, [Out] char[] name, ref uint nameCount,
        IntPtr reserved, IntPtr keyClass, IntPtr classCount, IntPtr lastWriteTime);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string field, IntPtr reserved, out uint type,
        [Out] byte[]? data, ref uint byteCount);
}
