using System.ComponentModel;
using System.IO;
using System.Text;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void ShortcutSelectedRecheckFixture(string root, string selectedPath, string restoredTarget,
        CheckResult<ShortcutReport> snapshot, SmokeReport report, CancellationToken token)
    {
        const string check = "shortcut-selected-recheck-fixture";
        byte[]? originalLink = null;
        try
        {
            var sourceReport = snapshot.Items[0];
            var historical = sourceReport.Entries.FirstOrDefault(entry => PathEquals(entry.ShortcutPath, selectedPath));
            if (historical == null || historical.State != ShortcutTargetState.LocalTargetMissing ||
                !PathEquals(historical.TargetPath, restoredTarget))
            {
                report.Unknown(check, "The earlier actual scan did not establish the exact owned missing-local fixture observation. No new scan, sample observation or target creation substituted for it.");
                return;
            }
            var historicalRows = sourceReport.Entries.ToArray();
            var prepared = ShortcutRecheckScope.Create(root, snapshot, historical, token);
            if (prepared.Items.Count != 1 || prepared.State != snapshot.State)
            {
                report.Unknown(check, $"Owned recheck scope state={prepared.State}; no exact selected metadata read or external restore was started.");
                return;
            }
            var scope = prepared.Items[0];
            if (!PathEquals(scope.RootPath, root) || !PathEquals(scope.ShortcutPath, selectedPath) ||
                scope.PriorObservation != historical || ReferenceEquals(scope.PriorObservation, historical) || scope.SourceCoverageState != snapshot.State)
            {
                report.Fail(check, "The selected recheck scope did not preserve the exact immutable historical selection and source coverage; no native recheck started.");
                return;
            }
            originalLink = File.ReadAllBytes(selectedPath); // Owned link audit only, never exported.
            IShortcutRecheck reader = new WindowsShortcutInventory();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var stopped = reader.ReadAsync(scope, cancelled.Token).GetAwaiter().GetResult();
                if (stopped.State != CheckState.Cancelled || stopped.Items.Count != 0)
                {
                    report.Fail(check, "Pre-cancelled selected recheck did not remain Cancelled/empty; no actual selected read or target creation started.");
                    return;
                }
            }
            var firstStart = DateTimeOffset.UtcNow;
            var first = Query(ct => reader.ReadAsync(scope, ct), token);
            var firstEnd = DateTimeOffset.UtcNow;
            if (!KnownSelectedRead(first, scope, selectedPath, restoredTarget, firstStart, firstEnd, ShortcutTargetState.LocalTargetMissing, report, check)) return;

            // External-change simulation only after the original no-mutation check and fresh
            // known-missing observation. CreateNew cannot overwrite a newly appeared fixture.
            byte[] restoredBytes = Encoding.ASCII.GetBytes("Owned external-restore simulation; never launched.");
            try
            {
                using (var ownedTarget = new FileStream(restoredTarget, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    ownedTarget.Write(restoredBytes);
                var secondStart = DateTimeOffset.UtcNow;
                var second = Query(ct => reader.ReadAsync(scope, ct), token);
                var secondEnd = DateTimeOffset.UtcNow;
                if (!KnownSelectedRead(second, scope, selectedPath, restoredTarget, secondStart, secondEnd, ShortcutTargetState.TargetExists, report, check)) return;
                if (second.Items[0].CheckedAt < first.Items[0].CheckedAt || historical.State != ShortcutTargetState.LocalTargetMissing ||
                    scope.PriorObservation != historical || !sourceReport.Entries.SequenceEqual(historicalRows))
                {
                    report.Fail(check, "Fresh selected observations altered the historical snapshot or contradicted query-time order. No file-identity continuity or obsolescence claim is made.");
                    return;
                }
                byte[] currentLink = File.ReadAllBytes(selectedPath), currentTarget = File.ReadAllBytes(restoredTarget);
                try
                {
                    if (!currentLink.SequenceEqual(originalLink) || !currentTarget.SequenceEqual(restoredBytes))
                    {
                        report.Fail(check, "Owned shortcut bytes or externally created target bytes changed during selected read-only rechecks.");
                        return;
                    }
                }
                finally { Array.Clear(currentLink); Array.Clear(currentTarget); }
                report.Pass(check, $"Source coverage={snapshot.State}; firstReadState={first.State}; secondReadState={second.State}; selectedReadCount=2; preCancellationObserved=true; missingThenExistsVerified=true; historicalSnapshotUnchanged=true; shortcutBytesUnchanged=true; ownedTargetPreserved=true; queryTimeBoundsVerified=true. " +
                    "The same exact selected scope was reused after a developer-owned CreateNew target restore simulation. No folder rescan, target launch/resolution, network lookup or removal occurred. No paths, identities, timestamps, link bytes or provider messages exported; availability observations do not establish identity continuity, obsolescence or removal safety.");
            }
            finally { Array.Clear(restoredBytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException)
        {
            report.Unknown(check, $"Owned selected recheck fixture did not complete (code 0x{ex.HResult:X8}); no missing-to-existing native proof is claimed. The owned target, if created, is left for normal ownership-checked fixture teardown.");
        }
        finally { if (originalLink != null) Array.Clear(originalLink); }
    }

    private static bool KnownSelectedRead(CheckResult<ShortcutObservation> result, ShortcutRecheckScope scope, string selectedPath,
        string target, DateTimeOffset start, DateTimeOffset end, ShortcutTargetState expected, SmokeReport report, string check)
    {
        if (result.State is not (CheckState.Available or CheckState.Partial) || result.Items.Count == 0 ||
            result.Items.Count == 1 && result.Items[0].State is ShortcutTargetState.Unknown or ShortcutTargetState.Unsupported or ShortcutTargetState.UnresolvedMetadata)
        {
            report.Unknown(check, $"Selected native readState={result.State}; observationCount={result.Items.Count}; required local availability evidence remains unverified. No absence or broken-shortcut conclusion substituted.");
            return false;
        }
        if (result.Items.Count != 1 || result.Items[0].State != expected || !PathEquals(result.Items[0].ShortcutPath, selectedPath) ||
            !PathEquals(result.Items[0].TargetPath, target) || result.Items[0].Source != scope.PriorObservation.Source ||
            result.Items[0].CheckedAt.Offset != TimeSpan.Zero || result.Items[0].CheckedAt < start || result.Items[0].CheckedAt > end ||
            result.Items[0].CheckedAt < scope.ValidatedUtc || scope.SourceCoverageState == CheckState.Partial && result.State != CheckState.Partial)
        {
            report.Fail(check, "The selected native result contradicted known owned target presence, exact selection/source/query-time bounds or retained Partial coverage. All actual values withheld.");
            return false;
        }
        return true;
    }
}
