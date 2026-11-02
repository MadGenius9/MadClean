using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal enum StartupTransactionNamespaceState { Recorded, Inconclusive, Failed, Pending }
internal enum StartupTransactionNamespaceCase { SourceRenameSourceOnly, AncestorRenameSourceOnly, AncestorRenameAll, SourceReplacementAll }
internal sealed record StartupTransactionNamespaceCaseResult(StartupTransactionNamespaceCase Case,
    StartupTransactionNamespaceState State, string Stage, int? NativeError, int? RenameError,
    int? CreateError, int? WriteError, uint? FirstOutcome, bool FirstOutcomeObservedBeforeTimeout,
    bool InterferenceSucceeded, bool AbortObserved, bool TerminalObserved, bool BaselinePreserved, bool TeardownCompleted);
internal sealed record StartupTransactionNamespaceResult(StartupTransactionNamespaceState State,
    IReadOnlyList<StartupTransactionNamespaceCaseResult> Cases, bool TeardownCompleted, bool PendingWorker,
    string Stage, int? NativeError);

// Development harness only. No real startup source, REG_LINK, native name query, journal or commit.
// A Recorded result describes an observed sequence, never protection or production binding proof.
internal static class StartupTransactionNamespaceProbe
{
    private static int active, retain;
    private static Task<StartupTransactionNamespaceResult>? pending;
    private static readonly List<object> RetainedWorkers = [];
    internal static bool HasPendingNativeProbe => Volatile.Read(ref active) != 0;
    internal static bool RequiresFixtureRetention => Volatile.Read(ref retain) != 0;
    internal static Task<StartupTransactionNamespaceResult>? PendingCompletion => Volatile.Read(ref pending);

    internal static async Task<StartupTransactionNamespaceResult> ProbeAsync(string fixtureDir, string marker, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return Empty("windows-required");
        if (token.IsCancellationRequested) return Empty("cancelled-before-start");
        if (RequiresFixtureRetention || Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return new(StartupTransactionNamespaceState.Pending, [], false, HasPendingNativeProbe, "prior-owned-evidence-retained", null);
        Volatile.Write(ref retain, 1);
        var worker = Task.Run(() =>
        {
            var owner = new Worker(fixtureDir, marker, token);
            // Root ownership before any native call, including an unexpected worker fault.
            lock (RetainedWorkers) RetainedWorkers.Add(owner);
            try
            {
                var result = owner.Run();
                if (result.TeardownCompleted)
                {
                    lock (RetainedWorkers) RetainedWorkers.Remove(owner);
                    Volatile.Write(ref retain, 0);
                }
                return result;
            }
            finally { Volatile.Write(ref active, 0); }
        }, CancellationToken.None);
        Volatile.Write(ref pending, worker);
        _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false); }
        catch (TimeoutException)
        { return new(StartupTransactionNamespaceState.Pending, [], false, true, "caller-wait-expired-worker-owns-resources", null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(StartupTransactionNamespaceState.Pending, [], false, true, "caller-cancelled-worker-owns-resources", null); }
    }
    private static StartupTransactionNamespaceResult Empty(string stage) =>
        new(StartupTransactionNamespaceState.Inconclusive, [], !RequiresFixtureRetention, HasPendingNativeProbe, stage, null);

