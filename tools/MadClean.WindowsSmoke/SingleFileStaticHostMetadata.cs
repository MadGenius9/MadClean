using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MadClean.WindowsSmoke;

internal enum SingleFileStaticHostMetadataState { Observed, Unknown, Unsupported, Malformed, LimitReached }
internal enum SingleFileStaticHostMetadataReason
{
    Observed, StreamUnsupported, ImageUnsupported, MalformedImage, RequiredExportsMissing,
    ExportAmbiguous, ForwardedExport, RuntimeInfoMismatch, ReadLimitReached, PositionRestoreFailed
}
internal sealed record SingleFileStaticHostMetadataResult(SingleFileStaticHostMetadataState State, SingleFileStaticHostMetadataReason Reason)
{
    internal bool StaticHostMetadataObserved => State == SingleFileStaticHostMetadataState.Observed && Reason == SingleFileStaticHostMetadataReason.Observed;
}

// Development evidence only, pinned to the official release/10.0 Windows static
// host exports and RuntimeInfo v2. Never loads an image, invokes an export or
// interprets these bytes as publisher trust, execution or production permission.
internal static class SingleFileStaticHostMetadata
{
    private const int ReadLimit = 1_048_576;
    private static readonly string[] Required =
        ["g_CLREngineMetrics", "CLRJitAttachState", "DotNetRuntimeInfo", "g_dacTable", "MetaDataGetDispenser", "DotNetRuntimeContractDescriptor"];

