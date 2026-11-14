using System.Buffers.Binary;
using System.Text;
using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

public sealed class ModernJournalDirectoryBatchTests
{
    private static byte[] Batch(params string[] names)
    {
        var sizes = names.Select(n => (104 + Encoding.Unicode.GetByteCount(n) + 7) & ~7).ToArray();
        var bytes = new byte[sizes.Sum()];
        int offset = 0;
        for (int i = 0; i < names.Length; i++)
        {
            var target = bytes.AsSpan(offset);
            BinaryPrimitives.WriteUInt32LittleEndian(target, i == names.Length - 1 ? 0u : (uint)sizes[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(target[56..], i == 0 ? 0x10u : 0x80u);
            BinaryPrimitives.WriteUInt32LittleEndian(target[60..], (uint)Encoding.Unicode.GetByteCount(names[i]));
            Encoding.Unicode.GetBytes(names[i], target[104..]);
            offset += sizes[i];
        }
        return bytes;
    }

    [Fact]
    public void CountedNamesKeepCaseUnicodeAndSpacesWithoutRequiringATerminator()
    {
        byte[] bytes = Batch(" Lease.bin ", "é\U0001F680.jsonl", ".", "..");
        Assert.True(ModernJournalDirectoryBatch.TryRead(bytes, out var entries));
        Assert.Equal(new[] { " Lease.bin ", "é\U0001F680.jsonl", ".", ".." }, entries.Select(e => e.Name));
        Assert.Equal(0x10u, entries[0].Attributes);
        Assert.Equal(0x80u, entries[1].Attributes);
        // The exact unpadded final name has no NUL and need not occupy sizeof(struct).
        Assert.True(ModernJournalDirectoryBatch.TryRead(Batch("a")[..106], out var single));
        Assert.Equal("a", Assert.Single(single).Name);
    }

    [Fact]
    public void BatchEndDoesNotMeanEnumerationEndAndStaleErrorsDoNotOverrideSuccess()
    {
        Assert.True(ModernJournalDirectoryBatch.TryRead(Batch("lease.bin"), out _));
        foreach (int staleError in new[] { 0, 18, 234, 997, -1 })
            Assert.Equal(ModernJournalDirectoryQuery.Batch, ModernJournalDirectoryBatch.ClassifyQueryResult(true, staleError));
        Assert.Equal(ModernJournalDirectoryQuery.Batch, ModernJournalDirectoryBatch.ClassifyQueryResult(true, 0));
        Assert.Equal(ModernJournalDirectoryQuery.End, ModernJournalDirectoryBatch.ClassifyQueryResult(false, 18));
    }

    [Theory]
    [InlineData(234, 2)]
    [InlineData(0, 3)]
    [InlineData(997, 3)]
    [InlineData(5, 3)]
    [InlineData(-1, 3)]
    public void FailedCallsCannotConferBatchAuthorityEvenIfBufferLooksValid(int error, int expected)
        => Assert.Equal((ModernJournalDirectoryQuery)expected, ModernJournalDirectoryBatch.ClassifyQueryResult(false, error));

    [Fact]
    public void OpaqueIdsIndexesShortNamesAndUnusedTailDoNotSupplyIdentityOrExtraRecords()
    {
        byte[] bytes = new byte[ModernJournalDirectoryBatch.BufferBytes];
        Batch("lease.bin").CopyTo(bytes, 0);
        bytes.AsSpan(4, 52).Fill(0xff);
        bytes.AsSpan(64, 40).Fill(0xff);
        bytes.AsSpan(128).Fill(0xff);
        Assert.True(ModernJournalDirectoryBatch.TryRead(bytes, out var entries));
        Assert.Equal("lease.bin", Assert.Single(entries).Name);
        bytes.AsSpan().Fill(0);
        Assert.Equal("lease.bin", Assert.Single(entries).Name); // Copied, immutable result.
        Assert.Throws<NotSupportedException>(() => ((IList<ModernJournalDirectoryName>)entries).Clear());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(103)]
    [InlineData(104)]
    [InlineData(105)]
    [InlineData(4097)]
    public void OutOfBoundsBuffersNeverProduceEntries(int length)
    {
        Assert.False(ModernJournalDirectoryBatch.TryRead(new byte[length], out var entries));
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(1000u)]
    [InlineData(uint.MaxValue)]
    public void InvalidCountedByteLengthsRejectWholeBatch(uint length)
    {
        byte[] bytes = Batch("first", "second");
        int second = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(second + 60), length);
        Assert.False(ModernJournalDirectoryBatch.TryRead(bytes, out var entries));
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(8u)]
    [InlineData(104u)]
    [InlineData(113u)]
    [InlineData(120u)]
    [InlineData(240u)]
    [InlineData(0xfffffff8u)]
    [InlineData(uint.MaxValue)]
    public void BadOffsetsCannotOverlapSkipOutsideOrReturnToEarlierRecords(uint next)
    {
        byte[] bytes = Batch("lease.bin", "second");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, next);
        Assert.False(ModernJournalDirectoryBatch.TryRead(bytes, out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void CountedNameCannotCrossTheNextRecordBoundary()
    {
        byte[] bytes = Batch("a", "second");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 20);
        Assert.False(ModernJournalDirectoryBatch.TryRead(bytes, out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void EveryTruncatedLaterNameRejectsTheEarlierValidPrefix()
    {
        byte[] bytes = Batch("first", "second");
        int second = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        int required = second + 104 + Encoding.Unicode.GetByteCount("second");
        for (int length = second; length < required; length++)
        {
            Assert.False(ModernJournalDirectoryBatch.TryRead(bytes.AsSpan(0, length), out var entries));
            Assert.Empty(entries);
        }
        Assert.True(ModernJournalDirectoryBatch.TryRead(bytes.AsSpan(0, required), out _));
    }

    [Fact]
    public void ExactBufferLimitAllowsACountedNameButNeverAnOverflowingLength()
    {
        byte[] bytes = Batch(new string('x', (ModernJournalDirectoryBatch.BufferBytes - 104) / 2));
        Assert.Equal(ModernJournalDirectoryBatch.BufferBytes, bytes.Length);
        Assert.True(ModernJournalDirectoryBatch.TryRead(bytes, out var entries));
        Assert.Equal(1996, Assert.Single(entries).Name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 3994);
        Assert.False(ModernJournalDirectoryBatch.TryRead(bytes, out entries));
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    public void UnsafeNameInLaterRecordCannotLeakAnAcceptedPrefix(string name)
    {
        Assert.False(ModernJournalDirectoryBatch.TryRead(Batch("first", name), out var entries));
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void InvalidUtf16IsRejectedRatherThanReplaced(int codeUnit)
    {
        byte[] bytes = Batch("a");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(104), (ushort)codeUnit);
        Assert.False(ModernJournalDirectoryBatch.TryRead(bytes, out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void RecordBoundAcceptsExactlyTheLimitButNeverAnExtraRecord()
    {
        Assert.True(ModernJournalDirectoryBatch.TryRead(Batch(Enumerable.Repeat("a", ModernJournalDirectoryBatch.MaxRecords).ToArray()), out var entries));
        Assert.Equal(ModernJournalDirectoryBatch.MaxRecords, entries.Count);
        Assert.False(ModernJournalDirectoryBatch.TryRead(Batch(Enumerable.Repeat("a", ModernJournalDirectoryBatch.MaxRecords + 1).ToArray()), out entries));
        Assert.Empty(entries);
    }
}
