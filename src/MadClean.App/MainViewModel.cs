using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed record ResultRow(string Name, string Detail, string State);
public sealed record DiskRow(string Name, string Detail, double UsedPercent, long FreeBytes, long TotalBytes)
{ public string Title => Name; }

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly IComputerInventory inventory = new WindowsInventory();
    private readonly ActivityLog log = new(ActivityLogPath());
    private CancellationTokenSource? cancellation;
    private bool busy;
    private int selectedPage;
    private string status = "Waiting for first inspection. Nothing has been checked.";
    private string diskSummary = "Not checked", cleanupSummary = "Not checked", startupSummary = "Not checked", securitySummary = "Unknown", updateSummary = "Not checked";
    private string diskMessage = "Disk capacity has not been checked.", cleanupMessage = "Cleanup has not been scanned.", startupMessage = "Startup entries have not been queried.", securityMessage = "Defender has not been queried.", appsMessage = "WinGet has not been queried.", systemMessage = "Windows Update has not been queried.", logMessage = @"Log: %LocalAppData%\MadClean\activity.jsonl";
    private static string ActivityLogPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.IsPathFullyQualified(root) ? Path.Combine(root, "MadClean", "activity.jsonl") : string.Empty;
    }
    public MainViewModel()
    {
        ScanCommand = new AsyncCommand(() => RunAsync(false), () => !IsBusy);
        UpdatesCommand = new AsyncCommand(() => RunAsync(true), () => !IsBusy);
        CancelCommand = new ActionCommand(Cancel, () => IsBusy);
        InitializeMadCleanUpdates();
        InitializeDiscovery();
        InitializeFirmwareDiscovery();
        InitializeHardwareDiscovery();
        InitializeOverview();
        InitializeAdditionalDiscovery();
        InitializeGuidedWindowsTools();
        InitializeSystemTools();
        InitializeStartupActions();
        InitializeWerMetadata();
        InitializeManufacturerServiceFacts();
    }
    public ICommand ScanCommand { get; }
    public ICommand UpdatesCommand { get; }
    public ICommand CancelCommand { get; }
    public ObservableCollection<ResultRow> CleanupRows { get; } = [];
    public ObservableCollection<string> CleanupCategories { get; } = [];
    public ObservableCollection<ResultRow> StartupRows { get; } = [];
    public ObservableCollection<ResultRow> SecurityRows { get; } = [];
    public ObservableCollection<ResultRow> AppRows { get; } = [];
    public ObservableCollection<ResultRow> SystemRows { get; } = [];
    public ObservableCollection<DiskRow> Disks { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    public bool IsBusy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public int SelectedPage { get => selectedPage; set { if (selectedPage == value) return; Set(ref selectedPage, value); NotifyShellNavigation(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string DiskSummary { get => diskSummary; private set => Set(ref diskSummary, value); }
    public string CleanupSummary { get => cleanupSummary; private set => Set(ref cleanupSummary, value); }
    public string StartupSummary { get => startupSummary; private set => Set(ref startupSummary, value); }
    public string SecuritySummary { get => securitySummary; private set => Set(ref securitySummary, value); }
    public string UpdateSummary { get => updateSummary; private set => Set(ref updateSummary, value); }
    public string DiskMessage { get => diskMessage; private set => Set(ref diskMessage, value); }
    public string CleanupMessage { get => cleanupMessage; private set => Set(ref cleanupMessage, value); }
    public string StartupMessage { get => startupMessage; private set => Set(ref startupMessage, value); }
    public string SecurityMessage { get => securityMessage; private set => Set(ref securityMessage, value); }
    public string AppsMessage { get => appsMessage; private set => Set(ref appsMessage, value); }
    public string SystemMessage { get => systemMessage; private set => Set(ref systemMessage, value); }
    public string LogMessage { get => logMessage; private set => Set(ref logMessage, value); }
    public void Cancel() => cancellation?.Cancel();
    public void Shutdown()
    {
        Cancel();
        // The service owns in-flight handles; disposal only cancels work before commit and
        // clears unused private previews. It never force-stops a Windows transaction.
        startupActions.Dispose();
        (manufacturerServiceFacts as IDisposable)?.Dispose();
        (firmwareReleaseHistory as IDisposable)?.Dispose();
    }

    private async Task RunAsync(bool online)
    {
        if (IsBusy) return;
        using var source = new CancellationTokenSource(); cancellation = source; IsBusy = true;
        Status = online ? "Checking trusted update providers…" : "Reading local inventory…";
        Event(ActivityArea.Session, ActivityOutcome.Started);
        try
        {
            if (online)
            {
                AppRows.Clear(); SystemRows.Clear(); ResetUpdateCategoryResults(); UpdateSummary = "Checking…";
                AppsMessage = SystemMessage = "Checking…";
                await Task.WhenAll(LoadApps(source.Token), LoadSystem(source.Token));
                UpdateSummary = "See provider results";
            }
            else
            {
                BeginStorageOverview();
                BeginInspectionEvidence();
                InvalidateCleanupEvidenceSnapshot();
                Disks.Clear(); CleanupRows.Clear(); CleanupCategories.Clear(); StartupRows.Clear(); SecurityRows.Clear();
                DiskSummary = CleanupSummary = StartupSummary = SecuritySummary = "Checking…";
                DiskMessage = CleanupMessage = StartupMessage = SecurityMessage = "Checking…";
                await Task.WhenAll(LoadDisks(source.Token), LoadCleanup(source.Token), LoadStartup(source.Token), LoadSecurity(source.Token));
            }
            Status = $"Checks finished · {DateTime.Now:t}. Review each provider's results and limitations.";
            Event(ActivityArea.Session, ActivityOutcome.Completed);
        }
        catch (OperationCanceledException) { if (online) UpdateSummary = "Cancelled"; else FinishPendingInspectionEvidence(CheckState.Cancelled); Status = "Cancelled · completed results remain; unfinished checks are marked Cancelled."; Event(ActivityArea.Session, ActivityOutcome.Cancelled); }
        catch (Exception) { if (online) UpdateSummary = "Failed"; else FinishPendingInspectionEvidence(CheckState.Failed); Status = "An unexpected query failure occurred. Retry the check; no successful result is claimed."; Event(ActivityArea.Session, ActivityOutcome.Failed); }
        finally { cancellation = null; IsBusy = false; }
    }
    private async Task LoadDisks(CancellationToken token) => await Guard(ActivityArea.Disks, async () =>
    {
        var result = await inventory.DisksAsync(token); token.ThrowIfCancellationRequested();
        foreach (var d in result.Items) Disks.Add(new(d.Name, $"{Display.Bytes(d.FreeBytes)} free / {Display.Bytes(d.TotalBytes)}", d.TotalBytes == 0 ? 0 : 100d * (d.TotalBytes - d.FreeBytes) / d.TotalBytes, d.FreeBytes, d.TotalBytes));
        DiskSummary = (result.Items.Count == 0 ? "Unknown" : Display.Bytes(result.Items.Sum(d => d.FreeBytes))) + (result.State == CheckState.Partial ? " (partial)" : "");
        CompleteStorageEvidence(result, token);
        DiskMessage = Stamp(result.State, result.Message); CompleteStorageOverview(result.State); Event(ActivityArea.Disks, Outcome(result.State));
    }, text => { DiskMessage = text; DiskSummary = text; CompleteStorageOverview(text.StartsWith("Cancelled", StringComparison.Ordinal) ? CheckState.Cancelled : CheckState.Failed); });
    private async Task LoadCleanup(CancellationToken token) => await Guard(ActivityArea.Cleanup, async () =>
    {
        var preferences = LoadCleanupPreferences();
        if (preferences.Error != null)
        {
            cleanupPreview = Array.Empty<CleanupCandidate>();
            CleanupSummary = "Unavailable"; CleanupMessage = preferences.Error;
            Event(ActivityArea.Cleanup, ActivityOutcome.Failed); return;
        }
        var report = await new CleanupScanner().ScanAsync(inventory.CleanupLocations, token, preferences.Items); token.ThrowIfCancellationRequested();
        var completedUtc = DateTimeOffset.UtcNow;
        cleanupPreview = report.Candidates.ToArray();
        foreach (var group in report.Candidates.GroupBy(c => c.Category)) CleanupCategories.Add($"{group.Key}: {group.Count()} files · {Display.Bytes(group.Sum(c => c.Bytes))}");
        int added = 0;
        foreach (var c in report.Candidates)
        {
            token.ThrowIfCancellationRequested();
            CleanupRows.Add(new(c.Path, $"{Display.Bytes(c.Bytes)} · modified {c.ModifiedUtc:u} · active use unknown", c.Category));
            if (++added % 128 == 0) await Task.Delay(1, token);
        }
        foreach (var issue in report.Issues) CleanupRows.Add(new(issue.Path, issue.Reason, issue.Category + " · excluded/error"));
        CleanupSummary = Display.Bytes(report.TotalBytes) + (report.Truncated || report.Issues.Count > 0 ? " (partial)" : "");
        var state = report.Truncated || report.Issues.Count > 0 ? CheckState.Partial : CheckState.Available;
        CleanupMessage = Stamp(state, $"{report.Candidates.Count} candidates; {report.ExcludedCount} exclusions; {report.Issues.Count} recorded errors. Check reported paths/permissions or close active files before retrying. {(report.Truncated ? "Safety limit reached; inventory incomplete." : "Scan finished.")} Nothing was deleted.");
        PublishCleanupEvidenceSnapshot(report, preferences.Items, completedUtc);
        Event(ActivityArea.Cleanup, Outcome(state));
    }, text => { CleanupMessage = text; CleanupSummary = text; });
    private async Task LoadStartup(CancellationToken token) => await Guard(ActivityArea.Startup, async () =>
    {
        var result = await inventory.StartupAsync(token); token.ThrowIfCancellationRequested();
        foreach (var e in result.Items) StartupRows.Add(new(e.Name, $"Publisher: {e.Publisher}. {e.Detail}", e.Source));
        StartupSummary = result.Items.Count + (result.State == CheckState.Partial ? " (partial)" : "");
        StartupMessage = Stamp(result.State, result.Message); Event(ActivityArea.Startup, Outcome(result.State));
    }, text => { StartupMessage = text; StartupSummary = text; });
    private async Task LoadSecurity(CancellationToken token) => await Guard(ActivityArea.Defender, async () =>
    {
        var result = await inventory.DefenderAsync(token); token.ThrowIfCancellationRequested();
        SecuritySummary = result.State.ToString();
        foreach (var s in result.Items)
        {
            string Flag(bool? v) => v.HasValue ? v.Value ? "Enabled" : "Disabled" : "Unknown";
            SecuritySummary = "Real-time: " + Flag(s.RealTimeEnabled);
            SecurityRows.Add(new("Antivirus", "Defender provider", Flag(s.AntivirusEnabled)));
            SecurityRows.Add(new("Real-time protection", "Current provider setting", Flag(s.RealTimeEnabled)));
            SecurityRows.Add(new("Defender service", s.Mode, Flag(s.ServiceEnabled)));
            SecurityRows.Add(new("Signatures", s.SignatureVersion, s.SignatureUpdated));
            SecurityRows.Add(new("Detection history", $"{s.Findings.Count} recorded entries; not a new scan", s.FindingsState));
            foreach (var f in s.Findings) SecurityRows.Add(new("Threat ID " + f.ThreatId, "Last status change: " + f.LastStatusChange, "Action success: " + f.ActionSuccess));
        }
        CompleteDefenderEvidence(result, token);
        SecurityMessage = Stamp(result.State, result.Message); Event(ActivityArea.Defender, Outcome(result.State));
    }, text => { SecurityMessage = text; SecuritySummary = text; });
    private async Task LoadApps(CancellationToken token) => await Guard(ActivityArea.Apps, async () =>
    {
        var result = await inventory.AppsAsync(token); token.ThrowIfCancellationRequested();
        foreach (var a in result.Items) AppRows.Add(new(a.Name, $"{a.Id} · {a.InstalledVersion} → {a.AvailableVersion}", $"{a.Status} · {a.Source}"));
        AppsMessage = Stamp(result.State, result.Message); Event(ActivityArea.Apps, Outcome(result.State));
    }, text => AppsMessage = text);
    private async Task LoadSystem(CancellationToken token) => await Guard(ActivityArea.SystemUpdates, async () =>
    {
        var result = await inventory.SystemUpdatesAsync(token); token.ThrowIfCancellationRequested();
        foreach (var u in result.Items) SystemRows.Add(new(u.Title, u.Identity, $"{u.Kind} · offered · reboot flag {u.RebootRequired}"));
        SystemMessage = Stamp(result.State, result.Message + $" {result.Items.Count} offered updates returned."); PublishUpdateCategories(result); Event(ActivityArea.SystemUpdates, Outcome(result.State));
    }, text => { SystemMessage = text; MarkUpdateCategoryFailure(text); });

    private async Task Guard(ActivityArea area, Func<Task> action, Action<string> markFailure)
    {
        try { await action(); }
        catch (OperationCanceledException) { InterruptInspectionEvidence(area, CheckState.Cancelled); markFailure("Cancelled"); Event(area, ActivityOutcome.Cancelled); throw; }
        catch (Exception) { InterruptInspectionEvidence(area, CheckState.Failed); markFailure("Failed · provider query could not complete; retry and check Windows access."); Event(area, ActivityOutcome.Failed); }
    }
    private static ActivityOutcome Outcome(CheckState state) => state switch
    { CheckState.Available => ActivityOutcome.Completed, CheckState.Partial => ActivityOutcome.Partial, CheckState.Cancelled => ActivityOutcome.Cancelled, _ => ActivityOutcome.Failed };
    private static string Stamp(CheckState state, string message) => $"{state} · {DateTime.Now:t} · {message}";
    private void Event(ActivityArea area, ActivityOutcome outcome)
    {
        Activity.Insert(0, $"{DateTime.Now:T}   {area} · {outcome}");
        if (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
        if (!log.TryWrite(area, outcome)) LogMessage = @"Activity log unavailable: check write permission in %LocalAppData%\MadClean. In-memory events remain visible.";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
}

internal sealed class ActionCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
internal sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await execute(); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
