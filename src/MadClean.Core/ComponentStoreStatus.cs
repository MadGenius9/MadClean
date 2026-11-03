namespace MadClean.Core;

public enum ComponentStoreCheckKind { RecordedFlags, ScanHealth }
public enum ComponentStoreScanFinding { Unknown, NoCorruptionReported, RepairableCorruptionReported, NonRepairableCorruptionReported }
public enum ComponentStoreRecordedFlag { Unknown, NoRecordedCorruptionFlag, RecordedRepairable, RecordedNonRepairable }
public enum ComponentStorePipelineState { NotStarted, Running, Stopping, Stopped, Completed, Failed, Disconnected }
public enum ComponentStoreStatusStage
{
    NotStarted, AdministratorCheck, HostIdentity, HostStart, ModuleImport, CmdletQualification,
    Invocation, StopRequested, OutputQualification, Cleanup, Completed
}
public enum ComponentStoreStatusOutcome
{
    NotStarted, Unavailable, MetadataUnqualified, InvocationFailed, PipelineStopped, UnexpectedOutput, Completed, CleanupUncertain
}
public enum ComponentStoreStatusDispatchReservation { Reserved, Invalidated, AlreadyReserved, Stale, Cancelled }

/// <summary>Fixed fields from the one owned host; null always means unobserved, not false or zero.</summary>
public sealed record ComponentStoreStatusEvidence
{
    public Guid RequestId { get; init; }
    // Legacy version-1 envelopes describe RecordedFlags only; version 2 supplies this explicitly.
    public ComponentStoreCheckKind Kind { get; init; } = ComponentStoreCheckKind.RecordedFlags;
    public ComponentStoreStatusStage Stage { get; init; }
    public ComponentStoreStatusOutcome Outcome { get; init; }
    public bool? HostQualified { get; init; }
    public bool? ModuleBound { get; init; }
    public bool? CmdletQualified { get; init; }
    public bool? OutputContractQualified { get; init; }
    public bool? InvocationStarted { get; init; }
    public ComponentStorePipelineState? PipelineState { get; init; }
    public bool? EndInvokeReturned { get; init; }
    public bool? CancelEventObserved { get; init; }
    public bool? BeginStopCalled { get; init; }
    public bool? EndStopReturned { get; init; }
    public int? OutputCount { get; init; }
    public int? ErrorCount { get; init; }
    public int? WarningCount { get; init; }
    public string? HealthState { get; init; }
    public bool? PowerShellDisposed { get; init; }
    public bool? RunspaceClosed { get; init; }
    public bool? CancelHandleDisposed { get; init; }
    // A managed wrapper observation, never a DISM native HRESULT or integrity verdict.
    public int? ManagedExceptionHResult { get; init; }
}

public sealed record ComponentStoreStatusTransport(bool? AdministratorObserved, bool? HostStartObserved,
    bool? HostExitObserved, int? HostExitCode, bool? OwnershipReleased)
{
    public bool? HostStartAttempted { get; init; }
    public bool? StdoutDrained { get; init; }
    public bool? StderrDrained { get; init; }
    public bool? OutputBoundExceeded { get; init; }
    public int? StderrCharacterCount { get; init; }
    public bool? StopSignalSucceeded { get; init; }
    public int? StopSignalNativeError { get; init; }
    public SystemToolIdentityDiagnostic? HostIdentity { get; init; }
}

public sealed record ComponentStoreStatusResult(Guid RequestId, CheckState State, ComponentStoreRecordedFlag Flag,
    DateTimeOffset AttemptedUtc, DateTimeOffset? CompletedUtc, string Message)
{
    public ComponentStoreCheckKind Kind { get; init; } = ComponentStoreCheckKind.RecordedFlags;
    public ComponentStoreScanFinding ScanFinding { get; init; } = ComponentStoreScanFinding.Unknown;
    public ComponentStoreStatusEvidence? Evidence { get; init; }
    public ComponentStoreStatusTransport Transport { get; init; } = new(null, null, null, null, null);
}

public interface IComponentStoreStatusSession
{
    Guid RequestId { get; }
    bool HasPendingWork { get; }
    bool RequiresRetention { get; }
    bool StopRequested { get; }
    void RequestStop();
    Task<ComponentStoreStatusResult> Completion { get; }
}

