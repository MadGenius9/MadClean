using MadClean.Core;

namespace MadClean.Windows;

internal enum ModernJournalAcquisition { Acquired, NotAcquired, Uncertain }
internal enum ModernJournalSampleState { Refused, Released, Retained }
internal enum ModernJournalSamplePhase { None, Prepare, Directory, Lease, Journal, Exercise, Release }

// Specific fresh/existing directory/lease/journal sample ownership, not an action capability.
// Constructors own no native resources; Prepare begins the owned resource prefix.
internal abstract class ModernOwnedJournalSampleFrame
{
    private int claimed;
    internal bool TryClaim() => Interlocked.CompareExchange(ref claimed, 1, 0) == 0;
    internal abstract bool Prepare(CancellationToken token);
    internal abstract ModernJournalAcquisition AcquireDirectory();
    internal abstract ModernJournalAcquisition AcquireLease();
    internal abstract ModernJournalAcquisition AcquireJournal();
    internal abstract bool ObserveSyntheticIntent();
    internal abstract bool ReleaseKnownOwnership();
}

internal sealed class ModernOwnedJournalSampleDomain
{
    private readonly object sync = new();
    private ModernOwnedJournalSampleAttempt? owner;
    private bool retained;
    internal bool HasPendingWork { get { lock (sync) return owner?.HasPendingWork == true; } }
    internal bool RequiresRetention { get { lock (sync) return retained || owner?.RequiresRetention == true; } }
    internal bool Owns(ModernOwnedJournalSampleFrame frame)
    { lock (sync) return owner != null && ReferenceEquals(owner.Frame, frame); }
    internal bool Owns(ModernOwnedJournalSampleAttempt attempt)
    { lock (sync) return ReferenceEquals(owner, attempt); }
    internal bool IsActive(ModernOwnedJournalSampleFrame frame, ModernJournalSamplePhase phase)
    { lock (sync) return owner != null && ReferenceEquals(owner.Frame, frame) && owner.IsActive(phase); }

    internal ModernOwnedJournalSampleAttempt Run(ModernOwnedJournalSampleFrame frame, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var attempt = new ModernOwnedJournalSampleAttempt(this, frame);
        lock (sync)
        {
            if (owner != null || retained) return attempt;
            owner = attempt; // Actual frame is retained before claim or any callback.
        }
        if (!frame.TryClaim())
        {
            lock (sync) { if (ReferenceEquals(owner, attempt)) owner = null; }
            return attempt;
        }
        attempt.Run(token);
        return attempt;
    }
    internal void Complete(ModernOwnedJournalSampleAttempt attempt)
    {
        lock (sync)
        {
            if (!ReferenceEquals(owner, attempt)) return;
            var settlement = attempt.Settlement;
            if (!settlement.Settled) return;
            if (settlement.Released) owner = null;
            else retained = true;
        }
    }
}

internal sealed class ModernOwnedJournalSampleAttempt
{
    private readonly object sync = new();
    private readonly ModernOwnedJournalSampleDomain domain;
    private bool started, active, releaseActive, releaseAttempted, released, retained, sampleVerified;
    private bool settled;
    private int callbackThread;
    private ModernJournalSamplePhase phase;
    private ModernJournalSampleState state = ModernJournalSampleState.Refused;
    internal ModernOwnedJournalSampleFrame Frame { get; }
    internal ModernJournalSampleState State { get { lock (sync) return state; } }
    internal bool HasPendingWork { get { lock (sync) return active || releaseActive; } }
    internal bool RequiresRetention { get { lock (sync) return retained; } }
    internal bool SampleVerified { get { lock (sync) return sampleVerified; } }
    internal bool ResourcesReleased { get { lock (sync) return released; } }
    internal (bool Settled, bool Released) Settlement
    { get { lock (sync) return (settled && !active && !releaseActive, released); } }
    internal bool IsActive(ModernJournalSamplePhase expected)
    { lock (sync) return !retained && !settled && (active || releaseActive) && phase == expected && callbackThread == Environment.CurrentManagedThreadId; }
    internal ModernOwnedJournalSampleAttempt(ModernOwnedJournalSampleDomain domain, ModernOwnedJournalSampleFrame frame)
    { this.domain = domain; Frame = frame; }

