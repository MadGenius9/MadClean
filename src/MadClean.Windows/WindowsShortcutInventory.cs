using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Explicit selected-folder Shell-link discovery only. The single background STA is never aborted;
/// cancellation/timeout stop further work and disclose any native call that has not returned yet.
/// </summary>
public sealed class WindowsShortcutInventory : IShortcutInventory
{
    private const int MaximumVisitedEntries = 10_000;
    private const int MaximumLinks = 1_000;
    private const int MaximumIssues = 100;
    private const int MaximumDepth = 64;
    private const ulong MaximumLinkBytes = 1_048_576;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);

    public async Task<CheckResult<ShortcutReport>> ScanAsync(string root, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return CheckResult<ShortcutReport>.Empty(CheckState.Unsupported, "Windows Shell .lnk discovery is unsupported on this platform; no Windows checks were simulated.");
        string? local = ShortcutPathPolicy.NormalizeLocalPath(root, allowDriveRoot: false);
        if (local == null)
            return CheckResult<ShortcutReport>.Empty(CheckState.Unsupported, "Choose an explicit supported local folder, not a drive root, UNC/device path, variable, relative path or unsupported long path.");
        if (token.IsCancellationRequested)
            return CheckResult<ShortcutReport>.Empty(CheckState.Cancelled, "Shortcut discovery cancelled before a worker was started.");
        if (!WorkerGate.Wait(0))
            return CheckResult<ShortcutReport>.Empty(CheckState.Unavailable, "A previous shortcut metadata worker is still completing a native call. No additional worker was started; retry after it finishes.");

        var progress = new Progress();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<CheckResult<ShortcutReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            bool initialized = false;
            try
            {
                int status = WindowsShortcutNative.CoInitializeEx(IntPtr.Zero, 2); // COINIT_APARTMENTTHREADED.
                if (status < 0)
                {
                    completion.TrySetResult(new(CheckState.Failed, $"The read-only Shell COM apartment could not initialize (code 0x{status:X8}).", [progress.Snapshot(true)]));
                    return;
                }
                initialized = true;
                completion.TrySetResult(Scan(local, progress, stop.Token));
            }
            catch (OperationCanceledException)
            { completion.TrySetResult(new(CheckState.Cancelled, "Shortcut discovery cancelled; completed observations remain review-only.", [progress.Snapshot(true)])); }
            catch (Exception)
            { completion.TrySetResult(new(CheckState.Failed, "Shortcut discovery could not complete. No target absence or obsolete-shortcut conclusion was inferred from the failure.", [progress.Snapshot(true)])); }
            finally
            {
                try { if (initialized) WindowsShortcutNative.CoUninitialize(); }
                finally { stop.Dispose(); WorkerGate.Release(); }
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
            return CheckResult<ShortcutReport>.Empty(CheckState.Failed, "The dedicated shortcut metadata thread could not start. No scan or target activation was performed.");
        }
        try { return await completion.Task.WaitAsync(ScanBudget, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            RequestStop(stop);
            return new(CheckState.Cancelled,
                "Shortcut discovery cancellation was requested. An in-flight read-only native/COM call may still finish before its worker and read leases close; no further work will be scheduled. No shortcut or target was changed.",
                [progress.Snapshot(true)]);
        }
        catch (TimeoutException)
        {
            RequestStop(stop);
            return new(CheckState.Partial,
                "Shortcut discovery reached its 60-second wait budget. An in-flight read-only native/COM call may still finish before its worker and read leases close; no additional worker is permitted while it remains pending. Coverage is incomplete, not a broken-shortcut verdict.",
                [progress.Snapshot(true)]);
        }
    }

    private static CheckResult<ShortcutReport> Scan(string root, Progress progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Keep the selected root and every ancestor leased without write/delete sharing through
        // discovery. Re-check each descendant root-to-leaf before enumerating or loading it.
        if (!WindowsShortcutNative.TryOpenAncestry(root, true, token, out var selectedLease, out var rootFailure))
        {
            progress.Issue(root, rootFailure.Reason);
            return new(CheckState.Unavailable, "The selected folder could not be verified as an accessible fixed-local, non-reparse, non-cloud scope. No contents were inspected.", [progress.Snapshot(true)]);
        }
        using (selectedLease)
        {
            var folders = new Stack<(string Path, int Depth)>();
            folders.Push((root, 0));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (folders.Count > 0 && !progress.Limited)
            {
                token.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= ScanBudget) { progress.Limit(root, "The scan time budget was reached; remaining scope was not inspected."); break; }
                var folder = folders.Pop();
                if (!WindowsShortcutNative.TryOpenAncestry(folder.Path, true, token, out var folderLease, out var failure))
                { progress.SkipDirectory(); progress.Issue(folder.Path, failure.Reason); continue; }
                using (folderLease)
                {
                    progress.Directory();
                    try
                    {
                        var options = new EnumerationOptions
                        {
                            RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0,
                            ReturnSpecialDirectories = false
                        };
                        foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(folder.Path, "*", options))
                        {
                            token.ThrowIfCancellationRequested();
                            if (progress.Limited) break;
                            if (elapsed.Elapsed >= ScanBudget) { progress.Limit(path, "The scan time budget was reached; remaining scope was not inspected."); break; }
                            if (!progress.Visit()) { progress.Limit(path, "The 10,000-entry scope limit was reached."); break; }
                            if (!WindowsShortcutNative.TryOpenAncestry(path, false, token, out var entryLease, out var entryFailure))
                            { progress.Issue(path, entryFailure.Reason); continue; }
                            using (entryLease)
                            {
                                if ((entryLease!.LeafInformation.FileAttributes & WindowsShortcutNative.DirectoryAttribute) != 0)
                                {
                                    if (folder.Depth >= MaximumDepth)
                                    { progress.SkipDirectory(); progress.Limit(path, "The directory-depth limit was reached; contents were not traversed."); }
                                    else folders.Push((path, folder.Depth + 1));
                                    continue;
                                }
                                progress.File();
                                if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                                { progress.SkipFile(); continue; }
                                if (progress.Count == MaximumLinks)
                                { progress.SkipFile(); progress.Limit(path, "The 1,000-link observation limit was reached."); break; }
                                if (entryLease.LeafInformation.Bytes == 0 || entryLease.LeafInformation.Bytes > MaximumLinkBytes)
                                {
                                    progress.SkipFile();
                                    progress.Add(ShortcutTargetClassifier.Classify(path,
                                        new(null, MetadataProblem: "The .lnk file was empty or exceeded the 1 MiB metadata bound; it was not loaded."),
                                        _ => throw new InvalidOperationException("No probe is permitted"), DateTimeOffset.UtcNow));
                                    continue;
                                }
                                using var readLease = WindowsShortcutNative.OpenReadOnlyLink(path);
                                if (readLease.IsInvalid || !WindowsShortcutNative.TryReadSafeFileInformation(readLease, out var readInfo) ||
                                    readInfo.Bytes == 0 || readInfo.Bytes > MaximumLinkBytes)
                                {
                                    progress.SkipFile();
                                    progress.Issue(path, "The shortcut is locked, changed, unreadable or unsupported; no metadata load was attempted.");
                                    progress.Add(new(path, "", ShortcutTargetState.Unknown,
                                        "Read-only shared access and stable supported shortcut metadata were not established; not a missing-target conclusion.",
                                        ShortcutTargetClassifier.Source, DateTimeOffset.UtcNow));
                                    continue;
                                }
                                var stored = WindowsShortcutMetadata.Read(path, token);
                                token.ThrowIfCancellationRequested();
                                var observation = ShortcutTargetClassifier.Classify(path, stored,
                                    target => WindowsShortcutNative.ProbeTarget(target, token), DateTimeOffset.UtcNow);
                                token.ThrowIfCancellationRequested();
                                progress.Add(observation with { CheckedAt = DateTimeOffset.UtcNow });
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
                    { progress.SkipDirectory(); progress.Issue(folder.Path, $"Selected-scope enumeration was unavailable (code 0x{ex.HResult:X8}); inaccessible coverage is unknown."); }
                }
            }
        }
        var report = progress.Snapshot(false);
        bool partial = report.Truncated || report.Issues.Count > 0 || report.Entries.Any(entry =>
            entry.State is not (ShortcutTargetState.TargetExists or ShortcutTargetState.LocalTargetMissing));
        return new(partial ? CheckState.Partial : CheckState.Available,
            $"Read-only shortcut discovery returned {report.Entries.Count} observations. Review source, check time and uncertainty; missing stored local paths are not proof of obsolete shortcuts. No removal is authorized.", [report]);
    }

    private static void RequestStop(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class Progress
    {
        private readonly object gate = new();
        private readonly List<ShortcutObservation> entries = [];
        private readonly List<FileReviewIssue> issues = [];
        private int visitedEntries, visitedFiles, skippedFiles, directories, skippedDirectories;
        private bool truncated;
        internal bool Limited { get { lock (gate) return truncated; } }
        internal int Count { get { lock (gate) return entries.Count; } }
        internal bool Visit() { lock (gate) return ++visitedEntries <= MaximumVisitedEntries; }
        internal void File() { lock (gate) visitedFiles++; }
        internal void SkipFile() { lock (gate) skippedFiles++; }
        internal void Directory() { lock (gate) directories++; }
        internal void SkipDirectory() { lock (gate) skippedDirectories++; }
        internal void Add(ShortcutObservation observation) { lock (gate) { if (entries.Count < MaximumLinks) entries.Add(observation); else truncated = true; } }
        internal void Issue(string path, string reason)
        {
            lock (gate)
            {
                if (issues.Count < MaximumIssues) issues.Add(new(path, reason));
                else truncated = true;
            }
        }
        internal void Limit(string path, string reason) { lock (gate) { truncated = true; Issue(path, reason); } }
        internal ShortcutReport Snapshot(bool incomplete)
        {
            lock (gate)
                return new(entries.ToArray(), issues.ToArray(), visitedFiles, skippedFiles, truncated || incomplete,
                    $"Selected-folder .lnk metadata only; {directories} directories observed, {skippedDirectories} directories skipped. " +
                    "Other file types, advertised/PIDL/packaged/argument/environment/extended-context targets and unavailable/reparse/cloud paths are not confirmed broken. " +
                    "Bounds: 10,000 directory entries, 1,000 link observations, depth 64, 100 issues, 1 MiB per link and 60-second caller wait; native calls may finish later after cancellation. " +
                    "No Resolve, search, tracking, target launch, argument/working-directory/icon collection, MSI activation, network query, save or removal was performed. " +
                    "TargetExists concerns the stored local path only; LocalTargetMissing is time-bound local metadata evidence, not an obsolescence or deletion verdict.");
        }
    }
}
