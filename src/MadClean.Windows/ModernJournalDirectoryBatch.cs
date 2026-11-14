using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MadClean.Windows;

internal sealed record ModernJournalDirectoryName(string Name, uint Attributes);
internal enum ModernJournalDirectoryQuery { Batch, End, InsufficientBuffer, Uncertain }

// One successful FILE_ID_BOTH_DIR_INFO buffer only. A final record is not enumeration EOF.
// SDK order/widths: WinBase.h, microsoft/win32metadata 15fc870a5e96df7c560877ff30136c0fb5a21e9b.
// The native binding separately asserts the x64 ABI; opaque FileId/short names are never identities here.
internal static class ModernJournalDirectoryBatch
{
    internal const int BufferBytes = 4096;
    internal const int MaxQueries = 8;
    internal const int MaxRecords = 32;
    private const int NameOffset = 104;
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    internal static ModernJournalDirectoryQuery ClassifyQueryResult(bool success, int error) => success
        ? ModernJournalDirectoryQuery.Batch
        : error switch
        {
            18 => ModernJournalDirectoryQuery.End,
            234 => ModernJournalDirectoryQuery.InsufficientBuffer,
            _ => ModernJournalDirectoryQuery.Uncertain
        };

    internal static bool TryRead(ReadOnlySpan<byte> buffer, out IReadOnlyList<ModernJournalDirectoryName> entries)
    {
        entries = Array.Empty<ModernJournalDirectoryName>();
        if (buffer.Length is < NameOffset + 2 or > BufferBytes) return false;
        var names = new List<ModernJournalDirectoryName>();
        int offset = 0;
        while (true)
        {
            var remaining = buffer[offset..];
            if (names.Count == MaxRecords || remaining.Length < NameOffset + 2) return false;
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(remaining[56..]);
            uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(remaining[60..]);
            if (nameBytes == 0 || (nameBytes & 1) != 0 || nameBytes > (uint)(remaining.Length - NameOffset)) return false;
            int recordBytes = NameOffset + (int)nameBytes;
            if (next != 0 && ((next & 7) != 0 || next < (uint)((recordBytes + 7) & ~7) ||
                next > (uint)(remaining.Length - (NameOffset + 2)))) return false;

            string name;
            try { name = StrictUtf16.GetString(remaining.Slice(NameOffset, (int)nameBytes)); }
            catch (DecoderFallbackException) { return false; }
            if (name.IndexOfAny(['\0', '/', '\\', ':']) >= 0 || !SafeUnicode(name)) return false;
            names.Add(new(name, attributes));
            if (next == 0)
            {
                entries = Array.AsReadOnly(names.ToArray());
                return true; // Caller must query again to establish ERROR_NO_MORE_FILES.
            }
            offset += (int)next;
        }
    }

    // Same Unicode safety policy as Core's internal ShortcutPathPolicy, without widening its API.
    private static bool SafeUnicode(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
