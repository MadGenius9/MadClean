namespace MadClean.Windows;

// A completed synchronous read and an uncertain call have different buffer lifetimes.
// The caller owns and pins the buffer; this helper never closes a pipe or cancels IO.
internal readonly record struct SystemRepairDirectPipeRead(bool Succeeded, uint BytesRead, int? NativeError);
internal readonly record struct SystemRepairDirectPipeReadOutcome(bool EndOfStreamObserved,
    bool AllCallsReturned, int? NativeError);

internal static class SystemRepairDirectPipeReader
{
    internal const int BufferSize = 4096;
    internal const int BrokenPipe = 109;

    internal static SystemRepairDirectPipeReadOutcome Discard(byte[] buffer,
        Func<SystemRepairDirectPipeRead> read)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(read);
        if (buffer.Length is < 1 or > BufferSize) throw new ArgumentOutOfRangeException(nameof(buffer));
        while (true)
        {
            SystemRepairDirectPipeRead received;
            try { received = read(); }
            catch
            {
                // No completed native-call proof: do not mutate a potentially live IO buffer.
                return new(false, false, null);
            }
            // Unexpected failures may describe IO whose completion is not established.
            // Only success or the documented anonymous-pipe EOF permits buffer mutation.
            if (!received.Succeeded && received.NativeError != BrokenPipe)
                return new(false, false, received.NativeError);
            Array.Clear(buffer);
            if (received.BytesRead > buffer.Length) return new(false, true, received.NativeError);
            if (!received.Succeeded)
                return new(received.NativeError == BrokenPipe, true, received.NativeError);
            // A successful zero-byte read can represent a zero-byte pipe write, not EOF.
        }
    }
}