public interface IComponentStoreStatusProvider
{
    bool ProductionCheckValidated { get; }
    bool IsCheckKindValidated(ComponentStoreCheckKind kind);
    bool HasPendingWork { get; }
    bool RequiresRetention { get; }
    Task<IComponentStoreStatusSession> StartAsync(ComponentStoreStatusIntent intent, CancellationToken prelaunchToken);
}

public sealed class ComponentStoreStatusPreview
{
    private readonly object confirmationLock = new();
    private bool invalidated, confirmed;
    internal ComponentStoreStatusPreview(ComponentStoreCheckKind kind, DateTimeOffset nowUtc)
    { Kind = kind; RequestId = Guid.NewGuid(); PreparedUtc = nowUtc; ExpiresUtc = nowUtc.Add(ComponentStoreStatusPolicy.PreviewLifetime); }
    public ComponentStoreCheckKind Kind { get; }
    public Guid RequestId { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public string CommandPreview => Kind == ComponentStoreCheckKind.ScanHealth
        ? @"Dism\Repair-WindowsImage -Online -ScanHealth -NoRestart -ErrorAction Stop"
        : @"Dism\Repair-WindowsImage -Online -CheckHealth -NoRestart -ErrorAction Stop";
    public string ExactEffect => Kind == ComponentStoreCheckKind.ScanHealth
        ? "Ask the inbox Windows DISM PowerShell cmdlet to scan the current online Windows image for component-store corruption. This can take several minutes and use system resources. No repair, component cleanup or restart is requested. Windows may record scan results/health metadata and write DISM logs, archives and temporary servicing metadata."
        : "Ask the inbox Windows DISM PowerShell cmdlet whether the current online Windows image has a previously recorded corruption flag. No fresh component scan or repair is requested. Windows may write DISM logs, archives and temporary servicing metadata.";
    public string Permissions => "The current caller must already be running with effective administrator rights. MadClean does not elevate, collect credentials or restart itself. Relaunch as administrator yourself and select this operation again if appropriate.";
    public string Cancellation => "Stop requests only this owned PowerShell pipeline to stop and waits for its actual completion and host exit. A native stage may continue before returning. No servicing cancellation, rollback or safe forced termination is promised; no process or service is killed.";
    public string Limitations => Kind == ComponentStoreCheckKind.ScanHealth
        ? "A result covers only this DISM component-store scan, not all Windows system files, antivirus, disks, hardware or overall PC health. No per-file coverage or earlier corruption cause is supplied. Request/completion times describe this invocation, not an atomic snapshot. Windows DISM logs and health metadata may be written; their contents are not read. No repair, source, path, cleanup, reset-base or restart option can be supplied."
        : "No recorded flag is not proof that Windows or this PC is healthy. No fresh scan was performed and the prior scan/flag date is unknown. Default Windows DISM log and archive writes are disclosed; their contents are not read. No source, path, repair, cleanup or restart option can be supplied. Other integrity checks and repairs are separate; no extra command or options can be supplied.";
    public void Invalidate() { lock (confirmationLock) invalidated = true; }
    internal ComponentStoreStatusIntent? Confirm(DateTimeOffset nowUtc)
    {
        lock (confirmationLock)
        {
            if (invalidated || confirmed || !ComponentStoreStatusPolicy.ValidUtc(nowUtc) || nowUtc < PreparedUtc || nowUtc >= ExpiresUtc) return null;
            confirmed = true;
            return new(RequestId, Kind, PreparedUtc, ExpiresUtc);
        }
    }
}

public sealed class ComponentStoreStatusIntent
{
    private readonly object dispatchLock = new();
    private bool invalidated, reserved;
    internal ComponentStoreStatusIntent(Guid requestId, ComponentStoreCheckKind kind, DateTimeOffset preparedUtc, DateTimeOffset expiresUtc)
    { RequestId = requestId; Kind = kind; PreparedUtc = preparedUtc; ExpiresUtc = expiresUtc; }
    public ComponentStoreCheckKind Kind { get; }
    public Guid RequestId { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public bool IsDispatchReserved { get { lock (dispatchLock) return reserved; } }
    public bool IsInvalidated { get { lock (dispatchLock) return invalidated; } }
    public void Invalidate() { lock (dispatchLock) { if (!reserved) invalidated = true; } }
    public ComponentStoreStatusDispatchReservation TryReserveDispatch(DateTimeOffset nowUtc, CancellationToken token = default)
    {
        lock (dispatchLock)
        {
            if (reserved) return ComponentStoreStatusDispatchReservation.AlreadyReserved;
            if (invalidated) return ComponentStoreStatusDispatchReservation.Invalidated;
            if (!ComponentStoreStatusPolicy.ValidUtc(nowUtc) || nowUtc < PreparedUtc || nowUtc >= ExpiresUtc)
            { invalidated = true; return ComponentStoreStatusDispatchReservation.Stale; }
            if (token.IsCancellationRequested)
            { invalidated = true; return ComponentStoreStatusDispatchReservation.Cancelled; }
            reserved = true;
            return ComponentStoreStatusDispatchReservation.Reserved;
        }
    }
}

public static class ComponentStoreStatusPolicy
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(2);
    public static CheckResult<ComponentStoreStatusPreview> CreatePreview(DateTimeOffset nowUtc, CancellationToken token = default) =>
        CreatePreview(ComponentStoreCheckKind.RecordedFlags, nowUtc, token);

