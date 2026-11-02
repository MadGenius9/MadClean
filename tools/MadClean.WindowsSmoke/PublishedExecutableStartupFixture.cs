using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal enum PublishedExecutableStartupState { Observed, Inconclusive, Failed, Pending, CancelledBeforeStart }
internal sealed record PublishedExecutableStartupReport(PublishedExecutableStartupState State, bool DescriptorValidated,
    bool ExecutableLeased, bool ProcessStarted, bool OwnedWindowObserved, bool NeutralOverviewObserved,
    bool OverviewNoDataObserved, bool OwnedBundledRuntimeObserved, bool CloseRequested, bool? ExitObserved,
    int? ExitCode, bool CleanupConfirmed, string Stage, int? NativeError)
{
    public string RuntimeRoute { get; init; } = "Unobserved";
    public bool? StaticHostMetadataObserved { get; init; }
    public string StaticHostMetadataStage { get; init; } = "not-attempted";
    public bool? OwnedAppHostModuleObserved { get; init; }
    public string RuntimeStage { get; init; } = "not-attempted";
    public int? RuntimeNativeError { get; init; }
    public string OverviewStage { get; init; } = "not-attempted";
    public int? OverviewNativeError { get; init; }
    public int? RuntimeNeededModuleCount { get; init; }
    public bool? RuntimeSnapshotComplete { get; init; }
    public bool? RuntimePathsComplete { get; init; }
    public bool? RuntimeHostFxrObserved { get; init; }
    public bool? RuntimeHostPolicyObserved { get; init; }
    public bool? RuntimeCoreClrObserved { get; init; }
    public bool? RuntimeClrJitObserved { get; init; }
    public bool? RuntimeWpfNativeObserved { get; init; }
    public int InventoryInvocations => 0;
    public int Captures => 0;
}

// Actual separately published apphost startup only. No query commands, UI invocations,
// environment permission flag, sample data, capture or distribution upload is provided.
internal static class PublishedExecutableStartupFixture
{
    private static int active, retention;
    private static readonly List<object> RetainedOwnership = [];
    internal static bool HasPendingStartup => Volatile.Read(ref active) != 0;
    internal static bool RequiresRetention => Volatile.Read(ref retention) != 0;
    private const uint ReadAttributes = 0x80, NoFollow = 0x00200000, NoRecall = 0x00100000, Backup = 0x02000000;
    private const uint Unsafe = 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;

