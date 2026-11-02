using System.Diagnostics;
using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool SystemToolJournalCrossInstanceCleanupConfirmed { get; set; } = true;
    private static SystemToolJournalStore? RetainedCrossInstanceStore;

    private static void SystemToolJournalCrossInstanceFixture(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-journal-cross-instance-fixture";
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        var children = new List<InertOwnedChild>(2);
        var prepared = new List<string>(2);
        InertApphostLease? apphost = null;
        SystemToolJournalStore? store = null;
        bool began = false, blocked = false, reopened = false, unchanged = false;
        bool previousUncertain = !SystemToolJournalCrossInstanceCleanupConfirmed || RetainedCrossInstanceStore != null ||
            !SystemToolProcessObservationCleanupConfirmed || UnconfirmedInertChildren.Count != 0 ||
            UnconfirmedInertApphost != null || SystemToolJournalStore.HasUncertainCleanup;
        string stage = "initializing";
        int? nativeCode = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (previousUncertain || !InertProcessChild.IsValidFixture(repository, fixture, marker))
                throw new InertObservationUnavailable("journal-cross-instance-prerequisite-unavailable");
            apphost = InertApphostLease.Open();
            began = true; SystemToolJournalCrossInstanceCleanupConfirmed = false;
            // Prepare fixed owned child directories before holding the store's root ancestry leases.
            prepared.Add(PrepareInertChild(repository, fixture, marker));
            prepared.Add(PrepareInertChild(repository, fixture, marker));
            stage = "parent-primary-open";
            store = SystemToolJournalStore.OpenOwnedFixture(fixture, marker, token: token);
            if (!store.WriteAndReadFixedIntent(token)) throw new InertObservationUnavailable("journal-parent-fixed-readback-unavailable");
            string intentPath = Path.Combine(fixture, SystemToolJournalStore.FixtureLeafName(SystemToolJournalFixtureCase.Primary), "intent.bin");
            var baseline = ShortcutEvidenceReadStamp(intentPath);

            stage = "contention-child";
            var contender = StartInert("journal-contention", repository, fixture, marker, apphost.Path, children, prepared[0]);
            WaitInertReady(contender, token);
            var contention = SystemToolProcessObserver.ObserveAsync(contender.Process, token).GetAwaiter().GetResult();
            if (contention.State != ClientProcessObservationState.Exited || contention.ExitCode != 0)
                throw new InertObservationUnavailable("journal-contention-fixed-branch-unobserved");
            RequireInertExit(contender, contention, 0);
            if (!AuditInertChild(repository, fixture, marker, contender))
                throw new InertObservationUnavailable("journal-contention-child-audit-unavailable");
            blocked = true;
            if (!store.WriteAndReadFixedIntent(token) || ShortcutEvidenceReadStamp(intentPath) != baseline)
                throw new SmokeAssertionException("journal-primary-changed-during-peer-contention");
            store.Dispose();
            if (SystemToolJournalStore.HasUncertainCleanup)
            {
                RetainedCrossInstanceStore = store; store = null;
                throw new InertObservationUnavailable("journal-parent-release-unconfirmed");
            }
            store = null;

            stage = "existing-read-child";
            var reader = StartInert("journal-read", repository, fixture, marker, apphost.Path, children, prepared[1]);
            WaitInertReady(reader, token);
            var observedRead = SystemToolProcessObserver.ObserveAsync(reader.Process, token).GetAwaiter().GetResult();
            if (observedRead.State != ClientProcessObservationState.Exited || observedRead.ExitCode != 0)
                throw new InertObservationUnavailable("journal-existing-read-fixed-branch-unobserved");
            RequireInertExit(reader, observedRead, 0);
            if (!AuditInertChild(repository, fixture, marker, reader))
                throw new InertObservationUnavailable("journal-existing-read-child-audit-unavailable");
            reopened = true;
            stage = "parent-final-fixed-readback";
            store = SystemToolJournalStore.OpenOwnedFixture(fixture, marker, token: token);
            if (store.CreatedIntentFile || !store.WriteAndReadFixedIntent(token) || ShortcutEvidenceReadStamp(intentPath) != baseline)
                throw new SmokeAssertionException("journal-primary-recreated-or-changed-after-peer-read");
            unchanged = true;
            stage = "cross-instance-observed";
        }
        catch (SmokeAssertionException failure)
        {
            report.Fail(check, "Owned cross-instance journal invariant " + failure.Code + " failed; no production journal or servicing result is validated.");
            stage = "fixed-invariant-failed";
        }
        catch (SystemToolJournalStoreFailure failure)
        { stage = "journal-store-" + failure.Reason; nativeCode = failure.NativeCode; }
        catch (Exception exception) when (exception is InertObservationUnavailable or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or OperationCanceledException or
            System.Security.SecurityException or SystemToolTrustFailure)
        { stage = exception is InertObservationUnavailable safe ? safe.Stage : "cross-instance-prerequisite-or-observation-unavailable"; }
        finally
        {
            bool actualExits = true, audited = true;
            foreach (var child in children)
            {
                bool exited = false;
                try
                {
                    // Journal modes have no release signal and no kill path. Keep the primary
                    // store held until this known child actually exits or ownership is retained.
                    exited = child.Process.WaitForExit(12_000) && child.Process.HasExited;
                    if (!exited || !AuditInertChild(repository, fixture, marker, child)) audited = false;
                }
                catch (Exception) { audited = false; }
                if (exited)
                {
                    try { child.Process.Dispose(); }
                    catch (Exception) { audited = false; if (UnconfirmedInertChildren.Count < 4) UnconfirmedInertChildren.Add(child.Process); }
                }
                else
                {
                    actualExits = false;
                    if (UnconfirmedInertChildren.Count < 4) UnconfirmedInertChildren.Add(child.Process);
                }
            }
            // Prepared but never-started directories contain only the exact owned marker.
            foreach (string child in prepared.Where(path => !children.Any(started => PathEquals(started.Directory, path))))
            {
                try { if (!InertProcessChild.IsValidChild(repository, child, marker, requireOwnerOnly: true)) audited = false; }
                catch (Exception) { audited = false; }
            }
            if (actualExits)
            {
                try
                {
                    store?.Dispose();
                    if (SystemToolJournalStore.HasUncertainCleanup && store != null) RetainedCrossInstanceStore = store;
                    store = null;
                }
                catch (Exception) { audited = false; if (store != null) RetainedCrossInstanceStore = store; store = null; }
            }
            else if (store != null) { RetainedCrossInstanceStore = store; store = null; }
            bool safe = !previousUncertain && actualExits && audited && !SystemToolJournalStore.HasUncertainCleanup;
            if (safe)
            {
                try { apphost?.Dispose(); apphost = null; }
                catch (Exception) { safe = false; }
            }
            if (!safe)
            {
                if (store != null) { RetainedCrossInstanceStore = store; store = null; }
                if (apphost != null) { UnconfirmedInertApphost = apphost; apphost = null; }
            }
            SystemToolJournalCrossInstanceCleanupConfirmed = safe && (!began || prepared.Count == 2);
        }
        if (blocked && reopened && unchanged && SystemToolJournalCrossInstanceCleanupConfirmed)
            report.Pass(check,
                "actualChildren=2; contentionChildExit0=true; exactStoreBusyNative32Branch=true; parentReleasedBeforeReadChild=true; existingReadChildExit0=true; existingObjectIdentityAndLengthMatched=true; fixedParentReadbackUnchanged=true; childAndNativeCleanupConfirmed=true. The fixed factory still attempts CREATE_NEW before OPEN_EXISTING; the read child required an existing anchored object and CreatedIntentFile=false. No payload repair/reset, crash recovery, durability, production store, UAC, servicing or installation is established.");
        else if (stage != "fixed-invariant-failed")
            report.Unknown(check, $"stage={stage}; nativeCode={nativeCode}; contentionObserved={blocked}; existingPeerReadObserved={reopened}; fixedReadbackUnchanged={unchanged}; cleanupConfirmed={SystemToolJournalCrossInstanceCleanupConfirmed}. No exit code was interpreted as tool health or production recovery; retain the complete marked tree whenever participant/native cleanup is uncertain.");
    }
}
