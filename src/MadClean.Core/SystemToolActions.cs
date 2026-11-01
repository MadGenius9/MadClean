namespace MadClean.Core;

public enum SystemToolOperation { DismCheckHealth, DismScanHealth, DismRestoreHealth, SfcVerifyOnly, SfcScanNow }
public enum SystemToolActionKind { NonRepairCheck, Repair }
public enum SystemToolTrustState { Unknown, MicrosoftSignatureVerified, Unavailable, Failed }
public enum SystemToolRunState { NotStarted, Unavailable, ConsentDenied, Launched, ClientExited, MonitoringLost }
public enum SystemToolIdentityStage { Unknown, SystemDirectory, FileLease, Signature, Fingerprint, VersionMetadata, Completed }
public enum SystemToolIdentityReason
{
    Unspecified, Observed, NativeTrustNotEstablished, SignerInspectionUnavailable, SignerNotQualified,
    OriginalFilenameMismatch, IdentityBoundsOrPath, OtherUnavailable, WaitExpired, PendingWorker, UnsupportedPlatform
}
public sealed record SystemToolIdentityDiagnostic(SystemToolIdentityStage Stage, SystemToolIdentityReason Reason,
    int? NativeTrustResult)
{
    // Bounded parser observations only; none of these diagnostics grants trust or launch permission.
    public SystemToolEmbeddedVersionState? EmbeddedVersionState { get; init; }
    public int? EmbeddedVersionResourceCount { get; init; }
    public int? EmbeddedOriginalNameCount { get; init; }
}
public sealed record SystemToolActionDefinition(SystemToolOperation Operation, string Title, string FileName,
    IReadOnlyList<string> Arguments, SystemToolActionKind Kind, string Effect, string Elevation,
    string Network, string Cancellation, string Reboot, string WindowsLogReference);
public sealed record SystemToolActionPreview(Guid PreviewId, SystemToolActionDefinition Definition,
    string SystemDirectory, string ToolPath, string ToolVersion, string Sha256, SystemToolTrustState TrustState,
    string TrustDetail, DateTimeOffset CheckedUtc, DateTimeOffset ExpiresUtc, bool CanLaunch, string Blocker)
{
    // Diagnostic metadata is never a trust/permission input. NativeTrustResult, when
    // supplied by Windows, is only an actual WinVerifyTrust LONG, not an HRESULT.
    public SystemToolIdentityDiagnostic Diagnostic { get; init; } =
        new(SystemToolIdentityStage.Unknown, SystemToolIdentityReason.Unspecified, null);
}
public sealed record SystemToolLaunchRequest(Guid PreviewId, SystemToolOperation Operation, string PreviewSha256,
    bool UserSelectedOperation, bool EffectsAndElevationAcknowledged);
public sealed record SystemToolRunRecord(Guid ExecutionId, SystemToolOperation Operation, SystemToolRunState State,
    DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, int? ProcessId, int? ClientExitCode,
    bool ElevationRequested, bool StopMonitoringRequested, string Outcome, string WindowsLogReference);
public interface ISystemToolActions
{
    bool ProductionExecutionValidated { get; }
    Task<CheckResult<SystemToolActionPreview>> PrepareAsync(SystemToolOperation operation, CancellationToken token);
    Task<SystemToolRunRecord> LaunchAsync(SystemToolLaunchRequest request, CancellationToken observationToken);
}

/// <summary>Fixed servicing descriptors only. No arbitrary path, shell, arguments, offline image or repair-source input.</summary>
public static class SystemToolActionsCatalog
{
    private const string Elevation = "Administrator access is required. A future enabled launch requests visible Windows UAC consent for this exact Microsoft executable; denial does not run the tool.";
    private const string Cancel = "No safe CLI cancellation is implemented. Stop monitoring does not stop or roll back servicing. Never kill the tool, TrustedInstaller, CBS or Windows Update services to claim cancellation.";
    private static readonly SystemToolActionDefinition[] Entries =
    [
        Dism(SystemToolOperation.DismCheckHealth, "DISM: check previously recorded corruption flags", "/CheckHealth", SystemToolActionKind.NonRepairCheck,
            "Checks existing component-store corruption flags and repairability; it is not a fresh component scan or a whole-PC health assessment.", "No repair source is selected; Windows servicing may access its normal local metadata and write logs."),
        Dism(SystemToolOperation.DismScanHealth, "DISM: scan component-store integrity", "/ScanHealth", SystemToolActionKind.NonRepairCheck,
            "Scans the Windows component store without requesting repair. It can update corruption flags and Windows servicing logs, so this is not zero-write passive inventory.", "No repair is requested. Windows controls any servicing metadata/source behavior."),
        Dism(SystemToolOperation.DismRestoreHealth, "DISM: repair the Windows component store", "/RestoreHealth", SystemToolActionKind.Repair,
            "Requests component-store scanning and repair. It changes system components and may use Windows Update under existing policy; it is not a guarantee that crashes or all corruption will be fixed.", "Windows Update or configured Windows repair sources may be used. No custom /Source or /LimitAccess option is exposed."),
        Sfc(SystemToolOperation.SfcVerifyOnly, "SFC: verify protected Windows files", "/verifyonly", SystemToolActionKind.NonRepairCheck,
            "Verifies protected Windows system files without requesting repair. Windows diagnostic/CBS logs can be written; personal and application files are outside scope."),
        Sfc(SystemToolOperation.SfcScanNow, "SFC: repair protected Windows files", "/scannow", SystemToolActionKind.Repair,
            "Verifies protected Windows files and attempts repair when possible. This is a system-changing repair action, not passive inventory or proof that all PC problems are fixed.")
    ];
    public static IReadOnlyList<SystemToolActionDefinition> All { get; } = Array.AsReadOnly(Entries);
    public static SystemToolActionDefinition? Find(SystemToolOperation operation) => Entries.FirstOrDefault(entry => entry.Operation == operation);
    private static SystemToolActionDefinition Dism(SystemToolOperation operation, string title, string option,
        SystemToolActionKind kind, string effect, string network) => new(operation, title, "Dism.exe",
            Array.AsReadOnly(new[] { "/Online", "/Cleanup-Image", option, "/NoRestart" }), kind, effect, Elevation, "Identity verification may contact Windows certificate/revocation infrastructure. " + network, Cancel,
            "/NoRestart suppresses DISM restart behavior. A reboot requirement remains an actual result to review, not permission to restart. No restart action is implemented.", @"%windir%\Logs\DISM\dism.log");
    private static SystemToolActionDefinition Sfc(SystemToolOperation operation, string title, string option,
        SystemToolActionKind kind, string effect) => new(operation, title, "sfc.exe", Array.AsReadOnly(new[] { option }), kind,
            effect, Elevation, "Identity verification may contact Windows certificate/revocation infrastructure. Uses Windows protected-file/component infrastructure. No remote repair-source, download or source policy override is exposed.", Cancel,
            "No restart is requested by MadClean. Any actual Windows reboot requirement must be reviewed separately; no forced restart or reboot action is implemented.", @"%windir%\Logs\CBS\CBS.log");
}

