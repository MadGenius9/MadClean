using System.ComponentModel;
using System.IO;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static ValidationWorkerLifetime? heldCatalogWorkerLifetime;

    private sealed record HeldCatalogCheck(SystemToolCatalogObservationResult Result, bool PreCancelled,
        bool StreamOpen, bool SameHandle, bool PositionRestored);

    private static void SystemToolHeldCatalogs(SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-held-catalog-observation";
        var outcomes = new List<string>();
        int observed = 0;
        foreach (var operation in new[] { SystemToolOperation.DismCheckHealth, SystemToolOperation.SfcVerifyOnly })
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Every file, directory lease and cancellation source belongs to this worker.
            // A bounded caller wait must never close resources underneath native trust.
            var worker = Task.Run(() =>
            {
                try { return ObserveHeldCatalog(operation, stop.Token); }
                finally { stop.Dispose(); }
            }, CancellationToken.None);
            // Preserve the actual held-resource worker through caller timeout.
            // Only its own finally releases leases; Main waits without doing so.
            heldCatalogWorkerLifetime = new ValidationWorkerLifetime(worker);
            _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            HeldCatalogCheck held;
            try { held = worker.WaitAsync(TimeSpan.FromSeconds(45), token).GetAwaiter().GetResult(); }
            catch (SmokeAssertionException ex)
            {
                report.Fail(check, "Fixed held-stream invariant failed: " + ex.Code + ". No actual catalog query followed failed pre-cancellation.");
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                try { stop.Cancel(); } catch (ObjectDisposedException) { }
                report.Unknown(check, $"fixedPositiveObservationCount={observed}; pendingWorker={!worker.IsCompleted}. Caller wait ended; all held resources remain worker-owned until return. No second fixed-tool query or forced cleanup.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or
                ArgumentException or System.Security.SecurityException or SystemToolTrustFailure)
            {
                report.Unknown(check, $"Guarded held catalog observation unavailable (code 0x{ex.HResult:X8}); no metadata values exported or production trust inferred.");
                return;
            }
            if (!held.PreCancelled || !held.StreamOpen || !held.SameHandle || !held.PositionRestored)
            {
                report.Fail(check, "The held catalog helper violated pre-cancellation or caller-owned stream/handle/position preservation. No source values exported.");
                return;
            }
            var result = held.Result;
            if (!Enum.IsDefined(result.State) || !Enum.IsDefined(result.Stage) || !Enum.IsDefined(result.Reason) || !result.Limited ||
                result.CatalogCount is < 0 or > 1 || result.MemberCount is < 0 or > 4096 || result.MatchingMemberCount is < 0 or > 1 ||
                result.MatchingMemberCount > result.MemberCount || result.CatalogCount == 0 && result.MemberCount != 0)
            {
                report.Fail(check, "Held catalog result contradicted fixed enum/count/limited-scope bounds.");
                return;
            }
            string error = result.NativeErrorCode is int e ? "0x" + unchecked((uint)e).ToString("X8") : "none";
            string trust = result.NativeTrustResult is int t ? "0x" + unchecked((uint)t).ToString("X8") : "none";
            outcomes.Add($"{operation}:state={result.State}/stage={result.Stage}/reason={result.Reason}/catalogs={result.CatalogCount}/members={result.MemberCount}/matches={result.MatchingMemberCount}/nativeError={error}/nativeTrust={trust}/streamOpen=true/sameHandle=true/positionRestored=true/preCancelled=true");
            if (result.State == CheckState.Available)
            {
                if (result.Stage != SystemToolCatalogStage.Completed || result.Reason != SystemToolCatalogReason.Observed ||
                    result.CatalogCount != 1 || result.MemberCount < 1 || result.MatchingMemberCount != 1 || result.NativeTrustResult != 0 ||
                    result.NativeErrorCode is not (null or 0))
                {
                    report.Fail(check, "Available held catalog result lacked the exact positive member/trust/limited contract.");
                    return;
                }
                observed++;
            }
            else if (result.Reason is SystemToolCatalogReason.PendingWorker or SystemToolCatalogReason.CleanupUnavailable)
            {
                report.Unknown(check, $"fixedPositiveObservationCount={observed}; " + string.Join("; ", outcomes) + ". Native ownership/cleanup uncertain; no second query or forced cleanup.");
                return;
            }
            else if (result.Stage == SystemToolCatalogStage.Completed || result.Reason == SystemToolCatalogReason.Observed)
            {
                report.Fail(check, "Held catalog result contradicted its completed/observed classification.");
                return;
            }
        }
        string detail = $"fixedPositiveObservationCount={observed}; " + string.Join("; ", outcomes) +
            ". Caller-owned streams remained open on the same handle at position seven after pre-cancel and observation; worker alone disposed them. Limited first-catalog evidence, no completeness, tool execution, elevation or repair. No paths, hashes, tags, versions or certificate values exported.";
        if (observed == 2) report.Pass(check, detail); else report.Unknown(check, detail);
    }

    private static HeldCatalogCheck ObserveHeldCatalog(SystemToolOperation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Path.IsPathFullyQualified(system)) throw new InvalidOperationException();
        system = Path.TrimEndingDirectorySeparator(Path.GetFullPath(system));
        string path = Path.Combine(system, SystemToolActionsCatalog.Find(operation)!.FileName);
        var directories = SystemToolTrustNative.LockDirectoryChain(system);
        try
        {
            using SafeFileHandle handle = SystemToolTrustNative.OpenFile(path);
            SystemToolTrustNative.ValidateAttributes(handle, false);
            SystemToolTrustNative.ValidateFinalPath(handle, path);
            using var stream = new FileStream(handle, FileAccess.Read, 65_536, false);
            stream.Position = 7;
            SafeFileHandle original = stream.SafeFileHandle;
            nint originalValue = original.DangerousGetHandle();
            bool Preserved() => stream.CanRead && stream.CanSeek && !original.IsClosed &&
                ReferenceEquals(original, stream.SafeFileHandle) && original.DangerousGetHandle() == originalValue && stream.Position == 7;
            bool cancelled = false;
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                try { SystemToolCatalogObservation.ReadHeld(operation, stream, system, preCancelled.Token); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            bool prePreserved = Preserved();
            if (!cancelled || !prePreserved) throw new SmokeAssertionException("held-catalog-pre-cancel-preservation");
            var result = SystemToolCatalogObservation.ReadHeld(operation, stream, system, token);
            return new(result, cancelled && prePreserved, stream.CanRead && stream.CanSeek && !original.IsClosed,
                ReferenceEquals(original, stream.SafeFileHandle) && original.DangerousGetHandle() == originalValue, stream.Position == 7);
        }
        finally { foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose(); }
    }
}
