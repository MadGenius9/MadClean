namespace MadClean.Core;

public enum SfcIntegrityAssessment { Unknown }
public enum SfcVerificationDispatchReservation { Reserved, Invalidated, AlreadyReserved, Stale, Cancelled }

/// <summary>Actual client observations only. Null means unobserved, never false or success.</summary>
public sealed record SfcVerificationTransport
{
    public bool? AdministratorObserved { get; init; }
    public bool? ClientStartAttempted { get; init; }
    public bool? ClientStartObserved { get; init; }
    public int? ProcessId { get; init; }
    public DateTimeOffset? ClientStartedUtc { get; init; }
    public bool? ClientExitObserved { get; init; }
    public int? ClientExitCode { get; init; }
    public DateTimeOffset? ClientExitedUtc { get; init; }
    public bool? OwnershipReleased { get; init; }
    public bool StopObservationRequested { get; init; }
}

public sealed record SfcVerificationResult(Guid RequestId, CheckState State, DateTimeOffset AttemptedUtc,
    DateTimeOffset? CompletedUtc, string Message, SfcVerificationTransport Transport)
{
    public SfcIntegrityAssessment Integrity => SfcIntegrityAssessment.Unknown;
    public string WindowsLogReference => SfcVerificationPolicy.WindowsLogReference;
}

public interface ISfcVerificationSession
{
    Guid RequestId { get; }
    bool HasPendingWork { get; }
    bool RequiresRetention { get; }
    bool StopObservationRequested { get; }
    void RequestStopObservation();
    Task<SfcVerificationResult> Completion { get; }
}

public interface ISfcVerificationProvider
{
    bool ProductionExecutionValidated { get; }
    bool HasPendingWork { get; }
    bool RequiresRetention { get; }
    Task<ISfcVerificationSession> StartAsync(SfcVerificationIntent intent, CancellationToken prelaunchToken);
}

public sealed class SfcVerificationPreview
{
    private readonly object sync = new();
    private bool invalidated, confirmed;
    internal SfcVerificationPreview(SystemToolActionPreview identity, DateTimeOffset nowUtc)
    {
        RequestId = Guid.NewGuid(); IdentityPreviewId = identity.PreviewId;
        ToolPath = identity.ToolPath; ToolVersion = identity.ToolVersion; Sha256 = identity.Sha256;
        PreparedUtc = nowUtc; ExpiresUtc = identity.ExpiresUtc;
    }
    public Guid RequestId { get; }
    public Guid IdentityPreviewId { get; }
    public string ToolPath { get; }
    public string ToolVersion { get; }
    public string Sha256 { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public string CommandPreview => "sfc.exe /verifyonly";
    public string ExactEffect => "Verify protected Windows system files without requesting repairs. Windows may write diagnostic/CBS logs and metadata. This is not zero-write passive inventory; personal and application files are outside this check's scope.";
    public string Permissions => "Already-effective administrator rights are required. MadClean does not request elevation, UAC, credentials or an administrator relaunch.";
    public string Cancellation => "Stop observation does not stop SFC or a Windows service. The owner retains its client and executable leases until actual exit; no process is killed and no rollback is claimed.";
    public string Limitations => "Actual client exit and its numeric code do not establish protected-file integrity or successful verification. Integrity remains Unknown; inspect genuine Windows output and the protected CBS log separately. No repair, offline image, custom source, log destination or restart option is accepted.";
    public void Invalidate() { lock (sync) invalidated = true; }
    internal SfcVerificationIntent? Confirm(DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            if (invalidated || confirmed || !SfcVerificationPolicy.ValidUtc(nowUtc) || nowUtc < PreparedUtc || nowUtc >= ExpiresUtc) return null;
            confirmed = true;
            return new(this);
        }
    }
}

public sealed class SfcVerificationIntent
{
    private readonly object sync = new();
    private bool invalidated, reserved;
    internal SfcVerificationIntent(SfcVerificationPreview preview)
    {
        RequestId = preview.RequestId; IdentityPreviewId = preview.IdentityPreviewId;
        ToolPath = preview.ToolPath; ToolVersion = preview.ToolVersion; Sha256 = preview.Sha256;
        PreparedUtc = preview.PreparedUtc; ExpiresUtc = preview.ExpiresUtc;
    }
    public Guid RequestId { get; }
    public Guid IdentityPreviewId { get; }
    public string ToolPath { get; }
    public string ToolVersion { get; }
    public string Sha256 { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public bool IsDispatchReserved { get { lock (sync) return reserved; } }
    public bool IsInvalidated { get { lock (sync) return invalidated; } }
    public void Invalidate() { lock (sync) { if (!reserved) invalidated = true; } }
    public SfcVerificationDispatchReservation TryReserveDispatch(DateTimeOffset nowUtc, CancellationToken token = default)
    {
        lock (sync)
        {
            if (reserved) return SfcVerificationDispatchReservation.AlreadyReserved;
            if (invalidated) return SfcVerificationDispatchReservation.Invalidated;
            if (!SfcVerificationPolicy.ValidUtc(nowUtc) || nowUtc < PreparedUtc || nowUtc >= ExpiresUtc)
            { invalidated = true; return SfcVerificationDispatchReservation.Stale; }
            if (token.IsCancellationRequested)
            { invalidated = true; return SfcVerificationDispatchReservation.Cancelled; }
            reserved = true;
            return SfcVerificationDispatchReservation.Reserved;
        }
    }
}

public static class SfcVerificationPolicy
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(2);
    public const string WindowsLogReference = @"%windir%\Logs\CBS\CBS.log";
    public static CheckResult<SfcVerificationPreview> CreatePreview(SystemToolActionPreview identity,
        DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (identity == null || !ValidUtc(nowUtc) || !ValidUtc(identity.CheckedUtc) || !ValidUtc(identity.ExpiresUtc))
            return CheckResult<SfcVerificationPreview>.Empty(CheckState.Unknown, "A fresh fixed SFC identity observation and valid UTC times are required; no request was prepared.");
        // Pure descriptor validation only. Legacy CanLaunch and its global production
        // gate are not identity facts; this separate preview grants no dispatch permission.
        var request = new SystemToolLaunchRequest(identity.PreviewId, SystemToolOperation.SfcVerifyOnly, identity.Sha256, true, true);
        if (SystemToolActionsPolicy.ValidateLaunch(identity with { CanLaunch = true }, request, nowUtc, true).Count != 0)
            return CheckResult<SfcVerificationPreview>.Empty(CheckState.Unknown, "The exact fresh Microsoft SFC identity descriptor could not be accepted. No tool was requested.");
        return new(CheckState.Available, "Fixed verification preview only. No administrator query, elevation or SFC client has started.", [new(identity, nowUtc)]);
    }