/// <summary>Preview validation is a gate, never permission to run a repair or an integrity verdict.</summary>
public static class SystemToolActionsPolicy
{
    public static IReadOnlyList<string> ValidateLaunch(SystemToolActionPreview preview, SystemToolLaunchRequest request,
        DateTimeOffset nowUtc, bool productionExecutionValidated)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(request);
        List<string> reasons = [];
        var expected = SystemToolActionsCatalog.Find(request.Operation);
        if (!productionExecutionValidated)
            reasons.Add("Production execution is disabled until separate disposable-Windows and manual runtime validation is completed. No setting, environment flag or consent checkbox bypasses this gate.");
        if (expected == null || preview.Definition == null || preview.Definition.Operation != request.Operation ||
            expected.FileName != preview.Definition.FileName || !expected.Arguments.SequenceEqual(preview.Definition.Arguments ?? []) ||
            expected.Title != preview.Definition.Title || expected.Kind != preview.Definition.Kind || expected.Effect != preview.Definition.Effect ||
            expected.Elevation != preview.Definition.Elevation || expected.Network != preview.Definition.Network || expected.Cancellation != preview.Definition.Cancellation ||
            expected.Reboot != preview.Definition.Reboot || expected.WindowsLogReference != preview.Definition.WindowsLogReference)
            reasons.Add("The operation or exact tool argument descriptor is unsupported or changed.");
        if (request.PreviewId == Guid.Empty || request.PreviewId != preview.PreviewId || !ValidHash(preview.Sha256) ||
            !string.Equals(request.PreviewSha256, preview.Sha256, StringComparison.Ordinal))
            reasons.Add("The request is not bound to the exact checked preview and executable hash.");
        if (preview.CheckedUtc > nowUtc || preview.ExpiresUtc <= nowUtc || preview.ExpiresUtc <= preview.CheckedUtc ||
            preview.ExpiresUtc - preview.CheckedUtc > TimeSpan.FromMinutes(2))
            reasons.Add("The identity preview is stale or has an invalid lifetime; verify the tool again.");
        if (!preview.CanLaunch) reasons.Add("This preview is disabled and cannot authorize a launch.");
        if (string.IsNullOrWhiteSpace(preview.ToolVersion) || preview.ToolVersion == "Unknown" || preview.ToolVersion.Length > 128 ||
            preview.ToolVersion.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
            reasons.Add("The genuine tool version was not established.");
        if (preview.TrustState != SystemToolTrustState.MicrosoftSignatureVerified ||
            expected == null || !ValidFixedPath(preview.SystemDirectory, preview.ToolPath, expected.FileName))
            reasons.Add("A fresh trusted Microsoft executable in the genuine fixed system directory is required.");
        if (!request.UserSelectedOperation || !request.EffectsAndElevationAcknowledged)
            reasons.Add("The user must select this exact operation and acknowledge its effects, elevation and monitoring limits.");
        return reasons;
    }

    public static string DescribeClientExit(SystemToolOperation operation, int exitCode)
    {
        if (SystemToolActionsCatalog.Find(operation) == null) return "Unknown operation; no integrity or repair conclusion can be drawn.";
        return $"The servicing client exited with code {exitCode}. This alone does not establish image health, file-integrity status or successful repair. Inspect the actual tool output and protected Windows log; localized or missing evidence remains Unknown. No reboot was requested by MadClean.";
    }
    public static string DescribeLostMonitoring() => "Monitoring stopped or was lost. The elevated Windows operation may continue. No cancellation, rollback, successful repair or healthy state is claimed; inspect the tool window and protected Windows logs.";
    public static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidFixedPath(string? directory, string? path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 1024 || directory.Any(char.IsControl) ||
            directory.Length < 4 || !char.IsAsciiLetter(directory[0]) || directory[1] != ':' || directory[2] != '\\' ||
            directory.Contains('/') || directory.Contains('%') || directory.Contains('"') || directory[2..].Contains(':') ||
            directory.Contains("..", StringComparison.Ordinal) || directory.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) ||
            directory.TrimEnd('\\').Split('\\').Skip(1).Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' '))) return false;
        // The Windows provider obtains this directory from the OS, never from request text.
        return string.Equals(path, directory.TrimEnd('\\') + "\\" + fileName, StringComparison.OrdinalIgnoreCase);
    }
}
