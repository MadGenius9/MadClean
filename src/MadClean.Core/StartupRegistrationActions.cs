namespace MadClean.Core;

public enum StartupRegistrationSource { CurrentUserRun, CurrentUserRunOnce }
public sealed record StartupRegistrationReference(StartupRegistrationSource Source, string ValueName);
public sealed record StartupActionPreview(string Token, StartupRegistrationReference Reference, DateTimeOffset ObservedUtc,
    string ValueKind, int ByteCount, string ExactEffect, string Permissions, string RecoveryLimits);
public sealed record StartupBackupSummary(string BackupId, StartupRegistrationReference Reference, DateTimeOffset CreatedUtc,
    string RecoveryState);
public enum StartupActionOutcome
{
    RegistrationRemoved, RegistrationRestored, Conflict, ExistingValueBlocked, CancelledBeforeCommit,
    CommitOutcomeUnknown, BackupFailed, Unavailable, Failed
}
public sealed record StartupActionResult(StartupActionOutcome Outcome, string Message, string? BackupId, DateTimeOffset ObservedUtc);

public interface IStartupRegistrationActions : IDisposable
{
    Task<CheckResult<StartupRegistrationReference>> InventoryAsync(CancellationToken token);
    Task<CheckResult<StartupActionPreview>> PreviewDisableAsync(StartupRegistrationReference reference, CancellationToken token);
    Task<StartupActionResult> DisableAsync(string previewToken, CancellationToken token);
    Task<CheckResult<StartupBackupSummary>> ListBackupsAsync(CancellationToken token);
    Task<CheckResult<StartupActionPreview>> PreviewRestoreAsync(string backupId, CancellationToken token);
    Task<StartupActionResult> RestoreAsync(string previewToken, CancellationToken token);
}
