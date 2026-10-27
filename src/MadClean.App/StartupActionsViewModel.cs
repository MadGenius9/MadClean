using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IStartupRegistrationActions startupActions = new StartupRegistrationActions();
    private StartupRegistrationReference? selectedStartupRegistration;
    private StartupBackupSummary? selectedStartupBackup;
    private StartupActionPreview? startupActionPreview;
    private bool startupPreviewIsRestore;
    private string startupActionMessage = "Not checked. The current-user Run/RunOnce recovery interface is separate from the broader startup inventory.";
    public ObservableCollection<StartupRegistrationReference> StartupRegistrations { get; } = [];
    public ObservableCollection<StartupBackupSummary> StartupBackups { get; } = [];
    public ICommand ReadStartupRegistrationsCommand { get; private set; } = null!;
    public ICommand ReadStartupBackupsCommand { get; private set; } = null!;
    public ICommand PreviewStartupDisableCommand { get; private set; } = null!;
    public ICommand PreviewStartupRestoreCommand { get; private set; } = null!;
    public string StartupActionMessage { get => startupActionMessage; private set => Set(ref startupActionMessage, value); }
    public StartupRegistrationReference? SelectedStartupRegistration
    {
        get => selectedStartupRegistration;
        set { if (IsBusy) return; Set(ref selectedStartupRegistration, value); ClearStartupActionPreview(); }
    }
    public StartupBackupSummary? SelectedStartupBackup
    {
        get => selectedStartupBackup;
        set { if (IsBusy) return; Set(ref selectedStartupBackup, value); ClearStartupActionPreview(); }
    }
    public string StartupActionDetails => startupActionPreview is { } preview
        ? $"Operation: {(startupPreviewIsRestore ? "Restore one backed-up registration" : "Remove one startup registration after a recovery backup")}\nSource: {preview.Reference.Source}\nValue name: {preview.Reference.ValueName}\nObserved UTC: {preview.ObservedUtc:u}\nOriginal data kind: {preview.ValueKind}; private byte count: {preview.ByteCount}\n\nExact effect: {preview.ExactEffect}\nPermissions: {preview.Permissions}\nRecovery limits: {preview.RecoveryLimits}\n\nExecution is disabled pending independent Windows validation. Raw commands and backup contents are never displayed or executed."
        : "Select one registration or recovery record, then request its preview. Only HKCU Run/RunOnce are within this action design. No command is executed and no startup setting changes on this page. Reversible registration recovery cannot undo what a startup program has done or guarantee it will run later.";
    private void InitializeStartupActions()
    {
        ReadStartupRegistrationsCommand = new AsyncCommand(ReadStartupRegistrations, () => !IsBusy);
        ReadStartupBackupsCommand = new AsyncCommand(ReadStartupBackups, () => !IsBusy);
        PreviewStartupDisableCommand = new AsyncCommand(PreviewStartupDisable, () => !IsBusy && selectedStartupRegistration != null);
        PreviewStartupRestoreCommand = new AsyncCommand(PreviewStartupRestore, () => !IsBusy && selectedStartupBackup != null);
    }
    private void ClearStartupActionPreview()
    {
        startupActionPreview = null;
        PropertyChanged?.Invoke(this, new(nameof(StartupActionDetails)));
        CommandManager.InvalidateRequerySuggested();
    }
    private Task ReadStartupRegistrations() => RunDiscoveryAsync(ActivityArea.StartupRecovery, "Reading selected-action startup registration coverage", async token =>
    {
        StartupRegistrations.Clear(); selectedStartupRegistration = null; ClearStartupActionPreview();
        PropertyChanged?.Invoke(this, new(nameof(SelectedStartupRegistration)));
        var result = await startupActions.InventoryAsync(token); token.ThrowIfCancellationRequested();
        foreach (var entry in result.Items) StartupRegistrations.Add(entry);
        StartupActionMessage = Stamp(result.State, result.Message);
        return result.State;
    }, error => StartupActionMessage = error);
    private Task ReadStartupBackups() => RunDiscoveryAsync(ActivityArea.StartupRecovery, "Reading MadClean recovery record metadata", async token =>
    {
        StartupBackups.Clear(); selectedStartupBackup = null; ClearStartupActionPreview();
        PropertyChanged?.Invoke(this, new(nameof(SelectedStartupBackup)));
        var result = await startupActions.ListBackupsAsync(token); token.ThrowIfCancellationRequested();
        foreach (var entry in result.Items) StartupBackups.Add(entry);
        StartupActionMessage = Stamp(result.State, result.Message);
        return result.State;
    }, error => StartupActionMessage = error);
    private Task PreviewStartupDisable() => ReadStartupActionPreview(false);
    private Task PreviewStartupRestore() => ReadStartupActionPreview(true);
    private Task ReadStartupActionPreview(bool restore) => RunDiscoveryAsync(ActivityArea.StartupRecovery, "Preparing read-only startup change preview", async token =>
    {
        var registration = selectedStartupRegistration; var backup = selectedStartupBackup;
        ClearStartupActionPreview();
        if (restore && backup == null || !restore && registration == null) return CheckState.Unknown;
        var result = restore
            ? await startupActions.PreviewRestoreAsync(backup!.BackupId, token)
            : await startupActions.PreviewDisableAsync(registration!, token);
        token.ThrowIfCancellationRequested();
        startupPreviewIsRestore = restore; startupActionPreview = result.Items.FirstOrDefault();
        PropertyChanged?.Invoke(this, new(nameof(StartupActionDetails)));
        StartupActionMessage = Stamp(result.State, result.Message) + " No registration change was requested.";
        return result.State;
    }, error => { ClearStartupActionPreview(); StartupActionMessage = error + " No registration change was requested."; });
}
