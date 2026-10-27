using System.Security.Cryptography;
using MadClean.Core;

namespace MadClean.Windows;

internal enum StartupActionFixtureStage { AfterFreshSourceRead, AfterBackupVerified, BeforeCommit, AfterCommitBeforeVerification }

/// <summary>
/// Separate explicitly reviewed current-user registration milestone. Production execution is source-code
/// gated off. The fixture factory never maps to Windows' actual Run/RunOnce keys.
/// </summary>
public sealed class StartupRegistrationActions : IStartupRegistrationActions
{
    private readonly StartupActionScope scope;
    private readonly StartupActionProtectedStore store;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private bool disposed;

    public StartupRegistrationActions() : this(StartupActionScope.Production()) { }
    private StartupRegistrationActions(StartupActionScope scope)
    { this.scope = scope; store = new(scope); lifetimeToken = lifetime.Token; }

    internal static StartupRegistrationActions ForFixture(string regBase, string backupDir) =>
        new(StartupActionScope.ForFixture(regBase, backupDir));

    internal void SetFixtureHook(Action<StartupActionFixtureStage>? hook)
    {
        if (!scope.Fixture) throw new InvalidOperationException("Fixture hooks are unavailable on production instances.");
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); }
        if (hook != null) throw new NotSupportedException("Mutation-stage hooks are unavailable in the read-only milestone.");
    }

    public Task<CheckResult<StartupRegistrationReference>> InventoryAsync(CancellationToken token) =>
        ReadAsync(Inventory, token);
    public Task<CheckResult<StartupActionPreview>> PreviewDisableAsync(StartupRegistrationReference reference, CancellationToken token) =>
        ReadAsync(stop => PreviewDisable(reference, stop), token);
    public Task<CheckResult<StartupBackupSummary>> ListBackupsAsync(CancellationToken token) =>
        ReadAsync(ListBackups, token);
    public Task<CheckResult<StartupActionPreview>> PreviewRestoreAsync(string backupId, CancellationToken token) =>
        ReadAsync(stop => PreviewRestore(backupId, stop), token);
    public Task<StartupActionResult> DisableAsync(string previewToken, CancellationToken token) => ApplyAsync(previewToken, restore: false, token);
    public Task<StartupActionResult> RestoreAsync(string previewToken, CancellationToken token) => ApplyAsync(previewToken, restore: true, token);

    private CheckResult<StartupRegistrationReference> Inventory(CancellationToken token)
    {
        using var ownership = scope.LeaseOwnership();
        var items = new List<StartupRegistrationReference>(); int failures = 0;
        foreach (var source in Enum.GetValues<StartupRegistrationSource>())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var key = StartupActionNative.OpenRead(scope.SourcePath(source), allowMissing: true);
                if (key.MissingComponents.Length != 0) continue;
                foreach (string name in StartupActionNative.Names(key.Leaf, false, StartupActionPolicy.MaximumInventoryEntries / 2, token))
                {
                    var reference = new StartupRegistrationReference(source, name);
                    if (StartupActionPolicy.IsValidReference(reference)) items.Add(reference); else failures++;
                }
            }
            catch (Exception ex) when (SafeFailure(ex)) { failures++; }
        }
        return new(failures == 0 ? CheckState.Available : CheckState.Partial,
            $"{items.Count} current-user Run/RunOnce registration names observed; {failures} source/entry coverage issues. Commands were not inventoried; registration presence does not establish startup execution or enabled state. Actions remain unavailable.", items);
    }

    private CheckResult<StartupActionPreview> PreviewDisable(StartupRegistrationReference reference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!StartupActionPolicy.IsValidReference(reference)) return CheckResult<StartupActionPreview>.Empty(CheckState.Unsupported, "Unsupported current-user registration reference.");
        using var ownership = scope.LeaseOwnership();
        using var key = StartupActionNative.OpenRead(scope.SourcePath(reference.Source), allowMissing: true);
        if (key.MissingComponents.Length != 0) return CheckResult<StartupActionPreview>.Empty(CheckState.Unavailable, "The selected registration source is absent; no change was made.");
        using var raw = StartupActionNative.ReadSource(key.Leaf, reference.ValueName);
        if (raw == null) return CheckResult<StartupActionPreview>.Empty(CheckState.Unavailable, "The selected registration is no longer present; refresh inventory.");
        token.ThrowIfCancellationRequested();
        return new(CheckState.Available, "Read-only preview verified the selected value's supported type and byte length. Execution is unavailable; no backup or registry change was made.",
            [MakePreview(reference, raw.Kind, raw.Data.Length, false)]);
    }

    private CheckResult<StartupBackupSummary> ListBackups(CancellationToken token)
    {
        using var ownership = scope.LeaseOwnership();
        using var root = StartupActionNative.OpenRead(scope.JournalBase, allowMissing: true);
        if (root.MissingComponents.Length != 0) return CheckResult<StartupBackupSummary>.Empty(CheckState.Available,
            "No recovery journal exists. This version creates no backups. Registration absence is never evidence of a committed action; unjournaled files are not recoverable backups.");
        var items = new List<StartupBackupSummary>(); int failures = 0;
        string sid = StartupActionPayloadCodec.CurrentSid();
        try
        {
            foreach (string id in StartupActionNative.Names(root.Leaf, true, StartupActionPolicy.MaximumBackups, token))
            {
                token.ThrowIfCancellationRequested();
                if (!StartupActionPolicy.IsOpaqueId(id)) { failures++; continue; }
                try
                {
                    using var payload = ReadRecovery(id, sid);
                    items.Add(new(id, payload.Reference, payload.CreatedUtc, payload.State == "Committed"
                        ? "Matching journal/backup structure verified; creator and real action commitment unproven; restoration unavailable"
                        : "Matching restored-record structure verified; creator and real action unproven; replay unavailable"));
                }
                catch (Exception ex) when (SafeFailure(ex)) { failures++; }
            }
        }
        catch (Exception ex) when (SafeFailure(ex)) { failures++; }
        return new(failures == 0 ? CheckState.Available : CheckState.Partial,
            $"{items.Count} verified recovery records; {failures} unverifiable or bounded-out records. No automatic restoration is performed; unjournaled files are excluded.", items);
    }

    private CheckResult<StartupActionPreview> PreviewRestore(string backupId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!StartupActionPolicy.IsOpaqueId(backupId)) return CheckResult<StartupActionPreview>.Empty(CheckState.Unsupported, "Unsupported backup identity.");
        using var ownership = scope.LeaseOwnership();
        using var payload = ReadRecovery(backupId, StartupActionPayloadCodec.CurrentSid());
        if (payload.State != "Committed") return CheckResult<StartupActionPreview>.Empty(CheckState.Unavailable, "The journal does not identify an unrestored committed action; replay is blocked.");
        using var key = StartupActionNative.OpenRead(scope.SourcePath(payload.Reference.Source), allowMissing: true);
        if (key.MissingComponents.Length != 0) return CheckResult<StartupActionPreview>.Empty(CheckState.Unavailable, "The original registration source is absent; source creation is unsupported.");
        using var present = StartupActionNative.ReadSource(key.Leaf, payload.Reference.ValueName);
        if (present != null) return CheckResult<StartupActionPreview>.Empty(CheckState.Unavailable, "An existing registration blocks restoration; it will never be overwritten.");
        token.ThrowIfCancellationRequested();
        return new(CheckState.Available, "Matching recovery structure verified; creator identity and real action commitment are unproven. Restoration remains unavailable.",
            [MakePreview(payload.Reference, payload.Kind, payload.Data.Length, true)]);
    }

    private StartupActionPayload ReadRecovery(string id, string sid)
    {
        using var key = StartupActionNative.OpenRead(scope.JournalPath(id));
        byte[] cipher = StartupActionNative.ReadJournal(key.Leaf);
        StartupActionPayload? payload = null;
        try
        {
            payload = StartupActionPayloadCodec.Decode(cipher, scope, sid, "Committed", "Restored");
            if (payload.BackupId != id) throw new InvalidDataException("Recovery identity mismatch.");
            using var backup = store.Open(id, sid);
            if (!StartupActionPayloadCodec.SameOriginal(payload, backup.Payload)) throw new InvalidDataException("Recovery evidence mismatch.");
            var result = payload; payload = null; return result;
        }
        finally { payload?.Dispose(); CryptographicOperations.ZeroMemory(cipher); }
    }

    private static StartupActionPreview MakePreview(StartupRegistrationReference reference, uint kind, int bytes, bool restore) =>
        new(Guid.NewGuid().ToString("N"), reference, DateTimeOffset.UtcNow, StartupActionPolicy.ValueKind(kind), bytes,
            restore ? "Preview only: a future reviewed action would restore this registration only if absent; execution is unavailable."
                    : "Preview only: a future reviewed action would remove only this selected current-user registration; execution is unavailable.",
            "Current-user metadata read only; no elevation, command execution or registry writes.",
            "No backup or recovery guarantee is created by a preview. RunOnce restoration may replay one-time work. Other startup sources, policy and running processes are unaffected; global disablement is not established.");

    private Task<StartupActionResult> ApplyAsync(string previewToken, bool restore, CancellationToken token)
    {
        // Unconditionally gated in BOTH production and fixture instances. No preview token authorizes a write.
        // A later separately reviewed implementation must prove no-follow exact-object TxR binding first.
        return Task.FromResult(token.IsCancellationRequested || lifetimeToken.IsCancellationRequested
            ? Result(StartupActionOutcome.CancelledBeforeCommit, "Cancelled before any operation; no registration or backup changed.")
            : Result(StartupActionOutcome.Unavailable, "Startup execution is unavailable: exact-object no-follow transaction binding and durable recovery have not been validated. Use Windows Settings; no registration or backup changed."));
    }

    private async Task<CheckResult<T>> ReadAsync<T>(Func<CancellationToken, CheckResult<T>> operation, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return CheckResult<T>.Empty(CheckState.Unsupported, "Startup registration actions require Windows; no state was simulated.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeToken);
        try
        {
            await operations.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                var result = await Task.Run(() => operation(stop.Token), CancellationToken.None).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally { operations.Release(); }
        }
        catch (OperationCanceledException) { return CheckResult<T>.Empty(CheckState.Cancelled, "Startup preview/inventory cancelled; no registration changed."); }
        catch (Exception ex) when (SafeFailure(ex))
        { return CheckResult<T>.Empty(CheckState.Unavailable, $"Startup metadata/recovery could not be verified (code 0x{ex.HResult:X8}); no state change or automatic recovery was attempted."); }
    }
    private static bool SafeFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or CryptographicException or NotSupportedException or ArgumentException or
        InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException;
    private static StartupActionResult Result(StartupActionOutcome outcome, string message, string? backupId = null) =>
        new(outcome, message, backupId, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        // Active work owns its private decrypted buffers and native leases. This only requests a
        // pre-commit stop; it never closes an active transaction/file handle or kills a worker.
        lifetime.Cancel();
        // Do not dispose the semaphore or lifetime source while an operation can still be using it.
    }
}
