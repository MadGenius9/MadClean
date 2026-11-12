namespace MadClean.Core;

/// <summary>An admitted direct kernel owner bound to the exact coordinator permit, never a caller assertion of creation.</summary>
public sealed class SystemRepairDirectDispatchSession : SystemRepairDispatchSession
{
    private readonly object sync = new();
    private Task<SystemRepairDirectDispatchEvidence>? observation;
    private SystemRepairDirectDispatchSession(SystemRepairDispatchPermit permit, SystemRepairDirectLaunchSession session) : base(permit.Context)
    { Permit = permit; Session = session; }
    public SystemRepairDispatchPermit Permit { get; }
    public SystemRepairDirectLaunchSession Session { get; }
    public static SystemRepairDirectDispatchSession Create(SystemRepairDispatchPermit permit, SystemRepairDirectLaunchSession session)
    {
        ArgumentNullException.ThrowIfNull(permit); ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(permit.Context, session.Context) || !session.WasAdmittedFor(permit))
            throw new ArgumentException("The direct session must be admitted for this exact permit.", nameof(session));
        return new(permit, session);
    }
    internal bool BelongsTo(SystemRepairDispatchPermit permit) => ReferenceEquals(Permit, permit) &&
        ReferenceEquals(Context, permit.Context) && ReferenceEquals(Session.Context, Context) && Session.WasAdmittedFor(permit);
    internal Task<SystemRepairDirectDispatchEvidence> ObserveOwnedAsync()
    { lock (sync) return observation ??= ObserveAsync(); }
    private async Task<SystemRepairDirectDispatchEvidence> ObserveAsync()
    {
        SystemRepairDirectCreation? creation = null;
        SystemRepairDirectLaunchResult? completion = null;
        try
        {
            // Both actual tasks remain owned even if either await faults. No caller cancellation is passed through.
            var creating = Session.Creation; var completing = Session.Completion;
            try { creation = await creating.ConfigureAwait(false); } catch { }
            try { completion = await completing.ConfigureAwait(false); } catch { }
            if (creation == null || completion == null || !BelongsTo(Permit) ||
                !ReferenceEquals(creation.Context, Context) || !ReferenceEquals(completion.Context, Context) ||
                creation.State != completion.CreationState || creation.CreateAttempted != completion.CreateAttempted ||
                creation.ProcessId != completion.ProcessId || !SystemRepairWorkflow.Utc(creation.ObservedUtc) ||
                creation.ObservedUtc < Context.Request.PreparedUtc || creation.ObservedUtc > DateTimeOffset.UtcNow ||
                (completion.ExitObservedUtc is { } exit && (!SystemRepairWorkflow.Utc(exit) || exit < creation.ObservedUtc || exit > DateTimeOffset.UtcNow)))
                return Invalid();
            bool noClient = creation.State switch
            {
                SystemRepairDirectCreationState.NotAttempted => !creation.CreateAttempted && !Permit.WasClaimed,
                SystemRepairDirectCreationState.KnownNotCreated => creation.CreateAttempted && Permit.WasClaimed,
                _ => false
            };
            bool created = creation.State == SystemRepairDirectCreationState.Created && creation.CreateAttempted && Permit.WasClaimed;
            if ((!created && !noClient) || (noClient && (completion.ProcessId != null || completion.ExitObserved != null ||
                    completion.ExitCode != null || completion.ExitObservedUtc != null)) ||
                (created && completion.ProcessId is <= 0) ||
                (created && completion.ExitObserved != true && (completion.ExitCode != null || completion.ExitObservedUtc != null)) ||
                (created && completion.ExitObserved == true && completion.ExitObservedUtc == null)) return Invalid();
            bool clean = completion.CleanupConfirmed && completion.Stage == SystemRepairDirectStage.Completed &&
                !completion.RequiresRetention && !Session.HasPendingWork && !Session.RequiresRetention &&
                (!created || completion.ExitObserved == true) &&
                DrainReleased(Session.StartedStdout, completion.Stdout) && DrainReleased(Session.StartedStderr, completion.Stderr);
            return new(creation, completion, true, noClient, created) { CleanupQualified = clean };
        }
        catch { return Invalid(); }
        static bool DrainReleased(bool started, SystemRepairDirectDrainResult? result) => started
            ? result is { EndOfStreamObserved: true, WorkerReleased: true } : result == null;
        SystemRepairDirectDispatchEvidence Invalid() => new(
            creation != null && ReferenceEquals(creation.Context, Context) ? creation : null,
            completion != null && ReferenceEquals(completion.Context, Context) ? completion : null, false, false, false);
    }
}

internal sealed record SystemRepairDirectDispatchEvidence(SystemRepairDirectCreation? Creation,
    SystemRepairDirectLaunchResult? Completion, bool IsConsistent, bool KnownNoClient, bool ClientWasCreated)
{
    internal bool CleanupQualified { get; init; }
}
