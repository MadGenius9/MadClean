using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private bool repeatedDiagnosticsOnly;
    private string diagnosticRecurrence = "Not checked. Read event evidence to summarize observed records; no system-health verdict is inferred.";
    public ICollectionView DiagnosticFindingsView { get; private set; } = null!;
    public string DiagnosticRecurrence { get => diagnosticRecurrence; private set => Set(ref diagnosticRecurrence, value); }
    public bool RepeatedDiagnosticsOnly
    {
        get => repeatedDiagnosticsOnly;
        set
        {
            if (repeatedDiagnosticsOnly == value) return;
            Set(ref repeatedDiagnosticsOnly, value);
            DiagnosticFindingsView.Refresh();
            if (selectedDiagnosticFinding == null || !DiagnosticFindingsView.Contains(selectedDiagnosticFinding))
                SelectedDiagnosticFinding = DiagnosticFindingsView.Cast<DiagnosticFinding>().FirstOrDefault();
        }
    }
    private void InitializeDiagnosticRecurrence()
    {
        DiagnosticFindingsView = CollectionViewSource.GetDefaultView(DiagnosticRows);
        DiagnosticFindingsView.Filter = item => item is DiagnosticFinding finding && (!RepeatedDiagnosticsOnly || finding.Count >= 2);
    }
    private void ResetDiagnosticRecurrence(string message) => DiagnosticRecurrence = message +
        " No current record totals or system-health verdict are claimed. The repeated-only filter changes this view, not the queried sources or time range.";
    private void PublishDiagnosticRecurrence(CheckResult<DiagnosticReport> result, CancellationToken token)
    {
        var summary = DiagnosticRecurrenceSummaryBuilder.Build(result, DateTimeOffset.UtcNow, token);
        if (summary.ObservedRecords is not long records || summary.ObservedGroups is not int groups ||
            summary.RepeatedGroups is not int repeated || summary.RecordsInRepeatedGroups is not long repeatedRecords)
        {
            DiagnosticRecurrence = summary.State + " · " + summary.Explanation;
            return;
        }
        DiagnosticRecurrence = $"{summary.State} · {records:N0} observed event records in {groups:N0} groups; {repeated:N0} groups have two or more records ({repeatedRecords:N0} records in those groups).\n" +
            $"UTC window [{summary.StartUtc?.ToString("u", CultureInfo.InvariantCulture)}, {summary.EndUtc?.ToString("u", CultureInfo.InvariantCulture)}); collected {summary.CollectedUtc:u}.\n" +
            summary.Explanation + " The summary always covers all returned groups; the repeated-only filter changes the list below without another query.";
    }
}
