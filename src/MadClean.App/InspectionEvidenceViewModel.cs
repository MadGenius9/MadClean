using System.Collections.ObjectModel;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private DateTimeOffset inspectionEvidenceAttempt;
    private DiagnosticInspectionEvidenceEntry? selectedInspectionEvidence;
    public ObservableCollection<DiagnosticInspectionEvidenceEntry> InspectionEvidenceRows { get; } =
    [DiagnosticInspectionEvidence.NotChecked(DiagnosticEvidenceArea.Storage),
        DiagnosticInspectionEvidence.NotChecked(DiagnosticEvidenceArea.Defender),
        DiagnosticInspectionEvidence.NotChecked(DiagnosticEvidenceArea.ComponentStoreRecordedFlags),
        DiagnosticInspectionEvidence.NotChecked(DiagnosticEvidenceArea.ComponentStoreScan),
        DiagnosticInspectionEvidence.NotChecked(DiagnosticEvidenceArea.SystemFiles)];

    public DiagnosticInspectionEvidenceEntry? SelectedInspectionEvidence
    {
        get => selectedInspectionEvidence;
        set
        {
            // Selection must refer to a current row, never an old or caller-created observation.
            if (value != null && !InspectionEvidenceRows.Any(entry => ReferenceEquals(entry, value))) return;
            Set(ref selectedInspectionEvidence, value);
            PropertyChanged?.Invoke(this, new(nameof(InspectionEvidenceDetails)));
        }
    }
    public string InspectionEvidenceDetails => selectedInspectionEvidence is { } entry
        ? $"Area: {entry.Title}\nObservation: {entry.Observation}\nSource: {entry.Source}\nState: {entry.State}\n" +
            $"Request attempt: {DiagnosticTimestamp(entry.AttemptedUtc)}\n" +
            $"Collection completed: {DiagnosticTimestamp(entry.CompletedUtc)}\n" +
            $"Coverage: {entry.Coverage}\nUncertainty: {entry.Uncertainty}\nImpact: {entry.Impact}\nNext step: {entry.NextStep}\n\n" +
            "These are independent in-memory source observations from this session, not live monitoring, a combined health verdict or permission to change the PC. Each source has its own request and time. Collection time does not establish when Windows last refreshed an individual provider field."
        : "Select an inspection observation to review its source, collection time, scope, uncertainty and next step. Inspect this PC collects disk and Defender observations. Component-store and SFC observations require their own reviewed, explicitly confirmed requests. Reading or selecting this panel starts no query.";

    private void BeginInspectionEvidence()
    {
        inspectionEvidenceAttempt = DateTimeOffset.UtcNow;
        ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Pending(DiagnosticEvidenceArea.Storage, inspectionEvidenceAttempt));
        ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Pending(DiagnosticEvidenceArea.Defender, inspectionEvidenceAttempt));
    }
    private void CompleteStorageEvidence(CheckResult<DiskInfo> result, CancellationToken token) =>
        ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Storage(result, inspectionEvidenceAttempt, DateTimeOffset.UtcNow, token));
    private void CompleteDefenderEvidence(CheckResult<DefenderInfo> result, CancellationToken token) =>
        ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Defender(result, inspectionEvidenceAttempt, DateTimeOffset.UtcNow, token));
    private void InterruptInspectionEvidence(ActivityArea area, CheckState state)
    {
        DiagnosticEvidenceArea? source = area switch
        {
            ActivityArea.Disks => DiagnosticEvidenceArea.Storage,
            ActivityArea.Defender => DiagnosticEvidenceArea.Defender,
            _ => null
        };
        if (source.HasValue)
            ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Interrupted(source.Value, state, inspectionEvidenceAttempt, DateTimeOffset.UtcNow));
    }
    private void FinishPendingInspectionEvidence(CheckState state)
    {
        foreach (var entry in InspectionEvidenceRows.ToArray())
            if ((entry.Area == DiagnosticEvidenceArea.Storage || entry.Area == DiagnosticEvidenceArea.Defender) &&
                entry.AttemptedUtc == inspectionEvidenceAttempt && entry.CompletedUtc == null)
                ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Interrupted(entry.Area, state,
                    inspectionEvidenceAttempt, DateTimeOffset.UtcNow));
    }
    private static DiagnosticEvidenceArea ComponentStoreEvidenceArea(ComponentStoreCheckKind kind) => kind switch
    {
        ComponentStoreCheckKind.RecordedFlags => DiagnosticEvidenceArea.ComponentStoreRecordedFlags,
        ComponentStoreCheckKind.ScanHealth => DiagnosticEvidenceArea.ComponentStoreScan,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private DateTimeOffset BeginIntegrityEvidence(DiagnosticEvidenceArea area)
    {
        var attemptedUtc = DateTimeOffset.UtcNow;
        // Confirmation is recorded here; Pending does not establish provider/client invocation.
        ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Pending(area, attemptedUtc));
        return attemptedUtc;
    }
    private void InterruptIntegrityEvidence(DiagnosticEvidenceArea area, CheckState state, DateTimeOffset attemptedUtc) =>
        ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Interrupted(area, state, attemptedUtc, DateTimeOffset.UtcNow));

    private void ReplaceInspectionEvidence(DiagnosticInspectionEvidenceEntry entry)
    {
        int index = -1;
        for (int candidate = 0; candidate < InspectionEvidenceRows.Count; candidate++)
            if (InspectionEvidenceRows[candidate].Area == entry.Area) { index = candidate; break; }
        if (index < 0) return;
        bool selected = SelectedInspectionEvidence?.Area == entry.Area;
        InspectionEvidenceRows[index] = entry;
        if (selected) SelectedInspectionEvidence = entry;
    }
}
