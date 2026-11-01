using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private CheckResult<DuplicateReport>? duplicatePairSnapshot;
    private DuplicateEntry? duplicateCandidate, duplicateRetainedEntry;
    private string duplicatePairMessage = "Not prepared. Choose both files yourself after scanning. Nothing is preselected or removed.";
    private string duplicatePairDetails = "This worksheet reviews a content-comparison snapshot only. Current file identity, unchanged contents, ownership, active use and protection of the retained copy are unverified. No removal is available.";
    public ICommand PreviewDuplicatePairCommand { get; private set; } = null!;
    public ICommand ClearDuplicatePairCommand { get; private set; } = null!;
    public string DuplicatePairMessage { get => duplicatePairMessage; private set => Set(ref duplicatePairMessage, value); }
    public string DuplicatePairDetails { get => duplicatePairDetails; private set => Set(ref duplicatePairDetails, value); }
    public DuplicateEntry? DuplicateCandidate
    {
        get => duplicateCandidate;
        set { if (ReferenceEquals(duplicateCandidate, value)) return; duplicateCandidate = value; PropertyChanged?.Invoke(this, new(nameof(DuplicateCandidate))); InvalidateDuplicatePairPreview(); }
    }
    public DuplicateEntry? DuplicateRetainedEntry
    {
        get => duplicateRetainedEntry;
        set { if (ReferenceEquals(duplicateRetainedEntry, value)) return; duplicateRetainedEntry = value; PropertyChanged?.Invoke(this, new(nameof(DuplicateRetainedEntry))); InvalidateDuplicatePairPreview(); }
    }
    private void InitializeDuplicatePairReview()
    {
        InitializeDuplicatePairRecheck();
        PreviewDuplicatePairCommand = new ActionCommand(PreviewDuplicatePair, () => !IsBusy && duplicatePairSnapshot != null && DuplicateCandidate != null && DuplicateRetainedEntry != null);
        ClearDuplicatePairCommand = new ActionCommand(ResetDuplicatePairReview, () => !IsBusy && (DuplicateCandidate != null || DuplicateRetainedEntry != null));
    }
    private void InvalidateDuplicatePairPreview()
    {
        InvalidateDuplicatePairRecheck();
        DuplicatePairMessage = "Selection changed. Prepare a new snapshot pair preview; no file change is authorized.";
        DuplicatePairDetails = "Choose a retained independent copy and a different candidate from the same byte-compared group. A hard-link alias is not an independent backup. Snapshot evidence cannot establish current removal safety.";
        CommandManager.InvalidateRequerySuggested();
    }
    private void ResetDuplicatePairReview()
    {
        InvalidateDuplicatePairRecheck();
        DuplicateCandidate = null;
        DuplicateRetainedEntry = null;
        DuplicatePairMessage = "Not prepared. Choose both files yourself after scanning. Nothing is preselected or removed.";
        DuplicatePairDetails = "No pair preview is current. Snapshot observations are not ownership, active-use, backup, deletion or reclaimable-space evidence. No removal is available.";
        CommandManager.InvalidateRequerySuggested();
    }
    private void InvalidateDuplicatePairSnapshot()
    {
        duplicatePairSnapshot = null;
        ResetDuplicatePairReview();
    }
    private void PreviewDuplicatePair()
    {
        var review = DuplicatePairReviewBuilder.Build(duplicatePairSnapshot, DuplicateCandidate, DuplicateRetainedEntry, DateTimeOffset.UtcNow);
        DuplicatePairMessage = $"{review.State} · {review.Reason} Source coverage: {review.SourceCoverageState}. Execution is unavailable.";
        if (review.State != DuplicatePairReviewState.Preview || review.Candidate == null || review.RetainedEntry == null)
        {
            DuplicatePairDetails = "No valid pair preview was prepared. Review the selection and evidence; rescan if observations expired. No filesystem query or file change was performed by this worksheet.";
            return;
        }
        static string Describe(string role, DuplicateEntry entry) =>
            $"{role}\nExact path: {entry.Path}\nGroup: {entry.GroupId}\nObserved logical bytes: {entry.Bytes:N0}\nObserved file identity: {entry.Identity}\nReported links: {entry.ObservedLinkCount}\nEvidence: {entry.EvidenceKind}\nObserved UTC: {entry.ObservedUtc:u}\n";
        DuplicatePairDetails = Describe("Copy you chose to retain", review.RetainedEntry) + "\n" + Describe("Candidate you chose to review", review.Candidate) +
            "\nProposed future intent only: retain the first file and review the second for a possible later action. Nothing will change. Both entries came from the same byte-compared snapshot, but handles have been released. Current contents/identity, inactive use, exclusive ownership and protection of the retained copy remain unverified. This is not a backup, permission to remove a file or a reclaimable-space estimate. A later action requires separate fresh validation and explicit review; removal is not implemented.";
    }
}
