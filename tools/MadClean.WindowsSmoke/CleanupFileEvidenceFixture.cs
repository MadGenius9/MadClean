using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool CleanupFileEvidenceCleanupConfirmed { get; set; } = true;

    private static void CleanupFileEvidenceFixture(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "cleanup-file-evidence-fixture";
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        string basePath = Path.Combine(fixture, "cleanup-file-evidence"), root = Path.Combine(basePath, "user-temp");
        string outside = Path.Combine(basePath, "outside-control"), selectedPath = Path.Combine(root, "selected.tmp");
        string changedPath = Path.Combine(root, "changed.tmp"), multiPath = Path.Combine(root, "multi.tmp");
        string originalDirectory = Path.Combine(root, "redirect-control"), movedDirectory = Path.Combine(root, "redirect-original");
        string redirectPath = Path.Combine(originalDirectory, "outside.tmp"), sentinel = Path.Combine(outside, "outside.tmp");
        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var links = new List<string>();
        bool previousUncertain = !CleanupFileEvidenceCleanupConfirmed || WindowsCleanupFileEvidence.HasPendingMetadataRead;
        bool created = false, completed = false, pending = false, reparseCreated = false, reparseBlocked = false;
        bool changedObserved = false, exclusionBlocked = false, hardLinkBlocked = false, preCancel = false;
        CheckState writerState = CheckState.Unknown;
        string stage = "initializing";
        try
        {
            token.ThrowIfCancellationRequested();
            if (previousUncertain || !InertProcessChild.IsValidFixture(repository, fixture, marker))
                throw new InertObservationUnavailable("cleanup-evidence-prerequisite-unavailable");
            if (Directory.Exists(basePath)) throw new InertObservationUnavailable("cleanup-evidence-owned-root-already-exists");
            CleanupFileEvidenceCleanupConfirmed = false;
            Directory.CreateDirectory(root); Directory.CreateDirectory(outside); Directory.CreateDirectory(originalDirectory); created = true;
            foreach (string path in new[] { selectedPath, changedPath, multiPath, redirectPath, sentinel })
            {
                byte[] bytes = Encoding.ASCII.GetBytes("Inert owned cleanup evidence bytes; no provider mutation.");
                expected.Add(path, bytes);
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
            }
            var settings = new CleanupExclusionStore(Path.Combine(basePath, "cleanup-exclusions.json"));
            if (settings.Save([]) != null) throw new InertObservationUnavailable("cleanup-evidence-settings-unavailable");
            stage = "genuine-core-inventory";
            var snapshot = Query(ct => new CleanupScanner().ScanAsync([new("User temporary files", root)], ct), token);
            var completedUtc = DateTimeOffset.UtcNow;
            if (snapshot.Issues.Count != 0 || snapshot.Truncated)
                throw new InertObservationUnavailable("cleanup-evidence-genuine-inventory-incomplete");
            CleanupFileEvidenceScope Scope(string path)
            {
                var selected = snapshot.Candidates.SingleOrDefault(candidate => PathEquals(candidate.Path, path));
                var result = CleanupFileEvidenceScope.Create(snapshot, selected, completedUtc, [], DateTimeOffset.UtcNow, token);
                if (result.State is not (CheckState.Available or CheckState.Partial) || result.Items.Count != 1)
                    throw new InertObservationUnavailable("cleanup-evidence-genuine-selection-unavailable");
                return result.Items[0];
            }
            var selectedScope = Scope(selectedPath); var changedScope = Scope(changedPath);
            var multiScope = Scope(multiPath); var redirectScope = Scope(redirectPath);
            var provider = new WindowsCleanupFileEvidence();
            var baseline = CleanupEvidenceNativeStamp(selectedPath, false);
            var rootBaseline = CleanupEvidenceNativeStamp(root, true);
            stage = "selected-metadata";
            var first = Query(ct => provider.ReadOwnedFixtureAsync(selectedScope, fixture, marker, ct), token);
            if (CleanupEvidencePending(report)) { pending = true; return; }
            var evidence = CleanupEvidenceRequireReport(first);
            CleanupEvidenceValidate(evidence, selectedScope, baseline, rootBaseline, root);
            if (!evidence.HistoricalPathMatches || !evidence.HistoricalSizeMatches || !evidence.HistoricalModifiedMatches)
                throw new SmokeAssertionException("cleanup-evidence-history-fields");
            CleanupEvidenceAudit(expected);

            stage = "changed-current-size";
            // Modify exactly one known owned file, retaining its old modification timestamp.
            using (var data = new FileStream(changedPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                if (data.Length != expected[changedPath].Length) throw new SmokeAssertionException("cleanup-evidence-size-before-owned-change");
                data.Position = data.Length; data.WriteByte(0x41); data.Flush(true);
            }
            byte[] extended = new byte[expected[changedPath].Length + 1]; expected[changedPath].CopyTo(extended, 0); extended[^1] = 0x41;
            Array.Clear(expected[changedPath]); expected[changedPath] = extended;
            File.SetLastWriteTimeUtc(changedPath, changedScope.Candidate.ModifiedUtc);
            var changedStamp = CleanupEvidenceNativeStamp(changedPath, false);
            var changed = Query(ct => provider.ReadOwnedFixtureAsync(changedScope, fixture, marker, ct), token);
            if (CleanupEvidencePending(report)) { pending = true; return; }
            var changedEvidence = CleanupEvidenceRequireReport(changed);
            CleanupEvidenceValidate(changedEvidence, changedScope, changedStamp, rootBaseline, root);
            if (changed.State != CheckState.Partial || changedEvidence.HistoricalSizeMatches || !changedEvidence.HistoricalModifiedMatches ||
                changedEvidence.File.Bytes != changedScope.Candidate.Bytes + 1 || changedScope.Candidate.Bytes != expected[changedPath].Length - 1)
                throw new SmokeAssertionException("cleanup-evidence-current-size-not-historical");
            changedObserved = true;
            CleanupEvidenceAudit(expected);

            stage = "fresh-user-exclusion";
            if (settings.Save([new(selectedPath, false)]) != null) throw new InertObservationUnavailable("cleanup-evidence-exclusion-save-unavailable");
            var excluded = Query(ct => provider.ReadOwnedFixtureAsync(selectedScope, fixture, marker, ct), token);
            if (CleanupEvidencePending(report)) { pending = true; return; }
            if (excluded.Items.Count != 0 || excluded.State is CheckState.Available or CheckState.Partial)
                throw new SmokeAssertionException("cleanup-evidence-new-exclusion-ignored");
            exclusionBlocked = true;
            if (settings.Save([]) != null) throw new InertObservationUnavailable("cleanup-evidence-exclusion-reset-unavailable");
            CleanupEvidenceAudit(expected);

            stage = "current-multiple-links";
            string alias = Path.Combine(outside, "multi-alias.tmp");
            if (!CreateHardLinkW(alias, multiPath, 0)) throw new InertObservationUnavailable("cleanup-evidence-hardlink-capability-unavailable");
            expected.Add(alias, (byte[])expected[multiPath].Clone());
            if (CleanupEvidenceNativeStamp(multiPath, false).Links != 2) throw new SmokeAssertionException("cleanup-evidence-owned-hardlink-count");
            var multi = Query(ct => provider.ReadOwnedFixtureAsync(multiScope, fixture, marker, ct), token);
            if (CleanupEvidencePending(report)) { pending = true; return; }
            if (multi.Items.Count != 0 || multi.State is CheckState.Available or CheckState.Partial)
                throw new SmokeAssertionException("cleanup-evidence-current-hardlink-qualified");
            hardLinkBlocked = true;
            CleanupEvidenceAudit(expected);

            stage = "ordinary-writer-observation";
            using (var writer = new FileStream(selectedPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var observedWriter = Query(ct => provider.ReadOwnedFixtureAsync(selectedScope, fixture, marker, ct), token);
                if (CleanupEvidencePending(report)) { pending = true; return; }
                writerState = observedWriter.State;
                if (observedWriter.Items.Any(item => item.CanDelete || item.ActiveUse != "Unknown"))
                    throw new SmokeAssertionException("cleanup-evidence-writer-inactive-claim");
                // READ_ATTRIBUTES is not data-read access; coexistence is an honest observation,
                // not a failing lock control or evidence that a file is inactive.
                if (observedWriter.State is CheckState.Available or CheckState.Partial)
                    CleanupEvidenceValidate(CleanupEvidenceRequireReport(observedWriter), selectedScope, baseline, rootBaseline, root);
            }
            CleanupEvidenceAudit(expected);
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                try
                {
                    var cancelled = provider.ReadOwnedFixtureAsync(selectedScope, fixture, marker, stop.Token).GetAwaiter().GetResult();
                    preCancel = cancelled.State == CheckState.Cancelled && cancelled.Items.Count == 0;
                }
                catch (OperationCanceledException) { preCancel = true; }
            }
            if (CleanupEvidencePending(report)) { pending = true; return; }
            if (!preCancel) throw new SmokeAssertionException("cleanup-evidence-precancel");

            stage = "owned-reparse-escape";
            // Move only the exact owned directory, preserving its file; then put a symlink at
            // the historical path. Its target is another owned control, outside selected Temp.
            Directory.Move(originalDirectory, movedDirectory);
            expected.Add(Path.Combine(movedDirectory, "outside.tmp"), expected[redirectPath]); expected.Remove(redirectPath);
            try { Directory.CreateSymbolicLink(originalDirectory, outside); links.Add(originalDirectory); reparseCreated = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            { /* Unsupported link capability remains unverified, never a positive reparse proof. */ }
            if (reparseCreated)
            {
                var redirected = Query(ct => provider.ReadOwnedFixtureAsync(redirectScope, fixture, marker, ct), token);
                if (CleanupEvidencePending(report)) { pending = true; return; }
                if (redirected.Items.Count != 0 || redirected.State is CheckState.Available or CheckState.Partial)
                    throw new SmokeAssertionException("cleanup-evidence-reparse-escape-qualified");
                reparseBlocked = true;
            }
            CleanupEvidenceAudit(expected);
            completed = true; stage = "owned-metadata-observed";
        }
        catch (SmokeAssertionException failure)
        { stage = "fixed-invariant-failed"; report.Fail(check, "Owned cleanup evidence invariant " + failure.Code + " failed; no cleanup action is validated."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Security.SecurityException or InertObservationUnavailable)
        { stage = exception is InertObservationUnavailable safe ? safe.Stage : "cleanup-evidence-prerequisite-or-observation-unavailable"; }
        finally
        {
            pending |= WindowsCleanupFileEvidence.HasPendingMetadataRead;
            bool safe = !previousUncertain && !pending;
            if (safe)
            {
                foreach (string link in links)
                {
                    try
                    {
                        if (!PathEquals(link, originalDirectory) || !PathEquals(Path.GetDirectoryName(link)!, root) ||
                            (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0 ||
                            !PathEquals(new DirectoryInfo(link).LinkTarget ?? "", outside)) throw new IOException("Owned link identity unavailable.");
                        Directory.Delete(link, recursive: false);
                    }
                    catch (Exception) { safe = false; }
                }
                if (created)
                {
                    try { CleanupEvidenceAudit(expected); }
                    catch (Exception) { safe = false; }
                }
            }
            CleanupFileEvidenceCleanupConfirmed = safe;
            foreach (byte[] bytes in expected.Values) Array.Clear(bytes);
        }
        if (completed && changedObserved && exclusionBlocked && hardLinkBlocked && preCancel && CleanupFileEvidenceCleanupConfirmed)
            report.Pass(check, $"source=OwnedDevelopmentFixture; genuineCoreInventory=true; fullFileAndRootIdentitiesAndStampsMatched=true; historicalMetadataMatched=true; changedSizeObserved=true; freshExclusionBlocked=true; currentHardlinkBlocked=true; writerObservationState={writerState}; activeUse=Unknown; preCancel=true; createdReparseCount={(reparseCreated ? 1 : 0)}; reparseBlocked={reparseBlocked}; ownedBytesUnchangedExceptExplicitFixtureSizeChange=true; cleanupConfirmed=true; CanDelete=false. Uncreated reparse cases remain unverified. No production Temp inspection, content read by provider, deletion, inactivity, historical identity continuity or reclaimed-space claim.");
        else if (stage != "fixed-invariant-failed")
            report.Unknown(check, $"stage={stage}; pending={pending}; changedSizeObserved={changedObserved}; exclusionBlocked={exclusionBlocked}; hardlinkBlocked={hardLinkBlocked}; preCancel={preCancel}; cleanupConfirmed={CleanupFileEvidenceCleanupConfirmed}. No sample evidence or action eligibility was substituted; retain the full owned fixture tree on pending or uncertain cleanup.");
    }

    private static bool CleanupEvidencePending(SmokeReport report)
    {
        if (!WindowsCleanupFileEvidence.HasPendingMetadataRead) return false;
        report.Unknown("cleanup-file-evidence-fixture", "A cleanup metadata worker remains pending; no further fixture audit, mutation or link teardown was attempted. Worker resources and the full owned parent tree must remain intact.");
        return true;
    }
    private static CleanupFileEvidenceReport CleanupEvidenceRequireReport(CheckResult<CleanupFileEvidenceReport> result)
    {
        if (result.State is not (CheckState.Available or CheckState.Partial) || result.Items.Count != 1)
            throw new InertObservationUnavailable("cleanup-evidence-completed-measurement-unavailable");
        return result.Items[0];
    }
    private static void CleanupEvidenceValidate(CleanupFileEvidenceReport data, CleanupFileEvidenceScope scope,
        CleanupEvidenceAuditStamp expected, CleanupEvidenceAuditStamp root, string rootPath)
    {
        if (data.SourceKind != CleanupEvidenceSourceKind.OwnedDevelopmentFixture || data.CanDelete || data.ActiveUse != "Unknown" ||
            data.Prior != scope.Candidate || data.RootKind != scope.RootKind || data.SourceCoverageState != scope.SourceCoverageState ||
            data.ExclusionCoverageState is not (CheckState.Available or CheckState.Partial) || !PathEquals(data.RootPath, rootPath) ||
            data.RootIdentity != root.Identity || !PathEquals(data.File.Path, scope.Candidate.Path) || data.File.Identity != expected.Identity ||
            data.File.Bytes != expected.Bytes || data.File.LinkCount != expected.Links || expected.Links != 1 || data.File.Attributes != expected.Attributes ||
            data.File.CreationUtc != expected.Creation || data.File.LastWriteUtc != expected.Write || data.File.ChangeUtc != expected.Change ||
            data.QueryStartedUtc.Offset != TimeSpan.Zero || data.QueryCompletedUtc.Offset != TimeSpan.Zero ||
            data.QueryStartedUtc < scope.ValidatedUtc || data.QueryCompletedUtc < data.QueryStartedUtc || data.QueryCompletedUtc > DateTimeOffset.UtcNow ||
            !data.HistoricalPathMatches || data.Coverage.Count == 0)
            throw new SmokeAssertionException("cleanup-evidence-native-report-contract");
    }
    private sealed record CleanupEvidenceAuditStamp(string Identity, long Bytes, uint Links, uint Attributes,
        DateTimeOffset Creation, DateTimeOffset Write, DateTimeOffset Change);
    private static CleanupEvidenceAuditStamp CleanupEvidenceNativeStamp(string path, bool directory)
    {
        using var handle = ShortcutEvidenceCreateFileW(path, 0x80, 7, IntPtr.Zero, 3,
            0x00200000 | 0x00100000 | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (handle.IsInvalid) throw new InertObservationUnavailable("cleanup-evidence-native-audit-open-unavailable");
        SystemToolTrustNative.ValidateAttributes(handle, directory); SystemToolTrustNative.ValidateFinalPath(handle, path);
        if (!ShortcutEvidenceGetIdentity(handle, 18, out var identity, 24) || identity.Id == Guid.Empty ||
            !ShortcutEvidenceGetStandard(handle, 1, out var size, 24) || size.DeletePending || size.Directory != directory ||
            !CleanupEvidenceGetBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<CleanupEvidenceBasic>()))
            throw new InertObservationUnavailable("cleanup-evidence-native-audit-unavailable");
        return new(identity.Volume.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(identity.Id.ToByteArray()),
            size.EndOfFile, size.NumberOfLinks, basic.Attributes, DateTimeOffset.FromFileTime(basic.Creation), DateTimeOffset.FromFileTime(basic.Write), DateTimeOffset.FromFileTime(basic.Change));
    }
    private static void CleanupEvidenceAudit(IReadOnlyDictionary<string, byte[]> expected)
    {
        foreach (var entry in expected)
        {
            byte[] actual = File.ReadAllBytes(entry.Key);
            try { if (!actual.AsSpan().SequenceEqual(entry.Value)) throw new SmokeAssertionException("cleanup-evidence-owned-bytes-changed"); }
            finally { Array.Clear(actual); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CleanupEvidenceBasic
    { public long Creation, Access, Write, Change; public uint Attributes; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CleanupEvidenceGetBasic(SafeFileHandle handle, int kind, out CleanupEvidenceBasic basic, uint bytes);
}
