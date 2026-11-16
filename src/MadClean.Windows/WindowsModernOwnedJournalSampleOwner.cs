using MadClean.Core;

namespace MadClean.Windows;

internal enum ModernJournalAcquisition { Acquired, NotAcquired, Uncertain }
internal enum ModernJournalSampleState { Refused, Released, Retained }
internal enum ModernJournalSamplePhase { None, Prepare, Directory, Lease, Journal, Exercise, Release }
internal enum ModernJournalCallbackOutcome { NotEntered, Running, Confirmed, Refused, Uncertain }
internal enum ModernJournalNamesObservation { NotObserved, InProgress, Refused, Complete, Uncertain }

// Fixed observation facts only: no paths, native errors, journal content or action authority.
internal sealed record ModernJournalSampleObservation(
    ModernJournalCallbackOutcome Prepare, ModernJournalCallbackOutcome Directory,
    ModernJournalCallbackOutcome Lease, ModernJournalCallbackOutcome Journal,
    ModernJournalCallbackOutcome Exercise, ModernJournalCallbackOutcome Release,
    bool StopObserved, ModernJournalNamesObservation Names);

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
    internal ModernJournalSampleObservation? Observation { get { lock (sync) return owner?.Observation; } }
    internal bool RequiresRetention { get { lock (sync) return retained || owner?.RequiresRetention == true; } }
    internal bool Owns(ModernOwnedJournalSampleFrame frame)
    { lock (sync) return owner != null && ReferenceEquals(owner.Frame, frame); }
    internal bool Owns(ModernOwnedJournalSampleAttempt attempt)
    { lock (sync) return ReferenceEquals(owner, attempt); }
    internal bool IsActive(ModernOwnedJournalSampleFrame frame, ModernJournalSamplePhase phase)
    { lock (sync) return owner != null && ReferenceEquals(owner.Frame, frame) && owner.IsActive(phase); }

    internal bool TryRecordNames(ModernOwnedJournalSampleFrame frame, ModernJournalNamesObservation value)
    {
        lock (sync)
            return owner != null && ReferenceEquals(owner.Frame, frame) && owner.TryRecordNames(value);
    }

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
    private ModernJournalCallbackOutcome prepareOutcome, directoryOutcome, leaseOutcome, journalOutcome, exerciseOutcome, releaseOutcome;
    private ModernJournalNamesObservation namesObservation;
    private bool stopObserved;
    private ModernJournalSampleState state = ModernJournalSampleState.Refused;
    internal ModernOwnedJournalSampleFrame Frame { get; }
    internal ModernJournalSampleState State { get { lock (sync) return state; } }
    internal bool HasPendingWork { get { lock (sync) return active || releaseActive; } }
    internal bool RequiresRetention { get { lock (sync) return retained; } }
    internal bool SampleVerified { get { lock (sync) return sampleVerified; } }
    internal bool ResourcesReleased { get { lock (sync) return released; } }
    internal ModernJournalSampleObservation Observation
    {
        get
        {
            lock (sync) return new(prepareOutcome, directoryOutcome, leaseOutcome, journalOutcome,
                exerciseOutcome, releaseOutcome, stopObserved, namesObservation);
        }
    }
    internal bool TryRecordNames(ModernJournalNamesObservation value)
    {
        lock (sync)
        {
            if (retained || settled || !active || phase != ModernJournalSamplePhase.Exercise ||
                callbackThread != Environment.CurrentManagedThreadId) return false;
            if (namesObservation == ModernJournalNamesObservation.NotObserved && value == ModernJournalNamesObservation.InProgress ||
                namesObservation == ModernJournalNamesObservation.InProgress &&
                    value is ModernJournalNamesObservation.Refused or ModernJournalNamesObservation.Complete or ModernJournalNamesObservation.Uncertain)
            {
                namesObservation = value;
                return true;
            }
            return false;
        }
    }
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
            if (ObserveStop(token)) return;
            preparationEntered = true;
            SetPhase(ModernJournalSamplePhase.Prepare);
            bool prepared = Frame.Prepare(token);
            SetOutcome(ModernJournalSamplePhase.Prepare, prepared ? ModernJournalCallbackOutcome.Confirmed : ModernJournalCallbackOutcome.Refused);
            if (!prepared) return;
            var stages = new (ModernJournalSamplePhase Phase, Func<ModernJournalAcquisition> Acquire)[]
            {
                (ModernJournalSamplePhase.Directory, Frame.AcquireDirectory),
                (ModernJournalSamplePhase.Lease, Frame.AcquireLease),
                (ModernJournalSamplePhase.Journal, Frame.AcquireJournal)
            };
            foreach (var stage in stages)
            {
                if (ObserveStop(token)) return;
                // A throwing or undefined response cannot authorize cleanup or retry.
                known = false;
                SetPhase(stage.Phase);
                var result = stage.Acquire();
                if (result == ModernJournalAcquisition.Uncertain || !Enum.IsDefined(result))
                {
                    SetOutcome(stage.Phase, ModernJournalCallbackOutcome.Uncertain);
                    return;
                }
                SetOutcome(stage.Phase, result == ModernJournalAcquisition.Acquired
                    ? ModernJournalCallbackOutcome.Confirmed : ModernJournalCallbackOutcome.Refused);
                known = true;
                if (result == ModernJournalAcquisition.NotAcquired) return;
            }
            if (ObserveStop(token)) return;
            SetPhase(ModernJournalSamplePhase.Exercise);
            bool verified = Frame.ObserveSyntheticIntent();
            lock (sync)
            {
                sampleVerified = verified;
                exerciseOutcome = verified ? ModernJournalCallbackOutcome.Confirmed : ModernJournalCallbackOutcome.Refused;
            }
        }
        catch
        {
            known = false;
            lock (sync)
            {
                // A post-return failure must not rewrite a callback already observed to return.
                if (Outcome(phase) == ModernJournalCallbackOutcome.Running)
                    SetOutcomeUnderLock(phase, ModernJournalCallbackOutcome.Uncertain);
            }
        }
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
            releaseOutcome = ModernJournalCallbackOutcome.Running;
        }
        bool confirmed = false;
        try { confirmed = Frame.ReleaseKnownOwnership(); }
        catch { }
        finally
        {
            lock (sync)
            {
                releaseActive = false; released = confirmed; retained = !confirmed;
                releaseOutcome = confirmed ? ModernJournalCallbackOutcome.Confirmed : ModernJournalCallbackOutcome.Uncertain;
                settled = true; phase = ModernJournalSamplePhase.None; callbackThread = 0;
                state = confirmed ? ModernJournalSampleState.Released : ModernJournalSampleState.Retained;
            }
            domain.Complete(this);
        }
        return confirmed;
    }
    // Only the owner's existing between-call checks are observed; false does not
    // exclude checks or cancellation inside a frame, and true proves no native stop.
    private bool ObserveStop(CancellationToken token)
    {
        if (!token.IsCancellationRequested) return false;
        lock (sync) stopObserved = true;
        return true;
    }
    private void SetPhase(ModernJournalSamplePhase value)
    {
        lock (sync)
        {
            phase = value; callbackThread = Environment.CurrentManagedThreadId;
            SetOutcomeUnderLock(value, ModernJournalCallbackOutcome.Running);
        }
    }
    private void SetOutcome(ModernJournalSamplePhase value, ModernJournalCallbackOutcome outcome)
    { lock (sync) SetOutcomeUnderLock(value, outcome); }
    private ModernJournalCallbackOutcome Outcome(ModernJournalSamplePhase value) => value switch
    {
        ModernJournalSamplePhase.Prepare => prepareOutcome,
        ModernJournalSamplePhase.Directory => directoryOutcome,
        ModernJournalSamplePhase.Lease => leaseOutcome,
        ModernJournalSamplePhase.Journal => journalOutcome,
        ModernJournalSamplePhase.Exercise => exerciseOutcome,
        ModernJournalSamplePhase.Release => releaseOutcome,
        _ => ModernJournalCallbackOutcome.NotEntered
    };
    private void SetOutcomeUnderLock(ModernJournalSamplePhase value, ModernJournalCallbackOutcome outcome)
    {
        switch (value)
        {
            case ModernJournalSamplePhase.Prepare: prepareOutcome = outcome; break;
            case ModernJournalSamplePhase.Directory: directoryOutcome = outcome; break;
            case ModernJournalSamplePhase.Lease: leaseOutcome = outcome; break;
            case ModernJournalSamplePhase.Journal: journalOutcome = outcome; break;
            case ModernJournalSamplePhase.Exercise: exerciseOutcome = outcome; break;
            case ModernJournalSamplePhase.Release: releaseOutcome = outcome; break;
        }
    }
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
