using System.Collections.ObjectModel;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private DateTimeOffset inspectionEvidenceAttempt;
    private DiagnosticInspectionEvidenceEntry? selectedInspectionEvidence;
    public ObservableCollection<DiagnosticInspectionEvidenceEntry> InspectionEvidenceRows { get; } =
    [DiagnosticInspectionEvidence.NotChecked(DiagnosticEvidenceArea.Storage),
        DiagnosticInspectionEvidence.NotChecked(DiagnosticEvidenceArea.Defender)];

    public DiagnosticInspectionEvidenceEntry? SelectedInspectionEvidence
    {
        get => selectedInspectionEvidence;
        set { Set(ref selectedInspectionEvidence, value); PropertyChanged?.Invoke(this, new(nameof(InspectionEvidenceDetails))); }
    }
    public string InspectionEvidenceDetails => selectedInspectionEvidence is { } entry
        ? $"Observation: {entry.Observation}\nSource: {entry.Source}\nState: {entry.State}\n" +
            $"Inspection attempt UTC: {(entry.AttemptedUtc.HasValue ? entry.AttemptedUtc.Value.ToString("u") : "not checked")}\n" +
            $"Collection completed UTC: {(entry.CompletedUtc.HasValue ? entry.CompletedUtc.Value.ToString("u") : "not completed")}\n" +
            $"Coverage: {entry.Coverage}\nUncertainty: {entry.Uncertainty}\nImpact: {entry.Impact}\nNext step: {entry.NextStep}\n\n" +
            "These are in-memory observations from the last local inspection, not live monitoring, a health score or permission to change the PC. Collection time does not establish when Windows last refreshed an individual provider field."
        : "Select an inspection observation to review its source, collection time, scope, uncertainty and next step. Choose Inspect this PC to collect local disk and Defender observations; this panel starts no additional query.";

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
            if (entry.AttemptedUtc == inspectionEvidenceAttempt && entry.CompletedUtc == null)
                ReplaceInspectionEvidence(DiagnosticInspectionEvidence.Interrupted(entry.Area, state,
                    inspectionEvidenceAttempt, DateTimeOffset.UtcNow));
    }
    private void ReplaceInspectionEvidence(DiagnosticInspectionEvidenceEntry entry)
    {
        int index = entry.Area == DiagnosticEvidenceArea.Storage ? 0 : 1;
        bool selected = SelectedInspectionEvidence?.Area == entry.Area;
        InspectionEvidenceRows[index] = entry;
        if (selected) SelectedInspectionEvidence = entry;
    }
}
