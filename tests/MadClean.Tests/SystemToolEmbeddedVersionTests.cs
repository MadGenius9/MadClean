using System.Buffers.Binary;
using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemToolEmbeddedVersionTests
{
    // Independent synthetic PE fixture: DOS at0, PE at128, one file-backed .rsrc
    // section at512/RVA4096, type16/name1/language directory at0/32/64.
    private static void Word(byte[] data, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
    private static void Dword(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
    private static byte[] Block(string key, ushort type, byte[] value, ushort valueLength, params byte[][] children)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode, true);
        writer.Write((ushort)0); writer.Write(valueLength); writer.Write(type);
        writer.Write(Encoding.Unicode.GetBytes(key + "\0"));
        while (stream.Position % 4 != 0) writer.Write((byte)0);
        writer.Write(value);
        foreach (byte[] child in children)
        {
            while (stream.Position % 4 != 0) writer.Write((byte)0);
            writer.Write(child);
        }
        byte[] result = stream.ToArray(); Word(result, 0, checked((ushort)result.Length)); return result;
    }
    private static byte[] StringField(string key, string value) => Block(key, 1, Encoding.Unicode.GetBytes(value + "\0"), checked((ushort)(value.Length + 1)));
    private static byte[] Version(string? original = "Dism.exe", uint ms = 0x000A0000, uint ls = 0x00010002,
        bool fixedInfo = true, params byte[][] extraTables)
    {
        byte[] fixedBytes = new byte[52]; Dword(fixedBytes, 0, 0xFEEF04BD); Dword(fixedBytes, 4, 0x00010000);
        Dword(fixedBytes, 8, ms); Dword(fixedBytes, 12, ls);
        byte[] table = Block("040904B0", 1, [], 0, original == null ? [] : [StringField("OriginalFilename", original)]);
        byte[][] tables = [table, .. extraTables];
        byte[] strings = Block("StringFileInfo", 1, [], 0, tables);
        byte[] translation = Block("VarFileInfo", 1, [], 0, Block("Translation", 0, [9, 4, 0xB0, 4], 4));
        return Block("VS_VERSION_INFO", 0, fixedInfo ? fixedBytes : [], fixedInfo ? (ushort)52 : (ushort)0, strings, translation);
    }
    private static byte[] Pe(bool plus = false, params byte[][] versions)
    {
        var data = new byte[512 + 8192]; Word(data, 0, 0x5A4D); Dword(data, 60, 128); Dword(data, 128, 0x4550);
        Word(data, 132, plus ? (ushort)0x8664 : (ushort)0x14C); Word(data, 134, 1);
        int optionalLength = plus ? 240 : 224; Word(data, 148, (ushort)optionalLength);
        int optional = 152, directory = optional + (plus ? 112 : 96);
        Word(data, optional, plus ? (ushort)0x20B : (ushort)0x10B); Dword(data, optional + 60, 512);
        Dword(data, directory - 4, 16); Dword(data, directory + 16, 4096); Dword(data, directory + 20, 8192);
        int section = optional + optionalLength;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(data, section); Dword(data, section + 8, 8192); Dword(data, section + 12, 4096);
        Dword(data, section + 16, 8192); Dword(data, section + 20, 512);
        int root = 512; Word(data, root + 14, versions.Length == 0 ? (ushort)0 : (ushort)1);
        Dword(data, root + 16, 16); Dword(data, root + 20, 0x80000020);
        Word(data, root + 32 + 14, 1); Dword(data, root + 48, 1); Dword(data, root + 52, 0x80000040);
        Word(data, root + 64 + 14, (ushort)versions.Length);
        int blob = 512;
        for (int i = 0; i < versions.Length; i++)
        {
            Dword(data, root + 80 + i * 8, (uint)(1033 + i)); Dword(data, root + 84 + i * 8, (uint)(192 + i * 16));
            Dword(data, root + 192 + i * 16, (uint)(4096 + blob)); Dword(data, root + 196 + i * 16, (uint)versions[i].Length);
            versions[i].CopyTo(data, root + blob); blob = (blob + versions[i].Length + 3) & ~3;
        }
        return data;
    }
    private static SystemToolEmbeddedVersion Read(byte[] data, string expected = "Dism.exe")
    { using var stream = new MemoryStream(data, false); return SystemToolEmbeddedVersionReader.Read(stream, expected); }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ReadsEmbeddedFixedVersionAndExactOriginalNameWithoutPathLoader(bool plus)
    {
        var result = Read(Pe(plus, Version()));
        Assert.Equal(SystemToolEmbeddedVersionState.Matched, result.State); Assert.Equal("10.0.1.2", result.FileVersion);
        Assert.Equal(1, result.ResourceCount); Assert.Equal(1, result.OriginalFilenameCount);
        Assert.Null(result.FailureSite);
    }
    [Fact]
    public void MalformedFixtureReportsOnlyBoundedCompiledValidationSite()
    {
        var data = Pe(false, Version());
        Dword(data, 512 + 192, uint.MaxValue - 4);
        var result = Read(data);
        Assert.Equal(SystemToolEmbeddedVersionState.Malformed, result.State);
        Assert.True(result.FailureSite is > 0 and <= 1000);
        Assert.Equal("Unknown", result.FileVersion);
    }
    [Fact]
    public void ConsistentLanguagesAndTablesAreAcceptedButConflictingVersionsAreNot()
    {
        byte[] table = Block("040C04B0", 1, [], 0, StringField("OriginalFilename", "DISM.EXE"));
        var valid = Read(Pe(false, Version(extraTables: [table]), Version("dism.exe")));
        Assert.Equal(SystemToolEmbeddedVersionState.Matched, valid.State); Assert.Equal(2, valid.ResourceCount); Assert.Equal(3, valid.OriginalFilenameCount);
        Assert.Equal(SystemToolEmbeddedVersionState.Ambiguous, Read(Pe(false, Version(), Version(ls: 0x00010003))).State);
    }
    [Theory]
    [InlineData("Dism.exe.mui")][InlineData("Other.exe")][InlineData("Dism.exe ")][InlineData("Dism.exe\0Other.exe")]
    public void FilenameAliasesSuffixesAndEmbeddedNullsCannotMatch(string original)
    {
        var result = Read(Pe(false, Version(original)));
        Assert.NotEqual(SystemToolEmbeddedVersionState.Matched, result.State); Assert.Equal("Unknown", result.FileVersion);
    }
    [Fact]
    public void MissingResourceNameOrFixedVersionNeverEstablishesIdentity()
    {
        Assert.Equal(SystemToolEmbeddedVersionState.Missing, Read(Pe()).State);
        Assert.Equal(SystemToolEmbeddedVersionState.Missing, Read(Pe(false, Version(null))).State);
        Assert.Equal(SystemToolEmbeddedVersionState.Missing, Read(Pe(false, Version(fixedInfo: false))).State);
    }
    [Fact]
    public void ConflictingNamesAndDuplicateFieldsOrLanguageResourcesFailClosed()
    {
        Assert.Equal(SystemToolEmbeddedVersionState.Mismatched, Read(Pe(false, Version(), Version("Other.exe"))).State);
        byte[] duplicate = Block("040C04B0", 1, [], 0, StringField("OriginalFilename", "Dism.exe"), StringField("OriginalFilename", "Dism.exe"));
        Assert.Equal(SystemToolEmbeddedVersionState.Ambiguous, Read(Pe(false, Version(extraTables: [duplicate]))).State);
        var pe = Pe(false, Version(), Version()); Dword(pe, 512 + 88, 1033);
        Assert.Equal(SystemToolEmbeddedVersionState.Ambiguous, Read(pe).State);
    }
    [Fact]
    public void RejectsMalformedResourceRangesCyclesMisalignmentAndTruncatedPE()
    {
        var valid = Pe(false, Version());
        var mutations = new Action<byte[]>[]
        {
            data => Dword(data, 152 + 96 + 16, uint.MaxValue - 4),
            data => Dword(data, 512 + 20, 0x80000000),
            data => Dword(data, 512 + 52, 0x80000021),
            data => Dword(data, 512 + 192, 4096 + 8190),
            data => Dword(data, 512 + 196, uint.MaxValue),
            data => Dword(data, 152 + 224 + 16, 100),
            data => Word(data, 512 + 512, ushort.MaxValue),
            data => Dword(data, 512 + 512 + 40, 0),
            data => Word(data, 512 + 14, 257)
        };
        foreach (var mutate in mutations)
        {
            var data = (byte[])valid.Clone(); mutate(data);
            Assert.NotEqual(SystemToolEmbeddedVersionState.Matched, Read(data).State);
        }
        foreach (int length in new[] { 1, 63, 150, 500, valid.Length - 1 })
            Assert.NotEqual(SystemToolEmbeddedVersionState.Matched, Read(valid[..length]).State);
    }
    [Fact]
    public void RejectsOverlappingRawSectionsAndVirtualOnlyResourceBytes()
    {
        var data = Pe(false, Version()); Word(data, 134, 2);
        int section = 152 + 224; data.AsSpan(section, 40).CopyTo(data.AsSpan(section + 40, 40));
        Assert.Equal(SystemToolEmbeddedVersionState.Malformed, Read(data).State);
        data = Pe(false, Version()); Dword(data, section + 16, 128);
        Assert.Equal(SystemToolEmbeddedVersionState.Malformed, Read(data).State);
    }
    [Fact]
    public void MalformedUtf16AndProductBoundsNeverProduceKnownVersion()
    {
        var malformed = Version();
        int filename = malformed.AsSpan().IndexOf(Encoding.Unicode.GetBytes("Dism.exe\0"));
        Assert.True(filename >= 0); Word(malformed, filename, 0xD800);
        Assert.Equal(SystemToolEmbeddedVersionState.Malformed, Read(Pe(false, malformed)).State);
        Assert.NotEqual(SystemToolEmbeddedVersionState.Matched, Read(Pe(false, Version(new string('x', 257)))).State);
        Assert.NotEqual(SystemToolEmbeddedVersionState.Matched, Read(Pe(false, Enumerable.Range(0, 9).Select(_ => Version()).ToArray())).State);
        Assert.Equal(SystemToolEmbeddedVersionState.Unsupported, Read(Pe(false, Version()), "../Dism.exe").State);
        var tables = Enumerable.Range(0, 16).Select(i => Block(i.ToString("X8"), 1, [], 0, StringField("OriginalFilename", "Dism.exe"))).ToArray();
        Assert.NotEqual(SystemToolEmbeddedVersionState.Matched, Read(Pe(false, Version(extraTables: tables))).State);
        var fields = Enumerable.Range(0, 257).Select(i => StringField("F" + i, "x")).ToArray();
        var excessiveFields = Block("040C04B0", 1, [], 0, fields);
        Assert.NotEqual(SystemToolEmbeddedVersionState.Matched, Read(Pe(false, Version(extraTables: [excessiveFields]))).State);
    }
    [Fact]
    public void ShortReadsAreCompletedWithinBoundsAndCallerPositionIsRestoredWithoutClosing()
    {
        using var stream = new LimitedStream(Pe(false, Version())); stream.Position = 17;
        var result = SystemToolEmbeddedVersionReader.Read(stream, "Dism.exe");
        Assert.Equal(SystemToolEmbeddedVersionState.Matched, result.State); Assert.Equal(17, stream.Position); Assert.True(stream.CanRead);
        Assert.True(stream.MaximumReadRequest <= 65535); Assert.True(stream.TotalRead < 2000);
    }
    [Fact]
    public void NonseekableAndFailedPositionRestorationCannotReturnMatched()
    {
        using var noSeek = new LimitedStream(Pe(false, Version())) { Seekable = false };
        Assert.Equal(SystemToolEmbeddedVersionState.Unsupported, SystemToolEmbeddedVersionReader.Read(noSeek, "Dism.exe").State);
        using var noRestore = new LimitedStream(Pe(false, Version())) { FailRestore = true }; noRestore.Position = 17;
        Assert.Equal(SystemToolEmbeddedVersionState.Unsupported, SystemToolEmbeddedVersionReader.Read(noRestore, "Dism.exe").State);
    }
    [Fact]
    public void CancellationDuringReadRestoresPositionAndOverridesRestoreFailure()
    {
        using var stop = new CancellationTokenSource();
        using var stream = new LimitedStream(Pe(false, Version())) { AfterRead = stop.Cancel, FailRestore = true }; stream.Position = 17;
        Assert.Throws<OperationCanceledException>(() => SystemToolEmbeddedVersionReader.Read(stream, "Dism.exe", stop.Token));
        Assert.True(stream.CanRead);
        Assert.Throws<OperationCanceledException>(() => SystemToolEmbeddedVersionReader.Read(stream, "Dism.exe", stop.Token));
    }
    private sealed class LimitedStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        internal bool Seekable = true, FailRestore;
        internal int MaximumReadRequest, TotalRead;
        internal Action? AfterRead;
        public override bool CanSeek => Seekable;
        public override long Seek(long offset, SeekOrigin loc)
        {
            if (FailRestore && offset == 17 && loc == SeekOrigin.Begin) throw new IOException("TEST-only restore failure");
            return base.Seek(offset, loc);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            MaximumReadRequest = Math.Max(MaximumReadRequest, count);
            int result = base.Read(buffer, offset, Math.Min(count, 7)); TotalRead += result; AfterRead?.Invoke(); return result;
        }
    }
}
