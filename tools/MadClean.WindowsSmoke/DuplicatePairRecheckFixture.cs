using System.Buffers.Binary;
using System.IO;
using System.Text;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void DuplicatePairRecheckFixture(string fixture, SmokeReport report, CancellationToken token)
    {
        const string check = "duplicate-pair-recheck-fixture";
        string root = Path.Combine(fixture, "duplicate-pair-recheck");
        string outside = Path.Combine(fixture, "duplicate-pair-recheck-control");
        string candidatePath = Path.Combine(root, "candidate.txt"), retainedPath = Path.Combine(root, "retained.txt");
        string sentinel = Path.Combine(outside, "sentinel.bin"), aliasPath = Path.Combine(outside, "candidate-alias.txt");
        byte[] original = new byte[131_121];
        for (int i = 0; i < original.Length; i++) original[i] = (byte)(i % 251);
        byte[] changed = (byte[])original.Clone(); changed[^1] ^= 1;
        byte[] sentinelBytes = Encoding.ASCII.GetBytes("Owned outside-scope duplicate recheck control; not a selected file.");
        try
        {
            token.ThrowIfCancellationRequested();
            if (DuplicateRecheckPending(report)) return;
            Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
            foreach (string path in new[] { candidatePath, retainedPath })
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(original);
            using (var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(sentinelBytes);
            var inventory = new WindowsDuplicateInventory();
            var snapshot = Query(ct => inventory.ScanAsync(root, ct), token);
            if (DuplicateRecheckPending(report)) return;
            if (snapshot.State is not (CheckState.Available or CheckState.Partial) || snapshot.Items.Count != 1)
            { report.Unknown(check, $"Native sourceState={snapshot.State}; no genuine qualified pair snapshot was returned. No sample evidence or removal claim was substituted."); return; }
            var source = snapshot.Items[0];
            var candidate = source.Entries.SingleOrDefault(entry => PathEquals(entry.Path, candidatePath));
            var retained = source.Entries.SingleOrDefault(entry => PathEquals(entry.Path, retainedPath));
            if (candidate == null || retained == null || candidate.Identity == retained.Identity ||
                candidate.EvidenceKind != DuplicateEvidenceKind.ByteComparedIndependentFile || retained.EvidenceKind != DuplicateEvidenceKind.ByteComparedIndependentFile ||
                candidate.ObservedLinkCount != 1 || retained.ObservedLinkCount != 1)
            { report.Unknown(check, "The actual source did not provide two typed independent single-link fixture observations; no recheck scope was manufactured."); return; }
            var selected = DuplicatePairRecheckScope.Create(root, snapshot, candidate, retained, token);
            if (selected.State is not (CheckState.Available or CheckState.Partial) || selected.Items.Count != 1)
            { report.Unknown(check, "The genuine exact source pair did not create a bounded read-only scope; no action permission was inferred."); return; }
            var scope = selected.Items[0];
            var historical = source.Entries.Select(entry => entry with { }).ToArray();
            var originalCandidateStamp = ShortcutEvidenceReadStamp(candidatePath);
            var originalRetainedStamp = ShortcutEvidenceReadStamp(retainedPath);
            if (originalCandidateStamp.LinkCount != 1 || originalRetainedStamp.LinkCount != 1 ||
                originalCandidateStamp.FileId == originalRetainedStamp.FileId && originalCandidateStamp.Volume == originalRetainedStamp.Volume)
                throw new SmokeAssertionException("duplicate-recheck-owned-native-baseline");
            IDuplicatePairRecheck reader = inventory;
            var equalResult = Query(ct => reader.ReadAsync(scope, ct), token);
            if (DuplicateRecheckPending(report)) return;
            var equal = DuplicateRecheckCompleted(equalResult, DuplicatePairRecheckState.Equal, report);
            if (equal == null) return;
            DuplicateRecheckValidate(equal, scope, originalCandidateStamp, originalRetainedStamp, original.Length);
            DuplicateRecheckAudit(candidatePath, original, retainedPath, original, sentinel, sentinelBytes);
            DuplicateRecheckHistory(source, historical);

            // Only the two exact owned files are opened; success witnesses that ordinary data
            // leases are no longer held. No surviving metadata-handle or inactivity theorem is claimed.
            token.ThrowIfCancellationRequested();
            using (var candidateStream = new FileStream(candidatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var retainedStream = new FileStream(retainedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (candidateStream.Length != original.Length || retainedStream.Length != original.Length)
                    throw new SmokeAssertionException("duplicate-recheck-owned-length-before-change");
                candidateStream.Position = original.Length - 1;
                if (candidateStream.ReadByte() != original[^1]) throw new SmokeAssertionException("duplicate-recheck-owned-tail-before-change");
                candidateStream.Position = original.Length - 1;
                candidateStream.WriteByte(changed[^1]);
                candidateStream.Flush();
            }
            token.ThrowIfCancellationRequested();
            var changedStamp = ShortcutEvidenceReadStamp(candidatePath);
            if (changedStamp.FileId != originalCandidateStamp.FileId || changedStamp.Volume != originalCandidateStamp.Volume || changedStamp.Bytes != original.Length)
                throw new SmokeAssertionException("duplicate-recheck-owned-write-changed-identity-or-length");
            var differentResult = Query(ct => reader.ReadAsync(scope, ct), token);
            if (DuplicateRecheckPending(report)) return;
            var different = DuplicateRecheckCompleted(differentResult, DuplicatePairRecheckState.Different, report);
            if (different == null) return;
            DuplicateRecheckValidate(different, scope, changedStamp, originalRetainedStamp, original.Length);
            DuplicateRecheckAudit(candidatePath, changed, retainedPath, original, sentinel, sentinelBytes);
            DuplicateRecheckHistory(source, historical);

            // Add only an owned alias outside the selected root. The old selection still says
            // one link; a fresh exact-pair read must not reuse that historical independence.
            token.ThrowIfCancellationRequested();
            if (!CreateHardLinkW(aliasPath, candidatePath, 0))
            { report.Unknown(check, "The actual owned hard-link control could not be created; the full current-link boundary remains inconclusive."); return; }
            var linkedStamp = ShortcutEvidenceReadStamp(candidatePath);
            if (linkedStamp.LinkCount != 2)
                throw new SmokeAssertionException("duplicate-recheck-owned-hard-link-count");
            var linkedResult = Query(ct => reader.ReadAsync(scope, ct), token);
            if (DuplicateRecheckPending(report)) return;
            if (linkedResult.Items.Count != 1 || linkedResult.Items[0].CandidateStamp?.LinkCount != 2)
            { report.Unknown(check, "The current pair read did not expose the actual multi-link stamp; source/protection gaps are not a link-boundary pass."); return; }
            var linked = linkedResult.Items[0];
            if (linked.CanRemove || linkedResult.State != CheckState.Unknown ||
                linked.State is DuplicatePairRecheckState.Equal or DuplicatePairRecheckState.Different || linked.ComparedBytes != 0 ||
                linked.CandidateStamp?.Identity != DuplicateRecheckIdentity(linkedStamp) ||
                linked.RetainedStamp?.Identity != DuplicateRecheckIdentity(originalRetainedStamp) ||
                linked.Candidate != scope.Candidate || linked.RetainedEntry != scope.RetainedEntry || linked.SourceCoverageState != scope.SourceCoverageState)
                throw new SmokeAssertionException("duplicate-recheck-hard-link-current-equality");
            bool cancelled = false;
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                try
                {
                    var interrupted = reader.ReadAsync(scope, stop.Token).GetAwaiter().GetResult();
                    cancelled = interrupted.State == CheckState.Cancelled && interrupted.Items.Count == 0;
                }
                catch (OperationCanceledException) { cancelled = true; }
            }
            if (DuplicateRecheckPending(report)) return;
            if (!cancelled) throw new SmokeAssertionException("duplicate-recheck-precancel");
            DuplicateRecheckAudit(candidatePath, changed, retainedPath, original, sentinel, sentinelBytes);
            if (!File.ReadAllBytes(aliasPath).AsSpan().SequenceEqual(changed)) throw new SmokeAssertionException("duplicate-recheck-owned-alias-bytes");
            DuplicateRecheckHistory(source, historical);
            report.Pass(check,
                $"sourceState={snapshot.State}; equalState={equalResult.State}; differentState={differentResult.State}; equalComparedBytes={equal.ComparedBytes}; differentComparedBytes={different.ComparedBytes}; currentExclusionCoverage={equal.CurrentExclusionCoverage}; exclusionStage={equal.Diagnostic?.Stage}; exclusionReason={equal.Diagnostic?.Reason}; exclusionIssues={equal.Diagnostic?.ExclusionIssues}; " +
                "nativeFullIdentitiesMatched=true; equalThenCurrentDifferent=true; exclusiveDataOpenAfterPublication=true; sameLengthOwnedTailChange=true; currentMultiLinkNonEqual=true; preCancel=true; historyUnchanged=true; keeperAndOutsideControlBytesUnchanged=true; CanRemove=false. This is current bounded read evidence only; no keeper protection, inactivity, removal eligibility or reclaimable-space claim is made. No file was deleted or replaced.");
        }
        catch (SmokeAssertionException ex)
        { report.Fail(check, "Owned duplicate-pair recheck invariant " + ex.Code + " failed; no removal or retained-copy protection is validated."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or
            System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException or ArgumentException)
        { report.Unknown(check, $"The owned current-pair fixture could not complete (0x{ex.HResult:X8}); no complete comparison or action proof is inferred. Pending workers retain their resources and require parent-tree retention."); }
        finally { Array.Clear(original); Array.Clear(changed); Array.Clear(sentinelBytes); }
        // No local tree teardown. The parent owns all exact fixture paths and pending-reader retention.
    }

    private static bool DuplicateRecheckPending(SmokeReport report)
    {
        if (!WindowsDuplicateInventory.HasPendingFileRead) return false;
        report.Unknown("duplicate-pair-recheck-fixture", "A duplicate content worker is pending; no further fixture audits, writes or alias creation were attempted. Retain the complete marked parent tree until the worker returns.");
        return true;
    }
    private static DuplicatePairRecheckReport? DuplicateRecheckCompleted(CheckResult<DuplicatePairRecheckReport> result,
        DuplicatePairRecheckState expected, SmokeReport report)
    {
        if (result.Items.Count != 1)
        { report.Unknown("duplicate-pair-recheck-fixture", $"Current state={result.State}; complete exclusions/access or one completed selected-pair report were unavailable. No historical equality was substituted."); return null; }
        var data = result.Items[0];
        var diagnostic = DuplicatePairReadDiagnosticPolicy.Normalize(data.Diagnostic);
        string Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved";
        string code = diagnostic.RegistryNativeResult is int native ? $"0x{unchecked((uint)native):X8}" : "unobserved";
        string details = $"stage={diagnostic.Stage}; reason={diagnostic.Reason}; registryNativeResult={code}; knownFoldersChecked={Number(diagnostic.KnownFoldersChecked)}; registrationKeysVisited={Number(diagnostic.RegistrationKeysVisited)}; locationsAccepted={Number(diagnostic.RegisteredLocationsAccepted)}; exclusionIssues={Number(diagnostic.ExclusionIssues)}; comparedBytes={data.ComparedBytes}. ";
        if (data.State is DuplicatePairRecheckState.Unknown or DuplicatePairRecheckState.Changed)
        { report.Unknown("duplicate-pair-recheck-fixture", details + $"Current state={data.State}; no complete {expected} observation was established. The result remains inconclusive rather than action eligibility."); return null; }
        if (data.State != expected || result.State is not (CheckState.Available or CheckState.Partial))
            throw new SmokeAssertionException("duplicate-recheck-current-result-state");
        if (data.CurrentExclusionCoverage is not (CheckState.Available or CheckState.Partial) ||
            (data.CurrentExclusionCoverage == CheckState.Partial || data.SourceCoverageState == CheckState.Partial) && result.State != CheckState.Partial ||
            diagnostic.Stage == DuplicatePairReadStage.RegistrationExclusions && diagnostic.ExclusionIssues > 0 &&
                (data.CurrentExclusionCoverage != CheckState.Partial || result.State != CheckState.Partial))
            throw new SmokeAssertionException("duplicate-recheck-current-exclusion-coverage");
        return data;
    }
    private static void DuplicateRecheckValidate(DuplicatePairRecheckReport report, DuplicatePairRecheckScope scope,
        ShortcutEvidenceStamp candidate, ShortcutEvidenceStamp retained, long bytes)
    {
        if (report.CanRemove || report.Candidate != scope.Candidate || report.RetainedEntry != scope.RetainedEntry ||
            report.SourceCoverageState != scope.SourceCoverageState || report.ComparedBytes != checked(2 * bytes) ||
            report.CandidateStamp == null || report.RetainedStamp == null ||
            report.CandidateStamp.Identity != DuplicateRecheckIdentity(candidate) || report.RetainedStamp.Identity != DuplicateRecheckIdentity(retained) ||
            report.CandidateStamp.Bytes != bytes || report.RetainedStamp.Bytes != bytes || report.CandidateStamp.LinkCount != 1 || report.RetainedStamp.LinkCount != 1 ||
            report.StartedUtc.Offset != TimeSpan.Zero || report.CompletedUtc.Offset != TimeSpan.Zero || report.StartedUtc < scope.ValidatedUtc ||
            report.StartedUtc > report.CompletedUtc || report.CompletedUtc > DateTimeOffset.UtcNow)
            throw new SmokeAssertionException("duplicate-recheck-current-native-contract");
    }
    private static string DuplicateRecheckIdentity(ShortcutEvidenceStamp stamp)
    {
        byte[] raw = stamp.FileId.ToByteArray();
        try { return $"{stamp.Volume:X16}:{BinaryPrimitives.ReadUInt64LittleEndian(raw):X16}{BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(8)):X16}"; }
        finally { Array.Clear(raw); }
    }
    private static void DuplicateRecheckAudit(string candidate, byte[] candidateBytes, string retained, byte[] retainedBytes, string sentinel, byte[] sentinelBytes)
    {
        if (!File.ReadAllBytes(candidate).AsSpan().SequenceEqual(candidateBytes) ||
            !File.ReadAllBytes(retained).AsSpan().SequenceEqual(retainedBytes) ||
            !File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(sentinelBytes))
            throw new SmokeAssertionException("duplicate-recheck-owned-byte-audit");
    }
    private static void DuplicateRecheckHistory(DuplicateReport source, IReadOnlyList<DuplicateEntry> expected)
    {
        if (source.Entries.Count != expected.Count || !source.Entries.SequenceEqual(expected))
            throw new SmokeAssertionException("duplicate-recheck-mutated-historical-snapshot");
    }
}
