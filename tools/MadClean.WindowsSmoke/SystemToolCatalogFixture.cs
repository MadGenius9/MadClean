using System.ComponentModel;
using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void SystemToolCatalogs(SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-catalog-observation";
        SystemToolOperation[] operations = [SystemToolOperation.DismCheckHealth, SystemToolOperation.SfcVerifyOnly];
        var outcomes = new List<string>();
        int observed = 0;
        try
        {
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                foreach (var operation in operations)
                {
                    bool stopped = false;
                    try
                    {
                        Task.Run(() => SystemToolCatalogObservation.Read(operation, cancelled.Token), CancellationToken.None)
                            .GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) { stopped = true; }
                    if (!stopped)
                    {
                        report.Fail(check, "Pre-cancelled fixed catalog observation did not throw before querying; no actual catalog observation followed.");
                        return;
                    }
                }
            }
            foreach (var operation in operations)
            {
                token.ThrowIfCancellationRequested();
                var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                // The synchronous helper and its native resources belong to this worker only.
                // Scheduling is not cancellable: its finally must always dispose its own source.
                var worker = Task.Run(() =>
                {
                    try { return SystemToolCatalogObservation.Read(operation, stop.Token); }
                    finally { stop.Dispose(); }
                }, CancellationToken.None);
                _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                SystemToolCatalogObservationResult result;
                try { result = worker.WaitAsync(TimeSpan.FromSeconds(45), token).GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    try { stop.Cancel(); } catch (ObjectDisposedException) { /* Worker already returned. */ }
                    report.Unknown(check, $"fixedPositiveObservationCount={observed}; preCancellationObserved=true; pendingWorker={!worker.IsCompleted}. The caller wait ended; the worker retains its catalog/trust/file resources until return. No second fixed-tool query, force-close or thread abort attempted; no paths, digests, tags, subjects or certificates exported.");
                    return;
                }
                if (!Enum.IsDefined(result.State) || !Enum.IsDefined(result.Stage) || !Enum.IsDefined(result.Reason) || !result.Limited ||
                    result.CatalogCount is < 0 or > 1 || result.MemberCount is < 0 or > 4096 || result.MatchingMemberCount is < 0 or > 1 ||
                    result.MatchingMemberCount > result.MemberCount || result.CatalogCount == 0 && result.MemberCount != 0)
                {
                    report.Fail(check, "The fixed catalog observation violated enum/count/limited-scope bounds; no native values or source data exported.");
                    return;
                }
                string error = result.NativeErrorCode.HasValue ? "0x" + unchecked((uint)result.NativeErrorCode.Value).ToString("X8") : "none";
                string trust = result.NativeTrustResult.HasValue ? "0x" + unchecked((uint)result.NativeTrustResult.Value).ToString("X8") : "none";
                outcomes.Add($"{operation}:state={result.State}/stage={result.Stage}/reason={result.Reason}/catalogs={result.CatalogCount}/members={result.MemberCount}/matches={result.MatchingMemberCount}/nativeError={error}/nativeTrust={trust}/limited=true");
                if (result.State != CheckState.Available && result.Reason is (SystemToolCatalogReason.PendingWorker or SystemToolCatalogReason.CleanupUnavailable))
                {
                    report.Unknown(check, $"fixedPositiveObservationCount={observed}; " + string.Join("; ", outcomes) +
                        ". Native ownership/cleanup is not established; no second fixed-tool query or forced cleanup attempted. No source values exported.");
                    return;
                }
                if (result.State == CheckState.Available)
                {
                    if (result.Stage != SystemToolCatalogStage.Completed || result.Reason != SystemToolCatalogReason.Observed ||
                        result.CatalogCount != 1 || result.MemberCount < 1 || result.MatchingMemberCount != 1 || result.NativeTrustResult != 0 ||
                        result.NativeErrorCode is not (null or 0))
                    {
                        report.Fail(check, "An Available catalog observation lacked the exact positive member/trust/signer/limited outcome contract; no production trust promotion.");
                        return;
                    }
                    observed++;
                }
                else if (result.Stage == SystemToolCatalogStage.Completed || result.Reason == SystemToolCatalogReason.Observed)
                {
                    report.Fail(check, "The catalog result contradicted its completed/observed classification; no identity or completeness inferred.");
                    return;
                }
            }
            string detail = $"fixedPositiveObservationCount={observed}; preCancellationObserved=true; " + string.Join("; ", outcomes) +
                ". First-catalog existential observations only; no complete/unique catalog or broader trust coverage. This isolated fixture does not invoke production identity acceptance, tool commands, installs, elevation or repair; no paths, hashes, tags, subject identifiers or certificate data exported. Native certificate/cache behavior remains controlled by Windows.";
            if (observed == 2) report.Pass(check, detail);
            else report.Unknown(check, detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or
            OperationCanceledException or System.Security.SecurityException or SystemToolTrustFailure)
        {
            report.Unknown(check, $"Fixed catalog fixture did not establish both observations (code 0x{ex.HResult:X8}); no source-value export, forced native cleanup, identity fallback or servicing command attempted.");
        }
    }
}
