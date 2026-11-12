namespace MadClean.Windows;

/// <summary>
/// Records one actual managed Start call, not native process identity or permission.
/// The caller must root its Process, leases and proof before invoking this boundary.
/// A thrown call supplies no documented no-child result and cannot be reset or retried.
/// </summary>
internal sealed class MsiClientStartBoundary
{
    private int attempted;
    private int returnedStarted = -1;

    internal bool Attempted => Volatile.Read(ref attempted) != 0;
    internal bool? ReturnedStarted => Volatile.Read(ref returnedStarted) switch
    { 0 => false, 1 => true, _ => null };
    internal bool RequiresRetention => Attempted && ReturnedStarted != false;

    internal bool Invoke(Func<bool> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (Interlocked.CompareExchange(ref attempted, 1, 0) != 0)
            throw new InvalidOperationException("The exact client start boundary has already been entered.");
        // No catch supplies a false value: only an actual normal return records one.
        bool observed = start();
        Volatile.Write(ref returnedStarted, observed ? 1 : 0);
        return observed;
    }
}
