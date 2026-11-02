using MadClean.Core;

namespace MadClean.Windows;

/// <summary>A separate explicit exact-pair read. It never enumerates siblings or retains a keeper lease.</summary>
public sealed partial class WindowsDuplicateInventory : IDuplicatePairRecheck
{
    public Task<CheckResult<DuplicatePairRecheckReport>> ReadAsync(DuplicatePairRecheckScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scope);
        var requested = DateTimeOffset.UtcNow;
        if (!PairScopeFresh(scope))
            return Task.FromResult(PairPreparationUnknown(scope, requested,
                new(DuplicatePairReadStage.ScopeValidation, DuplicatePairReadReason.ScopeExpired, null, 0, 0, 0, 0)));
        return RunOwnedFileReadAsync(stop =>
        {
            var started = DateTimeOffset.UtcNow;
            stop.ThrowIfCancellationRequested();
            if (!PairScopeFresh(scope))
                return PairPreparationUnknown(scope, started,
                    new(DuplicatePairReadStage.ScopeValidation, DuplicatePairReadReason.ScopeExpired, null, 0, 0, 0, 0));
            var access = new WindowsAccess();
            try
            {
                // Refresh protection metadata before either selected file can be opened.
                // Unavailable essential known roots block content. Incomplete ARP roots
                // remain explicitly Partial; valid registered roots still exclude content.
                // This read-only comparison never establishes absence of app ownership.
                access.PrepareScope(scope.RootPath, stop, requireEssentialKnownRoots: true);
            }
            catch (DuplicateReadException)
            {
                stop.ThrowIfCancellationRequested();
                return PairPreparationUnknown(scope, started, access.PreparationDiagnostic, access.CurrentExclusionCoverage);
            }
            var result = new DuplicatePairRecheckEngine().Read(scope, access, stop);
            var observed = result with
            {
                Items = result.Items.Select(report => report with
                {
                    Diagnostic = DuplicatePairReadDiagnosticPolicy.Normalize(access.PreparationDiagnostic.Reason != DuplicatePairReadReason.None
                        ? access.PreparationDiagnostic : access.PreparationDiagnostic with
                    {
                        Stage = report.State is DuplicatePairRecheckState.Equal or DuplicatePairRecheckState.Different
                            ? DuplicatePairReadStage.Completed : DuplicatePairReadStage.SelectedFiles,
                        Reason = report.State is DuplicatePairRecheckState.Equal or DuplicatePairRecheckState.Different
                            ? DuplicatePairReadReason.None : DuplicatePairReadReason.ContentNotEstablished
                    })
                }).ToArray()
            };
            return DuplicatePairExclusionProjection.Apply(observed, access.CurrentExclusionCoverage);
        }, token);
    }

    private static CheckResult<DuplicatePairRecheckReport> PairPreparationUnknown(DuplicatePairRecheckScope scope,
        DateTimeOffset started, DuplicatePairReadDiagnostic diagnostic, CheckState currentCoverage = CheckState.Unknown)
    {
        const string reason = "The selected pair read was blocked before content access. Review the preparation diagnostic and refresh the selection or required metadata; no current comparison was established.";
        return new(CheckState.Unknown, reason,
        [new(DuplicatePairRecheckState.Unknown, reason, scope.Candidate with { }, scope.RetainedEntry with { },
            null, null, started, DateTimeOffset.UtcNow, 0, scope.SourceCoverageState,
            "No selected file was opened for contents during this attempt. Zero compared bytes describes this blocked query, not file size or equality. " +
            "Historical entries remain historical; no current stamps, keeper lease, removal permission or retained-copy protection were established.")
            { Diagnostic = DuplicatePairReadDiagnosticPolicy.Normalize(diagnostic), CurrentExclusionCoverage = currentCoverage }]);
    }

    private static bool PairScopeFresh(DuplicatePairRecheckScope scope)
    {
        var now = DateTimeOffset.UtcNow;
        return scope.ValidatedUtc != default && scope.ValidatedUtc.Offset == TimeSpan.Zero &&
            scope.ValidatedUtc <= now && now - scope.ValidatedUtc <= DuplicatePairRecheckEngine.ScopeLifetime;
    }
}
