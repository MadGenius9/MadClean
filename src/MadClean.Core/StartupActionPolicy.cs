using System.Buffers.Binary;
using System.Globalization;

namespace MadClean.Core;

/// <summary>Fail-closed scope and data policy, independent of registry/native execution.</summary>
public static class StartupActionPolicy
{
    // This source-code gate is intentionally false until independent review and native fixture gates pass.
    // It has no environment, configuration, UI or command-line override.
    public const bool ProductionMutationValidated = false;
    public const int MaximumValueNameCharacters = 256;
    public const int MaximumValueBytes = 522; // Documented Run command: at most 260 UTF-16 characters, plus NUL.
    public const int MaximumInventoryEntries = 2_048;
    public const int MaximumBackups = 128;
    public const int MaximumPrivatePayloadBytes = 16_384;
    public const int MaximumPendingPreviews = 16;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(2);
    public const uint DataProtectionFlags = 1; // CRYPTPROTECT_UI_FORBIDDEN; never LOCAL_MACHINE or prompt/audit flow.
    public const string FixtureRegistryPrefix = @"Software\MadClean.WindowsSmoke\";

    public static bool IsValidReference(StartupRegistrationReference? reference) => reference != null &&
        SourceSubkey(reference.Source) != null && !string.IsNullOrWhiteSpace(reference.ValueName) &&
        reference.ValueName.Length <= MaximumValueNameCharacters &&
        !reference.ValueName.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format);

    public static string? SourceSubkey(StartupRegistrationSource source) => source switch
    {
        StartupRegistrationSource.CurrentUserRun => "Run",
        StartupRegistrationSource.CurrentUserRunOnce => "RunOnce",
        _ => null
    };

    public static string ValueKind(uint kind) => kind switch { 1 => "REG_SZ", 2 => "REG_EXPAND_SZ", _ => "Unsupported" };

    public static bool IsSupportedRawValue(uint kind, ReadOnlySpan<byte> data)
    {
        if (kind is not (1 or 2) || data.Length < 2 || data.Length > MaximumValueBytes || data.Length % 2 != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[^2..]) != 0) return false;
        // Validate UTF-16 without materializing or normalizing the command as an immutable string.
        for (int i = 0; i < data.Length - 2; i += 2)
        {
            ushort current = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            if (current == 0 || current is >= 0xDC00 and <= 0xDFFF) return false;
            if (current is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 4 >= data.Length) return false;
                ushort next = BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 2)..]);
                if (next is < 0xDC00 or > 0xDFFF) return false;
                i += 2;
            }
        }
        return true;
    }

    public static bool IsOpaqueId(string? value) => value != null && Guid.TryParseExact(value, "N", out var id) &&
        value.Equals(id.ToString("N"), StringComparison.Ordinal);

    public static bool TryFixtureRegistryBase(string? value, out Guid fixture)
    {
        fixture = default;
        if (value == null || !value.StartsWith(FixtureRegistryPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string tail = value[FixtureRegistryPrefix.Length..];
        return (Guid.TryParseExact(tail, "N", out fixture) || Guid.TryParseExact(tail, "D", out fixture)) && fixture != Guid.Empty;
    }

    public static bool IsFixtureBackupDirectory(string? directory, string? artifactsDirectory, Guid fixture)
    {
        string? local = InstalledApplicationPaths.NormalizeLocalDirectory(directory);
        string? artifacts = InstalledApplicationPaths.NormalizeLocalDirectory(artifactsDirectory);
        if (local == null || artifacts == null || fixture == Guid.Empty) return false;
        string expected = artifacts + "\\windows-validation-fixtures-" + fixture.ToString("N") + "\\startup-backups";
        return local.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
