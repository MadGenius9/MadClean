using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MadClean.Core;

public enum RegistrationMetadataState { Valid, Missing, Invalid }
public sealed record RegistrationTextMetadata(RegistrationMetadataState State, string? Value, string Issue);
public sealed record RegistrationFlagMetadata(RegistrationMetadataState State, bool? Value, string Issue);

/// <summary>Bounded literal metadata decoding, never command decoding or environment expansion.</summary>
public static class RegistrationMetadataDecoder
{
    public const int NameCharacters = 1_024;
    public const int VersionCharacters = 256;
    public const int PublisherCharacters = 512;
    public const int LocationCharacters = 8_192;
    public const int MaximumTextCharacters = LocationCharacters;
    public const int FlagBytes = 4;
    public static RegistrationTextMetadata MissingText { get; } = new(RegistrationMetadataState.Missing, null, "");
    public static RegistrationFlagMetadata MissingFlag { get; } = new(RegistrationMetadataState.Missing, null, "");

    public static RegistrationTextMetadata DecodeText(uint nativeType, uint declaredByteCount, ReadOnlySpan<byte> data,
        int maximumCharacters, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Reject invalid bounds BEFORE arithmetic, allocation or decoding. The supplied span must
        // represent the exact successful native byte count, never capacity or undefined error data.
        if (maximumCharacters is < 1 or > MaximumTextCharacters || nativeType is not (1 or 2) ||
            declaredByteCount != (uint)data.Length || declaredByteCount < 2 || (declaredByteCount & 1) != 0 ||
            declaredByteCount > (uint)((maximumCharacters + 1) * 2))
            return TextInvalid("Unsupported text type, byte count or field bound.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[^2..]) != 0)
            return TextInvalid("Text lacks the required terminal NUL; no terminator was synthesized.");
        int contentBytes = data.Length - 2;
        for (int index = 0; index < contentBytes; index += 2)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data[index..]);
            if (value == 0) return TextInvalid("Embedded or additional terminal NUL is unsupported.");
            if (value is >= 0xDC00 and <= 0xDFFF) return TextInvalid("Text is not valid UTF-16.");
            if (value is >= 0xD800 and <= 0xDBFF)
            {
                if (index + 2 >= contentBytes) return TextInvalid("Text has a truncated UTF-16 surrogate pair.");
                ushort low = BinaryPrimitives.ReadUInt16LittleEndian(data[(index + 2)..]);
                if (low is < 0xDC00 or > 0xDFFF) return TextInvalid("Text has an invalid UTF-16 surrogate pair.");
                int scalar = char.ConvertToUtf32((char)value, (char)low);
                if (Rune.GetUnicodeCategory(new Rune(scalar)) is UnicodeCategory.Control or UnicodeCategory.Format)
                    return TextInvalid("Control or format characters are unsupported in display metadata.");
                index += 2;
            }
            else if (char.GetUnicodeCategory((char)value) is UnicodeCategory.Control or UnicodeCategory.Format)
                return TextInvalid("Control or format characters are unsupported in display metadata.");
        }
        token.ThrowIfCancellationRequested();
        // Every code unit has been checked. UTF-16LE is fixed by the W API; do not guess encodings,
        // replace malformed sequences, trim text, expand environment references or parse commands.
        string text = Encoding.Unicode.GetString(data[..contentBytes]);
        token.ThrowIfCancellationRequested();
        return new(RegistrationMetadataState.Valid, text, "");
    }

    public static RegistrationFlagMetadata DecodeFlag(uint nativeType, uint declaredByteCount, ReadOnlySpan<byte> data,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (nativeType != 4 || declaredByteCount != FlagBytes || data.Length != FlagBytes)
            return new(RegistrationMetadataState.Invalid, null, "Flag requires REG_DWORD with exactly four bytes.");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return value switch
        {
            0 => new(RegistrationMetadataState.Valid, false, ""),
            1 => new(RegistrationMetadataState.Valid, true, ""),
            _ => new(RegistrationMetadataState.Invalid, null, "Flag DWORD is not a supported 0/1 value.")
        };
    }
    private static RegistrationTextMetadata TextInvalid(string issue) => new(RegistrationMetadataState.Invalid, null, issue);
}
