using System.Buffers.Binary;
using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class CleanupStreamScopeTests
{
    private static byte[] Buffer(params (string Name, long Bytes, long Allocation)[] entries)
    {
        var buffer = new byte[CleanupStreamScope.MaximumBufferBytes]; int offset = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index]; byte[] name = Encoding.Unicode.GetBytes(entry.Name);
            int stride = (24 + name.Length + 7) & ~7;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), index == entries.Length - 1 ? 0u : (uint)stride);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), (uint)name.Length);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 8), entry.Bytes);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 16), entry.Allocation);
            name.CopyTo(buffer, offset + 24); offset += stride;
        }
        return buffer;
    }
    private static CleanupStreamSnapshot Decode(byte[] buffer, long expected = 17) => CleanupStreamScope.Decode(buffer, expected);

    [Fact]
    public void CountedUnterminatedDefaultNameAndZeroLengthFilesAreRealMetadata()
    {
        var nonempty = Decode(Buffer(("::$DATA", 17, 4096)));
        var empty = Decode(Buffer(("::$DATA", 0, 0)), 0);
        Assert.Equal(CleanupStreamScopeState.UnnamedDataOnly, nonempty.State);
        Assert.Equal(1, nonempty.StreamCount); Assert.Equal(17, nonempty.UnnamedStreamBytes);
        Assert.Equal(CleanupStreamScopeState.UnnamedDataOnly, empty.State); Assert.Equal(0, empty.UnnamedStreamBytes);
        var agreed = CleanupStreamScope.Compare(nonempty, nonempty);
        Assert.True(CleanupStreamScope.IsConsistent(agreed, 17)); Assert.True(agreed.BookendsMatch);
    }

    [Fact]
    public void LiteralDataNamedStreamIsNotTheUnnamedDefaultAndNoNamesArePubliclyExposed()
    {
        var data = Decode(Buffer(("::$DATA", 17, 4096), (":$DATA:$DATA", 0, 0)));
        Assert.Equal(CleanupStreamScopeState.AlternateStreamsObserved, data.State);
        Assert.Equal(2, data.StreamCount);
        Assert.DoesNotContain(typeof(CleanupStreamSnapshot).GetProperties(), property => property.Name.Contains("Name", StringComparison.Ordinal));
        var observation = CleanupStreamScope.Compare(data, data);
        Assert.Equal(CleanupStreamScopeState.AlternateStreamsObserved, observation.State);
        Assert.True(CleanupStreamScope.IsConsistent(observation, 17));
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(Buffer((":$DATA:$DATA", 17, 4096))).State);
    }

    [Fact]
    public void OrderMayChangeButStreamSizesNamesAllocationOrMembershipCannotMasqueradeAsStable()
    {
        var first = Decode(Buffer(("::$DATA", 17, 4096), (":owned:$DATA", 2, 4096)));
        var reordered = Decode(Buffer((":owned:$DATA", 2, 4096), ("::$DATA", 17, 4096)));
        Assert.True(CleanupStreamScope.Compare(first, reordered).BookendsMatch);
        foreach (var other in new[] { Decode(Buffer(("::$DATA", 17, 4096))), Decode(Buffer(("::$DATA", 17, 4096), (":owned:$DATA", 3, 4096))),
            Decode(Buffer(("::$DATA", 17, 4096), (":other:$DATA", 2, 4096))), Decode(Buffer(("::$DATA", 17, 4096), (":owned:$DATA", 2, 8192))) })
        {
            var result = CleanupStreamScope.Compare(first, other);
            Assert.Equal(CleanupStreamScopeState.Unknown, result.State); Assert.Equal(CleanupStreamScopeReason.ChangedDuringRead, result.Reason);
            Assert.False(result.BookendsMatch); Assert.Null(result.StreamCount);
            Assert.True(CleanupStreamScope.IsConsistent(result, 17));
        }
    }

    [Fact]
    public void ApiFailuresAndSuccessfulNoStreamsNeverBecomeDefaultOnlyOrInventedNativeErrors()
    {
        var valid = Decode(Buffer(("::$DATA", 17, 4096)));
        foreach (int native in new[] { 0, 38, 50, 234 })
        {
            var failure = CleanupStreamScope.Compare(null, valid, native);
            Assert.Equal(CleanupStreamScopeReason.NativeQueryFailed, failure.Reason); Assert.Equal(native, failure.FirstNativeError);
            Assert.Null(failure.SecondNativeError); Assert.Null(failure.BookendsMatch); Assert.True(CleanupStreamScope.IsConsistent(failure, 17));
        }
        var noStreams = CleanupStreamScope.Compare(CleanupStreamScope.NoStreamsReturned(), CleanupStreamScope.NoStreamsReturned());
        Assert.Equal(CleanupStreamScopeState.Unknown, noStreams.State); Assert.Equal(CleanupStreamScopeReason.NoStreamsReturned, noStreams.Reason);
        Assert.Null(noStreams.FirstNativeError); Assert.Null(noStreams.SecondNativeError);
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(new byte[CleanupStreamScope.MaximumBufferBytes]).State);
    }

    [Fact]
    public void CyclicOverlappingUnalignedOrOutOfBufferChainsAreRejected()
    {
        foreach (uint next in new uint[] { 8, 24, 39, 65536, uint.MaxValue })
        {
            var buffer = Buffer(("::$DATA", 17, 4096)); BinaryPrimitives.WriteUInt32LittleEndian(buffer, next);
            Assert.Equal(CleanupStreamScopeState.Unknown, Decode(buffer).State);
        }
        var nameOverlap = Buffer(("::$DATA", 17, 4096), (":other:$DATA", 2, 4096));
        BinaryPrimitives.WriteUInt32LittleEndian(nameOverlap.AsSpan(4), 64);
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(nameOverlap).State);
    }

    [Fact]
    public void CountedNamesRequireStrictUtf16NotNullScanningAndDuplicateDefaultIsInvalid()
    {
        foreach (string invalid in new[] { "::$DATA\0", ":bad\u200B:$DATA", ":bad\0:$DATA" })
            Assert.Equal(CleanupStreamScopeState.Unknown, Decode(Buffer((invalid, 17, 4096))).State);
        var invalidUnicode = Buffer(("::$DATA", 17, 4096)); invalidUnicode[24] = 0; invalidUnicode[25] = 0xD8;
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(invalidUnicode).State);
        var oddLength = Buffer(("::$DATA", 17, 4096)); BinaryPrimitives.WriteUInt32LittleEndian(oddLength.AsSpan(4), 13);
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(oddLength).State);
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(Buffer(("::$DATA", 17, 4096), ("::$DATA", 17, 4096))).State);
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(Buffer(("::$DATA", 17, 4096), (":one:$DATA", 2, 2), (":ONE:$DATA", 2, 2))).State);
    }

    [Fact]
    public void PaddingAndTailAreConservativelyZeroOnlyWithoutClaimingNativeWrittenLength()
    {
        var tail = Buffer(("::$DATA", 17, 4096)); tail[^1] = 1;
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(tail).State);
        var gap = Buffer(("::$DATA", 17, 4096), (":one:$DATA", 2, 2)); gap[38] = 1;
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(gap).State);
        Assert.Equal(CleanupStreamScopeState.UnnamedDataOnly, Decode(Buffer(("::$DATA", 17, 4096)).AsSpan(0, 38).ToArray()).State);
    }

    [Fact]
    public void BoundsNegativeSizesAndDisagreementWithHeldFileLengthBlockAcceptance()
    {
        Assert.Equal(CleanupStreamScopeReason.DefaultSizeMismatch, Decode(Buffer(("::$DATA", 18, 4096))).Reason);
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(Buffer(("::$DATA", -1, 4096))).State);
        Assert.Equal(CleanupStreamScopeState.Unknown, Decode(Buffer(("::$DATA", 17, -1))).State);
        Assert.Equal(CleanupStreamScopeReason.LimitExceeded, Decode(new byte[65537]).Reason);
        var tooMany = Enumerable.Range(0, 33).Select(i => (i == 0 ? "::$DATA" : $":entry{i}:$DATA", i == 0 ? 17L : 2L, 4096L)).ToArray();
        Assert.Equal(CleanupStreamScopeReason.LimitExceeded, Decode(Buffer(tooMany)).Reason);
        var hugeName = Buffer(("::$DATA", 17, 4096)); BinaryPrimitives.WriteUInt32LittleEndian(hugeName.AsSpan(4), 8194);
        Assert.Equal(CleanupStreamScopeReason.LimitExceeded, Decode(hugeName).Reason);
    }

    [Fact]
    public void ProjectionValidatorRejectsImpossiblePositiveResultsAndNeverPromotesUnknown()
    {
        var positive = CleanupStreamScope.Compare(Decode(Buffer(("::$DATA", 17, 4096))), Decode(Buffer(("::$DATA", 17, 4096))));
        foreach (var invalid in new[] { positive with { StreamCount = 2 }, positive with { UnnamedStreamBytes = 18 }, positive with { QueryCount = 1 },
            positive with { BookendsMatch = false }, positive with { FirstNativeError = 0 }, positive with { Reason = CleanupStreamScopeReason.NotChecked },
            positive with { State = CleanupStreamScopeState.AlternateStreamsObserved }, positive with { State = (CleanupStreamScopeState)99 } })
            Assert.False(CleanupStreamScope.IsConsistent(invalid, 17));
        var unknown = new CleanupStreamScopeObservation(CleanupStreamScopeState.Unknown, CleanupStreamScopeReason.NotChecked, null, null, 0, null, null, null);
        Assert.True(CleanupStreamScope.IsConsistent(null, 17)); Assert.True(CleanupStreamScope.IsConsistent(unknown, 17));
        Assert.False(CleanupStreamScope.IsConsistent(unknown with { StreamCount = 1 }, 17));
        Assert.False(CleanupStreamScope.IsConsistent(unknown with { UnnamedStreamBytes = 17 }, 17));
    }

    [Fact]
    public void CancellationStopsDecodingAndBookendProjection()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => CleanupStreamScope.Decode(Buffer(("::$DATA", 17, 4096)), 17, stop.Token));
        Assert.Throws<OperationCanceledException>(() => CleanupStreamScope.Compare(null, null, token: stop.Token));
    }
}
