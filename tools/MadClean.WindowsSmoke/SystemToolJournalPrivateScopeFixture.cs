using System.Globalization;
using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private enum PrivateJournalScopeFixtureStage
    {
        OwnershipPrerequisites, ClosedProductionWriter, FixedPathAndFreshScope, ExistingOnlyAbsence, CreateOwnedScope,
        ReadEmptyHistory, ContendedRead, BeginFirstIntent, ReadUnresolvedHistory,
        AppendObservedAndExit, ReadTerminalHistory, CloseFirstStore, ReopenExisting,
        ReadExistingTerminal, RejectReadonlyIntent, CloseReadonlyStore, ReopenWritable,
        BeginSecondIntent, AppendObservationLost, CloseUnresolvedStore, ReopenUnresolved,
        VerifyUnresolvedBlocks, CloseFinalStore, FinalContract
    }

    public static bool SystemToolJournalPrivateScopeCleanupConfirmed { get; private set; } = true;

    private static void SystemToolJournalPrivateScopeFixture(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-private-journal-scope";
        SystemToolJournalPrivateStore? store = null;
        SystemToolJournalPrivateScopeCleanupConfirmed = false;
        bool noCreationRead = false, privatePathPolicy = false, emptyObserved = false, busyBlocked = false;
        bool intentBlocks = false, terminalObserved = false, reopened = false, lossBlocks = false;
        bool productionRejectedWithoutStore = false;
        string? directory = null;
        var stage = PrivateJournalScopeFixtureStage.OwnershipPrerequisites;
        try
        {
            token.ThrowIfCancellationRequested();
            if (SystemToolJournalStore.HasUncertainCleanup || !InertProcessChild.IsValidFixture(Path.GetFullPath(Environment.CurrentDirectory), fixture, marker))
                throw new SmokeAssertionException("private-journal-fixture-owner");
            stage = PrivateJournalScopeFixtureStage.ClosedProductionWriter;
            if (SystemToolJournalPrivateStore.ProductionCreationValidated)
                throw new SmokeAssertionException("private-journal-production-guard-open");
            try { using var unexpected = SystemToolJournalPrivateStore.OpenForSelectedAction(token); }
            catch (SystemToolJournalStoreFailure failure) when (failure.Reason == SystemToolJournalStoreReason.ScopeUnsupported)
            { productionRejectedWithoutStore = true; }
            if (!productionRejectedWithoutStore) throw new SmokeAssertionException("private-journal-production-guard-refusal");
            stage = PrivateJournalScopeFixtureStage.FixedPathAndFreshScope;
            directory = Path.Combine(fixture, "system-tool-private-journal-v1");
            if (Directory.Exists(directory)) throw new SmokeAssertionException("private-journal-fixture-preexists");
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify), "MadClean", "operation-journal-v1");
            privatePathPolicy = SystemToolJournalPrivateStore.FixedUserDirectory == expected && Path.IsPathFullyQualified(expected);
            // The production path is derived only; neither production factory is invoked.
            stage = PrivateJournalScopeFixtureStage.ExistingOnlyAbsence;
            try { using var absent = SystemToolJournalPrivateStore.OpenOwnedFixture(fixture, marker, false, token); }
            catch (SystemToolJournalStoreFailure) { noCreationRead = !Directory.Exists(directory); }
            if (!noCreationRead || !privatePathPolicy) throw new SmokeAssertionException("private-journal-existing-only-absence");
            stage = PrivateJournalScopeFixtureStage.CreateOwnedScope;
            store = SystemToolJournalPrivateStore.OpenOwnedFixture(fixture, marker, true, token);
            stage = PrivateJournalScopeFixtureStage.ReadEmptyHistory;
            var empty = store.ReadHistory(DateTimeOffset.UtcNow, token);
            emptyObserved = empty.State == SystemToolJournalHistoryState.NoRecordsObserved && empty.EnumerationComplete;
            stage = PrivateJournalScopeFixtureStage.ContendedRead;
            try { using var conflict = SystemToolJournalPrivateStore.OpenOwnedFixture(fixture, marker, false, token); }
            catch (SystemToolJournalStoreFailure failure) { busyBlocked = failure.Reason == SystemToolJournalStoreReason.StoreBusy && failure.NativeCode == 32; }
            var intent = new SystemToolJournalEntry(Guid.NewGuid(), SystemToolOperation.DismCheckHealth, 1,
                DateTimeOffset.UtcNow, SystemToolJournalPhase.DispatchIntent, null, null, null, null);
            stage = PrivateJournalScopeFixtureStage.BeginFirstIntent;
            var first = store.BeginIntent(intent, DateTimeOffset.UtcNow, token);
            stage = PrivateJournalScopeFixtureStage.ReadUnresolvedHistory;
            var unresolved = store.ReadHistory(DateTimeOffset.UtcNow, token);
            intentBlocks = first.State == SystemToolJournalState.Unresolved && unresolved.State == SystemToolJournalHistoryState.UnresolvedPresent;
            var observed = intent with { Sequence = 2, TimestampUtc = DateTimeOffset.UtcNow, Phase = SystemToolJournalPhase.ClientObserved, ProcessId = 123 };
            var exited = observed with { Sequence = 3, TimestampUtc = DateTimeOffset.UtcNow, Phase = SystemToolJournalPhase.ClientExited, ExitCode = 0 };
            stage = PrivateJournalScopeFixtureStage.AppendObservedAndExit;
            if (store.AppendSnapshot([intent, observed], DateTimeOffset.UtcNow, token).State != SystemToolJournalState.Unresolved ||
                store.AppendSnapshot([intent, observed, exited], DateTimeOffset.UtcNow, token).State != SystemToolJournalState.RecordedClientExit)
                throw new SmokeAssertionException("private-journal-owned-lifecycle");
            stage = PrivateJournalScopeFixtureStage.ReadTerminalHistory;
            terminalObserved = store.ReadHistory(DateTimeOffset.UtcNow, token).State == SystemToolJournalHistoryState.RecordedTerminalOnly;
            stage = PrivateJournalScopeFixtureStage.CloseFirstStore;
            store.Dispose(); store = null;
            if (SystemToolJournalStore.HasUncertainCleanup) throw new SmokeAssertionException("private-journal-first-close");
            stage = PrivateJournalScopeFixtureStage.ReopenExisting;
            store = SystemToolJournalPrivateStore.OpenOwnedFixture(fixture, marker, false, token);
            stage = PrivateJournalScopeFixtureStage.ReadExistingTerminal;
            reopened = store.ReadHistory(DateTimeOffset.UtcNow, token).State == SystemToolJournalHistoryState.RecordedTerminalOnly;
            // An existing-only scope cannot acquire write permission even with valid typed intent.
            var second = intent with { ExecutionId = Guid.NewGuid(), TimestampUtc = DateTimeOffset.UtcNow };
            stage = PrivateJournalScopeFixtureStage.RejectReadonlyIntent;
            if (store.BeginIntent(second, DateTimeOffset.UtcNow, token).State != SystemToolJournalState.Unknown)
                throw new SmokeAssertionException("private-journal-readonly-refusal");
            stage = PrivateJournalScopeFixtureStage.CloseReadonlyStore;
            store.Dispose();
            stage = PrivateJournalScopeFixtureStage.ReopenWritable;
            store = SystemToolJournalPrivateStore.OpenOwnedFixture(fixture, marker, true, token);
            stage = PrivateJournalScopeFixtureStage.BeginSecondIntent;
            if (store.BeginIntent(second, DateTimeOffset.UtcNow, token).State != SystemToolJournalState.Unresolved)
                throw new SmokeAssertionException("private-journal-next-new-guid");
            var lost = second with { Sequence = 2, TimestampUtc = DateTimeOffset.UtcNow, Phase = SystemToolJournalPhase.ObservationLost };
            stage = PrivateJournalScopeFixtureStage.AppendObservationLost;
            if (store.AppendSnapshot([second, lost], DateTimeOffset.UtcNow, token).State != SystemToolJournalState.Unresolved)
                throw new SmokeAssertionException("private-journal-loss-append");
            stage = PrivateJournalScopeFixtureStage.CloseUnresolvedStore;
            store.Dispose();
            stage = PrivateJournalScopeFixtureStage.ReopenUnresolved;
            store = SystemToolJournalPrivateStore.OpenOwnedFixture(fixture, marker, true, token);
            var third = second with { ExecutionId = Guid.NewGuid(), TimestampUtc = DateTimeOffset.UtcNow };
            stage = PrivateJournalScopeFixtureStage.VerifyUnresolvedBlocks;
            var before = Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).ToArray();
            lossBlocks = store.ReadHistory(DateTimeOffset.UtcNow, token).State == SystemToolJournalHistoryState.UnresolvedPresent &&
                store.BeginIntent(third, DateTimeOffset.UtcNow, token).State == SystemToolJournalState.Unknown &&
                before.SequenceEqual(Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal));
            stage = PrivateJournalScopeFixtureStage.CloseFinalStore;
            store.Dispose(); store = null;
            stage = PrivateJournalScopeFixtureStage.FinalContract;
            if (!emptyObserved || !busyBlocked || !intentBlocks || !terminalObserved || !reopened || !lossBlocks)
                throw new SmokeAssertionException("private-journal-fixed-scope-contract");
            SystemToolJournalPrivateScopeCleanupConfirmed = !SystemToolJournalStore.HasUncertainCleanup;
            if (!SystemToolJournalPrivateScopeCleanupConfirmed) throw new SmokeAssertionException("private-journal-cleanup-unconfirmed");
            report.Pass(check, "productionCreationGuardFalse=true; productionFactoryRejectedWithoutStore=true; fixedProductionPathDerivedOnly=true; existingOnlyAbsenceNoCreate=true; ownedPrivateStore=true; existingExclusiveLeaseBlocks=true; emptyStoreObservationOnly=true; immutableIntentLifecycle=true; existingOnlyCannotWrite=true; recordedTerminalReopened=true; unresolvedLossBlocksNewIntent=true; participantsClosed=true. Two explicitly synthetic inert operation records used only the fixed owned GUID fixture backend; no actual process start/exit, servicing or operation success was inferred. Guard refusal is not an instrumented all-I/O trace. No production writer scope, PID reopen, dispatch, identity/SID/path export, journal clearing or permission was introduced.");
        }
        catch (SmokeAssertionException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or System.Security.SecurityException)
        {
            bool storeReferencePresent = store != null;
            bool cleanupUncertainWhenCaught = SystemToolJournalStore.HasUncertainCleanup;
            var failure = exception as SystemToolJournalStoreFailure;
            string reason = failure == null ? "NotAStoreFailure" :
                Enum.IsDefined(failure.Reason) ? failure.Reason.ToString() : "UnrecognizedStoreReason";
            int? nativeCode = failure?.NativeCode;
            int? hResult = exception.HResult;
            report.Unknown(check, $"stage={stage}; reason={reason}; nativeCode={nativeCode?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}; hResult={hResult?.ToString("X8", CultureInfo.InvariantCulture) ?? "Unknown"}; " +
                $"storeReferencePresent={(storeReferencePresent ? "true" : "false")}; cleanupUncertainWhenCaught={(cleanupUncertainWhenCaught ? "true" : "false")}. " +
                "These are managed catch-time diagnostics, not final cleanup or native-lifetime proof. Owned fixed private-store prerequisites or lifecycle could not be confirmed. Retain the marked outer tree if cleanup remains uncertain.");
        }
        finally
        {
            if (store != null) SystemToolJournalStore.DisposeChecked(store);
            if (SystemToolJournalStore.HasUncertainCleanup) SystemToolJournalPrivateScopeCleanupConfirmed = false;
        }
    }
}
