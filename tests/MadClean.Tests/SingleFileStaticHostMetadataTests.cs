using System.Buffers.Binary;
using System.Text;
using MadClean.WindowsSmoke;
using Xunit;

namespace MadClean.Tests;

// Inert handcrafted bytes only. None of these images is loaded or executed.
public sealed class SingleFileStaticHostMetadataTests
{
    private const int Pe = 0x80, Optional = 0x98, Sections = 0x188;
    private const int Exports = 0x400, Functions = 0x440, Names = 0x460, Ordinals = 0x480, RuntimeInfo = 0x1480;
    private static readonly string[] ExportNames =
        ["g_CLREngineMetrics", "CLRJitAttachState", "DotNetRuntimeInfo", "g_dacTable", "MetaDataGetDispenser", "DotNetRuntimeContractDescriptor"];
    private static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static byte[] Image(int fileBytes = 0x2400)
    {
        var bytes = new byte[fileBytes];
        U16(bytes, 0, 0x5A4D); U32(bytes, 0x3C, Pe); U32(bytes, Pe, 0x00004550);
        U16(bytes, Pe + 4, 0x8664); U16(bytes, Pe + 6, 2); U16(bytes, Pe + 20, 240); U16(bytes, Pe + 22, 0x22);
        U16(bytes, Optional, 0x20B); U32(bytes, Optional + 56, 0x4000); U32(bytes, Optional + 60, 0x400);
        U32(bytes, Optional + 108, 16); U32(bytes, Optional + 112, 0x1000); U32(bytes, Optional + 116, 0x300);
        // Two disjoint sections: exports at RVA1000/raw400; metadata at RVA3000/raw1400.
        U32(bytes, Sections + 8, 0x1000); U32(bytes, Sections + 12, 0x1000); U32(bytes, Sections + 16, 0x1000); U32(bytes, Sections + 20, 0x400);
        U32(bytes, Sections + 48, 0x1000); U32(bytes, Sections + 52, 0x3000); U32(bytes, Sections + 56, 0x1000); U32(bytes, Sections + 60, 0x1400);
        U32(bytes, Exports + 16, 1); U32(bytes, Exports + 20, 6); U32(bytes, Exports + 24, 6);
        U32(bytes, Exports + 28, 0x1040); U32(bytes, Exports + 32, 0x1060); U32(bytes, Exports + 36, 0x1080);
        uint[] targets = [0x3100, 0x3020, 0x3030, 0x3080, 0x3040, 0x3050];
        ushort[] indexes = [1, 2, 3, 4, 0, 5];
        for (int i = 0; i < targets.Length; i++) U32(bytes, Functions + i * 4, targets[i]);
        int nameOffset = 0x500;
        for (int i = 0; i < ExportNames.Length; i++)
        {
            U32(bytes, Names + i * 4, (uint)(nameOffset + 0xC00)); U16(bytes, Ordinals + i * 2, indexes[i]);
            byte[] name = Encoding.ASCII.GetBytes(ExportNames[i] + "\0"); name.CopyTo(bytes, nameOffset); nameOffset += name.Length;
        }
        Encoding.ASCII.GetBytes("DotNetRuntimeInfo\0").CopyTo(bytes, RuntimeInfo);
        U32(bytes, RuntimeInfo + 20, 2); U32(bytes, RuntimeInfo + 96, 10); U32(bytes, RuntimeInfo + 100, 0);
        U32(bytes, RuntimeInfo + 104, 1234); U32(bytes, RuntimeInfo + 108, 5678);
        return bytes;
    }
    private static SingleFileStaticHostMetadataResult Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false); stream.Position = 7;
        var result = SingleFileStaticHostMetadata.Read(stream);
        Assert.Equal(7, stream.Position); Assert.True(stream.CanRead);
        return result;
    }
    private static void NotObserved(byte[] bytes) => Assert.False(Read(bytes).StaticHostMetadataObserved);

    [Fact]
    public void QualifiedMetadataDoesNotReadImageOverlayOrRequireZeroPadding()
    {
        byte[] bytes = Image(2_000_000); bytes[RuntimeInfo + 18] = 0xA5; bytes[RuntimeInfo + 19] = 0xFF;
        using var stream = new TrackingStream(bytes, maximumChunk: 3); stream.Position = 7;
        var result = SingleFileStaticHostMetadata.Read(stream);
        Assert.True(result.StaticHostMetadataObserved); Assert.Equal(SingleFileStaticHostMetadataState.Observed, result.State);
        Assert.Equal(7, stream.Position); Assert.False(stream.WasDisposed);
        Assert.InRange(stream.BytesRead, 112, 1_048_576); Assert.True(stream.BytesRead < bytes.Length);
        Assert.InRange(stream.MaximumRequested, 1, 120);
    }

    [Fact]
    public void NonReadDataExportMayOccupyZeroFilledVirtualMemory()
    {
        var bytes = Image(); U32(bytes, Sections + 56, 0x200); U32(bytes, Functions + 2 * 4, 0x3F00);
        using var stream = new TrackingStream(bytes); var result = SingleFileStaticHostMetadata.Read(stream);
        Assert.True(result.StaticHostMetadataObserved);
        Assert.DoesNotContain(stream.Reads, span => span.Offset >= 0x2300);
    }

    [Fact]
    public void RuntimeInfoMustBeActuallyFileBackedRatherThanInventedBssZeros()
    {
        var bytes = Image(); U32(bytes, Sections + 56, 0x200); U32(bytes, Functions + 3 * 4, 0x3F00);
        NotObserved(bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlappingVirtualOrRawSectionsAreAmbiguous(bool virtualOverlap)
    {
        var bytes = Image(); U32(bytes, Sections + (virtualOverlap ? 52 : 60), virtualOverlap ? 0x1800u : 0xC00u);
        var result = Read(bytes); Assert.False(result.StaticHostMetadataObserved);
        Assert.Equal(SingleFileStaticHostMetadataReason.ExportAmbiguous, result.Reason);
    }

    [Fact]
    public void DuplicateNamesAreNotAcceptedAsSixIndependentRequiredExports()
    {
        var bytes = Image(); U32(bytes, Names + 8, 0x1100);
        Assert.Equal(SingleFileStaticHostMetadataReason.ExportAmbiguous, Read(bytes).Reason);
    }

    [Fact]
    public void ReusedNamedOrdinalCannotAliasAnotherRequiredSymbol()
    {
        var bytes = Image(); U16(bytes, Ordinals + 6, 1);
        Assert.Equal(SingleFileStaticHostMetadataReason.ExportAmbiguous, Read(bytes).Reason);
    }

    [Fact]
    public void MissingNameDoesNotBecomeAQualifiedStaticHost()
    {
        var bytes = Image(); bytes[0x500] = (byte)'x';
        Assert.Equal(SingleFileStaticHostMetadataReason.RequiredExportsMissing, Read(bytes).Reason);
    }

    [Fact]
    public void ForwarderStringIsNotRuntimeDataOrAnExportToInvoke()
    {
        var bytes = Image(); U32(bytes, Functions + 4, 0x1120);
        Assert.Equal(SingleFileStaticHostMetadataReason.ForwardedExport, Read(bytes).Reason);
    }

    [Fact]
    public void OrdinalIndexOutsideFunctionTableIsRejected()
    {
        var bytes = Image(); U16(bytes, Ordinals, 6); NotObserved(bytes);
    }

    [Fact]
    public void ExplicitSourceOrdinalsMustMatchEvenIfNamesAndAddressesExist()
    {
        var bytes = Image(); U32(bytes, Exports + 16, 8);
        Assert.Equal(SingleFileStaticHostMetadataReason.RuntimeInfoMismatch, Read(bytes).Reason);
    }

    [Theory]
    [InlineData(112, 0xFFFFFFF0u)]
    [InlineData(112, 0u)]
    public void InvalidExportRvaCannotWrapIntoHeaders(int offset, uint invalid)
    {
        var bytes = Image(); U32(bytes, Optional + offset, invalid); NotObserved(bytes);
    }

    [Fact]
    public void TableRvaMustHaveOneCompleteFileBackedSpan()
    {
        var bytes = Image(); U32(bytes, Exports + 28, 0x1FF8); NotObserved(bytes);
    }

    [Fact]
    public void RuntimeInfoCannotStraddleAdjacentVirtualSections()
    {
        var bytes = Image(); U32(bytes, Optional + 56, 0x3000); U32(bytes, Sections + 52, 0x2000);
        for (int i = 0; i < 6; i++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Functions + i * 4, 4));
            U32(bytes, Functions + i * 4, value - 0x1000);
        }
        U32(bytes, Functions + 12, 0x1FC0); NotObserved(bytes);
    }

    [Fact]
    public void NameCannotBeAssembledAcrossAdjacentFileBackedSections()
    {
        var bytes = Image(); U32(bytes, Optional + 56, 0x3000); U32(bytes, Sections + 52, 0x2000);
        for (int i = 0; i < 6; i++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Functions + i * 4, 4));
            U32(bytes, Functions + i * 4, value - 0x1000);
        }
        // Both one-byte mappings exist and the string bytes are physically adjacent,
        // but one complete name must not be stitched across two distinct sections.
        U32(bytes, Names, 0x1FF8);
        Encoding.ASCII.GetBytes(ExportNames[0] + "\0").CopyTo(bytes, 0x13F8);
        NotObserved(bytes);
    }

    [Fact]
    public void NonReadExportStillCannotPointIntoAnUnmappedImageGap()
    {
        var bytes = Image(); U32(bytes, Functions + 8, 0x2FF0); NotObserved(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(96)]
    [InlineData(100)]
    [InlineData(104)]
    [InlineData(108)]
    public void RuntimeSignatureContractOrVersionMismatchNeverQualifies(int field)
    {
        var bytes = Image();
        if (field == 0) bytes[RuntimeInfo] = (byte)'x';
        else U32(bytes, RuntimeInfo + field, field is 104 or 108 ? uint.MaxValue : 9u);
        Assert.Equal(SingleFileStaticHostMetadataReason.RuntimeInfoMismatch, Read(bytes).Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArchitectureOrPe32MagicIsNotThePinnedAmd64Profile(bool architecture)
    {
        var bytes = Image(); U16(bytes, architecture ? Pe + 4 : Optional, architecture ? (ushort)0x14C : (ushort)0x10B);
        Assert.Equal(SingleFileStaticHostMetadataState.Unsupported, Read(bytes).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CountAndDirectorySizeProductBoundsAreEnforcedBeforeAllocation(int bound)
    {
        var bytes = Image();
        if (bound == 0) U16(bytes, Pe + 6, 97);
        if (bound == 1) U32(bytes, Exports + 20, 4097);
        if (bound == 2) U32(bytes, Exports + 24, 4097);
        if (bound == 3) U32(bytes, Optional + 116, 1_048_577);
        var result = Read(bytes); Assert.False(result.StaticHostMetadataObserved);
        Assert.Equal(SingleFileStaticHostMetadataState.LimitReached, result.State);
    }

    [Fact]
    public void NameReadHasAnAsciiAndTerminationBound()
    {
        var bytes = Image(); bytes.AsSpan(0x500, 128).Fill((byte)'Z');
        Assert.Equal(SingleFileStaticHostMetadataState.LimitReached, Read(bytes).State);
        bytes = Image(); bytes[0x500] = 0xFF; NotObserved(bytes);
    }

    [Fact]
    public void TruncationAndShortReadsCannotProduceAnEmptySuccessfulProfile()
    {
        var bytes = Image()[..0x88]; NotObserved(bytes);
        using var stream = new TrackingStream(Image(), maximumChunk: 1);
        Assert.True(SingleFileStaticHostMetadata.Read(stream).StaticHostMetadataObserved);
    }

    [Fact]
    public void NonSeekableStreamIsNotReadOrDisposed()
    {
        using var stream = new TrackingStream(Image(), seekable: false);
        Assert.Equal(SingleFileStaticHostMetadataState.Unsupported, SingleFileStaticHostMetadata.Read(stream).State);
        Assert.Equal(0, stream.BytesRead); Assert.False(stream.WasDisposed);
    }

    [Fact]
    public void RestoreFailureOverridesAnOtherwiseQualifiedResult()
    {
        using var stream = new TrackingStream(Image()) { FailRestoreAt = 7 }; stream.Position = 7;
        var result = SingleFileStaticHostMetadata.Read(stream);
        Assert.False(result.StaticHostMetadataObserved); Assert.Equal(SingleFileStaticHostMetadataReason.PositionRestoreFailed, result.Reason);
        Assert.False(stream.WasDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationPropagatesAfterRestoreAttemptEvenIfRestorationFails(bool restoreFailure)
    {
        using var stop = new CancellationTokenSource();
        using var stream = new TrackingStream(Image()) { AfterRead = stop.Cancel, FailRestoreAt = restoreFailure ? 7 : null };
        stream.Position = 7;
        Assert.Throws<OperationCanceledException>(() => SingleFileStaticHostMetadata.Read(stream, stop.Token));
        Assert.True(stream.RestoreAttempted); Assert.False(stream.WasDisposed);
        if (!restoreFailure) Assert.Equal(7, stream.Position);
    }

    private sealed class TrackingStream(byte[] bytes, int maximumChunk = int.MaxValue, bool seekable = true) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);
        internal int BytesRead { get; private set; }
        internal int MaximumRequested { get; private set; }
        internal bool WasDisposed { get; private set; }
        internal bool RestoreAttempted { get; private set; }
        internal long? FailRestoreAt { get; init; }
        internal Action? AfterRead { get; init; }
        internal List<(long Offset, int Count)> Reads { get; } = [];
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            MaximumRequested = Math.Max(MaximumRequested, count);
            long position = inner.Position;
            int read = inner.Read(buffer, offset, Math.Min(maximumChunk, count));
            BytesRead += read; Reads.Add((position, read)); AfterRead?.Invoke(); return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!seekable) throw new NotSupportedException();
            if (origin == SeekOrigin.Begin && offset == 7) RestoreAttempted = true;
            if (FailRestoreAt == offset && origin == SeekOrigin.Begin) throw new IOException("Inert restoration fixture.");
            return inner.Seek(offset, origin);
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
