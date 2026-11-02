using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IDuplicatePairRecheck duplicatePairRecheck = new WindowsDuplicateInventory();
    private long duplicatePairRecheckGeneration;
    private string duplicatePairRecheckMessage = "Not checked. Explicitly select both files, then request a fresh comparison.";
    private string duplicatePairRecheckDetails = DuplicatePairRecheckLimits;
    private string duplicatePairExclusionSummary = "Current exclusions: Not checked. Application ownership remains unverified.";
    private const string DuplicatePairRecheckLimits = "This separately reads exactly the two selected files, up to 64 MiB each. Current byte equality is an observation, not ownership, inactivity, immutable contents or removal permission. Matching identity values do not prove uninterrupted history or unchanged original bytes. All read handles close before publication; no retained copy stays protected. Removal remains unavailable.";
    public ICommand RecheckDuplicatePairCommand { get; private set; } = null!;
    public string DuplicatePairRecheckMessage { get => duplicatePairRecheckMessage; private set => Set(ref duplicatePairRecheckMessage, value); }
    public string DuplicatePairRecheckDetails { get => duplicatePairRecheckDetails; private set => Set(ref duplicatePairRecheckDetails, value); }
    public string DuplicatePairExclusionSummary { get => duplicatePairExclusionSummary; private set => Set(ref duplicatePairExclusionSummary, value); }

    private void InitializeDuplicatePairRecheck() => RecheckDuplicatePairCommand = new AsyncCommand(RecheckDuplicatePair,
        () => !IsBusy && duplicatePairSnapshot != null && DuplicateCandidate != null && DuplicateRetainedEntry != null &&
            !ReferenceEquals(DuplicateCandidate, DuplicateRetainedEntry));

    private void InvalidateDuplicatePairRecheck()
    {
        duplicatePairRecheckGeneration++;
        DuplicatePairRecheckMessage = "Not checked for this pair. Any earlier current-file result is invalidated.";
        DuplicatePairRecheckDetails = DuplicatePairRecheckLimits;
        DuplicatePairExclusionSummary = "Current exclusions: Not checked for this pair. Application ownership remains unverified.";
        CommandManager.InvalidateRequerySuggested();
    }

    private Task RecheckDuplicatePair()
    {
        var candidate = DuplicateCandidate;
        var retained = DuplicateRetainedEntry;
        var snapshot = duplicatePairSnapshot;
        string root = DuplicateFolder;
        long generation = duplicatePairRecheckGeneration;
        bool Current() => generation == duplicatePairRecheckGeneration && ReferenceEquals(candidate, DuplicateCandidate) &&
            ReferenceEquals(retained, DuplicateRetainedEntry) && ReferenceEquals(snapshot, duplicatePairSnapshot) && root == DuplicateFolder;
        return RunDiscoveryAsync(ActivityArea.DuplicatePairRecheck, "Comparing the selected duplicate pair", async token =>
        {
            DuplicatePairRecheckMessage = "Reading exactly the two selected files after refreshing protection metadata…";
            DuplicatePairRecheckDetails = "In progress. No current equality, retained-copy protection or action permission is claimed.";
            DuplicatePairExclusionSummary = "Current exclusions: Checking known folders and application-location metadata…";
            var prepared = DuplicatePairRecheckScope.Create(root, snapshot, candidate, retained, token);
            token.ThrowIfCancellationRequested();
            if (prepared.Items.Count != 1)
            {
                if (Current())
                {
                    DuplicatePairRecheckMessage = Stamp(prepared.State, prepared.Message);
                    DuplicatePairRecheckDetails = "No fresh pair read started. Refresh discovery or select a supported independent single-link pair. " + DuplicatePairRecheckLimits;
                    DuplicatePairExclusionSummary = "Current exclusions: Not checked; no pair read started.";
                }
                return prepared.State;
            }
            var scope = prepared.Items[0];
            var result = await duplicatePairRecheck.ReadAsync(scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            DuplicatePairRecheckMessage = Stamp(result.State, result.Message);
            if (result.Items.Count != 1)
            {
                DuplicatePairRecheckDetails = "No completed pair report was returned. Missing, interrupted or unverified data does not mean equal files. " + DuplicatePairRecheckLimits;
                DuplicatePairExclusionSummary = "Current exclusions: Not established for a completed comparison.";
                return result.State;
            }
            var report = result.Items[0];
            var diagnostic = DuplicatePairReadDiagnosticPolicy.Normalize(report.Diagnostic);
            if (report.CanRemove || !Enum.IsDefined(report.State) || report.Candidate != scope.Candidate ||
                report.RetainedEntry != scope.RetainedEntry || report.SourceCoverageState != scope.SourceCoverageState ||
                report.StartedUtc.Offset != TimeSpan.Zero || report.StartedUtc < scope.ValidatedUtc ||
                report.CompletedUtc.Offset != TimeSpan.Zero || report.CompletedUtc < report.StartedUtc || report.CompletedUtc > DateTimeOffset.UtcNow ||
                report.ComparedBytes is < 0 or > DuplicatePairRecheckEngine.MaximumReadBytes)
                throw new InvalidOperationException("Current duplicate-pair evidence was inconsistent.");
            DuplicatePairExclusionSummary = report.CurrentExclusionCoverage switch
            {
                CheckState.Available => "Current known exclusion metadata: Available. Unregistered application ownership remains unknown; removal is unavailable.",
                CheckState.Partial => "Current application exclusions: Partial. Some locations could not be verified; selected files may belong to applications. This comparison grants no removal permission.",
                _ => "Current exclusions: Unknown. No protected current comparison is accepted."
            };
            string observation = report.State switch
            {
                DuplicatePairRecheckState.Equal => "The two current held streams were fully byte-compared and matched during this read.",
                DuplicatePairRecheckState.Different => "The current files differ. The earlier snapshot does not establish current duplicate contents.",
                DuplicatePairRecheckState.Changed => "Identity, length or stability changed. No complete current duplicate conclusion was accepted.",
                _ => "The current pair remains unverified; no equality or retained-copy safety is inferred."
            };
            static string Describe(string role, DuplicateEntry original, DuplicateFileStamp? current) =>
                $"{role}\nExact path: {original.Path}\nHistorical logical bytes: {original.Bytes:N0}; observed UTC: {original.ObservedUtc:u}\n" +
                (current == null ? "No accepted current file stamp."
                    : $"Current observed logical bytes: {current.Bytes:N0}; reported links: {current.LinkCount}\nCurrent identity value matches historical observation: {string.Equals(original.Identity, current.Identity, StringComparison.OrdinalIgnoreCase)}");
            string registryResult = diagnostic.RegistryNativeResult is int native ? $"0x{unchecked((uint)native):X8}" : "Not observed";
            DuplicatePairRecheckDetails = $"Current comparison: {report.State}\n{observation}\nReason: {report.Reason}\nCheck interval UTC: {report.StartedUtc:u} — {report.CompletedUtc:u}\nTotal content bytes read across both files: {report.ComparedBytes:N0}\nOriginal source coverage: {report.SourceCoverageState}\nPreparation diagnostic: {diagnostic.Stage} / {diagnostic.Reason}\nFirst blocker registry result: {registryResult}\n\n" +
                Describe("Copy you chose to retain", report.RetainedEntry, report.RetainedStamp) + "\n\n" +
                Describe("Candidate you chose to review", report.Candidate, report.CandidateStamp) +
                $"\n\nCoverage: {report.Coverage}\n\n{DuplicatePairRecheckLimits}";
            return result.State;
        }, error =>
        {
            if (!Current()) return;
            DuplicatePairRecheckMessage = error;
            DuplicatePairRecheckDetails = "No completed fresh comparison is claimed. A native read may still be finishing under its worker's ownership. " + DuplicatePairRecheckLimits;
            DuplicatePairExclusionSummary = "Current exclusions: Not established for a completed comparison.";
        });
    }
}
