using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IStartupRegistrationActions startupActions = new StartupRegistrationActions();
    private StartupRegistrationReference? selectedStartupRegistration, startupPreviewRegistration;
    private StartupBackupSummary? selectedStartupBackup, startupPreviewBackup;
    private StartupActionPreview? startupActionPreview;
    private bool startupPreviewIsRestore, startupRegistrationReading, startupBackupReading;
    private DateTimeOffset? startupRegistrationAttemptedUtc, startupRegistrationReceivedUtc, startupRegistrationInterruptedUtc;
    private DateTimeOffset? startupBackupAttemptedUtc, startupBackupReceivedUtc, startupBackupInterruptedUtc;
    private DateTimeOffset? startupPreviewAttemptedUtc, startupPreviewReceivedUtc;
    private CheckState startupRegistrationState = CheckState.Unknown, startupBackupState = CheckState.Unknown;
    private CheckState startupPreviewInventoryState = CheckState.Unknown;
    private string startupActionMessage = "Not checked. The current-user Run/RunOnce recovery interface is separate from the broader startup inventory.";
    public ObservableCollection<StartupRegistrationReference> StartupRegistrations { get; } = [];
    public ObservableCollection<StartupBackupSummary> StartupBackups { get; } = [];
    public ICommand ReadStartupRegistrationsCommand { get; private set; } = null!;
    public ICommand ReadStartupBackupsCommand { get; private set; } = null!;
    public ICommand PreviewStartupDisableCommand { get; private set; } = null!;
    public ICommand PreviewStartupRestoreCommand { get; private set; } = null!;
    public string StartupActionMessage { get => startupActionMessage; private set => Set(ref startupActionMessage, value); }
    private StartupRegistrationReference? CurrentStartupRegistration => selectedStartupRegistration is { } entry &&
        StartupRegistrations.Any(row => ReferenceEquals(row, entry)) ? entry : null;
    private StartupBackupSummary? CurrentStartupBackup => selectedStartupBackup is { } entry &&
        StartupBackups.Any(row => ReferenceEquals(row, entry)) ? entry : null;
    public StartupRegistrationReference? SelectedStartupRegistration
    {
        get => CurrentStartupRegistration;
        set
        {
            if (value != null && (IsBusy || !StartupRegistrations.Any(row => ReferenceEquals(row, value))))
            { PropertyChanged?.Invoke(this, new(nameof(SelectedStartupRegistration))); return; }
            if (ReferenceEquals(selectedStartupRegistration, value)) return;
            selectedStartupRegistration = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedStartupRegistration)));
            ClearStartupActionPreview();
        }
    }
    public StartupBackupSummary? SelectedStartupBackup
    {
        get => CurrentStartupBackup;
        set
        {
            if (value != null && (IsBusy || !StartupBackups.Any(row => ReferenceEquals(row, value))))
            { PropertyChanged?.Invoke(this, new(nameof(SelectedStartupBackup))); return; }
            if (ReferenceEquals(selectedStartupBackup, value)) return;
            selectedStartupBackup = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedStartupBackup)));
            ClearStartupActionPreview();
        }
    }
    private StartupActionPreview? CurrentStartupActionPreview => startupActionPreview != null &&
        (startupPreviewIsRestore
            ? startupPreviewBackup != null && ReferenceEquals(CurrentStartupBackup, startupPreviewBackup)
            : startupPreviewRegistration != null && ReferenceEquals(CurrentStartupRegistration, startupPreviewRegistration))
        ? startupActionPreview : null;
    public string StartupRegistrationQueryDetails => StartupSourceQueryDetails("Covered current-user registrations", startupRegistrationAttemptedUtc,
        startupRegistrationReceivedUtc, startupRegistrationInterruptedUtc, startupRegistrationReading, startupRegistrationState) +
        "\nRun/RunOnce names only; coverage does not establish enabled execution or global startup state.";
    public string StartupBackupQueryDetails => StartupSourceQueryDetails("Existing recovery metadata", startupBackupAttemptedUtc,
        startupBackupReceivedUtc, startupBackupInterruptedUtc, startupBackupReading, startupBackupState) +
        "\nStructural records are not proof of creator identity, a committed action or a recoverable change. No record is created by this read.";
    private static string StartupSourceQueryDetails(string source, DateTimeOffset? attempted, DateTimeOffset? received,
        DateTimeOffset? interrupted, bool reading, CheckState state) => attempted is not { } start
        ? source + ": Not checked. No request has started."
        : $"{source} · app attempt UTC: {start:u}\n" + (reading
            ? "Reading metadata; no completed response from this attempt."
            : received is { } end
                ? $"App response received UTC: {end:u}; source state: {state}. Receipt is not proof of complete coverage."
                : $"Interruption observed UTC: {interrupted:u}; state: {state}. No completed source response is displayed.") +
            "\nThese are request/response times, not the last registry change or record creation time.";
    public string StartupActionDetails => CurrentStartupActionPreview is { } preview
        ? $"Read-only hypothetical {(startupPreviewIsRestore ? "recovery" : "registration-change")} preview; execution is unavailable.\nSource: {preview.Reference.Source}\nValue name: {preview.Reference.ValueName}\nApp preview attempt UTC: {startupPreviewAttemptedUtc:u}; response received UTC: {startupPreviewReceivedUtc:u}\nSelected metadata observed UTC: {preview.ObservedUtc:u}\nSource inventory coverage: {startupPreviewInventoryState}; a selected value preview does not make its inventory complete.\nOriginal data kind: {preview.ValueKind}; private byte count: {preview.ByteCount}\n\nHypothetical effect: {preview.ExactEffect}\nPermissions: {preview.Permissions}\nRecovery limits: {preview.RecoveryLimits}\n\nThis observes supported metadata only. Creator identity, real action commitment and enabled execution are not established. No command, backup content or preview identifier is displayed or executed; no change or recovery guarantee is created."
        : "Select one current registration or recovery record, then explicitly request its read-only preview. Only HKCU Run/RunOnce are within this design. No command is executed and no startup setting changes on this page. Reversible registration recovery cannot undo what a startup program has done or guarantee it will run later.";
    private void InitializeStartupActions()
    {
        ReadStartupRegistrationsCommand = new AsyncCommand(ReadStartupRegistrations, () => !IsBusy);
        ReadStartupBackupsCommand = new AsyncCommand(ReadStartupBackups, () => !IsBusy);
        PreviewStartupDisableCommand = new AsyncCommand(PreviewStartupDisable, () => !IsBusy && CurrentStartupRegistration != null);
        PreviewStartupRestoreCommand = new AsyncCommand(PreviewStartupRestore, () => !IsBusy && CurrentStartupBackup != null);
    }
    private void ClearStartupActionPreview()
    {
        startupActionPreview = null; startupPreviewRegistration = null; startupPreviewBackup = null;
        startupPreviewAttemptedUtc = startupPreviewReceivedUtc = null; startupPreviewInventoryState = CheckState.Unknown;
        PropertyChanged?.Invoke(this, new(nameof(StartupActionDetails)));
        CommandManager.InvalidateRequerySuggested();
    }
    private void ClearCurrentStartupRegistrations()
    {
        StartupRegistrations.Clear(); SelectedStartupRegistration = null; ClearStartupActionPreview();
    }
    private void ClearCurrentStartupBackups()
    {
        StartupBackups.Clear(); SelectedStartupBackup = null; ClearStartupActionPreview();
    }
    private Task ReadStartupRegistrations() => RunDiscoveryAsync(ActivityArea.StartupRecovery, "Reading selected-action startup registration coverage", async token =>
    {
        ClearCurrentStartupRegistrations();
        var attempted = DateTimeOffset.UtcNow;
        startupRegistrationAttemptedUtc = attempted; startupRegistrationReceivedUtc = startupRegistrationInterruptedUtc = null;
        startupRegistrationReading = true; startupRegistrationState = CheckState.Unknown; NotifyStartupSourceQueries();
        var result = await startupActions.InventoryAsync(token); token.ThrowIfCancellationRequested();
        var received = DateTimeOffset.UtcNow;
        bool positive = result.State is CheckState.Available or CheckState.Partial;
        if (!Enum.IsDefined(result.State) || received < attempted || result.Items.Count > StartupActionPolicy.MaximumInventoryEntries ||
            !positive && result.Items.Count != 0 || result.Items.Any(entry => !StartupActionPolicy.IsValidReference(entry)))
            throw new InvalidOperationException("Startup registration response scope or state is inconsistent.");
        foreach (var entry in result.Items) StartupRegistrations.Add(entry);
        startupRegistrationReceivedUtc = received; startupRegistrationReading = false; startupRegistrationState = result.State;
        StartupActionMessage = Stamp(result.State, result.Message); NotifyStartupSourceQueries();
        return result.State;
    }, error =>
    {
        ClearCurrentStartupRegistrations();
        startupRegistrationReading = false; startupRegistrationReceivedUtc = null; startupRegistrationInterruptedUtc = DateTimeOffset.UtcNow;
        startupRegistrationState = cancellation?.IsCancellationRequested == true ? CheckState.Cancelled : CheckState.Failed;
        StartupActionMessage = error + " No current registration rows or preview remain from this attempt."; NotifyStartupSourceQueries();
    });
    private Task ReadStartupBackups() => RunDiscoveryAsync(ActivityArea.StartupRecovery, "Reading MadClean recovery record metadata", async token =>
    {
        ClearCurrentStartupBackups();
        var attempted = DateTimeOffset.UtcNow;
        startupBackupAttemptedUtc = attempted; startupBackupReceivedUtc = startupBackupInterruptedUtc = null;
        startupBackupReading = true; startupBackupState = CheckState.Unknown; NotifyStartupSourceQueries();
        var result = await startupActions.ListBackupsAsync(token); token.ThrowIfCancellationRequested();
        var received = DateTimeOffset.UtcNow;
        bool positive = result.State is CheckState.Available or CheckState.Partial;
        if (!Enum.IsDefined(result.State) || received < attempted || result.Items.Count > StartupActionPolicy.MaximumBackups ||
            !positive && result.Items.Count != 0 || result.Items.Any(entry => entry == null ||
                !StartupActionPolicy.IsValidReference(entry.Reference) || !StartupActionPolicy.IsOpaqueId(entry.BackupId)))
            throw new InvalidOperationException("Startup recovery response scope or state is inconsistent.");
        foreach (var entry in result.Items) StartupBackups.Add(entry);
        startupBackupReceivedUtc = received; startupBackupReading = false; startupBackupState = result.State;
        StartupActionMessage = Stamp(result.State, result.Message); NotifyStartupSourceQueries();
        return result.State;
    }, error =>
    {
        ClearCurrentStartupBackups();
        startupBackupReading = false; startupBackupReceivedUtc = null; startupBackupInterruptedUtc = DateTimeOffset.UtcNow;
        startupBackupState = cancellation?.IsCancellationRequested == true ? CheckState.Cancelled : CheckState.Failed;
        StartupActionMessage = error + " No current recovery rows or preview remain from this attempt."; NotifyStartupSourceQueries();
    });
    private void NotifyStartupSourceQueries()
    {
        PropertyChanged?.Invoke(this, new(nameof(StartupRegistrationQueryDetails)));
        PropertyChanged?.Invoke(this, new(nameof(StartupBackupQueryDetails)));
        CommandManager.InvalidateRequerySuggested();
    }
    private Task PreviewStartupDisable() => ReadStartupActionPreview(false);
    private Task PreviewStartupRestore() => ReadStartupActionPreview(true);
    private Task ReadStartupActionPreview(bool restore) => RunDiscoveryAsync(ActivityArea.StartupRecovery, "Preparing read-only startup change preview", async token =>
    {
        var registration = CurrentStartupRegistration; var backup = CurrentStartupBackup;
        var sourceState = restore ? startupBackupState : startupRegistrationState;
        ClearStartupActionPreview();
        if (restore && backup == null || !restore && registration == null)
        { StartupActionMessage = "Select a current source row first. No metadata preview or change was requested."; return CheckState.Unknown; }
        var attempted = DateTimeOffset.UtcNow;
        var result = restore
            ? await startupActions.PreviewRestoreAsync(backup!.BackupId, token)
            : await startupActions.PreviewDisableAsync(registration!, token);
        token.ThrowIfCancellationRequested();
        var received = DateTimeOffset.UtcNow;
        if (restore ? !ReferenceEquals(CurrentStartupBackup, backup) : !ReferenceEquals(CurrentStartupRegistration, registration))
        { StartupActionMessage = "Selection changed. No current preview was accepted; request a new preview for the current source row. No change occurred."; return CheckState.Cancelled; }
        if (!StartupPreviewEvidence.IsConsistent(result, restore ? backup!.Reference : registration, attempted, received))
        {
            var state = Enum.IsDefined(result.State) && result.State != CheckState.Available ? result.State : CheckState.Unknown;
            // Current provider failures use fixed text (or a numeric native code), never private command data.
            // Preserve actionable reasons only for well-formed empty failure responses.
            bool emptyFailure = Enum.IsDefined(result.State) && result.State == state && result.Items.Count == 0 &&
                (state is CheckState.Unknown or CheckState.Unavailable or CheckState.Unsupported or CheckState.Failed or CheckState.Cancelled);
            string reason = emptyFailure && result.Message is { Length: > 0 and <= 1024 } message &&
                !message.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                    ? message : "No consistent current metadata preview was accepted. Refresh the covered source, check permissions and select the row again.";
            StartupActionMessage = Stamp(state, reason + " No registration or recovery change occurred.");
            return state;
        }
        startupPreviewIsRestore = restore; startupActionPreview = result.Items[0];
        startupPreviewRegistration = restore ? null : registration; startupPreviewBackup = restore ? backup : null;
        startupPreviewAttemptedUtc = attempted; startupPreviewReceivedUtc = received; startupPreviewInventoryState = sourceState;
        PropertyChanged?.Invoke(this, new(nameof(StartupActionDetails)));
        StartupActionMessage = "Available · selected supported metadata was observed for this current row. Inventory coverage remains " + sourceState + "; execution remains disabled. No backup or registration change was requested.";
        return CheckState.Available;
    }, error => { ClearStartupActionPreview(); StartupActionMessage = error + " No registration change was requested."; });
}
