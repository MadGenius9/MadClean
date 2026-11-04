namespace MadClean.Windows;

/// <summary>Consumes an owned client's borrowed raw stream without retaining or interpreting its output.</summary>
internal static class SystemToolOutputDrain
{
    internal static async Task<bool> DiscardAsync(Stream ownedBorrowedStream)
    {
        ArgumentNullException.ThrowIfNull(ownedBorrowedStream);
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = await ownedBorrowedStream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) return true;
                if (read < 0 || read > buffer.Length) return false;
                // No decoding, aggregation, logging or output-count export.
                Array.Clear(buffer);
            }
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        { return false; }
        finally
        {
            Array.Clear(buffer);
            // The session owns the stream/process. Never dispose a borrowed stream
            // here or cancel an in-flight read underneath its actual owner.
        }
    }
}
