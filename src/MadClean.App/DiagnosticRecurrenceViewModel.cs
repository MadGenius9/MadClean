using System.ComponentModel;
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
            repeatedDiagnosticsOnly = value;
            long generation = diagnosticRangeGeneration;
            var scope = new DiagnosticPublicationScope(() => IsCurrentDiagnosticRange(generation) && repeatedDiagnosticsOnly == value);
            if (!scope.Publish(() => DiagnosticFindingsView.Refresh())) return;
            if (SelectedDiagnosticFinding == null)
                if (!scope.Publish(() => SelectedDiagnosticFinding = DiagnosticFindingsView.Cast<DiagnosticFinding>().FirstOrDefault(IsCurrentVisibleDiagnosticFinding))) return;
            scope.Publish(() => PropertyChanged?.Invoke(this, new(nameof(RepeatedDiagnosticsOnly))));
        }
    }
    private void InitializeDiagnosticRecurrence()
    {
        DiagnosticFindingsView = CollectionViewSource.GetDefaultView(DiagnosticRows);
        DiagnosticFindingsView.Filter = item => item is DiagnosticFinding finding && (!RepeatedDiagnosticsOnly || finding.Count >= 2);
    }
    private bool ResetDiagnosticRecurrence(string message, DiagnosticPublicationScope? scope = null)
    {
        long generation = diagnosticRangeGeneration;
        scope ??= new DiagnosticPublicationScope(() => IsCurrentDiagnosticRange(generation));
        return scope.Publish(() => DiagnosticRecurrence = message +
            " No current record totals or system-health verdict are claimed. The repeated-only filter changes this view, not the queried sources or time range.");
    }
    private bool PublishDiagnosticRecurrence(CheckResult<DiagnosticReport> result, CancellationToken token, DiagnosticPublicationScope scope)
    {
        if (!scope.IsCurrent) return false;
        var summary = DiagnosticRecurrenceSummaryBuilder.Build(result, DateTimeOffset.UtcNow, token);
        if (summary.ObservedRecords is not long records || summary.ObservedGroups is not int groups ||
            summary.RepeatedGroups is not int repeated || summary.RecordsInRepeatedGroups is not long repeatedRecords ||
            summary.StartUtc is not { } startUtc || summary.EndUtc is not { } endUtc)
        {
            return scope.Publish(() => DiagnosticRecurrence = summary.State + " · " + summary.Explanation);
        }
        return scope.Publish(() => DiagnosticRecurrence = $"{summary.State} · {records:N0} observed event records in {groups:N0} groups; {repeated:N0} groups have two or more records ({repeatedRecords:N0} records in those groups).\n" +
            DiagnosticWindowText(startUtc, endUtc) + $"\nCollected: {DiagnosticTimestamp(summary.CollectedUtc)}.\n" +
            summary.Explanation + " The summary always covers all returned groups; the repeated-only filter changes the list below without another query.");
    }
}