    public static CheckResult<SfcVerificationIntent> Confirm(SfcVerificationPreview preview, bool userSelected,
        bool effectsAcknowledged, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (preview == null || !userSelected || !effectsAcknowledged)
            return CheckResult<SfcVerificationIntent>.Empty(CheckState.Unknown, "Select and acknowledge this exact verification operation before requesting it.");
        var intent = preview.Confirm(nowUtc);
        return intent == null
            ? CheckResult<SfcVerificationIntent>.Empty(CheckState.Unknown, "The preview is stale, invalidated or already confirmed. Prepare a new preview.")
            : new(CheckState.Available, "One reviewed intent prepared. The production gate, current effective administrator rights and fresh held executable identity must still be checked.", [intent]);
    }

    public static SfcVerificationResult Assess(SfcVerificationIntent intent, SfcVerificationTransport transport,
        DateTimeOffset attemptedUtc, DateTimeOffset? completedUtc)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(transport);
        bool valid = IsTransportConsistent(transport) && ValidUtc(attemptedUtc) && attemptedUtc >= intent.PreparedUtc &&
            (completedUtc == null || ValidUtc(completedUtc.Value) && completedUtc >= attemptedUtc) &&
            (transport.ClientStartedUtc == null || transport.ClientStartedUtc >= attemptedUtc &&
                (completedUtc == null || transport.ClientStartedUtc <= completedUtc)) &&
            (transport.ClientExitedUtc == null || completedUtc.HasValue && transport.ClientExitedUtc >= attemptedUtc &&
                transport.ClientExitedUtc <= completedUtc);
        var state = CheckState.Unknown;
        string message = "No qualified completed SFC client observation is available. Protected-file integrity remains Unknown; no repair or restart was requested.";
        if (!valid)
            message = "Client observations are inconsistent or incomplete. Preserve actual facts separately; no completion or integrity conclusion is accepted.";
        else if (transport.StopObservationRequested)
        { state = CheckState.Cancelled; message = "Stopping observation was requested. Actual client facts are historical; no SFC cancellation, rollback or integrity verdict is claimed. The owner must remain until actual client exit."; }
        else if (transport.AdministratorObserved == false)
        { state = CheckState.Unavailable; message = "The caller was not effectively elevated. No elevation was requested; protected-file integrity remains Unknown."; }
        else if (intent.IsDispatchReserved && transport.AdministratorObserved == true && transport.ClientStartObserved == true &&
            transport.ClientExitObserved == true && transport.ClientExitCode.HasValue && transport.OwnershipReleased == true && completedUtc.HasValue)
        {
            state = CheckState.Available;
            message = $"The selected SFC client exited with code {transport.ClientExitCode.Value}. This is a client observation, not proof of successful verification or protected-file integrity. Integrity remains Unknown; inspect genuine Windows output and the protected CBS log separately.";
        }
        return new(intent.RequestId, state, attemptedUtc, completedUtc, message, transport);
    }

    public static bool IsTransportConsistent(SfcVerificationTransport transport)
    {
        if (transport == null || transport.ProcessId is <= 0 ||
            transport.ClientStartedUtc is { } start && !ValidUtc(start) || transport.ClientExitedUtc is { } end && !ValidUtc(end)) return false;
        if (transport.ClientStartObserved == true && (transport.ClientStartAttempted != true || transport.AdministratorObserved != true || !transport.ProcessId.HasValue)) return false;
        if ((transport.ProcessId.HasValue || transport.ClientStartedUtc.HasValue) && transport.ClientStartObserved != true) return false;
        if (transport.ClientExitObserved == true && (transport.ClientStartObserved != true || !transport.ClientExitCode.HasValue || !transport.ClientExitedUtc.HasValue)) return false;
        if ((transport.ClientExitCode.HasValue || transport.ClientExitedUtc.HasValue) && transport.ClientExitObserved != true) return false;
        if (transport.ClientStartedUtc.HasValue && transport.ClientExitedUtc < transport.ClientStartedUtc) return false;
        if (transport.ClientStartObserved == true && transport.OwnershipReleased == true && transport.ClientExitObserved != true) return false;
        // A possibly dispatched but unobserved client cannot safely release ownership.
        if (transport.ClientStartAttempted == true && transport.ClientStartObserved != true && transport.OwnershipReleased == true) return false;
        return true;
    }
    internal static bool ValidUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
