using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32;

namespace MadClean.App;

public sealed record DiagnosticRangeChoice(string Title, int Days);

public sealed partial class MainViewModel
{
    private readonly IDuplicateFileInventory duplicateInventory = new WindowsDuplicateInventory();
    private readonly IShortcutInventory shortcutInventory = new WindowsShortcutInventory();
    private readonly IDiagnosticsInventory diagnosticsInventory = new WindowsDiagnosticsInventory();
    private string duplicateFolder = "", shortcutFolder = "";
    private string duplicateMessage = "Not checked. Choose a local folder, review the scope, then explicitly scan file contents.";
    private string shortcutMessage = "Not checked. Choose a local folder, then inspect stored .lnk metadata without launching or resolving targets.";
    private string diagnosticsMessage = "Not checked. This reads existing local Application/System event metadata only; no integrity check or repair command runs.";
    private string duplicateCoverage = "No duplicate-file scope has been scanned.", shortcutCoverage = "No shortcut scope has been inspected.", diagnosticsWindow = "No event-log window queried.";
    private string duplicateAttempt = "Not checked", shortcutAttempt = "Not checked", diagnosticsAttempt = "Not checked";
    private DuplicateEntry? selectedDuplicateEntry;
    private ShortcutObservation? selectedShortcutObservation;
    private DiagnosticFinding? selectedDiagnosticFinding;
    private DiagnosticRangeChoice selectedDiagnosticRange = new("Last 7 days", 7);
    public ObservableCollection<DuplicateEntry> DuplicateRows { get; } = [];
    public ObservableCollection<FileReviewIssue> DuplicateIssues { get; } = [];
    public ObservableCollection<ShortcutObservation> ShortcutRows { get; } = [];
    public ObservableCollection<FileReviewIssue> ShortcutIssues { get; } = [];
    public ObservableCollection<DiagnosticFinding> DiagnosticRows { get; } = [];
    public ObservableCollection<string> DiagnosticCoverage { get; } = [];
    public IReadOnlyList<DiagnosticRangeChoice> DiagnosticRanges { get; } = [new("Last 24 hours", 1), new("Last 7 days", 7), new("Last 30 days", 30)];
    public ICommand ChooseDuplicateFolderCommand { get; private set; } = null!;
    public ICommand ScanDuplicatesCommand { get; private set; } = null!;
    public ICommand ChooseShortcutFolderCommand { get; private set; } = null!;
    public ICommand ScanShortcutsCommand { get; private set; } = null!;
    public ICommand ReadDiagnosticsCommand { get; private set; } = null!;
    public string DuplicateFolder { get => duplicateFolder; private set => Set(ref duplicateFolder, value); }
    public string ShortcutFolder { get => shortcutFolder; private set => Set(ref shortcutFolder, value); }
    public string DuplicateMessage { get => duplicateMessage; private set => Set(ref duplicateMessage, value); }
    public string ShortcutMessage { get => shortcutMessage; private set => Set(ref shortcutMessage, value); }
    public string DiagnosticsMessage { get => diagnosticsMessage; private set => Set(ref diagnosticsMessage, value); }
    public string DuplicateCoverage { get => duplicateCoverage; private set => Set(ref duplicateCoverage, value); }
    public string ShortcutCoverage { get => shortcutCoverage; private set => Set(ref shortcutCoverage, value); }
    public string DiagnosticsWindow { get => diagnosticsWindow; private set => Set(ref diagnosticsWindow, value); }
    public string DuplicateAttempt { get => duplicateAttempt; private set => Set(ref duplicateAttempt, value); }
    public string ShortcutAttempt { get => shortcutAttempt; private set => Set(ref shortcutAttempt, value); }
    public string DiagnosticsAttempt { get => diagnosticsAttempt; private set => Set(ref diagnosticsAttempt, value); }
    public DiagnosticRangeChoice SelectedDiagnosticRange
    {
        get => selectedDiagnosticRange;
        set
        {
            if (value == null || !DiagnosticRanges.Contains(value) || selectedDiagnosticRange == value) return;
            Set(ref selectedDiagnosticRange, value);
            InvalidateWerMetadata();
            DiagnosticRows.Clear(); DiagnosticCoverage.Clear(); SelectedDiagnosticFinding = null;
            DiagnosticsMessage = "Time range changed. Choose Read event evidence to query the selected window.";
            DiagnosticsWindow = "Not checked for the new selection.";
            DiagnosticsAttempt = "Not checked for the new selection";
            ResetDiagnosticRecurrence("Time range changed. Read event evidence for the new selection.");
        }
    }
    public DuplicateEntry? SelectedDuplicateEntry
    {
        get => selectedDuplicateEntry;
        set { Set(ref selectedDuplicateEntry, value); PropertyChanged?.Invoke(this, new(nameof(DuplicateDetails))); }
    }
    public ShortcutObservation? SelectedShortcutObservation
    {
        get => selectedShortcutObservation;
        set { Set(ref selectedShortcutObservation, value); PropertyChanged?.Invoke(this, new(nameof(ShortcutDetails))); }
    }
    public DiagnosticFinding? SelectedDiagnosticFinding
    {
        get => selectedDiagnosticFinding;
        set { Set(ref selectedDiagnosticFinding, value); PropertyChanged?.Invoke(this, new(nameof(DiagnosticDetails))); }
    }
    public string DuplicateDetails => selectedDuplicateEntry is { } entry
        ? $"Group: {entry.GroupId}\nExact path: {entry.Path}\nObserved logical bytes: {entry.Bytes}\nFile identity: {entry.Identity}\nRelationship: {entry.Relationship}\nVerification: {entry.Verification}\nAttempt: {DuplicateAttempt}. This is an observation, not deletion eligibility or guaranteed recoverable space. No removal is implemented."
        : "Select a group member to inspect/copy its full path, identity and verification. Byte equality does not establish ownership, purpose or safe removal.";
    public string ShortcutDetails => selectedShortcutObservation is { } entry
        ? $"Shortcut: {entry.ShortcutPath}\nStored target: {entry.TargetPath}\nState: {entry.State}\nReason: {entry.Reason}\nSource: {entry.Source}\nObserved: {entry.CheckedAt:u}\nA local missing target is evidence for review, not proof the shortcut is obsolete. No target is launched and no shortcut is removed."
        : "Select a shortcut to inspect its stored target, source and reason. Network/offline/access/reparse/unsupported contexts remain unknown, not broken.";
    public string DiagnosticDetails => selectedDiagnosticFinding is { } finding
        ? $"Observation: {finding.Observation}\nSource: local Windows Event Log / {finding.Channel} / {finding.Provider}, event {finding.EventId}, schema {finding.Version}, reported level {finding.Level}.\nRecord count in covered window: {finding.Count}; these are records, not unique crashes.\nFirst UTC: {finding.FirstUtc:u}; last UTC: {finding.LastUtc:u}.\nRetained record references: {string.Join(", ", finding.RecordIds)}\nUncertainty: {finding.Uncertainty}\nImpact: {finding.Impact}\nNext step: {finding.NextStep}\n{DiagnosticsWindow}"
        : "Select an observation to inspect source, time range, record references, uncertainty and impact. No records does not establish a healthy system.";
    private void InitializeAdditionalDiscovery()
    {
        selectedDiagnosticRange = DiagnosticRanges.Single(range => range.Days == 7);
        ChooseDuplicateFolderCommand = new ActionCommand(ChooseDuplicateFolder, () => !IsBusy);
        ScanDuplicatesCommand = new AsyncCommand(ScanDuplicates, () => !IsBusy && !string.IsNullOrWhiteSpace(DuplicateFolder));
        ChooseShortcutFolderCommand = new ActionCommand(ChooseShortcutFolder, () => !IsBusy);
        ScanShortcutsCommand = new AsyncCommand(ScanShortcuts, () => !IsBusy && !string.IsNullOrWhiteSpace(ShortcutFolder));
        ReadDiagnosticsCommand = new AsyncCommand(ReadDiagnostics, () => !IsBusy);
        InitializeDiagnosticRecurrence();
        InitializeDuplicatePairReview();
    }
    private void ChooseDuplicateFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose one local folder for read-only content review", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        InvalidateDuplicatePairSnapshot();
        DuplicateFolder = dialog.FolderName; DuplicateRows.Clear(); DuplicateIssues.Clear(); SelectedDuplicateEntry = null;
        DuplicateMessage = "Scope selected. This scan reads file contents; review the exclusions before choosing Scan selected folder.";
        DuplicateCoverage = "Selected scope has not been scanned. System/application/credential/reparse/offline/active/unsupported files are protected by the provider policy.";
        DuplicateAttempt = "Not checked for this selection";
        CommandManager.InvalidateRequerySuggested();
    }
    private void ChooseShortcutFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose one local folder for read-only .lnk inspection", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        ShortcutFolder = dialog.FolderName; ShortcutRows.Clear(); ShortcutIssues.Clear(); SelectedShortcutObservation = null;
        ShortcutMessage = "Scope selected. Choose Inspect shortcuts; no target is launched, searched, tracked or saved.";
        ShortcutCoverage = "Selected scope has not been inspected."; ShortcutAttempt = "Not checked for this selection";
        CommandManager.InvalidateRequerySuggested();
    }
    private Task ScanDuplicates() => RunDiscoveryAsync(ActivityArea.DuplicateReview, "Reviewing duplicate-file contents", async token =>
    {
        InvalidateDuplicatePairSnapshot();
        DuplicateRows.Clear(); DuplicateIssues.Clear(); SelectedDuplicateEntry = null;
        DuplicateAttempt = DateTimeOffset.Now.ToString("g"); DuplicateMessage = "Reading bounded selected-scope content and comparing stable file observations…";
        DuplicateCoverage = "In progress; no completed duplicate report is claimed.";
        var result = await duplicateInventory.ScanAsync(DuplicateFolder, token); token.ThrowIfCancellationRequested();
        DuplicateCoverage = "No completed duplicate report was returned; review the provider status.";
        foreach (var report in result.Items)
        {
            foreach (var entry in report.Entries) DuplicateRows.Add(entry);
            foreach (var issue in report.Issues) DuplicateIssues.Add(issue);
            DuplicateCoverage = $"Visited {report.VisitedFiles}; skipped {report.SkippedFiles}; limited {report.Truncated}. {report.Coverage}";
        }
        duplicatePairSnapshot = result;
        DuplicateMessage = Stamp(result.State, result.Message); SelectedDuplicateEntry = DuplicateRows.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested();
        return result.State;
    }, error =>
    {
        InvalidateDuplicatePairSnapshot(); DuplicateRows.Clear(); DuplicateIssues.Clear(); SelectedDuplicateEntry = null;
        DuplicateMessage = error; DuplicateCoverage = "No completed report from this attempt; absence of rows does not prove no duplicates.";
    });
    private Task ScanShortcuts() => RunDiscoveryAsync(ActivityArea.ShortcutReview, "Inspecting shortcut metadata", async token =>
    {
        ShortcutRows.Clear(); ShortcutIssues.Clear(); SelectedShortcutObservation = null;
        ShortcutAttempt = DateTimeOffset.Now.ToString("g"); ShortcutMessage = "Reading bounded .lnk metadata; targets are never launched or actively resolved…";
        ShortcutCoverage = "In progress; no completed shortcut report is claimed.";
        var result = await shortcutInventory.ScanAsync(ShortcutFolder, token); token.ThrowIfCancellationRequested();
        ShortcutCoverage = "No completed shortcut report was returned; review the provider status.";
        foreach (var report in result.Items)
        {
            foreach (var entry in report.Entries) ShortcutRows.Add(entry);
            foreach (var issue in report.Issues) ShortcutIssues.Add(issue);
            ShortcutCoverage = $"Visited {report.VisitedFiles}; skipped {report.SkippedFiles}; limited {report.Truncated}. {report.Coverage}";
        }
        ShortcutMessage = Stamp(result.State, result.Message); SelectedShortcutObservation = ShortcutRows.FirstOrDefault();
        return result.State;
    }, error => { ShortcutMessage = error + " Any in-flight native/COM metadata read may still finish; no target is launched."; ShortcutCoverage = "No completed report from this attempt; unavailable targets are not confirmed broken."; });
    private Task ReadDiagnostics() => RunDiscoveryAsync(ActivityArea.DiagnosticsReport, "Reading existing event evidence", async token =>
    {
        DiagnosticRows.Clear(); DiagnosticCoverage.Clear(); SelectedDiagnosticFinding = null;
        DiagnosticsAttempt = DateTimeOffset.Now.ToString("g"); DiagnosticsMessage = "Reading bounded local Application/System event metadata…";
        DiagnosticsWindow = "In progress; no completed time window is claimed.";
        ResetDiagnosticRecurrence("Reading event evidence; no completed recurrence summary is available.");
        var result = await diagnosticsInventory.ReadAsync(SelectedDiagnosticRange.Days, token); token.ThrowIfCancellationRequested();
        DiagnosticsWindow = "No completed event-evidence window was returned; review the source status.";
        foreach (var report in result.Items)
        {
            foreach (var finding in report.Findings) DiagnosticRows.Add(finding);
            foreach (var coverage in report.Coverage) DiagnosticCoverage.Add(coverage);
            DiagnosticsWindow = $"UTC window [{report.StartUtc:u}, {report.EndUtc:u}); local {report.StartUtc.ToLocalTime():g} to {report.EndUtc.ToLocalTime():g} ({TimeZoneInfo.Local.DisplayName}).";
        }
        PublishDiagnosticRecurrence(result, token);
        DiagnosticsMessage = Stamp(result.State, result.Message); SelectedDiagnosticFinding = DiagnosticFindingsView.Cast<DiagnosticFinding>().FirstOrDefault();
        return result.State;
    }, error =>
    {
        DiagnosticRows.Clear(); DiagnosticCoverage.Clear(); SelectedDiagnosticFinding = null;
        DiagnosticsMessage = error; DiagnosticsWindow = "No completed event-evidence window from this attempt; no healthy/clean verdict is made.";
        ResetDiagnosticRecurrence(error);
    });
}
