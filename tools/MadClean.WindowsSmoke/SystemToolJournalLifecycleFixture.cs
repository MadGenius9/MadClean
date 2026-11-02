using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool SystemToolJournalLifecycleCleanupConfirmed { get; set; } = true;
    private static bool LifecycleCursorUncertain;
    private static SystemToolJournalStore? RetainedLifecycleStore;
    private static IDisposable? RetainedLifecycleAncestry;
    private static SafeFileHandle? RetainedLifecycleAnchor;

    private static void SystemToolJournalLifecycleFixture(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-journal-lifecycle-fixture";
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        var children = new List<InertOwnedChild>(1);
        string? prepared = null;
        InertApphostLease? apphost = null;
        SystemToolJournalStore? store = null;
        WindowsShortcutNative.AncestryLease? ancestry = null;
        SafeFileHandle? anchor = null;
        byte[]? before = null, after = null, malformed = null;
        bool previousUncertain = !SystemToolJournalLifecycleCleanupConfirmed || LifecycleCursorUncertain ||
            RetainedLifecycleStore != null || !SystemToolProcessObservationCleanupConfirmed ||
            UnconfirmedInertChildren.Count != 0 || UnconfirmedInertApphost != null || SystemToolJournalStore.HasUncertainCleanup;
        bool exited37 = false, intentReopened = false, prefixRejected = false, lossAppended = false, tailRejected = false;
        string stage = "initializing";
        try
        {
            token.ThrowIfCancellationRequested();
            if (previousUncertain || !InertProcessChild.IsValidFixture(repository, fixture, marker))
                throw new InertObservationUnavailable("lifecycle-prerequisite-unavailable");
            apphost = InertApphostLease.Open();
            prepared = PrepareInertChild(repository, fixture, marker);
            SystemToolJournalLifecycleCleanupConfirmed = false;
            stage = "intent-child";
            var child = StartInert("journal-intent-exit", repository, fixture, marker, apphost.Path, children, prepared);
            WaitInertReady(child, token);
            var observed = SystemToolProcessObserver.ObserveAsync(child.Process, token).GetAwaiter().GetResult();
            if (observed.State != ClientProcessObservationState.Exited || observed.ExitCode != 37)
                throw new InertObservationUnavailable("lifecycle-child-fixed-exit-unobserved");
            RequireInertExit(child, observed, 37);
            if (!AuditInertChild(repository, fixture, marker, child)) throw new InertObservationUnavailable("lifecycle-child-audit-unavailable");
            exited37 = true;
            string directory = Path.Combine(fixture, SystemToolJournalStore.FixtureLeafName(SystemToolJournalFixtureCase.Lifecycle));
            string path = Path.Combine(directory, "intent.bin");
            if (!WindowsShortcutNative.TryOpenAncestry(directory, true, token, out ancestry, out _))
                throw new InertObservationUnavailable("lifecycle-ancestry-unavailable");
            anchor = ShortcutEvidenceCreateFileW(path, 0x80, 3, IntPtr.Zero, 3, 0x00200000 | 0x00100000, IntPtr.Zero);
            if (anchor.IsInvalid) throw new InertObservationUnavailable("lifecycle-anchor-unavailable");
            SystemToolTrustNative.ValidateAttributes(anchor, false);
            SystemToolTrustNative.ValidateFinalPath(anchor, path);
            var initial = LifecycleStamp(anchor);
            stage = "existing-intent-read";
            store = SystemToolJournalStore.OpenOwnedFixture(fixture, marker, SystemToolJournalFixtureCase.Lifecycle, token);
            if (store.CreatedIntentFile || LifecycleStamp(store.IntentHandle) != initial)
                throw new SmokeAssertionException("lifecycle-existing-object-replaced");
            var original = store.ReadJournal(DateTimeOffset.UtcNow, token);
            if (original.State != SystemToolJournalState.Unresolved || original.Entries.Count != 1 ||
                original.Entries[0].Phase != SystemToolJournalPhase.DispatchIntent)
                throw new SmokeAssertionException("lifecycle-intent-not-unresolved");
            intentReopened = true;
            var intent = original.Entries[0];
            var lost = intent with { Sequence = 2, TimestampUtc = DateTimeOffset.UtcNow, Phase = SystemToolJournalPhase.ObservationLost };
            var changedIntent = intent with { ExecutionId = Guid.NewGuid() };
            var changedLost = lost with { ExecutionId = changedIntent.ExecutionId };
            before = LifecycleAuditBytes(store.IntentHandle);
            var refused = store.AppendJournalSnapshot([changedIntent, changedLost], DateTimeOffset.UtcNow, token);
            after = LifecycleAuditBytes(store.IntentHandle);
            if (refused.State != SystemToolJournalState.Unknown || refused.Entries.Count != 0 || !before.AsSpan().SequenceEqual(after))
                throw new SmokeAssertionException("lifecycle-prefix-rewrite-not-refused");
            prefixRejected = true;
            Array.Clear(before); before = null; Array.Clear(after); after = null;
            var appended = store.AppendJournalSnapshot([intent, lost], DateTimeOffset.UtcNow, token);
            if (appended.State != SystemToolJournalState.Unresolved || appended.Entries.Count != 2 || appended.Entries[0] != intent || appended.Entries[1] != lost)
                throw new SmokeAssertionException("lifecycle-observation-loss-not-appended");
            lossAppended = true;
            before = LifecycleAuditBytes(store.IntentHandle);
            var beforeTail = LifecycleStamp(store.IntentHandle);
            store.Dispose();
            if (SystemToolJournalStore.HasUncertainCleanup)
            {
                RetainedLifecycleStore = store;
                throw new InertObservationUnavailable("lifecycle-store-release-unconfirmed");
            }
            store = null;
            stage = "owned-malformed-tail";
            token.ThrowIfCancellationRequested();
            using (var data = ShortcutEvidenceCreateFileW(path, 0xC0000000, 0, IntPtr.Zero, 3, 0x80000000 | 0x00200000 | 0x00100000, IntPtr.Zero))
            {
                if (data.IsInvalid) throw new InertObservationUnavailable("lifecycle-tail-handle-unavailable");
                SystemToolTrustNative.ValidateAttributes(data, false); SystemToolTrustNative.ValidateFinalPath(data, path);
                if (LifecycleStamp(data) != beforeTail || LifecycleStamp(anchor) != beforeTail || before.Length >= 4096)
                    throw new InertObservationUnavailable("lifecycle-tail-anchor-changed");
                using var stream = new FileStream(data, FileAccess.ReadWrite);
                if (stream.Seek(before.Length, SeekOrigin.Begin) != before.Length) throw new IOException("Owned tail seek unavailable.");
                stream.WriteByte((byte)'{'); stream.Flush(true);
            }
            store = SystemToolJournalStore.OpenOwnedFixture(fixture, marker, SystemToolJournalFixtureCase.Lifecycle, token);
            var reopenedStamp = LifecycleStamp(store.IntentHandle);
            if (store.CreatedIntentFile || reopenedStamp.Volume != beforeTail.Volume || reopenedStamp.FileId != beforeTail.FileId || reopenedStamp.Bytes != beforeTail.Bytes + 1)
                throw new SmokeAssertionException("lifecycle-tail-object-replaced");
            malformed = LifecycleAuditBytes(store.IntentHandle);
            if (malformed.Length != before.Length + 1 || malformed[^1] != (byte)'{' || !malformed.AsSpan(0, before.Length).SequenceEqual(before))
                throw new SmokeAssertionException("lifecycle-tail-not-exact-append");
            var invalid = store.ReadJournal(DateTimeOffset.UtcNow, token);
            var appendInvalid = store.AppendJournalSnapshot([intent, lost], DateTimeOffset.UtcNow, token);
            after = LifecycleAuditBytes(store.IntentHandle);
            if (invalid.State != SystemToolJournalState.Unknown || invalid.Entries.Count != 0 ||
                appendInvalid.State != SystemToolJournalState.Unknown || appendInvalid.Entries.Count != 0 || !malformed.AsSpan().SequenceEqual(after))
                throw new SmokeAssertionException("lifecycle-malformed-tail-not-preserved");
            tailRejected = true; stage = "owned-lifecycle-observed";
        }
        catch (SmokeAssertionException failure) { stage = "fixed-invariant-failed"; report.Fail(check, "Owned lifecycle invariant " + failure.Code + " failed; no production recovery is established."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            OperationCanceledException or System.ComponentModel.Win32Exception or System.Security.SecurityException or SystemToolTrustFailure or InertObservationUnavailable)
        { stage = exception is InertObservationUnavailable safe ? safe.Stage : "lifecycle-prerequisite-or-observation-unavailable"; }
        finally
        {
            if (before != null) Array.Clear(before); if (after != null) Array.Clear(after); if (malformed != null) Array.Clear(malformed);
            bool safe = !previousUncertain && !LifecycleCursorUncertain;
            foreach (var child in children)
            {
                bool actualExit = false;
                try { actualExit = child.Process.WaitForExit(12_000) && child.Process.HasExited; if (!actualExit || !AuditInertChild(repository, fixture, marker, child)) safe = false; }
                catch (Exception) { safe = false; }
                if (actualExit) { try { child.Process.Dispose(); } catch (Exception) { safe = false; if (UnconfirmedInertChildren.Count < 4) UnconfirmedInertChildren.Add(child.Process); } }
                else { safe = false; if (UnconfirmedInertChildren.Count < 4) UnconfirmedInertChildren.Add(child.Process); }
            }
            if (prepared != null && children.Count == 0)
            { try { if (!InertProcessChild.IsValidChild(repository, prepared, marker, true)) safe = false; } catch (Exception) { safe = false; } }
            if (safe)
            {
                try
                {
                    store?.Dispose();
                    if (SystemToolJournalStore.HasUncertainCleanup) safe = false;
                    if (safe)
                    {
                        store = null;
                        anchor?.Dispose(); anchor = null; ancestry?.Dispose(); ancestry = null;
                        apphost?.Dispose(); apphost = null;
                    }
                }
                catch (Exception) { safe = false; }
            }
            if (!safe)
            {
                if (store != null) RetainedLifecycleStore = store;
                if (anchor != null) RetainedLifecycleAnchor = anchor;
                if (ancestry != null) RetainedLifecycleAncestry = ancestry;
                if (apphost != null) UnconfirmedInertApphost = apphost;
            }
            SystemToolJournalLifecycleCleanupConfirmed = safe;
        }
        if (exited37 && intentReopened && prefixRejected && lossAppended && tailRejected && SystemToolJournalLifecycleCleanupConfirmed)
            report.Pass(check, "ownedChildExit37AfterIntentFlush=true; existingIntentUnresolved=true; changedPrefixRejectedBytesUnchanged=true; inertObservationLostAppended=true; malformedTailWholeLogUnknownEmpty=true; furtherAppendRefusedBytesUnchanged=true; cleanupConfirmed=true. This observes an exact harness self-exit after flush/readback, not crash/power-loss recovery, record authorship, a running process, production servicing or tool health.");
        else if (stage != "fixed-invariant-failed")
            report.Unknown(check, $"stage={stage}; childExit37={exited37}; intentReopened={intentReopened}; prefixRejected={prefixRejected}; lossAppended={lossAppended}; tailRejected={tailRejected}; cursorUncertain={LifecycleCursorUncertain}; cleanupConfirmed={SystemToolJournalLifecycleCleanupConfirmed}. No reset, salvage, production action or recovery claim; retain the owned tree if cleanup is uncertain.");
    }

    private static ShortcutEvidenceStamp LifecycleStamp(SafeFileHandle handle)
    {
        if (!ShortcutEvidenceGetIdentity(handle, 18, out var id, 24) || !ShortcutEvidenceGetStandard(handle, 1, out var size, 24) ||
            id.Id == Guid.Empty || size.Directory || size.DeletePending || size.NumberOfLinks != 1 || size.EndOfFile is <= 0 or > 4096)
            throw new InertObservationUnavailable("lifecycle-identity-unavailable");
        return new(id.Volume, id.Id, size.EndOfFile, size.NumberOfLinks);
    }

    // Borrow the synchronous store handle only. Preserve its native cursor so no owning
    // FileStream is created over the borrowed handle and subsequent store operations remain serial.
    private static byte[] LifecycleAuditBytes(SafeFileHandle handle)
    {
        byte[] scratch = new byte[4096]; byte[]? result = null;
        bool saved = false, restored = false, success = false; long cursor = 0;
        try
        {
            if (!LifecycleSeek(handle, 0, out cursor, 1) || cursor < 0) throw new IOException("Owned cursor unavailable.");
            saved = true;
            int length = checked((int)LifecycleStamp(handle).Bytes);
            result = new byte[length];
            if (!LifecycleSeek(handle, 0, out var start, 0) || start != 0) throw new IOException("Owned audit seek unavailable.");
            int offset = 0, calls = 0;
            while (offset < length)
            {
                if (++calls > 64 || !LifecycleRead(handle, scratch, (uint)(length - offset), out var read, IntPtr.Zero) || read == 0 || read > length - offset)
                    throw new IOException("Owned bounded audit unavailable.");
                scratch.AsSpan(0, (int)read).CopyTo(result.AsSpan(offset)); offset += (int)read;
            }
            if (LifecycleStamp(handle).Bytes != length || !LifecycleSeek(handle, 0, out var end, 1) || end != length)
                throw new IOException("Owned audit changed.");
            success = true;
        }
        finally
        {
            if (saved) restored = LifecycleSeek(handle, cursor, out var position, 0) && position == cursor && LifecycleSeek(handle, 0, out var checkedPosition, 1) && checkedPosition == cursor;
            if (!restored) LifecycleCursorUncertain = true;
            Array.Clear(scratch);
            if ((!success || !restored) && result != null) Array.Clear(result);
        }
        if (!restored) throw new InertObservationUnavailable("lifecycle-cursor-restoration-unconfirmed");
        return result!;
    }
    [DllImport("kernel32.dll", EntryPoint = "SetFilePointerEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LifecycleSeek(SafeFileHandle handle, long distance, out long position, uint origin);
    [DllImport("kernel32.dll", EntryPoint = "ReadFile", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LifecycleRead(SafeFileHandle handle, [Out] byte[] buffer, uint count, out uint read, IntPtr overlapped);
}