    public static CheckResult<ComponentStoreStatusPreview> CreatePreview(ComponentStoreCheckKind kind, DateTimeOffset nowUtc,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind) || !ValidUtc(nowUtc) || nowUtc > DateTimeOffset.MaxValue - PreviewLifetime)
            return CheckResult<ComponentStoreStatusPreview>.Empty(CheckState.Unknown, "A supported fixed check kind and valid UTC preparation time are required; no request was prepared.");
        return new(CheckState.Available, "Fixed operation preview only; no Windows host, check, scan or repair has started.", [new(kind, nowUtc)]);
    }

    public static CheckResult<ComponentStoreStatusIntent> Confirm(ComponentStoreStatusPreview preview,
        bool effectsAcknowledged, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (preview == null || !effectsAcknowledged)
            return CheckResult<ComponentStoreStatusIntent>.Empty(CheckState.Unknown, "Explicitly review and acknowledge this fixed operation before requesting it.");
        var intent = preview.Confirm(nowUtc);
        return intent == null
            ? CheckResult<ComponentStoreStatusIntent>.Empty(CheckState.Unknown, "The preview is expired, invalidated or already confirmed; prepare a new preview.")
            : new(CheckState.Available, "One reviewed request prepared. Current administrator, host and source qualification are still required.", [intent]);
    }

    public static ComponentStoreStatusResult Assess(ComponentStoreStatusIntent intent, ComponentStoreStatusEvidence? evidence,
        ComponentStoreStatusTransport transport, DateTimeOffset attemptedUtc, DateTimeOffset? completedUtc, bool callerStopRequested)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(transport);
        bool transportValid = IsTransportConsistent(transport);
        bool timeValid = ValidUtc(attemptedUtc) && attemptedUtc >= intent.PreparedUtc &&
            (completedUtc == null || ValidUtc(completedUtc.Value) && completedUtc >= attemptedUtc);
        bool sourceValid = evidence != null && ComponentStoreStatusParser.IsConsistent(evidence, intent.RequestId, intent.Kind);
        bool settled = transportValid && transport.HostStartObserved == true && transport.HostExitObserved == true &&
            transport.HostExitCode == 0 && transport.OwnershipReleased == true && completedUtc.HasValue &&
            transport.StdoutDrained == true && transport.StderrDrained == true &&
            transport.OutputBoundExceeded == false && transport.StderrCharacterCount == 0;
        bool hostIdentityQualified = transport.HostIdentity is
        {
            Stage: SystemToolIdentityStage.Completed, Reason: SystemToolIdentityReason.Observed, NativeTrustResult: 0,
            TrustRoute: SystemToolTrustRoute.File or SystemToolTrustRoute.Catalog
        };
        bool cleanup = sourceValid && evidence!.PowerShellDisposed == true && evidence.RunspaceClosed == true && evidence.CancelHandleDisposed == true;
        bool stopped = callerStopRequested || transportValid && transport.StopSignalSucceeded.HasValue || sourceValid && (evidence!.CancelEventObserved == true || evidence.BeginStopCalled == true ||
            evidence.PipelineState is ComponentStorePipelineState.Stopped or ComponentStorePipelineState.Stopping);
        CheckState state = CheckState.Unknown;
        var flag = ComponentStoreRecordedFlag.Unknown;
        var scanFinding = ComponentStoreScanFinding.Unknown;
        string message = "No qualified current component-store assessment is available. Preserve source, pipeline and host observations separately; this is not a health verdict.";
        if (stopped)
        { state = CheckState.Cancelled; message = "A stop was requested or observed for this check or its owned pipeline. Current assessment remains Unknown; any actual output and exit facts are historical. Windows servicing cancellation or rollback is not established."; }
        else if (transport.AdministratorObserved == false)
        { state = CheckState.Unavailable; message = "The current caller is not effectively elevated. MadClean did not request elevation; relaunch as administrator yourself and select this check again if appropriate."; }
        else if (!timeValid || !sourceValid)
            message = "The fixed source response was absent, malformed, mismatched or incomplete. No current assessment or completed Windows check is inferred.";
        else if (evidence!.Outcome == ComponentStoreStatusOutcome.Completed && intent.IsDispatchReserved && transport.AdministratorObserved == true && hostIdentityQualified && settled && cleanup &&
            evidence.CancelEventObserved == false && evidence.BeginStopCalled == false && evidence.WarningCount == 0 && evidence.ManagedExceptionHResult == null)
        {
            if (intent.Kind == ComponentStoreCheckKind.RecordedFlags)
            {
                flag = evidence.HealthState switch
                {
                    "Healthy" => ComponentStoreRecordedFlag.NoRecordedCorruptionFlag,
                    "Repairable" => ComponentStoreRecordedFlag.RecordedRepairable,
                    "NonRepairable" => ComponentStoreRecordedFlag.RecordedNonRepairable,
                    _ => ComponentStoreRecordedFlag.Unknown
                };
                if (flag != ComponentStoreRecordedFlag.Unknown)
                { state = CheckState.Available; message = "The inbox DISM cmdlet reported the recorded component-store flag. No fresh scan or repair was performed; this is not overall Windows or PC health, and the prior flag/scan date is unknown."; }
            }
            else if (intent.Kind == ComponentStoreCheckKind.ScanHealth)
            {
                scanFinding = evidence.HealthState switch
                {
                    "Healthy" => ComponentStoreScanFinding.NoCorruptionReported,
                    "Repairable" => ComponentStoreScanFinding.RepairableCorruptionReported,
                    "NonRepairable" => ComponentStoreScanFinding.NonRepairableCorruptionReported,
                    _ => ComponentStoreScanFinding.Unknown
                };
                if (scanFinding != ComponentStoreScanFinding.Unknown)
                { state = CheckState.Available; message = "The inbox DISM ScanHealth invocation reported its component-store finding. No repair or restart was requested. This is not a system-file, antivirus, disk, hardware or overall PC-health assessment; no per-file coverage or corruption cause is supplied."; }
            }
        }
        else if (evidence!.Outcome == ComponentStoreStatusOutcome.Unavailable)
        { state = CheckState.Unavailable; message = "The fixed inbox host/module was unavailable. No alternate command, DLL, ADK source or repair was used."; }
        else if (evidence.Outcome == ComponentStoreStatusOutcome.InvocationFailed || transport.HostExitObserved == true && transport.HostExitCode is not (null or 0))
        { state = CheckState.Failed; message = "The owned host or cmdlet reported a failure. Current assessment remains Unknown; managed exception metadata is not a DISM native HRESULT or health assessment."; }
        return new(intent.RequestId, state, flag, attemptedUtc, completedUtc, message)
        { Kind = intent.Kind, ScanFinding = scanFinding, Evidence = sourceValid ? evidence : null, Transport = transport };
    }

    public static bool IsTransportConsistent(ComponentStoreStatusTransport transport)
    {
        if (transport == null || transport.StderrCharacterCount is < 0) return false;
        if (transport.HostStartAttempted == false && transport.HostStartObserved == true) return false;
        if (transport.HostExitObserved == true && transport.HostStartObserved != true) return false;
        if (transport.HostExitCode.HasValue && transport.HostExitObserved != true) return false;
        if (transport.HostStartObserved == true && transport.OwnershipReleased == true && transport.HostExitObserved != true) return false;
        if (transport.StopSignalSucceeded == null && transport.StopSignalNativeError.HasValue ||
            transport.StopSignalSucceeded == true && transport.StopSignalNativeError.HasValue) return false;
        return true;
    }

    internal static bool ValidUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