    internal static SingleFileStaticHostMetadataResult Read(Stream stream, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(stream);
        long original = 0; bool captured = false, restoreFailed = false;
        var result = Result(SingleFileStaticHostMetadataState.Unsupported, SingleFileStaticHostMetadataReason.StreamUnsupported);
        try
        {
            if (stream.CanRead && stream.CanSeek)
            {
                original = stream.Position; captured = true;
                result = new Reader(stream, token).Parse();
            }
        }
        catch (Rejected failure) { result = Result(failure.State, failure.Reason); }
        catch (OverflowException) { result = Result(SingleFileStaticHostMetadataState.Malformed, SingleFileStaticHostMetadataReason.MalformedImage); }
        catch (Exception failure) when (failure is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        { result = Result(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.StreamUnsupported); }
        finally
        {
            if (captured)
            {
                try { restoreFailed = stream.Seek(original, SeekOrigin.Begin) != original || stream.Position != original; }
                catch (Exception failure) when (failure is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
                { restoreFailed = true; }
            }
        }
        // Cancellation is never hidden by a failed restoration. Ownership remains
        // with the caller; this parser never closes or disposes its input stream.
        token.ThrowIfCancellationRequested();
        return restoreFailed ? Result(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.PositionRestoreFailed) : result;
    }
    private static SingleFileStaticHostMetadataResult Result(SingleFileStaticHostMetadataState state, SingleFileStaticHostMetadataReason reason) => new(state, reason);
    private sealed class Rejected(SingleFileStaticHostMetadataState state, SingleFileStaticHostMetadataReason reason) : Exception
    { internal SingleFileStaticHostMetadataState State { get; } = state; internal SingleFileStaticHostMetadataReason Reason { get; } = reason; }
    private sealed record Section(uint Virtual, ulong VirtualEnd, uint Raw, uint RawBytes);
    private sealed class Reader(Stream stream, CancellationToken token)
    {
        private readonly long length = stream.Length;
        private int consumed;
        private uint imageBytes;
        private readonly List<Section> sections = [];

        internal SingleFileStaticHostMetadataResult Parse()
        {
            var dos = At(0, 64);
            if (U16(dos, 0) != 0x5A4D) Reject(SingleFileStaticHostMetadataState.Unsupported, SingleFileStaticHostMetadataReason.ImageUnsupported);
            int pe = I32(dos, 60); Need(pe >= 64);
            var coff = At(pe, 24); Need(U32(coff, 0) == 0x00004550);
            if (U16(coff, 4) != 0x8664 || (U16(coff, 22) & 0x2002) != 2)
                Reject(SingleFileStaticHostMetadataState.Unsupported, SingleFileStaticHostMetadataReason.ImageUnsupported);
            int count = U16(coff, 6), optionalBytes = U16(coff, 20);
            if (count > 96) Limit();
            Need(count > 0 && optionalBytes >= 120);
            long optionalOffset = checked((long)pe + 24);
            var optional = At(optionalOffset, 120);
            if (U16(optional, 0) != 0x20B) Reject(SingleFileStaticHostMetadataState.Unsupported, SingleFileStaticHostMetadataReason.ImageUnsupported);
            imageBytes = U32(optional, 56);
            uint headers = U32(optional, 60), directories = U32(optional, 108);
            long sectionOffset = checked(optionalOffset + optionalBytes);
            Need(directories >= 1 && directories <= (optionalBytes - 112) / 8 && headers >= sectionOffset + 40L * count && headers <= length && imageBytes >= headers);
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var data = At(sectionOffset + 40L * i, 40);
                uint virtualBytes = U32(data, 8), address = U32(data, 12), rawBytes = U32(data, 16), raw = U32(data, 20);
                ulong virtualEnd = (ulong)address + Math.Max(virtualBytes, rawBytes);
                Need(virtualEnd <= imageBytes && (rawBytes == 0 || raw >= headers && (ulong)raw + rawBytes <= (ulong)length));
                if (virtualEnd > address) Need(address >= headers);
                var section = new Section(address, virtualEnd, raw, rawBytes);
                foreach (var previous in sections)
                {
                    token.ThrowIfCancellationRequested();
                    if (Overlap(address, virtualEnd, previous.Virtual, previous.VirtualEnd) ||
                        Overlap(raw, (ulong)raw + rawBytes, previous.Raw, (ulong)previous.Raw + previous.RawBytes))
                        Reject(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.ExportAmbiguous);
                }
                sections.Add(section);
            }
            uint exportRva = U32(optional, 112), exportBytes = U32(optional, 116);
            if (exportRva == 0 && exportBytes == 0) return Result(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.RequiredExportsMissing);
            Need(exportRva != 0 && exportBytes >= 40);
            if (exportBytes > ReadLimit) Limit();
            Map(exportRva, checked((int)exportBytes));
            var table = Rva(exportRva, 40);
            uint ordinalBase = U32(table, 16), functions = U32(table, 20), names = U32(table, 24);
            if (functions > 4096 || names > 4096) Limit();
            if (functions == 0 || names == 0) return Result(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.RequiredExportsMissing);
            var functionTable = Rva(U32(table, 28), checked((int)functions * 4));
            var nameTable = Rva(U32(table, 32), checked((int)names * 4));
            var ordinalTable = Rva(U32(table, 36), checked((int)names * 2));
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var seenOrdinals = new HashSet<uint>();
            var matched = new Dictionary<string, uint>(StringComparer.Ordinal);
            ulong exportEnd = (ulong)exportRva + exportBytes;
            for (int i = 0; i < names; i++)
            {
                token.ThrowIfCancellationRequested();
                string name = Name(U32(nameTable, i * 4));
                uint index = U16(ordinalTable, i * 2);
                Need(index < functions);
                ulong ordinal = (ulong)ordinalBase + index; Need(ordinal <= uint.MaxValue);
                if (!seenNames.Add(name) || !seenOrdinals.Add((uint)ordinal))
                    Reject(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.ExportAmbiguous);
                uint target = U32(functionTable, checked((int)index * 4)); Need(target != 0);
                if (target >= exportRva && target < exportEnd)
                    Reject(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.ForwardedExport);
                // DATA exports can legitimately live in a zero-filled virtual tail.
                // We qualify their image addresses without reading or inventing values.
                ImageSection(target, 1, requireFileBytes: false);
                if (!Required.Contains(name, StringComparer.Ordinal)) continue;
                uint expected = name switch { "g_CLREngineMetrics" => 2, "CLRJitAttachState" => 3, "DotNetRuntimeInfo" => 4, _ => 0 };
                if (expected != 0 && ordinal != expected)
                    Reject(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.RuntimeInfoMismatch);
                matched.Add(name, target);
            }
            if (matched.Count != Required.Length)
                return Result(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.RequiredExportsMissing);
            uint infoRva = matched["DotNetRuntimeInfo"];
            if (Overlap(infoRva, (ulong)infoRva + 112, exportRva, exportEnd))
                Reject(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.ForwardedExport);
            var info = Rva(infoRva, 112);
            // Signature[18], ignored alignment[2], int Version at20, three24-byte
            // indexes at24..95, RuntimeVersion[4] at96. No index/pointer is followed.
            if (!info.AsSpan(0, 18).SequenceEqual("DotNetRuntimeInfo\0"u8) || I32(info, 20) != 2 ||
                I32(info, 96) != 10 || I32(info, 100) != 0 || I32(info, 104) < 0 || I32(info, 108) < 0)
                return Result(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.RuntimeInfoMismatch);
            return Result(SingleFileStaticHostMetadataState.Observed, SingleFileStaticHostMetadataReason.Observed);
        }

        private string Name(uint rva)
        {
            var bytes = new List<byte>(127);
            for (int i = 0; i < 128; i++)
            {
                token.ThrowIfCancellationRequested();
                ulong address = (ulong)rva + (uint)i; Need(address <= uint.MaxValue);
                byte value = Rva((uint)address, 1)[0];
                if (value == 0)
                {
                    Need(bytes.Count > 0); Map(rva, i + 1);
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                Need(value >= 0x21 && value <= 0x7E); bytes.Add(value);
            }
            Limit(); return "";
        }
        private byte[] Rva(uint rva, int count) => At(Map(rva, count), count);
        private long Map(uint rva, int count)
        {
            var section = ImageSection(rva, count, requireFileBytes: true);
            return checked((long)section.Raw + rva - section.Virtual);
        }
        private Section ImageSection(uint rva, int count, bool requireFileBytes)
        {
            Need(count > 0 && rva != 0 && (ulong)rva + (uint)count <= imageBytes);
            Section? match = null;
            foreach (var section in sections)
            {
                token.ThrowIfCancellationRequested();
                if (rva < section.Virtual || (ulong)rva + (uint)count > section.VirtualEnd ||
                    requireFileBytes && (ulong)rva - section.Virtual + (uint)count > section.RawBytes) continue;
                if (match != null) Reject(SingleFileStaticHostMetadataState.Unknown, SingleFileStaticHostMetadataReason.ExportAmbiguous);
                match = section;
            }
            Need(match != null);
            return match!;
        }
        private byte[] At(long offset, int count)
        {
            token.ThrowIfCancellationRequested();
            Need(count > 0 && offset >= 0 && offset <= length && count <= length - offset);
            if (count > ReadLimit - consumed) Limit();
            if (stream.Seek(offset, SeekOrigin.Begin) != offset) Reject(SingleFileStaticHostMetadataState.Unsupported, SingleFileStaticHostMetadataReason.StreamUnsupported);
            byte[] data = new byte[count]; int total = 0;
            while (total < count)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(data, total, count - total);
                Need(read > 0 && read <= count - total);
                consumed += read; total += read;
            }
            token.ThrowIfCancellationRequested();
            return data;
        }
        private static bool Overlap(ulong first, ulong end, ulong other, ulong otherEnd) => first < end && other < otherEnd && first < otherEnd && other < end;
        private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static void Need(bool condition) { if (!condition) Reject(SingleFileStaticHostMetadataState.Malformed, SingleFileStaticHostMetadataReason.MalformedImage); }
        private static void Limit() => Reject(SingleFileStaticHostMetadataState.LimitReached, SingleFileStaticHostMetadataReason.ReadLimitReached);
        private static void Reject(SingleFileStaticHostMetadataState state, SingleFileStaticHostMetadataReason reason) => throw new Rejected(state, reason);
    }
}
