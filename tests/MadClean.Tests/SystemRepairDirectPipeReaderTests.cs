using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemRepairDirectPipeReaderTests
{
    [Fact]
    public void EmptySuccessfulReadDoesNotHideLaterBytesOrInventEof()
    {
        var buffer = new byte[16];
        int calls = 0;
        var result = SystemRepairDirectPipeReader.Discard(buffer, () =>
        {
            Assert.All(buffer, value => Assert.Equal(0, value));
            calls++;
            if (calls == 1) return new(true, 0, null);
            buffer[0] = 123;
            return calls == 2 ? new(true, 1, null) : new(false, 0, 109);
        });
        Assert.Equal(3, calls);
        Assert.True(result.EndOfStreamObserved);
        Assert.True(result.AllCallsReturned);
        Assert.All(buffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public void UnexpectedErrorDoesNotEstablishCompletedIoOrPermitBufferMutation()
    {
        var buffer = new byte[] { 42 };
        var result = SystemRepairDirectPipeReader.Discard(buffer, () => new(false, 0, 5));
        Assert.False(result.EndOfStreamObserved);
        Assert.False(result.AllCallsReturned);
        Assert.Equal(5, result.NativeError);
        Assert.Equal(42, buffer[0]);
    }

    [Fact]
    public void UncertainReadPreservesPotentiallyLiveBuffer()
    {
        var buffer = new byte[] { 42 };
        var result = SystemRepairDirectPipeReader.Discard(buffer, () => throw new InvalidOperationException());
        Assert.False(result.EndOfStreamObserved);
        Assert.False(result.AllCallsReturned);
        Assert.Null(result.NativeError);
        Assert.Equal(42, buffer[0]);
    }

    [Fact]
    public void ImpossibleReturnedLengthIsNotAcceptedAsEof()
    {
        var buffer = new byte[1];
        var result = SystemRepairDirectPipeReader.Discard(buffer, () => new(false, 2, 109));
        Assert.False(result.EndOfStreamObserved);
        Assert.True(result.AllCallsReturned);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void InvalidBufferRefusesBeforeRead(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemRepairDirectPipeReader.Discard(new byte[size],
            () => throw new Exception("must not read")));
    }
}
