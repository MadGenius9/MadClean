namespace MadClean.App;

// UI publication fence only. It owns no provider work and asserts no cancellation.
// Call on the existing presentation thread; no cross-thread synchronization is provided.
internal sealed class DiagnosticPublicationScope
{
    private readonly Func<bool> current;
    private bool usable = true;

    internal DiagnosticPublicationScope(Func<bool> current)
        => this.current = current ?? throw new ArgumentNullException(nameof(current));

    internal bool IsCurrent
    {
        get
        {
            if (!usable) return false;
            try { return usable = current(); }
            catch { usable = false; throw; }
        }
    }

    internal bool Publish(Action mutation)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(mutation);
            if (!IsCurrent) return false;
            mutation();
            // Collection/property notifications may synchronously change the selected range.
            return IsCurrent;
        }
        catch { usable = false; throw; }
    }
}
