using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace MadClean.Core;

public enum SystemToolEmbeddedVersionState { Matched, Missing, Mismatched, Ambiguous, Malformed, Unsupported }
public sealed record SystemToolEmbeddedVersion(SystemToolEmbeddedVersionState State, string FileVersion,
    int ResourceCount, int OriginalFilenameCount)
{
    // Compiled validation call site only; no input data, exception text or stack trace.
    public int? FailureSite { get; init; }
    public int? DeclaredRootLength { get; init; }
    public int? ResourceBlobLength { get; init; }
    public bool? RootTrailingBytesZero { get; init; }
}

/// <summary>Reads bounded embedded PE VERSIONINFO bytes only, never a path, MUI loader or executable image.</summary>
public static class SystemToolEmbeddedVersionReader
{
    private sealed class Rejected(SystemToolEmbeddedVersionState state, int? site = null) : Exception
    {
        internal SystemToolEmbeddedVersionState State { get; } = state;
        internal int? Site { get; } = site;
    }
    private sealed record Section(uint Rva, uint VirtualSize, uint RawSize, uint Offset);
    private sealed record Block(int Start, int End, ushort ValueLength, ushort Type, string Key, int Value, int Children);
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    public static SystemToolEmbeddedVersion Read(Stream stream, string expectedOriginalFilename, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        long original = 0; bool restore = false;
        var parser = new Reader(stream, expectedOriginalFilename, token);
        SystemToolEmbeddedVersion result;
        try
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek ||
                !ValidExpected(expectedOriginalFilename)) return Result(SystemToolEmbeddedVersionState.Unsupported, 0, 0);
            original = stream.Position; restore = true;
            if (original < 0 || stream.Length is <= 0 or > 33_554_432) throw new Rejected(SystemToolEmbeddedVersionState.Unsupported);
            parser.Length = stream.Length;
            parser.Seek(0);
            result = parser.Parse();
        }
        catch (Rejected exception) { result = Result(exception.State, parser.ResourceCount, parser.OriginalCount) with { FailureSite = exception.Site }; }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException or
            ArgumentException or OverflowException)
        { result = Result(SystemToolEmbeddedVersionState.Malformed, parser.ResourceCount, parser.OriginalCount); }
        finally
        {
            if (restore)
            {
                try { if (stream.Seek(original, SeekOrigin.Begin) != original || stream.Position != original) restore = false; }
                catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException or ArgumentException)
                { restore = false; }
            }
        }
        token.ThrowIfCancellationRequested();
        return (restore ? result : Result(SystemToolEmbeddedVersionState.Unsupported, parser.ResourceCount, parser.OriginalCount)) with
        {
            DeclaredRootLength = parser.DeclaredRootLength,
            ResourceBlobLength = parser.ResourceBlobLength,
            RootTrailingBytesZero = parser.RootTrailingBytesZero
        };
    }

    private static SystemToolEmbeddedVersion Result(SystemToolEmbeddedVersionState state, int resources, int originals) => new(state, "Unknown", resources, originals);
    private static bool ValidExpected(string? value) => value is { Length: > 4 and <= 256 } && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && value[..^4].Length > 0;

    private sealed class Reader(Stream stream, string expected, CancellationToken token)
    {
        internal long Length;
        internal int ResourceCount, OriginalCount;
        internal int? DeclaredRootLength, ResourceBlobLength;
        internal bool? RootTrailingBytesZero;
        private uint resourceRva, resourceSize;
        private long resourceFile;
        private int directoryEntries, stringFields;
        private readonly List<Section> sections = [];
        private readonly HashSet<uint> directories = [];
        private readonly HashSet<(uint, uint)> dataSpans = [];
        private string? fixedVersion;
        private bool mismatched;

        internal void Seek(long offset)
        {
            token.ThrowIfCancellationRequested();
            Require(offset >= 0 && offset <= Length);
            Require(stream.Seek(offset, SeekOrigin.Begin) == offset && stream.Position == offset);
            token.ThrowIfCancellationRequested();
        }
        private byte[] ReadAt(long offset, int count)
        {
            token.ThrowIfCancellationRequested();
            Require(count >= 0 && offset >= 0 && offset <= Length - count);
            Seek(offset);
            var bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                token.ThrowIfCancellationRequested();
                int amount = stream.Read(bytes, read, count - read);
                Require(amount > 0 && amount <= count - read);
                read += amount;
            }
            token.ThrowIfCancellationRequested();
            return bytes;
        }
        internal SystemToolEmbeddedVersion Parse()
        {
            var dos = ReadAt(0, 64); Require(U16(dos, 0) == 0x5A4D);
            uint pe = U32(dos, 60); Require(pe >= 64);
            var header = ReadAt(pe, 24); Require(U32(header, 0) == 0x00004550);
            int sectionCount = U16(header, 6), optionalLength = U16(header, 20);
            Require(sectionCount is > 0 and <= 96 && optionalLength is >= 120 and <= 4096);
            var optional = ReadAt((long)pe + 24, optionalLength);
            ushort magic = U16(optional, 0);
            int directoryOffset = magic switch { 0x10B => 96, 0x20B => 112, _ => throw new Rejected(SystemToolEmbeddedVersionState.Unsupported) };
            Require(optionalLength >= directoryOffset);
            uint directoryCount = U32(optional, directoryOffset - 4);
            if (directoryCount <= 2) throw new Rejected(SystemToolEmbeddedVersionState.Missing);
            Require(optionalLength >= directoryOffset + 24);
            resourceRva = U32(optional, directoryOffset + 16); resourceSize = U32(optional, directoryOffset + 20);
            if (resourceRva == 0 && resourceSize == 0) throw new Rejected(SystemToolEmbeddedVersionState.Missing);
            Require(resourceRva > 0 && resourceSize is >= 16 and <= 1_048_576 && (ulong)resourceRva + resourceSize <= uint.MaxValue);
            long sectionTable = (long)pe + 24 + optionalLength;
            Require(sectionTable <= Length - sectionCount * 40);
            uint headersSize = U32(optional, 60);
            Require(headersSize >= sectionTable + sectionCount * 40 && headersSize <= Length);
            for (int i = 0; i < sectionCount; i++)
            {
                var bytes = ReadAt(sectionTable + i * 40, 40);
                var section = new Section(U32(bytes, 12), U32(bytes, 8), U32(bytes, 16), U32(bytes, 20));
                Require((ulong)section.Rva + Math.Max(section.RawSize, section.VirtualSize) <= uint.MaxValue &&
                    (section.RawSize == 0 || section.Offset >= headersSize && (ulong)section.Offset + section.RawSize <= (ulong)Length));
                foreach (var prior in sections)
                    Require(!Overlap(section.Rva, Math.Max(section.RawSize, section.VirtualSize), prior.Rva, Math.Max(prior.RawSize, prior.VirtualSize)) &&
                        !Overlap(section.Offset, section.RawSize, prior.Offset, prior.RawSize));
                sections.Add(section);
            }
            resourceFile = Map(resourceRva, resourceSize);
            ReadDirectory(0, 0);
            token.ThrowIfCancellationRequested();
            if (ResourceCount == 0 || OriginalCount == 0 || fixedVersion == null) return Result(SystemToolEmbeddedVersionState.Missing, ResourceCount, OriginalCount);
            if (mismatched) return Result(SystemToolEmbeddedVersionState.Mismatched, ResourceCount, OriginalCount);
            return new(SystemToolEmbeddedVersionState.Matched, fixedVersion, ResourceCount, OriginalCount);
        }

        private long Map(uint rva, uint count)
        {
            Require(count > 0 && (ulong)rva + count <= uint.MaxValue);
            var matches = sections.Where(section => rva >= section.Rva &&
                (ulong)rva + count <= (ulong)section.Rva + section.RawSize).ToArray();
            Require(matches.Length == 1);
            long result = (long)matches[0].Offset + rva - matches[0].Rva;
            Require(result >= 0 && result <= Length - count);
            return result;
        }
        private byte[] ResourceRead(uint offset, int length)
        {
            Require(length >= 0 && (ulong)offset + (uint)length <= resourceSize);
            return ReadAt(resourceFile + offset, length);
        }
        private void ReadDirectory(uint offset, int depth)
        {
            token.ThrowIfCancellationRequested();
            Require(depth <= 2 && (offset & 3) == 0 && directories.Add(offset));
            var header = ResourceRead(offset, 16);
            int named = U16(header, 12), count = named + U16(header, 14);
            Require(count <= 256 && directoryEntries + count <= 256);
            directoryEntries += count;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = ResourceRead(checked(offset + 16u + (uint)i * 8), 8);
                uint name = U32(entry, 0), target = U32(entry, 4);
                bool namedEntry = (name & 0x80000000) != 0;
                Require(namedEntry == (i < named));
                string key;
                if (namedEntry)
                {
                    uint nameOffset = name & 0x7FFFFFFF;
                    int characters = U16(ResourceRead(nameOffset, 2), 0);
                    Require(characters is > 0 and <= 256);
                    key = "N:" + Utf16.GetString(ResourceRead(checked(nameOffset + 2), characters * 2));
                }
                else { Require(name <= ushort.MaxValue); key = "I:" + name.ToString(CultureInfo.InvariantCulture); }
                Require(keys.Add(key), SystemToolEmbeddedVersionState.Ambiguous);
                if (depth == 0 && (namedEntry || name != 16)) continue;
                bool directory = (target & 0x80000000) != 0;
                uint child = target & 0x7FFFFFFF;
                if (depth < 2) { Require(directory); ReadDirectory(child, depth + 1); }
                else
                {
                    Require(!directory && (child & 3) == 0);
                    var data = ResourceRead(child, 16);
                    uint rva = U32(data, 0), size = U32(data, 4);
                    Require(size is > 0 and <= 65535 && rva >= resourceRva && (ulong)rva + size <= (ulong)resourceRva + resourceSize && U32(data, 12) == 0);
                    Require((rva & 3) == 0 && ResourceCount < 8);
                    foreach (var span in dataSpans) Require(!Overlap(rva, size, span.Item1, span.Item2));
                    Require(dataSpans.Add((rva, size)));
                    long file = Map(rva, size);
                    ResourceCount++;
                    ParseVersion(ReadAt(file, checked((int)size)));
                }
            }
        }

        private void ParseVersion(byte[] bytes)
        {
            stringFields = 0;
            DeclaredRootLength = null; ResourceBlobLength = null; RootTrailingBytesZero = null;
            var root = ReadBlock(bytes, 0, bytes.Length);
            DeclaredRootLength = root.End; ResourceBlobLength = bytes.Length;
            bool tailZero = true;
            for (int index = root.End; index < bytes.Length; index++)
            { token.ThrowIfCancellationRequested(); if (bytes[index] != 0) tailZero = false; }
            RootTrailingBytesZero = tailZero;
            Require(root.Key == "VS_VERSION_INFO");
            Require(root.Type == 0);
            // VS_VERSIONINFO.wLength excludes alignment padding after the root.
            // Accept minimal alignment, or the exact extra zero DWORD observed on
            // already aligned Windows tool resources. It is never parsed as content.
            Require(root.End == bytes.Length || bytes.Length == Align(root.End) ||
                ((root.End & 3) == 0 && bytes.Length == root.End + 4));
            Require(bytes.Length - root.End <= 4);
            ZeroPadding(bytes, root.End, bytes.Length);
            if (root.ValueLength == 0) throw new Rejected(SystemToolEmbeddedVersionState.Missing);
            Require(root.ValueLength == 52 && U32(bytes, root.Value) == 0xFEEF04BD && U32(bytes, root.Value + 4) == 0x00010000);
            uint ms = U32(bytes, root.Value + 8), ls = U32(bytes, root.Value + 12);
            string version = FormattableString.Invariant($"{ms >> 16}.{ms & 65535}.{ls >> 16}.{ls & 65535}");
            Require(fixedVersion == null || fixedVersion == version, SystemToolEmbeddedVersionState.Ambiguous);
            fixedVersion = version;
            var roots = new HashSet<string>(StringComparer.Ordinal);
            int tables = 0;
            foreach (var info in Children(bytes, root))
            {
                Require(roots.Add(info.Key), SystemToolEmbeddedVersionState.Ambiguous);
                Require(info.Type == 1 && info.ValueLength == 0);
                if (info.Key == "StringFileInfo")
                {
                    var tableKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var table in Children(bytes, info))
                    {
                        Require(++tables <= 16 && table.Key.Length == 8 && table.Key.All(char.IsAsciiHexDigit) && table.Type == 1 && table.ValueLength == 0);
                        Require(tableKeys.Add(table.Key), SystemToolEmbeddedVersionState.Ambiguous);
                        var fieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var field in Children(bytes, table))
                        {
                            Require(++stringFields <= 256 && field.Type == 1 && field.Children == field.End);
                            Require(fieldKeys.Add(field.Key), SystemToolEmbeddedVersionState.Ambiguous);
                            string value = StringValue(bytes, field);
                            if (!field.Key.Equals("OriginalFilename", StringComparison.OrdinalIgnoreCase)) continue;
                            Require(value.Length <= 256);
                            OriginalCount++;
                            if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) mismatched = true;
                        }
                    }
                }
                else if (info.Key == "VarFileInfo")
                {
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var variable in Children(bytes, info))
                    {
                        Require(variable.Key == "Translation" && variable.Type == 0 && variable.ValueLength > 0 && variable.ValueLength % 4 == 0 && variable.Children == variable.End);
                        Require(keys.Add(variable.Key), SystemToolEmbeddedVersionState.Ambiguous);
                    }
                }
                else throw new Rejected(SystemToolEmbeddedVersionState.Unsupported);
            }
        }
        private IEnumerable<Block> Children(byte[] bytes, Block parent)
        {
            int position = parent.Children;
            while (position < parent.End)
            {
                token.ThrowIfCancellationRequested();
                var child = ReadBlock(bytes, position, parent.End);
                yield return child;
                if (child.End == parent.End) yield break;
                position = Align(child.End);
                Require(position <= parent.End); ZeroPadding(bytes, child.End, position);
            }
        }
        private Block ReadBlock(byte[] bytes, int start, int limit)
        {
            token.ThrowIfCancellationRequested();
            Require((start & 3) == 0 && start >= 0 && limit <= bytes.Length && start <= limit - 6);
            int length = U16(bytes, start), valueLength = U16(bytes, start + 2); ushort type = U16(bytes, start + 4);
            Require(length >= 6 && start <= limit - length && type <= 1);
            int end = start + length, position = start + 6, keyStart = position;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Require(position <= end - 2 && (position - keyStart) / 2 <= 256);
                if (U16(bytes, position) == 0) break;
                position += 2;
            }
            string key = Utf16.GetString(bytes, keyStart, position - keyStart);
            Require(key.Length is > 0 and <= 256);
            position += 2; int value = Align(position);
            Require(value <= end); ZeroPadding(bytes, position, value);
            int valueBytes = type == 1 ? valueLength * 2 : valueLength;
            Require(value <= end - valueBytes);
            int valueEnd = value + valueBytes, children = valueEnd == end ? end : Align(valueEnd);
            Require(children <= end); ZeroPadding(bytes, valueEnd, children);
            return new(start, end, (ushort)valueLength, type, key, value, children);
        }
        private string StringValue(byte[] bytes, Block block)
        {
            if (block.ValueLength == 0) return "";
            Require(U16(bytes, block.Value + block.ValueLength * 2 - 2) == 0);
            string text = Utf16.GetString(bytes, block.Value, (block.ValueLength - 1) * 2);
            Require(!text.Contains('\0'));
            return text;
        }
        private void ZeroPadding(byte[] bytes, int start, int end)
        { for (int i = start; i < end; i++) { token.ThrowIfCancellationRequested(); Require(bytes[i] == 0); } }
        private static int Align(int value) => checked((value + 3) & ~3);
        private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static bool Overlap(uint a, uint aLength, uint b, uint bLength) => aLength > 0 && bLength > 0 && (ulong)a < (ulong)b + bLength && (ulong)b < (ulong)a + aLength;
        private static void Require(bool condition, SystemToolEmbeddedVersionState state = SystemToolEmbeddedVersionState.Malformed,
            [CallerLineNumber] int site = 0)
        { if (!condition) throw new Rejected(state, site); }
    }
}