    private sealed class Stop(string stage, int? code = null, bool failed = false) : Exception("Owned namespace sequence incomplete.")
    {
        internal string Stage { get; } = stage;
        internal int? Code { get; } = code;
        internal bool Failed { get; } = failed;
    }
    private sealed class Worker(string fixtureDir, string marker, CancellationToken token)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<CaseWorker> cases = [];
        internal StartupTransactionNamespaceResult Run()
        {
            List<StartupTransactionNamespaceCaseResult> observations = [];
            try
            {
                ValidateOwner();
                foreach (var kind in Enum.GetValues<StartupTransactionNamespaceCase>())
                {
                    Check(); ValidateOwner();
                    var item = new CaseWorker(this, kind); cases.Add(item);
                    observations.Add(item.Run());
                    // Never start another native case over retained or uncertain resources.
                    if (!item.TeardownCompleted) break;
                }
                ValidateOwner();
                bool cleaned = cases.All(item => item.TeardownCompleted);
                bool recorded = observations.Count == 4 && observations.All(item => item.State == StartupTransactionNamespaceState.Recorded);
                var state = observations.Any(item => item.State == StartupTransactionNamespaceState.Failed)
                    ? StartupTransactionNamespaceState.Failed : recorded ? StartupTransactionNamespaceState.Recorded : StartupTransactionNamespaceState.Inconclusive;
                return new(state, observations.AsReadOnly(), cleaned, false,
                    recorded ? "four-owned-sequences-recorded-not-binding-proof" : "owned-sequences-incomplete", null);
            }
            catch (Stop ex)
            { return new(ex.Failed ? StartupTransactionNamespaceState.Failed : StartupTransactionNamespaceState.Inconclusive,
                observations.AsReadOnly(), false, false, ex.Stage, ex.Code); }
            catch (OperationCanceledException)
            { return new(StartupTransactionNamespaceState.Inconclusive, observations.AsReadOnly(), false, false, "worker-observed-cancellation", null); }
            catch (Exception ex) when (Expected(ex))
            { return new(StartupTransactionNamespaceState.Inconclusive, observations.AsReadOnly(), false, false, "required-owned-fixture-or-api-unavailable", null); }
        }
        internal void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= TimeSpan.FromSeconds(25)) throw new Stop("worker-cooperative-budget-expired");
        }
        internal void ValidateOwner()
        {
            if (marker == null || marker.Length != 32 || !marker.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new Stop("invalid-owner-marker-argument");
            string full = Path.GetFullPath(fixtureDir);
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
            const string prefix = "windows-validation-fixtures-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty)
                throw new Stop("invalid-owned-filesystem-guid");
            var ancestors = new Stack<string>();
            for (string? path = full; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            { if (ancestors.Count >= 64) throw new Stop("owned-filesystem-depth-bound"); ancestors.Push(path); }
            while (ancestors.Count > 0)
                if ((File.GetAttributes(ancestors.Pop()) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw new Stop("unsafe-owned-filesystem-ancestor");
            string markerPath = Path.Combine(full, ".madclean-fixture-owner");
            var info = new FileInfo(markerPath);
            if (info.Length != 32 || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0 ||
                File.ReadAllText(markerPath) != marker) throw new Stop("owned-filesystem-marker-mismatch");
        }
        internal string Owner => marker;
    }

    private sealed class CaseWorker(Worker owner, StartupTransactionNamespaceCase kind)
    {
        private const string Prefix = @"Software\MadClean.WindowsSmoke";
        private const string OwnerName = "MadCleanFixtureOwner", ValueName = "InertProbeDword";
        // Query/set/create/enumerate + READ_CONTROL/DELETE, only on the newly created owned nodes.
        private const uint OwnedAccess = 0x3000F;
        private const uint OpenLink = 8;
        private Key? prefix, root, ancestor, source, replacement;
        private Transaction? transaction;
        private readonly List<Key> enlisted = [];
        private readonly string guid = Guid.NewGuid().ToString("N");
        private bool rootCreationAttempted, rootCreated, rootMarked, ancestorCreated, ancestorMarked, sourceCreated, sourceMarked, replacementCreated, replacementMarked;
        private bool safeOrdinary = true, terminal, baseline, beforeTimeout, interference;
        private string ancestorName = "Ancestor", sourceName = "Source", stage = "initializing";
        private int? nativeError, renameError, createError, writeError;
        private uint? firstOutcome;
        private StartupTransactionNamespaceState state = StartupTransactionNamespaceState.Inconclusive;
        internal bool TeardownCompleted { get; private set; }

        internal StartupTransactionNamespaceCaseResult Run()
        {
            try
            {
                owner.Check(); owner.ValidateOwner();
                Native(RegOpenKeyExW(new IntPtr(unchecked((int)0x80000001)), Prefix, OpenLink, 0xF, out var opened), "open-existing-owned-prefix"); prefix = opened;
                rootCreationAttempted = true;
                Native(RegCreateKeyExW(prefix, guid, 0, IntPtr.Zero, 0, OwnedAccess, IntPtr.Zero, out opened, out uint disposition), "create-fresh-case-guid"); root = opened;
                if (disposition != 1) throw new Stop("case-guid-already-existed");
                rootCreated = true; WriteOwner(root); rootMarked = true;
                Native(RegCreateKeyExW(root, "Ancestor", 0, IntPtr.Zero, 0, OwnedAccess, IntPtr.Zero, out opened, out disposition), "create-owned-ancestor"); ancestor = opened;
                if (disposition != 1) throw new Stop("ancestor-already-existed");
                ancestorCreated = true; WriteOwner(ancestor); ancestorMarked = true;
                Native(RegCreateKeyExW(ancestor, "Source", 0, IntPtr.Zero, 0, OwnedAccess, IntPtr.Zero, out opened, out disposition), "create-owned-source"); source = opened;
                if (disposition != 1) throw new Stop("source-already-existed");
                sourceCreated = true; WriteOwner(source); sourceMarked = true; Native(WriteDword(source, 11), "write-inert-baseline");
                // Explicit ordinary no-follow opens are the handles that enter the transaction.
                Native(source.CloseChecked(), "close-created-source"); source = null;
                Native(ancestor.CloseChecked(), "close-created-ancestor"); ancestor = null;
                Native(root.CloseChecked(), "close-created-root"); root = null;
                Native(RegOpenKeyExW(prefix, guid, OpenLink, OwnedAccess, out opened), "open-ordinary-case-root"); root = opened;
                Native(RegOpenKeyExW(root, "Ancestor", OpenLink, OwnedAccess, out opened), "open-ordinary-ancestor"); ancestor = opened;
                Native(RegOpenKeyExW(ancestor, "Source", OpenLink, OwnedAccess, out opened), "open-ordinary-source"); source = opened;
                VerifyTree();
                if (ReadDword(source) != 11) throw new Stop("initial-inert-baseline-mismatch", failed: true);
                owner.Check(); owner.ValidateOwner();
                Observe();
                if (!safeOrdinary) throw new Stop("terminal-state-or-close-not-proven");
                owner.ValidateOwner();
                baseline = ReadDword(source) == 11 && (replacement == null || ReadDword(replacement) == 11);
                if (!baseline) throw new Stop("terminal-inert-baseline-mismatch", failed: true);
                if (firstOutcome == 2) throw new Stop("unexpected-committed-outcome-without-commit-call", failed: true);
                state = interference && (firstOutcome is 1 or 3) && beforeTimeout && terminal
                    ? StartupTransactionNamespaceState.Recorded : StartupTransactionNamespaceState.Inconclusive;
                stage = state == StartupTransactionNamespaceState.Recorded ? "owned-first-outcome-recorded-not-binding-proof" : "interference-or-timing-not-established";
            }
            catch (Stop ex) { state = ex.Failed ? StartupTransactionNamespaceState.Failed : StartupTransactionNamespaceState.Inconclusive; stage = ex.Stage; nativeError ??= ex.Code; }
            catch (OperationCanceledException) { stage = "worker-observed-cancellation"; }
            catch (Exception ex) when (Expected(ex)) { stage = "owned-fixture-or-api-unavailable"; }
            finally
            {
                // Observe always attempts terminal settlement first. No caller or finalizer is
                // allowed to dispose the retained ordinary handles while its outcome is unknown.
                if (safeOrdinary)
                {
                    try { Cleanup(); }
                    catch (Stop ex) { stage = ex.Stage; nativeError ??= ex.Code; }
                    catch (Exception ex) when (Expected(ex)) { stage = "owned-exact-teardown-unproven"; }
                }
            }
            if (!TeardownCompleted && state == StartupTransactionNamespaceState.Recorded) state = StartupTransactionNamespaceState.Inconclusive;
            return new(kind, state, stage, nativeError, renameError, createError, writeError, firstOutcome,
                beforeTimeout, interference, firstOutcome == 3, terminal, baseline, TeardownCompleted);
        }

        private void Observe()
        {
            const uint timeout = 5000;
            long started = Stopwatch.GetTimestamp();
            transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 1, 0, 0, timeout, "MadClean owned namespace observation");
            if (transaction.IsInvalid) throw new Stop("create-transaction-unavailable", Marshal.GetLastWin32Error());
            safeOrdinary = false;
            try
            {
                owner.Check();
                if (kind is StartupTransactionNamespaceCase.AncestorRenameAll or StartupTransactionNamespaceCase.SourceReplacementAll)
                { Enlist(root!, 1); Enlist(ancestor!, 1); }
                var transactedSource = Enlist(source!, 3);
                Native(WriteDword(transactedSource, 22), "write-transacted-inert-value");
                if (ReadDword(transactedSource) != 22) throw new Stop("transacted-inert-value-mismatch", failed: true);
                owner.Check();
                // Intentional ordinary operations only. Never query an ordinary value, owner or
                // topology until terminal outcome AND native closures have been established.
                stage = "ordinary-owned-rename";
                int code = RegRenameKey(kind is StartupTransactionNamespaceCase.AncestorRenameSourceOnly or StartupTransactionNamespaceCase.AncestorRenameAll ? ancestor! : source!, null, "Away");
                renameError = code;
                if (code == 0)
                {
                    if (kind is StartupTransactionNamespaceCase.AncestorRenameSourceOnly or StartupTransactionNamespaceCase.AncestorRenameAll) ancestorName = "Away";
                    else sourceName = "Away";
                    interference = true;
                    if (kind == StartupTransactionNamespaceCase.SourceReplacementAll)
                    {
                        stage = "ordinary-create-same-name-replacement";
                        code = RegCreateKeyExW(ancestor!, "Source", 0, IntPtr.Zero, 0, OwnedAccess, IntPtr.Zero, out var created, out uint disposition);
                        replacement = created; createError = code; interference = false;
                        if (code == 0 && disposition == 1)
                        {
                            replacementCreated = true;
                            byte[] data = Encoding.Unicode.GetBytes(owner.Owner + "\0");
                            try { code = RegSetValueExW(replacement, OwnerName, 0, 1, data, (uint)data.Length); }
                            finally { Array.Clear(data); }
                            writeError = code;
                            if (code == 0)
                            {
                                replacementMarked = true; code = WriteDword(replacement, 11); writeError = code; interference = code == 0;
                            }
                        }
                        else if (code == 0) safeOrdinary = false; // Never claim ownership of an existing replacement.
                    }
                }
                stage = "first-outcome-before-any-explicit-rollback";
                if (GetTransactionInformation(transaction, out uint outcome, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero))
                {
                    firstOutcome = outcome;
                    beforeTimeout = Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(timeout);
                    terminal = outcome is 2 or 3;
                }
                else nativeError = Marshal.GetLastWin32Error();
            }
            finally
            {
                bool canOwnReplacement = replacement == null || replacement.IsInvalid || replacementCreated;
                if (!terminal)
                {
                    _ = RollbackTransaction(transaction);
                    if (GetTransactionInformation(transaction, out uint outcome, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero)) terminal = outcome is 2 or 3;
                }
                if (terminal)
                {
                    bool closed = true;
                    foreach (var item in enlisted) if (item.CloseChecked() != 0) closed = false;
                    if (transaction.CloseChecked() != 0) closed = false;
                    safeOrdinary = closed && canOwnReplacement;
                }
                // Unknown transactions/failed closes remain rooted by the parent Worker. No
                // CloseHandle/finalizer fallback is represented as rollback or cleanup proof.
            }
            if (!terminal) throw new Stop("transaction-terminal-state-unproven");
        }
        private Key Enlist(Key ordinary, uint access)
        {
            int code = RegOpenKeyTransactedW(ordinary, null, 0, access, out var opened, transaction!, IntPtr.Zero);
            enlisted.Add(opened); Native(code, "enlist-retained-ordinary-handle"); return opened;
        }
        private void VerifyTree()
        {
            if (!ReadOwner(root!) || !ReadOwner(ancestor!) || !ReadOwner(source!)) throw new Stop("case-owner-marker-mismatch");
            VerifyNames(root!, [ancestorName], 1);
            VerifyNames(ancestor!, replacementCreated ? [sourceName, "Source"] : [sourceName], 1);
            VerifyNames(source!, [], 2);
            if (replacementCreated)
            {
                if (!replacementMarked || !ReadOwner(replacement!)) throw new Stop("replacement-owner-unproven");
                VerifyNames(replacement!, [], 2);
            }
        }
        private void Cleanup()
        {
            owner.ValidateOwner();
            if (!rootCreated)
            {
                if (rootCreationAttempted) throw new Stop("case-guid-creation-or-exclusive-ownership-unproven");
                if (root != null && root.CloseChecked() != 0 || prefix != null && prefix.CloseChecked() != 0) throw new Stop("unused-case-handle-close-unproven");
                TeardownCompleted = true; return;
            }
            // Initialization failures retain partial marked evidence rather than broad deletion.
            if (!rootMarked || !ancestorCreated || !ancestorMarked || !sourceCreated || !sourceMarked || root == null || ancestor == null || source == null)
                throw new Stop("case-initialization-incomplete-retained");
            VerifyTree();
            _ = ReadDword(source);
            if (replacementCreated)
            {
                _ = ReadDword(replacement!);
                Native(replacement!.CloseChecked(), "close-owned-replacement"); replacement = null;
                Native(RegDeleteKeyExW(ancestor, "Source", 0, 0), "delete-owned-replacement");
            }
            owner.ValidateOwner();
            Native(source.CloseChecked(), "close-original-source"); source = null;
            Native(RegDeleteKeyExW(ancestor, sourceName, 0, 0), "delete-owned-original-source");
            if (!ReadOwner(ancestor)) throw new Stop("ancestor-owner-changed-before-delete");
            VerifyNames(ancestor, [], 1);
            Native(ancestor.CloseChecked(), "close-owned-ancestor"); ancestor = null;
            Native(RegDeleteKeyExW(root, ancestorName, 0, 0), "delete-owned-ancestor");
            if (!ReadOwner(root)) throw new Stop("root-owner-changed-before-delete");
            VerifyNames(root, [], 1); owner.ValidateOwner();
            Native(root.CloseChecked(), "close-owned-root"); root = null;
            Native(RegDeleteKeyExW(prefix!, guid, 0, 0), "delete-fresh-case-guid");
            int code = RegOpenKeyExW(prefix!, guid, OpenLink, 1, out var remaining);
            int close = remaining.CloseChecked();
            if (code != 2 || close != 0) throw new Stop("case-guid-removal-unproven", code);
            Native(prefix!.CloseChecked(), "close-owned-prefix"); prefix = null;
            TeardownCompleted = true;
        }
        private static void VerifyNames(Key key, IReadOnlyList<string> expected, uint valueCount)
        {
            Native(RegQueryInfoKeyW(key, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out uint subkeys, IntPtr.Zero, IntPtr.Zero,
                out uint values, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "owned-topology-count");
            if (subkeys != expected.Count || values != valueCount) throw new Stop("owned-topology-count-mismatch");
            List<string> observed = [];
            for (uint index = 0; index < subkeys; index++)
            {
                char[] buffer = new char[32]; uint length = (uint)buffer.Length;
                try { Native(RegEnumKeyExW(key, index, buffer, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "owned-child-name"); observed.Add(new(buffer, 0, (int)length)); }
                finally { Array.Clear(buffer); }
            }
            if (!observed.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))) throw new Stop("owned-child-name-mismatch");
        }
        private void WriteOwner(Key key)
        {
            byte[] data = Encoding.Unicode.GetBytes(owner.Owner + "\0");
            try { Native(RegSetValueExW(key, OwnerName, 0, 1, data, (uint)data.Length), "write-fixed-owner-marker"); }
            finally { Array.Clear(data); }
        }
        private bool ReadOwner(Key key)
        {
            byte[] data = new byte[66]; uint bytes = (uint)data.Length;
            try { Native(RegQueryValueExW(key, OwnerName, IntPtr.Zero, out uint type, data, ref bytes), "read-fixed-owner-marker"); return type == 1 && bytes == 66 && Encoding.Unicode.GetString(data) == owner.Owner + "\0"; }
            finally { Array.Clear(data); }
        }
        private static int WriteDword(Key key, uint value)
        {
            byte[] data = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            try { return RegSetValueExW(key, ValueName, 0, 4, data, 4); }
            finally { Array.Clear(data); }
        }
        private static uint ReadDword(Key key)
        {
            byte[] data = new byte[4]; uint bytes = 4;
            try { Native(RegQueryValueExW(key, ValueName, IntPtr.Zero, out uint type, data, ref bytes), "read-inert-dword"); if (type != 4 || bytes != 4) throw new Stop("inert-value-shape-mismatch", failed: true); return BinaryPrimitives.ReadUInt32LittleEndian(data); }
            finally { Array.Clear(data); }
        }
    }
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
    private static void Native(int code, string stage) { if (code != 0) throw new Stop(stage, code); }
    private sealed class Key : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Key() : base(true) { }
        internal int CloseChecked() { if (IsClosed || IsInvalid) return 0; int code = RegCloseKey(handle); if (code == 0) { SetHandleAsInvalid(); Dispose(); } return code; }
        protected override bool ReleaseHandle() => RegCloseKey(handle) == 0;
    }
    private sealed class Transaction : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Transaction() : base(true) { }
        internal int CloseChecked() { if (IsClosed || IsInvalid) return 0; bool ok = CloseHandle(handle); int code = ok ? 0 : Marshal.GetLastWin32Error(); if (ok) { SetHandleAsInvalid(); Dispose(); } return code; }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern Transaction CreateTransaction(IntPtr security, IntPtr uow, uint options, uint isolationLevel, uint isolationFlags, uint timeout, string description);
    [DllImport("ktmw32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RollbackTransaction(Transaction transaction);
    [DllImport("ktmw32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTransactionInformation(Transaction transaction, out uint outcome, IntPtr isolationLevel, IntPtr isolationFlags, IntPtr timeout, uint length, IntPtr description);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(IntPtr root, string child, uint options, uint access, out Key result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(Key root, string child, uint options, uint access, out Key result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegCreateKeyExW(Key root, string child, uint reserved, IntPtr keyClass, uint options, uint access, IntPtr security, out Key result, out uint disposition);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyTransactedW(Key root, string? child, uint options, uint access, out Key result, Transaction transaction, IntPtr extended);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegRenameKey(Key root, string? child, string newName);
    [DllImport("advapi32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegCloseKey(IntPtr key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegSetValueExW(Key key, string name, uint reserved, uint type, byte[] data, uint bytes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueExW(Key key, string name, IntPtr reserved, out uint type, [Out] byte[] data, ref uint bytes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryInfoKeyW(Key key, IntPtr keyClass, IntPtr classLength, IntPtr reserved, out uint subkeys, IntPtr maxSubkeyLength, IntPtr maxClassLength, out uint values, IntPtr maxValueNameLength, IntPtr maxValueLength, IntPtr securityLength, IntPtr lastWrite);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegEnumKeyExW(Key key, uint index, [Out] char[] name, ref uint length, IntPtr reserved, IntPtr keyClass, IntPtr classLength, IntPtr lastWrite);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegDeleteKeyExW(Key key, string child, uint view, uint reserved);
}