    internal static async Task<PublishedExecutableStartupReport> RunAsync(string repository, string expectedSourceCommit, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Empty(PublishedExecutableStartupState.CancelledBeforeStart, "cancelled-before-start");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || RequiresRetention)
            return Empty(PublishedExecutableStartupState.Inconclusive, "platform-or-prior-ownership-unavailable");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return Empty(PublishedExecutableStartupState.Pending, "prior-startup-worker-pending");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<PublishedExecutableStartupReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            PublishedExecutableStartupReport result;
            try { result = new Worker(repository, expectedSourceCommit, stop.Token).Run(); }
            catch (Exception) { Retain(); result = Empty(PublishedExecutableStartupState.Inconclusive, "worker-unexpected-failure"); }
            finally { stop.Dispose(); Volatile.Write(ref active, 0); }
            completion.TrySetResult(result);
        }, CancellationToken.None);
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            return token.IsCancellationRequested ? result with { State = PublishedExecutableStartupState.Inconclusive, Stage = "stop-at-publication" } : result;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            Retain(); try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return Empty(PublishedExecutableStartupState.Pending, "caller-stopped-worker-still-owns-process-and-automation");
        }
    }
    private static void Retain() => Volatile.Write(ref retention, 1);
    private static PublishedExecutableStartupReport Empty(PublishedExecutableStartupState state, string stage) =>
        new(state, false, false, false, false, false, false, false, false, null, null, false, stage, null);

    private sealed class Worker(string repository, string expectedCommit, CancellationToken token)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<OwnedHandle> handles = [];
        private readonly List<Guard> guards = [];
        private string descriptorPath = "", scope = "", publish = "", cache = "", exe = "", markerPath = "", marker = "", stage = "validate-repository";
        private OwnedHandle? descriptor, executable;
        private Stamp? executableStamp;
        private byte[]? descriptorBytes;
        private Stamp? rootStamp;
        private Process? process;
        private nint window;
        private bool descriptorValid, executableLeased, launchAttempted, started, ownedWindow, neutral, noData, bundled, closeRequested, cleaned;
        private bool? exited;
        private int? exitCode, nativeError;
        private string runtimeStage = "not-attempted", overviewStage = "not-attempted";
        private int? runtimeNativeError, overviewNativeError, runtimeNeededModuleCount;
        private bool? runtimeSnapshotComplete, runtimePathsComplete;
        private bool hostFxrSeen, hostPolicySeen, coreClrSeen, clrJitSeen, wpfNativeSeen, appHostSeen;
        private string runtimeRoute = "Unobserved", staticHostMetadataStage = "not-attempted";
        private bool? staticHostMetadataObserved;

        internal PublishedExecutableStartupReport Run()
        {
            bool failed = false;
            try { Execute(); }
            catch (Invariant failure) { failed = true; stage = failure.Stage; }
            catch (Unavailable failure) { stage = failure.Stage; nativeError = failure.Code; }
            catch (OperationCanceledException) { stage = "worker-stop-after-start"; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or
                JsonException or ElementNotAvailableException or ElementNotEnabledException or System.Security.SecurityException or COMException)
            { stage = "startup-prerequisite-or-observation-unavailable"; }
            finally
            {
                // A blocked UIA/native call has not reached this finally. The caller can never
                // close the process, release the guards or remove the bundle tree underneath it.
                try { CloseOwnedProcess(); }
                catch (Unavailable failure) { stage = failure.Stage; nativeError = failure.Code; Retain(); }
                catch (Exception) { stage = "owned-process-exit-unconfirmed"; Retain(); }
                if (!RequiresRetention)
                {
                    try
                    {
                        if (started && exited != true) throw new Unavailable("owned-process-exit-unconfirmed", null);
                        if (descriptorValid) CleanupScope();
                        else ReleaseHandles();
                    }
                    catch (Exception) { stage = "published-owned-scope-cleanup-unconfirmed"; Retain(); }
                }
                if (RequiresRetention) lock (RetainedOwnership) RetainedOwnership.Add(this);
                if (descriptorBytes != null) Array.Clear(descriptorBytes);
            }
            if (exited == true && exitCode is not null && exitCode != 0)
            { failed = true; stage = "published-process-observed-nonzero-exit"; }
            var state = failed ? PublishedExecutableStartupState.Failed : descriptorValid && executableLeased && started && ownedWindow && neutral && noData && bundled &&
                closeRequested && exited == true && exitCode == 0 && cleaned && !RequiresRetention ? PublishedExecutableStartupState.Observed : PublishedExecutableStartupState.Inconclusive;
            return new(state, descriptorValid, executableLeased, started, ownedWindow, neutral, noData, bundled,
                closeRequested, exited, exitCode, cleaned && !RequiresRetention, stage, nativeError)
            {
                RuntimeRoute = runtimeRoute, StaticHostMetadataObserved = staticHostMetadataObserved,
                StaticHostMetadataStage = staticHostMetadataStage, OwnedAppHostModuleObserved = RuntimeFlag(appHostSeen),
                RuntimeStage = runtimeStage, RuntimeNativeError = runtimeNativeError,
                OverviewStage = overviewStage, OverviewNativeError = overviewNativeError,
                RuntimeNeededModuleCount = runtimeNeededModuleCount, RuntimeSnapshotComplete = runtimeSnapshotComplete, RuntimePathsComplete = runtimePathsComplete,
                RuntimeHostFxrObserved = RuntimeFlag(hostFxrSeen), RuntimeHostPolicyObserved = RuntimeFlag(hostPolicySeen),
                RuntimeCoreClrObserved = RuntimeFlag(coreClrSeen), RuntimeClrJitObserved = RuntimeFlag(clrJitSeen), RuntimeWpfNativeObserved = RuntimeFlag(wpfNativeSeen)
            };
        }
        private bool? RuntimeFlag(bool seen) => seen ? true : runtimePathsComplete == true ? false : null;
        private void RequireSettledObservationOwnership()
        {
            Check();
            if (RequiresRetention) throw new Unavailable("independent-observation-ownership-unsettled", null);
            if (process == null || process.HasExited) throw new Invariant("published-process-exited-during-startup-observation");
            if (!OwnsWindow(window)) throw new Unavailable("original-owned-window-association-unconfirmed", null);
        }
        private void Execute()
        {
            Check();
            if (repository != Path.GetFullPath(repository) || !string.Equals(repository, Path.GetFullPath(Environment.CurrentDirectory), StringComparison.OrdinalIgnoreCase) ||
                !Hex(expectedCommit, 40) || !File.Exists(Path.Combine(repository, "MadClean.sln"))) throw new Unavailable("repository-or-source-unqualified", null);
            GuardChain(Path.Combine(repository, "artifacts"));
            descriptorPath = Path.Combine(repository, "artifacts", "published-startup-descriptor.json");
            descriptor = Open(descriptorPath, 0x80000000 | ReadAttributes, 1);
            descriptorBytes = ReadBounded(descriptor, descriptorPath, 512);
            using (var json = JsonDocument.Parse(descriptorBytes, new JsonDocumentOptions { MaxDepth = 2 }))
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new Unavailable("descriptor-shape-invalid", null);
                var fields = root.EnumerateObject().ToArray();
                if (fields.Length != 3 || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                    fields.Any(f => f.Name is not ("SourceCommit" or "ScopeId" or "OwnerMarker") || f.Value.ValueKind != JsonValueKind.String)) throw new Unavailable("descriptor-shape-invalid", null);
                string? source = root.GetProperty("SourceCommit").GetString(), id = root.GetProperty("ScopeId").GetString(), owner = root.GetProperty("OwnerMarker").GetString();
                if (source != expectedCommit || !Hex(source, 40) || !Guid.TryParseExact(id, "N", out var scopeId) || scopeId == Guid.Empty || id != scopeId.ToString("N") ||
                    !Guid.TryParseExact(owner, "N", out var ownerId) || ownerId == Guid.Empty || owner != ownerId.ToString("N")) throw new Unavailable("descriptor-source-or-owner-unqualified", null);
                marker = owner;
                scope = Path.Combine(repository, "artifacts", "published-startup-" + id);
            }
            publish = Path.Combine(scope, "publish"); cache = Path.Combine(scope, "bundle-cache"); exe = Path.Combine(publish, "MadClean.exe");
            markerPath = Path.Combine(scope, ".madclean-published-startup-owner");
            GuardChain(scope); rootStamp = guards.Single(g => PathEquals(g.Path, scope)).Stamp;
            RequireMarker(); GuardChain(publish); GuardChain(cache);
            if (Directory.EnumerateFileSystemEntries(cache).Any()) throw new Unavailable("bundle-cache-not-fresh", null);
            descriptorValid = true;
            executable = Open(exe, 0x80000000 | ReadAttributes, 1);
            executableStamp = Observe(executable, exe, false);
            if (executableStamp.Bytes is < 1 or > 536_870_912) throw new Unavailable("published-executable-size-unsupported", null);
            executableLeased = true; RecheckGuards();
            stage = "start-published-apphost";
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = publish };
            start.Environment.Clear();
            foreach (string name in new[] { "SystemRoot", "WINDIR", "SystemDrive", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA", "ProgramFiles", "ProgramFiles(x86)",
                "CommonProgramFiles", "CommonProgramFiles(x86)", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS" })
            { string? value = Environment.GetEnvironmentVariable(name); if (value != null) start.Environment[name] = value; }
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = cache;
            process = new Process { StartInfo = start };
            launchAttempted = true;
            if (!process.Start()) { Retain(); throw new Unavailable("published-process-not-started", null); }
            started = true;
            _ = process.SafeHandle; // Retain the handle returned for this own spawn, never reopen by PID.
            stage = "observe-owned-window";
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(15))
            {
                Check();
                if (process.HasExited) throw new Invariant("published-process-exited-before-window");
                process.Refresh(); window = process.MainWindowHandle;
                if (window != 0 && OwnsWindow(window)) break;
                Thread.Sleep(50);
            }
            if (window == 0 || !OwnsWindow(window)) throw new Unavailable("owned-window-unobserved", null);
            ownedWindow = true;
            stage = "observe-owned-bundled-runtime";
            runtimeStage = "attempting-runtime-observation";
            try { ObserveBundledRuntime(); bundled = true; runtimeStage = "observed"; }
            catch (Unavailable failure) { runtimeStage = failure.Stage; runtimeNativeError = failure.Code; }
            RequireSettledObservationOwnership();
            stage = "observe-neutral-overview";
            overviewStage = "attempting-overview-observation";
            try
            {
                ObserveNeutralOverview(); Thread.Sleep(250); Check(); ObserveNeutralOverview();
                neutral = true; noData = true; overviewStage = "observed";
            }
            catch (Unavailable failure) { overviewStage = failure.Stage; overviewNativeError = failure.Code; }
            RequireSettledObservationOwnership();
            stage = bundled && neutral && noData ? "published-neutral-startup-observed" : "independent-startup-observations-incomplete";
        }
        private void ObserveNeutralOverview()
        {
            Check(); if (!OwnsWindow(window)) throw new Unavailable("owned-window-association-changed", null);
            var root = AutomationElement.FromHandle(window);
            if (root.Current.ProcessId != process!.Id || root.Current.ControlType != ControlType.Window || root.Current.AutomationId != "MadClean.MainWindow")
                throw new Unavailable("published-window-automation-unavailable", null);
            AutomationElement Find(string id)
            {
                var found = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                string label = "neutral-" + id[(id.LastIndexOf('.') + 1)..];
                if (found.Count != 1) throw new Unavailable(label + (found.Count == 0 ? "-missing" : "-multiple"), null);
                if (found[0].Current.ProcessId != process.Id) throw new Unavailable(label + "-owner-unconfirmed", null);
                return found[0];
            }
            var inspect = Find("MadClean.Overview.Inspect");
            if (inspect.Current.ControlType != ControlType.Button || !inspect.Current.IsEnabled) throw new Invariant("neutral-inspect-button-unavailable");
            foreach (var expected in new[] { ("MadClean.Overview.CleanupStatus", "Not checked"), ("MadClean.Overview.StartupStatus", "Not checked"),
                ("MadClean.Overview.SecurityStatus", "Unknown"), ("MadClean.Overview.UpdateStatus", "Not checked"), ("MadClean.Overview.StorageFree", "Not checked"),
                ("MadClean.Overview.StorageCapacity", "No confirmed capacity reading"), ("MadClean.Footer.Status", "Waiting for first inspection. Nothing has been checked.") })
            {
                var element = Find(expected.Item1);
                bool summary = expected.Item1 is "MadClean.Overview.CleanupStatus" or "MadClean.Overview.StartupStatus" or "MadClean.Overview.SecurityStatus" or "MadClean.Overview.UpdateStatus";
                if (summary && element.Current.ControlType != ControlType.Button) throw new Invariant("published-summary-control-type-unexpected");
                string observed = summary ? element.Current.ItemStatus : element.Current.Name;
                if (observed != expected.Item2) throw new Invariant("published-startup-not-neutral-overview");
            }
            var drive = Find("MadClean.Overview.Drive");
            if (!drive.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection) || ((SelectionPattern)selection).Current.GetSelection().Length != 0)
                throw new Invariant("published-startup-drive-selected");
            // No InvokePattern, SelectionItemPattern, navigation, selector expansion or query.
        }
        private void ObserveBundledRuntime()
        {
            Check(); var modules = new nint[256]; uint capacity = checked((uint)(modules.Length * IntPtr.Size));
            try
            {
                runtimeSnapshotComplete = false;
                if (!K32EnumProcessModulesEx(process!.SafeHandle, modules, capacity, out uint needed, 3)) throw new Unavailable("own-module-snapshot-unavailable", Marshal.GetLastWin32Error());
                if (needed % IntPtr.Size == 0) runtimeNeededModuleCount = checked((int)(needed / IntPtr.Size));
                if (needed == 0 || needed > capacity || needed % IntPtr.Size != 0) throw new Unavailable("own-module-snapshot-bound", null);
                runtimeSnapshotComplete = true; runtimePathsComplete = false;
                bool found = false;
                for (int index = 0; index < needed / IntPtr.Size; index++)
                {
                    Check(); var buffer = new StringBuilder(1024);
                    uint length = K32GetModuleFileNameExW(process.SafeHandle, modules[index], buffer, (uint)buffer.Capacity);
                    if (length == 0) throw new Unavailable("own-module-path-unavailable", Marshal.GetLastWin32Error());
                    if (length >= buffer.Capacity - 1) throw new Unavailable("own-module-path-truncated", null);
                    string path = buffer.ToString();
                    string name = Path.GetFileName(path);
                    appHostSeen |= PathEquals(path, exe) && PathEquals(path, Path.GetFullPath(path));
                    hostFxrSeen |= string.Equals(name, "hostfxr.dll", StringComparison.OrdinalIgnoreCase);
                    hostPolicySeen |= string.Equals(name, "hostpolicy.dll", StringComparison.OrdinalIgnoreCase);
                    coreClrSeen |= string.Equals(name, "coreclr.dll", StringComparison.OrdinalIgnoreCase);
                    clrJitSeen |= string.Equals(name, "clrjit.dll", StringComparison.OrdinalIgnoreCase);
                    wpfNativeSeen |= name.Equals("PresentationNative_cor3.dll", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("PenImc_cor3.dll", StringComparison.OrdinalIgnoreCase);
                    if (!string.Equals(name, "coreclr.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!path.StartsWith(cache + "\\", StringComparison.OrdinalIgnoreCase) || !PathEquals(path, Path.GetFullPath(path))) throw new Unavailable("runtime-not-observed-in-owned-cache", null);
                    GuardChain(Path.GetDirectoryName(path)!); var file = Open(path, 0x80000000 | ReadAttributes, 1); _ = Observe(file, path, false); found = true;
                }
                runtimePathsComplete = true;
                if (found) runtimeRoute = "DynamicOwnedCache";
                else
                {
                    // .NET 10 may statically link CoreCLR into its single-file apphost.
                    // This is a separately bound observation route, never a missing-DLL fallback.
                    if (coreClrSeen || !appHostSeen) throw new Unavailable("own-bundled-runtime-unobserved", null);
                    ObserveStaticHostMetadata(); runtimeRoute = "StaticSingleFileHost";
                }
            }
            finally { Array.Clear(modules); }
        }
        private void ObserveStaticHostMetadata()
        {
            Check();
            if (runtimeSnapshotComplete != true || runtimePathsComplete != true || coreClrSeen || !appHostSeen ||
                executable == null || executableStamp == null || executable.IsInvalid || executable.IsClosed)
                throw new Unavailable("static-host-binding-unestablished", null);
            if (Observe(executable, exe, false) != executableStamp) throw new Unavailable("static-host-original-image-changed", null);
            staticHostMetadataStage = "attempting-static-host-metadata";
            bool reference = false;
            bool cursorSaved = false;
            long cursor = 0;
            SingleFileStaticHostMetadataResult metadata;
            try
            {
                executable.DangerousAddRef(ref reference);
                if (!SetFilePointerEx(executable, 0, out cursor, 1)) throw new Unavailable("static-host-cursor-unavailable", Marshal.GetLastWin32Error());
                cursorSaved = true;
                if (cursor < 0) throw new Unavailable("static-host-cursor-invalid", null);
                using (var borrowed = new SafeFileHandle(executable.DangerousGetHandle(), ownsHandle: false))
                using (var stream = new FileStream(borrowed, FileAccess.Read, 4096, isAsync: false))
                {
                    long streamPosition = stream.Position;
                    metadata = SingleFileStaticHostMetadata.Read(stream, token);
                    if (stream.Position != streamPosition) throw new Unavailable("static-host-stream-position-changed", null);
                }
            }
            finally
            {
                try
                {
                    if (cursorSaved)
                    {
                        if (!SetFilePointerEx(executable, cursor, out long restored, 0))
                        { int error = Marshal.GetLastWin32Error(); Retain(); throw new Unavailable("static-host-cursor-restore-unconfirmed", error); }
                        if (restored != cursor) { Retain(); throw new Unavailable("static-host-cursor-restore-unconfirmed", null); }
                        if (!SetFilePointerEx(executable, 0, out long current, 1))
                        { int error = Marshal.GetLastWin32Error(); Retain(); throw new Unavailable("static-host-cursor-readback-unconfirmed", error); }
                        if (current != cursor) { Retain(); throw new Unavailable("static-host-cursor-readback-unconfirmed", null); }
                        if (!SetFilePointerEx(executable, 0, out long verified, 1))
                        { int error = Marshal.GetLastWin32Error(); Retain(); throw new Unavailable("static-host-cursor-verify-unconfirmed", error); }
                        if (verified != cursor) { Retain(); throw new Unavailable("static-host-cursor-restore-unconfirmed", null); }
                    }
                }
                finally { if (reference) executable.DangerousRelease(); }
            }
            if (Observe(executable, exe, false) != executableStamp) throw new Unavailable("static-host-image-bookend-changed", null);
            staticHostMetadataObserved = metadata.StaticHostMetadataObserved;
            staticHostMetadataStage = metadata.State + "-" + metadata.Reason;
            if (!metadata.StaticHostMetadataObserved) throw new Unavailable("static-host-metadata-unestablished", null);
        }

        private bool OwnsWindow(nint candidate)
        {
            if (candidate == 0 || process == null || process.HasExited) return false;
            uint thread = GetWindowThreadProcessId(candidate, out uint id);
            return thread != 0 && id == process.Id;
        }
        private void CloseOwnedProcess()
        {
            if (process == null) return;
            if (!started)
            {
                if (launchAttempted) { Retain(); throw new Unavailable("process-start-outcome-unconfirmed", null); }
                process.Dispose(); process = null; return;
            }
            if (!process.HasExited)
            {
                // WM_CLOSE is only a request to this observed own window, never a kill.
                if (!OwnsWindow(window)) { process.Refresh(); window = process.MainWindowHandle; }
                if (!OwnsWindow(window)) throw new Unavailable("owned-close-window-unobserved", null);
                if (!PostMessageW(window, 0x10, 0, 0)) throw new Unavailable("owned-window-close-request-failed", Marshal.GetLastWin32Error());
                closeRequested = true;
                exited = process.WaitForExit(10_000) && process.HasExited;
            }
            else exited = true;
            if (exited != true) throw new Unavailable("owned-process-exit-unconfirmed", null);
            exitCode = process.ExitCode; process.Dispose(); process = null;
        }
        private void CleanupScope()
        {
            if (started && exited != true) throw new Unavailable("cleanup-process-not-exited", null);
            Check(); RecheckGuards(); RequireMarker();
            if (rootStamp == null || !SameObject(rootStamp, guards.Single(g => PathEquals(g.Path, scope)).Stamp)) throw new Unavailable("cleanup-root-identity-unconfirmed", null);
            var nodes = new List<(string Path, bool Directory)>(); var stack = new Stack<(string Path, int Depth)>(); stack.Push((scope, 0));
            long bytes = 0;
            while (stack.Count > 0)
            {
                Check(); var next = stack.Pop(); if (next.Depth > 8 || nodes.Count >= 512) throw new Unavailable("published-tree-bound", null);
                GuardChain(next.Path); nodes.Add((next.Path, true));
                foreach (string entry in Directory.EnumerateFileSystemEntries(next.Path))
                {
                    Check(); if (next.Depth + 1 > 8) throw new Unavailable("published-tree-depth-bound", null);
                    if (nodes.Count + stack.Count >= 512) throw new Unavailable("published-tree-bound", null);
                    uint attributes = GetFileAttributesW(entry);
                    if (attributes == uint.MaxValue) throw new Unavailable("published-tree-attributes-unavailable", Marshal.GetLastWin32Error());
                    if ((attributes & Unsafe) != 0) throw new Unavailable("published-tree-unsafe-entry", null);
                    if ((attributes & 0x10) != 0) stack.Push((entry, next.Depth + 1));
                    else { var file = Open(entry, ReadAttributes, 3); var stamp = Observe(file, entry, false); bytes = checked(bytes + stamp.Bytes); if (bytes > 2_147_483_648L) throw new Unavailable("published-tree-byte-bound", null); nodes.Add((entry, false)); }
                }
            }
            byte[] currentDescriptor = ReadBounded(descriptor!, descriptorPath, 512);
            try { if (descriptorBytes == null || !currentDescriptor.AsSpan().SequenceEqual(descriptorBytes)) throw new Unavailable("descriptor-changed-before-cleanup", null); }
            finally { Array.Clear(currentDescriptor); }
            RecheckGuards(); ReleaseHandles();
            // Bounded, already audited DEVELOPMENT artifacts only. This is not a cleanup
            // primitive fallback and never touches Temp/client files or an unverified tree.
            foreach (var node in nodes.OrderByDescending(n => n.Path.Length))
            { if (node.Directory) Directory.Delete(node.Path, false); else File.Delete(node.Path); }
            uint remaining = GetFileAttributesW(scope); int code = remaining == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
            if (remaining != uint.MaxValue || code != 2) throw new Unavailable("published-scope-removal-unconfirmed", remaining == uint.MaxValue ? code : null);
            File.Delete(descriptorPath);
            remaining = GetFileAttributesW(descriptorPath); code = remaining == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
            if (remaining != uint.MaxValue || code != 2) throw new Unavailable("descriptor-removal-unconfirmed", remaining == uint.MaxValue ? code : null);
            cleaned = true;
        }
        private void RequireMarker()
        {
            var file = Open(markerPath, 0x80000000 | ReadAttributes, 1); byte[] value = ReadBounded(file, markerPath, 32);
            try { if (Encoding.ASCII.GetString(value) != marker) throw new Unavailable("published-owner-marker-mismatch", null); }
            finally { Array.Clear(value); Close(file); }
        }
        private void Check() { token.ThrowIfCancellationRequested(); if (watch.Elapsed >= TimeSpan.FromSeconds(35)) throw new Unavailable("startup-cooperative-budget", null); }
        private OwnedHandle Open(string path, uint access, uint share = 3, uint flags = 0)
        {
            Check(); var handle = CreateFileW(path, access, share, 0, 3, NoFollow | NoRecall | flags, 0); int? error = handle.IsInvalid ? Marshal.GetLastWin32Error() : null; handles.Add(handle);
            if (handle.IsInvalid) throw new Unavailable("owned-open-unavailable", error); return handle;
        }
        private static void Close(OwnedHandle file) { if (!file.CloseChecked(out int error)) throw new Unavailable("native-close-unconfirmed", error); }
        private void ReleaseHandles()
        { for (int index = handles.Count - 1; index >= 0; index--) Close(handles[index]); }
        private byte[] ReadBounded(OwnedHandle file, string path, int maximum)
        {
            var stamp = Observe(file, path, false); if (stamp.Bytes is < 1 || stamp.Bytes > maximum) throw new Unavailable("owned-metadata-file-bound", null);
            byte[] bytes = new byte[(int)stamp.Bytes];
            try
            {
                if (!SetFilePointerEx(file, 0, out long position, 0)) throw new Unavailable("owned-read-seek-unavailable", Marshal.GetLastWin32Error());
                if (position != 0) throw new Unavailable("owned-read-position-unconfirmed", null);
                if (!ReadFile(file, bytes, (uint)bytes.Length, out uint read, 0)) throw new Unavailable("owned-file-read-unavailable", Marshal.GetLastWin32Error()); if (read != bytes.Length) throw new Unavailable("owned-file-read-count-unexpected", null);
                if (Observe(file, path, false) != stamp) throw new Unavailable("owned-metadata-file-changed", null);
                return bytes;
            }
            catch { Array.Clear(bytes); throw; }
        }
        private Stamp Observe(OwnedHandle file, string path, bool directory)
        {
            if (!GetIdentity(file, 18, out var id, 24) || !GetStandard(file, 1, out var size, 24) || !GetBasic(file, 0, out var basic, 40)) throw new Unavailable("owned-file-metadata-unavailable", Marshal.GetLastWin32Error());
            var text = new StringBuilder(1024); uint length = GetFinalPathNameByHandleW(file, text, 1024, 0);
            if (length == 0) throw new Unavailable("owned-final-path-unavailable", Marshal.GetLastWin32Error()); string actual = text.ToString(); if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (length >= 1024 || !PathEquals(actual, path) || id.Volume == 0 || id.Id == Guid.Empty || (basic.Attributes & Unsafe) != 0 ||
                ((basic.Attributes & 0x10) != 0) != directory || size.Directory != directory || size.DeletePending || size.Bytes < 0 || !directory && size.Links != 1)
                throw new Unavailable("owned-object-unsafe-or-changed", null);
            return new(id.Volume, id.Id, size.Bytes);
        }
        private void GuardChain(string path)
        {
            if (path.Length is < 3 or > 1023 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('/') || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl) || new DriveInfo(path[..3]).DriveType != DriveType.Fixed)
                throw new Unavailable("fixed-local-owned-path-required", null);
            var paths = new Stack<string>(); for (string? current = path; current != null; current = Path.GetDirectoryName(current)) { if (paths.Count >= 64) throw new Unavailable("owned-ancestor-bound", null); paths.Push(current); }
            while (paths.Count > 0) { string current = paths.Pop(); if (guards.Any(g => PathEquals(g.Path, current))) continue; var file = Open(current, ReadAttributes | 0x20, 3, Backup); guards.Add(new(current, file, Observe(file, current, true))); }
        }
        private void RecheckGuards() { foreach (var guard in guards) if (!SameObject(guard.Stamp, Observe(guard.Handle, guard.Path, true))) throw new Unavailable("owned-directory-identity-changed", null); }
    }
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool PathEquals(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);
    private static bool SameObject(Stamp left, Stamp right) => left.Volume == right.Volume && left.Id == right.Id;
    private sealed record Stamp(ulong Volume, Guid Id, long Bytes);
    private sealed record Guard(string Path, OwnedHandle Handle, Stamp Stamp);
    private sealed class Unavailable(string stage, int? code) : Exception("Published startup observation unavailable.") { internal string Stage => stage; internal int? Code => code; }
    private sealed class Invariant(string stage) : Exception("Published startup invariant failed.") { internal string Stage => stage; }
    private sealed class OwnedHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public OwnedHandle() : base(true) { }
        internal bool CloseChecked(out int error)
        { error = 0; if (IsInvalid || IsClosed) return true; bool success = CloseHandle(handle); error = success ? 0 : Marshal.GetLastWin32Error(); SetHandleAsInvalid(); Dispose(); if (!success) { Retain(); lock (RetainedOwnership) RetainedOwnership.Add(this); } return success; }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Identity { internal ulong Volume; internal Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct Basic { internal long Created, Accessed, Written, Changed; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Standard { internal long Allocation, Bytes; internal uint Links; [MarshalAs(UnmanagedType.U1)] internal bool DeletePending; [MarshalAs(UnmanagedType.U1)] internal bool Directory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern OwnedHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFilePointerEx(OwnedHandle file, long distance, out long position, uint origin);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(OwnedHandle file, [Out] byte[] buffer, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint file);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdentity(OwnedHandle file, int kind, out Identity value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBasic(OwnedHandle file, int kind, out Basic value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(OwnedHandle file, int kind, out Standard value, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFinalPathNameByHandleW(OwnedHandle file, StringBuilder path, uint count, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool K32EnumProcessModulesEx(SafeProcessHandle process, [Out] nint[] modules, uint bytes, out uint needed, uint filter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint K32GetModuleFileNameExW(SafeProcessHandle process, nint module, StringBuilder path, uint count);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
