using MadClean.Windows;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemToolOutputDrainTests
{
    // Controlled portable byte streams only; no process, console or Windows tool runs.
    [Fact]
    public async Task BinaryOutputIsDiscardedThroughEofWithOneBoundedZeroedBuffer()
    {
        byte[] binary = Enumerable.Range(0, 8209).Select(index => (byte)(index % 3 == 0 ? 255 : index % 3 == 1 ? 254 : 0)).ToArray();
        using var stream = new ObservedMemoryStream(binary);

        Assert.True(await SystemToolOutputDrain.DiscardAsync(stream));

        Assert.Equal(binary.Length, stream.BytesRead);
        Assert.Equal(4096, stream.MaximumReadRequest);
        Assert.True(stream.EndOfStreamObserved);
        Assert.False(stream.WasDisposed);
        Assert.All(stream.LastBuffer.ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task ReadFailureIsNotEofAndDoesNotDisposeOrRetainBufferContents()
    {
        using var stream = new FailingReadStream();

        Assert.False(await SystemToolOutputDrain.DiscardAsync(stream));

        Assert.False(stream.WasDisposed);
        Assert.All(stream.LastBuffer.ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task DisposedInputIsUnobservedEofRatherThanSuccessfulDrain()
    {
        var stream = new MemoryStream([255, 254]);
        stream.Dispose();
        Assert.False(await SystemToolOutputDrain.DiscardAsync(stream));
    }

    [Fact]
    public async Task CancelledCallerWaitCannotCancelReadOrCompleteTheActualDrain()
    {
        using var stream = new PendingReadStream();
        var drain = SystemToolOutputDrain.DiscardAsync(stream);
        await stream.ReadRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain.WaitAsync(new CancellationToken(true)));
        Assert.False(drain.IsCompleted);
        Assert.False(stream.WasDisposed);
        Assert.False(stream.ObservedReadToken.CanBeCanceled);

        stream.CompleteRead();
        Assert.True(await drain);
        Assert.False(stream.WasDisposed);
        Assert.All(stream.LastBuffer.ToArray(), value => Assert.Equal((byte)0, value));
    }

    private sealed class ObservedMemoryStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int BytesRead { get; private set; }
        internal int MaximumReadRequest { get; private set; }
        internal bool EndOfStreamObserved { get; private set; }
        internal bool WasDisposed { get; private set; }
        internal Memory<byte> LastBuffer { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LastBuffer = buffer;
            MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
            int read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            if (read == 0) EndOfStreamObserved = true;
            return read;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class FailingReadStream : MemoryStream
    {
        internal bool WasDisposed { get; private set; }
        internal Memory<byte> LastBuffer { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LastBuffer = buffer;
            buffer.Span.Fill(255);
            return ValueTask.FromException<int>(new IOException("Synthetic raw pipe read failure"));
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class PendingReadStream : MemoryStream
    {
        private readonly TaskCompletionSource<int> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool first = true;
        internal TaskCompletionSource ReadRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        internal CancellationToken ObservedReadToken { get; private set; }
        internal Memory<byte> LastBuffer { get; private set; }
        internal void CompleteRead() => read.SetResult(2);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LastBuffer = buffer;
            if (!first) return ValueTask.FromResult(0);
            first = false;
            ObservedReadToken = cancellationToken;
            buffer.Span[0] = 255; buffer.Span[1] = 254;
            ReadRequested.SetResult();
            return new(read.Task);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
