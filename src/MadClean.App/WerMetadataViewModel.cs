using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IWerMetadataInventory werMetadata = new WindowsWerMetadataInventory();
    private WerMetadataObservation? selectedWerObservation;
    private string werMessage = "Not checked. WER store metadata is a separate explicit read; no report or dump contents are opened.";
    private string werWindow = "No WER store window queried.";
    private string werCountSummary = "Store counts: not checked. Counts cover all currently stored reports, separately from the selected creation-time window.";
    public ObservableCollection<WerMetadataObservation> WerRows { get; } = [];
    public ObservableCollection<string> WerCoverage { get; } = [];
    public ICommand ReadWerMetadataCommand { get; private set; } = null!;
    public string WerMessage { get => werMessage; private set => Set(ref werMessage, value); }
    public string WerWindow { get => werWindow; private set => Set(ref werWindow, value); }
    public string WerCountSummary { get => werCountSummary; private set => Set(ref werCountSummary, value); }
    private WerMetadataObservation? CurrentWerObservation => selectedWerObservation is { } row &&
        WerRows.Any(item => ReferenceEquals(item, row)) ? row : null;
    public WerMetadataObservation? SelectedWerObservation
    {
        get => CurrentWerObservation;
        set
        {
            if (value != null && !WerRows.Any(row => ReferenceEquals(row, value)))
            {
                PropertyChanged?.Invoke(this, new(nameof(SelectedWerObservation)));
                return;
            }
            if (ReferenceEquals(selectedWerObservation, value)) return;
            selectedWerObservation = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedWerObservation)));
            PropertyChanged?.Invoke(this, new(nameof(WerDetails)));
        }
    }
    public string WerDetails => CurrentWerObservation is { } item
        ? $"Category: {item.Category}\nStore: {item.Store}\nReport reference: {item.ReportId}\nRecorded creation: {DiagnosticTimestamp(item.CreatedUtc)}\nSource: {item.Source}\n{WerWindow}\n\nThis is stored report metadata, not a confirmed unique crash, current problem, cause, severity or integrity verdict. Queue/archive entries can overlap, expire or be absent under retention/policy. App names, raw signatures/parameters, file names and dump contents are not read into this view."
        : "Select a stored-report observation to inspect its source and recorded time. Retention, access, OS/API support and bounded coverage limit this corroborating evidence; no records does not mean healthy.";
    private static string WerStoreStatusText(WerMetadataStoreStatus source)
    {
        string explanation = source.Reason switch
        {
            WerMetadataStoreReason.SuccessfulOpenReturnedNullHandle => "Windows returned success without a usable store handle. Store reading remains Unknown; this is not evidence of an empty store.",
            WerMetadataStoreReason.SuccessfulEnumerationReturnedNullKey => "Windows returned success without a usable report key. Enumeration remains Unknown; this is not evidence of no reports.",
            WerMetadataStoreReason.StoreOpenFailed => "Windows could not open this store. Review existing WER access/service policy before retrying; MadClean requests no elevation.",
            WerMetadataStoreReason.EnumerationFailed => "Windows did not complete report-key enumeration; existing history coverage remains incomplete.",
            WerMetadataStoreReason.EnumerationComplete => "Windows completed the current bounded key enumeration; this does not establish complete incident history or system health.",
            WerMetadataStoreReason.BudgetExpired => "The metadata time budget expired; additional records remain unknown.",
            WerMetadataStoreReason.RecordLimitReached => "The report-key limit was reached; additional records remain unknown.",
            WerMetadataStoreReason.MetadataQueryFailed => "Some report metadata was unreadable; usable references do not establish complete coverage.",
            _ => "The source response was missing, ambiguous or unqualified; no complete-store conclusion is possible."
        };
        string result = source.LastNativeResult is int code ? $"0x{unchecked((uint)code):X8}" : "not observed";
        return $"{WerMetadataProjection.Label(source.Store)} · {source.State}: {explanation} Last observed API result: {result}.";
    }
    private void InitializeWerMetadata() => ReadWerMetadataCommand = new AsyncCommand(ReadWerMetadata, () => !IsBusy);
    private bool InvalidateWerMetadata(DiagnosticPublicationScope? scope = null)
    {
        long generation = diagnosticRangeGeneration;
        scope ??= new DiagnosticPublicationScope(() => IsCurrentDiagnosticRange(generation));
        if (!scope.Publish(WerRows.Clear)) return false;
        if (!scope.Publish(WerCoverage.Clear)) return false;
        if (!scope.Publish(() => SelectedWerObservation = null)) return false;
        if (!scope.Publish(() => WerMessage = "Time range changed. Choose Read WER metadata for the selected range.")) return false;
        if (!scope.Publish(() => WerWindow = "Not checked for the new time range.")) return false;
        return scope.Publish(() => WerCountSummary = "Store counts: not checked for this attempt. Counts are independent of the selected creation-time window.");
    }
    private Task ReadWerMetadata()
    {
        long generation = diagnosticRangeGeneration;
        int days = SelectedDiagnosticRange.Days;
        var scope = new DiagnosticPublicationScope(() => IsCurrentDiagnosticRange(generation));
        return RunDiscoveryAsync(ActivityArea.WerMetadata, "Reading supported WER store metadata", async token =>
        {
            if (!scope.Publish(WerRows.Clear)) return CheckState.Cancelled;
            if (!scope.Publish(WerCoverage.Clear)) return CheckState.Cancelled;
            if (!scope.Publish(() => SelectedWerObservation = null)) return CheckState.Cancelled;
            if (!scope.Publish(() => WerMessage = "Reading bounded machine queue/archive references and creation times only…")) return CheckState.Cancelled;
            if (!scope.Publish(() => WerWindow = "In progress; no completed WER metadata window is claimed.")) return CheckState.Cancelled;
            if (!scope.Publish(() => WerCountSummary = "Store counts: checking separately; no result yet.")) return CheckState.Cancelled;
            var result = await werMetadata.ReadAsync(days, token); token.ThrowIfCancellationRequested();
            // A range change invalidates presentation only. The original adapter still owns
            // its work through actual completion; do not publish it into the new range.
            if (!scope.IsCurrent) return CheckState.Cancelled;
            if (!scope.Publish(() => WerWindow = "No completed WER store window was returned; review the source status and coverage.")) return CheckState.Cancelled;
            if (!scope.Publish(() => WerCountSummary = "Store counts: unavailable or not observed. No empty-store conclusion is valid.")) return CheckState.Cancelled;
            foreach (var report in result.Items)
            {
                foreach (var item in report.Observations)
                    if (!scope.Publish(() => WerRows.Add(item))) return CheckState.Cancelled;
                foreach (var item in report.Coverage)
                    if (!scope.Publish(() => WerCoverage.Add(item))) return CheckState.Cancelled;
                foreach (var source in report.StoreStatuses)
                    if (!scope.Publish(() => WerCoverage.Add(WerStoreStatusText(source)))) return CheckState.Cancelled;
                if (!scope.Publish(() => WerCountSummary = "Current-store counts (all creation times): " + string.Join("; ", report.StoreStatuses.Select(source =>
                    WerMetadataProjection.Label(source.Store) + (source.CountObservation is { NativeResult: 0, ReportCount: { } count } observation
                        ? $" reported {count} at {DiagnosticTimestamp(observation.CheckedUtc)}"
                        : " count unknown"))) +
                    ". Separate non-atomic observations; not a count of unique crashes or reports in the selected time range. Zero does not mean healthy. Enumeration status remains separate.")) return CheckState.Cancelled;
                if (!scope.Publish(() => WerWindow = DiagnosticWindowText(report.StartUtc, report.EndUtc))) return CheckState.Cancelled;
            }
            if (!scope.Publish(() => WerMessage = Stamp(result.State, result.Message))) return CheckState.Cancelled;
            return result.State;
        }, error =>
        {
            // A failed publication fence is terminal. A fresh fence may clean up only
            // this same captured range, never a reentrant replacement selection.
            var failure = new DiagnosticPublicationScope(() => IsCurrentDiagnosticRange(generation));
            if (!failure.Publish(WerRows.Clear)) return;
            if (!failure.Publish(WerCoverage.Clear)) return;
            if (!failure.Publish(() => SelectedWerObservation = null)) return;
            if (!failure.Publish(() => WerMessage = error + " Existing synchronous Windows calls may still finish; no forced cancellation or healthy verdict is claimed.")) return;
            if (!failure.Publish(() => WerWindow = "No completed WER report from this attempt.")) return;
            failure.Publish(() => WerCountSummary = "Store counts: this attempt did not return a completed observation. No empty-store conclusion is valid.");
        });
    }
}
