using MadClean.Core;

namespace MadClean.Windows;

public sealed partial class WindowsShortcutInventory
{
    public async Task<CheckResult<ShortcutObservation>> ReadAsync(ShortcutRecheckScope scope, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return CheckResult<ShortcutObservation>.Empty(CheckState.Unsupported,
            "Selected shortcut recheck requires Windows; no Windows metadata was simulated.");
        if (!ValidSelectedScope(scope)) return CheckResult<ShortcutObservation>.Empty(CheckState.Unsupported,
            "The selected shortcut scope is missing or invalid. Select one exact .lnk observation beneath its explicit local root; no metadata worker started.");
        return await RunMetadataWorkerAsync(
            workerToken => Recheck(scope, workerToken),
            CheckResult<ShortcutObservation>.Empty,
            "Selected shortcut recheck", CheckState.Unknown, token).ConfigureAwait(false);
    }

    private static bool ValidSelectedScope(ShortcutRecheckScope? scope)
    {
        if (scope == null || scope.PriorObservation == null || scope.ValidatedUtc == default ||
            scope.ValidatedUtc.Offset != TimeSpan.Zero || scope.ValidatedUtc > DateTimeOffset.UtcNow ||
            scope.SourceCoverageState is not (CheckState.Available or CheckState.Partial)) return false;
        string? root = ShortcutPathPolicy.NormalizeLocalPath(scope.RootPath, allowDriveRoot: false);
        string? path = ShortcutPathPolicy.NormalizeLocalPath(scope.ShortcutPath, allowDriveRoot: false);
        string? prior = ShortcutPathPolicy.NormalizeLocalPath(scope.PriorObservation.ShortcutPath, allowDriveRoot: false);
        return root != null && path != null && root.Equals(scope.RootPath, StringComparison.OrdinalIgnoreCase) &&
            path.Equals(scope.ShortcutPath, StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) &&
            path.Equals(prior, StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    private static CheckResult<ShortcutObservation> Recheck(ShortcutRecheckScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // No directory enumeration: only this root's ancestry and the exact selected link are
        // reopened. These current leases do not establish continuity with the historical scan.
        if (!WindowsShortcutNative.TryOpenAncestry(scope.RootPath, true, token, out var root, out var rootFailure))
            return SelectedUnknown(scope, "The selected root is currently unavailable or unsafe. " + rootFailure.Reason);
        using (root)
        {
            if (!WindowsShortcutNative.TryOpenAncestry(scope.ShortcutPath, false, token, out var entry, out var failure))
                return SelectedUnknown(scope, "The selected shortcut itself is absent, inaccessible or unsupported; its target cannot be evaluated. " + failure.Reason);
            using (entry)
            {
                if ((entry!.LeafInformation.FileAttributes & WindowsShortcutNative.DirectoryAttribute) != 0)
                    return SelectedUnknown(scope, "The selected .lnk path currently identifies a directory; no directory contents or target were inspected.");
                var inspected = InspectShortcut(scope.ShortcutPath, entry.LeafInformation.Bytes, token);
                token.ThrowIfCancellationRequested();
                var observation = inspected.Observation with
                {
                    Reason = inspected.Observation.Reason + " " + RecheckLimits,
                    CheckedAt = DateTimeOffset.UtcNow
                };
                bool partial = scope.SourceCoverageState == CheckState.Partial || inspected.Issue != null ||
                    observation.State is not (ShortcutTargetState.TargetExists or ShortcutTargetState.LocalTargetMissing);
                return new(partial ? CheckState.Partial : CheckState.Available,
                    "Rechecked only the selected .lnk using fresh guarded metadata. " +
                    (scope.SourceCoverageState == CheckState.Partial ? "The source scan's Partial coverage is retained. " : "") + RecheckLimits, [observation]);
            }
        }
    }

    private const string RecheckLimits = "The prior observation is historical; this recheck does not prove the same file persisted or establish full native file-identity continuity. Target presence/absence concerns only the currently stored supported local path. No resolution, launch, save, removal or action permission is provided.";

    private static CheckResult<ShortcutObservation> SelectedUnknown(ShortcutRecheckScope scope, string reason) =>
        new(CheckState.Partial, "The selected shortcut could not be evaluated as a current supported target check. " +
            (scope.SourceCoverageState == CheckState.Partial ? "The source scan's Partial coverage is retained. " : "") + RecheckLimits,
            [new(scope.ShortcutPath, "", ShortcutTargetState.Unknown, reason + " " + RecheckLimits,
                ShortcutTargetClassifier.Source, DateTimeOffset.UtcNow)]);

    // Shared by folder discovery and the exact-file mode. The caller holds validated ancestry
    // leases through this read; supported current target probes reuse the same native guard.
    private static ShortcutInspection InspectShortcut(string path, ulong bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes == 0 || bytes > MaximumLinkBytes)
            return new(ShortcutTargetClassifier.Classify(path,
                new(null, MetadataProblem: "The .lnk file was empty or exceeded the 1 MiB metadata bound; it was not loaded."),
                _ => throw new InvalidOperationException("No probe is permitted"), DateTimeOffset.UtcNow), true, null);
        using var readLease = WindowsShortcutNative.OpenReadOnlyLink(path);
        if (readLease.IsInvalid || !WindowsShortcutNative.TryReadSafeFileInformation(readLease, out var readInfo) ||
            readInfo.Bytes == 0 || readInfo.Bytes > MaximumLinkBytes)
            return new(new(path, "", ShortcutTargetState.Unknown,
                "Read-only shared access and supported current shortcut metadata were not established; not a missing-target conclusion.",
                ShortcutTargetClassifier.Source, DateTimeOffset.UtcNow), true,
                "The shortcut is locked, changed, unreadable or unsupported; no metadata load was attempted.");
        var stored = WindowsShortcutMetadata.Read(path, token);
        token.ThrowIfCancellationRequested();
        var observation = ShortcutTargetClassifier.Classify(path, stored,
            target => WindowsShortcutNative.ProbeTarget(target, token), DateTimeOffset.UtcNow);
        token.ThrowIfCancellationRequested();
        return new(observation with { CheckedAt = DateTimeOffset.UtcNow }, false, null);
    }
    private sealed record ShortcutInspection(ShortcutObservation Observation, bool Skipped, string? Issue);

    private static async Task<CheckResult<T>> RunMetadataWorkerAsync<T>(Func<CancellationToken, CheckResult<T>> read,
        Func<CheckState, string, CheckResult<T>> interrupted, string operation, CheckState timeoutState, CancellationToken token)
    {
        if (token.IsCancellationRequested) return CheckResult<T>.Empty(CheckState.Cancelled, operation + " cancelled before a worker started.");
        // One static gate covers BOTH folder discovery and selected-link rechecks, across instances.
        if (!WorkerGate.Wait(0)) return CheckResult<T>.Empty(CheckState.Unavailable,
            "A previous shortcut metadata worker is still completing a native call. No additional worker started; retry after it finishes.");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<CheckResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            bool initialized = false;
            CheckResult<T> result = CheckResult<T>.Empty(CheckState.Failed, "The shortcut metadata worker did not establish a completed result.");
            try
            {
                int status = WindowsShortcutNative.CoInitializeEx(IntPtr.Zero, 2);
                if (status < 0)
                    result = interrupted(CheckState.Failed,
                        $"The read-only Shell COM apartment could not initialize (code 0x{status:X8}).");
                else
                {
                    initialized = true;
                    result = read(stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException)
            { result = interrupted(CheckState.Cancelled, operation + " cancelled; no completed new check is claimed. Existing scan observations remain review-only."); }
            catch (Exception)
            { result = interrupted(CheckState.Failed, operation + " could not complete. No target absence or obsolete-shortcut conclusion was inferred."); }
            finally
            {
                // Completion means this worker's native/COM ownership has ended. Never publish
                // success before cleanup or release the gate from a cancelled caller's thread.
                bool cleanupFailed = false;
                try { if (initialized) WindowsShortcutNative.CoUninitialize(); }
                catch (Exception) { cleanupFailed = true; }
                try { stop.Dispose(); }
                catch (Exception) { cleanupFailed = true; }
                try { WorkerGate.Release(); }
                catch (Exception) { cleanupFailed = true; }
                if (cleanupFailed) result = CheckResult<T>.Empty(CheckState.Failed,
                    "The shortcut metadata worker's cleanup could not be confirmed. No completed target check or action permission is claimed.");
                completion.TrySetResult(result);
            }
        }) { IsBackground = true, Name = "MadClean read-only shortcut metadata" };
        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch (Exception)
        {
            stop.Dispose(); WorkerGate.Release();
            return CheckResult<T>.Empty(CheckState.Failed, "The dedicated shortcut metadata thread could not start. No query or target activation was performed.");
        }
        try
        {
            var result = await completion.Task.WaitAsync(ScanBudget, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            RequestStop(stop);
            return interrupted(CheckState.Cancelled, operation + " cancellation was requested. An in-flight read-only native/COM call may still finish before its worker and read leases close; no further work will be scheduled. No shortcut or target changed.");
        }
        catch (TimeoutException)
        {
            RequestStop(stop);
            return interrupted(timeoutState, operation + " reached its 60-second wait budget. An in-flight read-only native/COM call may still finish before its worker and read leases close; no additional worker is permitted while it remains pending. Coverage is incomplete, not a broken-shortcut verdict.");
        }
    }
}
