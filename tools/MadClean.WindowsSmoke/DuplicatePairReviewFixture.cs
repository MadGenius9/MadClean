using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void DuplicatePairReviewFixture(string fixture, CheckResult<DuplicateReport>? snapshot, SmokeReport report, CancellationToken token)
    {
        const string check = "duplicate-pair-review-fixture";
        if (snapshot == null || snapshot.State is not (CheckState.Available or CheckState.Partial) || snapshot.Items.Count != 1)
        {
            report.Unknown(check, "The earlier owned native duplicate scan supplied no qualified actual snapshot. No second scan, sample data or file action substituted for native worksheet evidence.");
            return;
        }
        var entries = snapshot.Items[0].Entries;
        string scope = System.IO.Path.Combine(fixture, "duplicates");
        string first = System.IO.Path.Combine(scope, "different-name-one.txt"), second = System.IO.Path.Combine(scope, "different-name-two.txt");
        string link = System.IO.Path.Combine(scope, "same-file-alias.txt");
        var candidate = entries.FirstOrDefault(entry => PathEquals(entry.Path, second) &&
            entry.EvidenceKind == DuplicateEvidenceKind.ByteComparedIndependentFile && entry.ObservedLinkCount == 1);
        var retained = entries.FirstOrDefault(entry => (PathEquals(entry.Path, first) || PathEquals(entry.Path, link)) &&
            entry.EvidenceKind == DuplicateEvidenceKind.ByteComparedIndependentFile && entry.ObservedLinkCount == 2);
        var alias = entries.FirstOrDefault(entry => (PathEquals(entry.Path, first) || PathEquals(entry.Path, link)) &&
            entry.EvidenceKind == DuplicateEvidenceKind.SameFileAlias && entry.ObservedLinkCount == 2);
        if (candidate == null || retained == null || alias == null || entries.Any(entry => entry.ObservedUtc == null))
        {
            report.Unknown(check, $"Native sourceState={snapshot.State}; required typed independent-single-link/retained-anchor/actual-alias/time evidence was not returned. No clone or invented native evidence was substituted.");
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var preview = DuplicatePairReviewBuilder.Build(snapshot, candidate, retained, now, token);
        if (preview.State != DuplicatePairReviewState.Preview || preview.ExecutionSupported || preview.SourceCoverageState != snapshot.State ||
            preview.Candidate != candidate || preview.RetainedEntry != retained)
        {
            report.Fail(check, "The actual owned single-link candidate/independent retained-anchor worksheet did not preserve exact immutable snapshot values and source coverage with execution disabled. No paths/identities/content exported.");
            return;
        }
        var aliasCandidate = DuplicatePairReviewBuilder.Build(snapshot, alias, candidate, now, token);
        var sameIdentity = DuplicatePairReviewBuilder.Build(snapshot, retained, alias, now, token);
        var clonedSelection = DuplicatePairReviewBuilder.Build(snapshot, candidate with { }, retained, now, token);
        var expiredAt = entries.Max(entry => entry.ObservedUtc!.Value).Add(DuplicatePairReviewBuilder.MaximumObservationAge).AddTicks(1);
        var expired = DuplicatePairReviewBuilder.Build(snapshot, candidate, retained, expiredAt, token);
        if (new[] { aliasCandidate, sameIdentity, clonedSelection, expired }.Any(review =>
            review.State != DuplicatePairReviewState.Blocked || review.ExecutionSupported || review.Candidate != null || review.RetainedEntry != null ||
            review.SourceCoverageState != snapshot.State))
        {
            report.Fail(check, "Alias, same-identity, cloned selection or expired snapshot evidence was promoted to a worksheet or execution support. No current-file checks or actions occurred.");
            return;
        }
        bool cancelled = false;
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            try { DuplicatePairReviewBuilder.Build(snapshot, candidate, retained, now, stop.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        if (!cancelled)
        {
            report.Fail(check, "Pre-cancelled snapshot worksheet did not observe cancellation; no action or file read was requested.");
            return;
        }
        report.Pass(check, $"Native sourceState={snapshot.State}; reusedSnapshot=true; exactPairPreviewVerified=true; realAliasBlocked=true; sameIdentityBlocked=true; clonedSelectionBlocked=true; expiredObservationBlocked=true; preCancellationObserved=true; executionSupported=false. " +
            "The earlier owned full-byte comparison supplied typed independent/alias/link/time evidence; this worksheet performed no additional file read, scan or change. Partial coverage remains incomplete. No paths, identities, timestamps, contents or provider messages exported; current-file freshness, retained-copy protection, ownership, removal eligibility and reclaimable space remain unverified.");
    }
}