    internal void Run(CancellationToken token)
    {
        if (!domain.Owns(this)) return;
        lock (sync)
        {
            if (started) return;
            started = active = true;
        }
        bool known = true;
        bool preparationEntered = false;
        try
        {
            if (token.IsCancellationRequested) return;
            preparationEntered = true;
            SetPhase(ModernJournalSamplePhase.Prepare);
            if (!Frame.Prepare(token)) return;
            var stages = new (ModernJournalSamplePhase Phase, Func<ModernJournalAcquisition> Acquire)[]
            {
                (ModernJournalSamplePhase.Directory, Frame.AcquireDirectory),
                (ModernJournalSamplePhase.Lease, Frame.AcquireLease),
                (ModernJournalSamplePhase.Journal, Frame.AcquireJournal)
            };
            foreach (var stage in stages)
            {
                if (token.IsCancellationRequested) return;
                // A throwing or undefined response cannot authorize cleanup or retry.
                known = false;
                SetPhase(stage.Phase);
                var result = stage.Acquire();
                if (result == ModernJournalAcquisition.Uncertain || !Enum.IsDefined(result)) return;
                known = true;
                if (result == ModernJournalAcquisition.NotAcquired) return;
            }
            if (token.IsCancellationRequested) return;
            SetPhase(ModernJournalSamplePhase.Exercise);
            bool verified = Frame.ObserveSyntheticIntent();
            lock (sync) sampleVerified = verified;
        }
        catch { known = false; }
        finally
        {
            lock (sync)
            {
                active = false;
                phase = ModernJournalSamplePhase.None; callbackThread = 0;
                if (!known) { retained = settled = true; state = ModernJournalSampleState.Retained; }
            }
            if (!preparationEntered)
            {
                lock (sync) { releaseAttempted = released = settled = true; }
                domain.Complete(this);
            }
            else if (known) Release();
            else domain.Complete(this);
        }
    }
    internal bool Release()
    {
        lock (sync)
        {
            if (!started || active || releaseActive || retained) return false;
            if (releaseAttempted) return released;
            releaseAttempted = releaseActive = true;
            phase = ModernJournalSamplePhase.Release; callbackThread = Environment.CurrentManagedThreadId;
        }
        bool confirmed = false;
        try { confirmed = Frame.ReleaseKnownOwnership(); }
        catch { }
        finally
        {
            lock (sync)
            {
                releaseActive = false; released = confirmed; retained = !confirmed;
                settled = true; phase = ModernJournalSamplePhase.None; callbackThread = 0;
                state = confirmed ? ModernJournalSampleState.Released : ModernJournalSampleState.Retained;
            }
            domain.Complete(this);
        }
        return confirmed;
    }
    private void SetPhase(ModernJournalSamplePhase value)
    { lock (sync) { phase = value; callbackThread = Environment.CurrentManagedThreadId; } }
}

// A fixed synthetic seed observation only. Valid bytes neither authenticate a journal
// nor establish complete history, a running client, successful health work or admission.
internal static class ModernJournalSyntheticSeed
{
    internal static bool IsExpected(SystemToolJournalReview? review, Guid executionId, DateTimeOffset nowUtc)
    {
        try
        {
            if (executionId == Guid.Empty || nowUtc == default || nowUtc.Offset != TimeSpan.Zero ||
                review is not { State: SystemToolJournalState.Unresolved, Entries.Count: 1 }) return false;
            var observed = review.Entries[0];
            if (observed == null) return false;
            var entry = observed with { }; // Revalidate this copied singleton, not the caller's state/message.
            if (review.Entries.Count != 1 || entry.ExecutionId != executionId ||
                entry.Operation != SystemToolOperation.DismCheckHealth || entry.Sequence != 1 ||
                entry.TimestampUtc == default || entry.TimestampUtc.Offset != TimeSpan.Zero || entry.TimestampUtc > nowUtc ||
                entry.Phase != SystemToolJournalPhase.DispatchIntent || entry.ProcessId.HasValue ||
                entry.ClientStartUtc.HasValue || entry.ExitCode.HasValue || entry.NativeError.HasValue) return false;
            var canonical = SystemToolJournal.Review(Array.AsReadOnly(new[] { entry }), nowUtc);
            return canonical.State == SystemToolJournalState.Unresolved && canonical.Entries.Count == 1 &&
                canonical.Entries[0] == entry;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        { return false; }
    }
}
