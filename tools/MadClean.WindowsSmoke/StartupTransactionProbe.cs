using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal enum StartupTransactionProbeState { Passed, Inconclusive, Failed, Pending }
internal sealed record StartupTransactionProbeResult(StartupTransactionProbeState State, bool RollbackObserved,
    bool CommitObserved, bool TeardownCompleted, string Stage, int? NativeError);

// Development-only proof of a narrow public API route. No production startup method calls this class.
internal static class StartupTransactionProbe
{
    private static int active;
    private static Task<StartupTransactionProbeResult>? pending;
    internal static bool HasPendingNativeProbe => Volatile.Read(ref active) != 0;
    internal static Task<StartupTransactionProbeResult>? PendingCompletion => Volatile.Read(ref pending);

    internal static async Task<StartupTransactionProbeResult> ProbeAsync(string fixtureDir, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(StartupTransactionProbeState.Inconclusive, false, false, false, "cancelled-before-start", null);
        if (!OperatingSystem.IsWindows()) return new(StartupTransactionProbeState.Inconclusive, false, false, false, "windows-required", null);
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return new(StartupTransactionProbeState.Pending, false, false, false, "previous-native-worker-pending", null);
        var worker = Task.Run(() =>
        {
            try { return new Worker(fixtureDir, token).Run(); }
            finally { Volatile.Write(ref active, 0); }
        }, CancellationToken.None);
        Volatile.Write(ref pending, worker);
        _ = worker.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var result = await worker.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (TimeoutException)
        { return new(StartupTransactionProbeState.Pending, false, false, false, "caller-wait-expired-worker-owns-resources", null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(StartupTransactionProbeState.Pending, false, false, false, "caller-cancelled-worker-owns-resources", null); }
    }

    private sealed class Stop(string stage, int? nativeError = null, bool failed = false) : Exception("Owned transaction probe did not complete.")
    {
        internal string Stage { get; } = stage;
        internal int? NativeError { get; } = nativeError;
        internal bool Failed { get; } = failed;
    }
    private sealed class Worker(string fixtureDir, CancellationToken token)
    {
        private const string Prefix = @"Software\MadClean.WindowsSmoke";
        private const string Child = "TxrHandleProbe", OwnerName = "MadCleanFixtureOwner", ValueName = "InertProbeDword";
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private ProbeKey? prefix, root, anchor;
        private string guid = "", owner = "";
        private bool created, marked, targetCreated, safeForCleanup = true;
        private bool rollbackObserved, commitObserved, teardown;
        private string stage = "initializing";
        private int? nativeError;
        private StartupTransactionProbeState state = StartupTransactionProbeState.Inconclusive;

        internal StartupTransactionProbeResult Run()
        {
            try
            {
                Check();
                ValidateFilesystemOwner();
                stage = "open-existing-fixture-prefix";
                int code = RegOpenKeyExW(new IntPtr(unchecked((int)0x80000001)), Prefix, 0, 0xF, out var openedPrefix);
                prefix = openedPrefix;
                Native(code, stage); Check();
                // Only the exact fresh GUID child may be created; no prefix/ancestor creation.
                stage = "create-new-owned-guid-root";
                code = RegCreateKeyExW(prefix, guid, 0, IntPtr.Zero, 0, 0xF, IntPtr.Zero, out var createdRoot, out uint disposition);
                root = createdRoot; Native(code, stage);
                if (disposition != 1) throw new Stop("owned-guid-root-already-existed");
                created = true;
                WriteOwner(root); marked = true; Check();
                stage = "create-inert-target";
                code = RegCreateKeyExW(root, Child, 0, IntPtr.Zero, 0, 3, IntPtr.Zero, out var createdTarget, out disposition);
                anchor = createdTarget; Native(code, stage);
                if (disposition != 1) throw new Stop("inert-target-already-existed");
                targetCreated = true; WriteOwner(anchor); WriteDword(anchor, 11); Check();
                Native(anchor.CloseChecked(), "close-created-target"); anchor = null;
                // The source for enlistment is deliberately an ordinary RegOpenKeyEx handle.
                Native(RegOpenKeyExW(root, Child, 0, 3, out var ordinary), "open-ordinary-anchor"); anchor = ordinary;
                VerifyTopology();
                if (ReadDword(anchor) != 11) throw new Stop("baseline-inert-value-mismatch", failed: true);
                Round(22, false); rollbackObserved = true;
                Round(33, true); commitObserved = true;
                state = StartupTransactionProbeState.Passed; stage = "rollback-and-commit-observed";
            }
            catch (Stop ex) { state = ex.Failed ? StartupTransactionProbeState.Failed : StartupTransactionProbeState.Inconclusive; stage = ex.Stage; nativeError = ex.NativeError; }
            catch (OperationCanceledException) { stage = "worker-observed-cancellation"; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { stage = "required-public-api-unavailable"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or InvalidOperationException)
            { stage = "owned-fixture-prerequisite-unavailable"; }
            finally
            {
                // No caller ever closes these resources. Unknown transaction outcomes deliberately
                // retain the owned subtree; closing an unknown committed transaction is not rollback proof.
                try
                {
                    if (created && marked && safeForCleanup) Cleanup();
                }
                catch (Stop ex) { teardown = false; stage = ex.Stage; nativeError = ex.NativeError; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or InvalidOperationException)
                { teardown = false; stage = "owned-teardown-not-proven"; }
                anchor?.Dispose(); root?.Dispose(); prefix?.Dispose();
            }
            if (!teardown && state == StartupTransactionProbeState.Passed) state = StartupTransactionProbeState.Inconclusive;
            return new(state, rollbackObserved, commitObserved, teardown, stage, nativeError);
        }

        private void ValidateFilesystemOwner()
        {
            string full = Path.GetFullPath(fixtureDir);
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
            const string start = "windows-validation-fixtures-";
            if (!name.StartsWith(start, StringComparison.Ordinal) || !Guid.TryParseExact(name[start.Length..], "N", out var parsed) || parsed == Guid.Empty)
                throw new Stop("invalid-filesystem-fixture-guid");
            // Validate ancestry before the fixed ownership marker is read; never follow a known link.
            var ancestors = new Stack<string>();
            for (string? path = full; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            { if (ancestors.Count >= 64) throw new Stop("fixture-depth-limit"); ancestors.Push(path); }
            while (ancestors.Count > 0)
            {
                Check();
                if ((File.GetAttributes(ancestors.Pop()) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw new Stop("unsafe-filesystem-fixture-ancestor");
            }
            string marker = Path.Combine(full, ".madclean-fixture-owner");
            var info = new FileInfo(marker);
            if (info.Length != 32 || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new Stop("invalid-filesystem-ownership-marker");
            owner = File.ReadAllText(marker);
            if (owner.Length != 32 || !owner.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new Stop("invalid-filesystem-ownership-marker");
            guid = parsed.ToString("N");
        }

        private void Round(uint proposed, bool commit)
        {
            Check();
            stage = commit ? "create-commit-transaction" : "create-rollback-transaction";
            using var transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 1, 0, 0, 5000, "MadClean development inert HKCU probe");
            int creationError = transaction.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            if (transaction.IsInvalid) throw new Stop(stage, creationError);
            safeForCleanup = false;
            ProbeKey? enlisted = null;
            bool terminal = false, terminalAttempted = false, outcomeAttempted = false;
            try
            {
                Check();
                stage = commit ? "enlist-ordinary-handle-for-commit" : "enlist-ordinary-handle-for-rollback";
                int code = RegOpenKeyTransactedW(anchor!, null, 0, 3, out var opened, transaction, IntPtr.Zero);
                enlisted = opened; Native(code, stage); Check();
                WriteDword(enlisted, proposed); Check();
                if (ReadDword(enlisted) != proposed) throw new Stop("transacted-inert-readback-mismatch", failed: true);
                Check();
                // Close the bound registry key before terminal APIs, using RegCloseKey, not CloseHandle.
                Native(enlisted.CloseChecked(), "close-transacted-key-before-terminal-call"); enlisted = null;
                terminalAttempted = true;
                stage = commit ? "commit-transaction" : "rollback-transaction";
                bool success = commit ? CommitTransaction(transaction) : RollbackTransaction(transaction);
                int error = success ? 0 : Marshal.GetLastWin32Error();
                // Query outcome before cancellation/budget checks or any ordinary registry operation.
                outcomeAttempted = true;
                uint outcome = Outcome(transaction);
                terminal = outcome is 2 or 3;
                safeForCleanup = terminal;
                if (!terminal) throw new Stop("transaction-outcome-undetermined");
                if (!success) throw new Stop(stage, error);
                if (outcome != (commit ? 2u : 3u)) throw new Stop("transaction-terminal-outcome-mismatch", failed: true);
            }
            finally
            {
                // Cleanup ignores caller cancellation: the worker must settle/observe its own native
                // transaction before allowing ordinary operations or registry fixture teardown.
                if (enlisted != null)
                {
                    int close = enlisted.CloseChecked();
                    enlisted.Dispose();
                    if (close != 0) { safeForCleanup = false; throw new Stop("transacted-key-close-uncertain", close); }
                }
                if (!terminal && !outcomeAttempted)
                {
                    if (!terminalAttempted)
                    {
                        bool rolledBack = RollbackTransaction(transaction);
                        int ignoredCode = rolledBack ? 0 : Marshal.GetLastWin32Error();
                        _ = ignoredCode; // Outcome, not an assumed rollback, controls teardown safety.
                    }
                    uint outcome = Outcome(transaction);
                    terminal = outcome is 2 or 3;
                    safeForCleanup = terminal;
                }
                if (!terminal) safeForCleanup = false;
            }
            // Outcome was explicitly verified. Close the transaction before ordinary anchor readback.
            int transactionClose = transaction.CloseChecked();
            if (transactionClose != 0)
            { safeForCleanup = false; throw new Stop("terminal-transaction-close-uncertain", transactionClose); }
            Check();
            stage = commit ? "ordinary-read-after-commit" : "ordinary-read-after-rollback";
            uint expected = commit ? proposed : 11u;
            if (ReadDword(anchor!) != expected) throw new Stop("ordinary-terminal-readback-mismatch", failed: true);
            Check();
        }
        private uint Outcome(ProbeTransaction transaction)
        {
            bool ok = GetTransactionInformation(transaction, out uint outcome, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok) { safeForCleanup = false; throw new Stop("transaction-outcome-query-failed", error); }
            return outcome;
        }

        private void VerifyTopology()
        {
            if (root == null || anchor == null || !ReadOwner(root) || !ReadOwner(anchor)) throw new Stop("owned-marker-mismatch");
            Counts(root, out uint subkeys, out uint values);
            if (subkeys != 1 || values != 1) throw new Stop("owned-root-topology-changed");
            char[] name = new char[32]; uint length = (uint)name.Length;
            try
            {
                Native(RegEnumKeyExW(root, 0, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "owned-child-name-query");
                if (length != Child.Length || new string(name, 0, (int)length) != Child) throw new Stop("owned-child-topology-changed");
            }
            finally { Array.Clear(name); }
            Counts(anchor, out subkeys, out values);
            if (subkeys != 0 || values != 2) throw new Stop("owned-target-topology-changed");
        }
        private void Cleanup()
        {
            // No recursive deletion, broad prefix cleanup or cleanup after uncertain commit/rollback.
            if (!targetCreated) throw new Stop("owned-target-initialization-incomplete");
            VerifyTopology();
            _ = ReadDword(anchor!); // Fixed kind/size, inert value only; no active transaction exists.
            Native(anchor!.CloseChecked(), "close-ordinary-target-before-teardown"); anchor = null;
            Native(RegDeleteKeyExW(root!, Child, 0, 0), "delete-owned-inert-child");
            if (!ReadOwner(root!)) throw new Stop("owned-root-marker-changed-before-teardown");
            Counts(root!, out uint subkeys, out uint values);
            if (subkeys != 0 || values != 1) throw new Stop("owned-root-topology-changed-before-teardown");
            Native(root!.CloseChecked(), "close-owned-root-before-teardown"); root = null;
            Native(RegDeleteKeyExW(prefix!, guid, 0, 0), "delete-owned-guid-root");
            int code = RegOpenKeyExW(prefix!, guid, 0, 1, out var remains);
            remains.Dispose();
            if (code != 2) throw new Stop("owned-root-removal-not-proven", code);
            teardown = true;
        }
        private void WriteOwner(ProbeKey key)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(owner + "\0");
            try { Native(RegSetValueExW(key, OwnerName, 0, 1, bytes, (uint)bytes.Length), "write-owned-marker"); }
            finally { Array.Clear(bytes); }
        }
        private bool ReadOwner(ProbeKey key)
        {
            byte[] data = new byte[66]; uint count = (uint)data.Length;
            try
            {
                Native(RegQueryValueExW(key, OwnerName, IntPtr.Zero, out uint kind, data, ref count), "read-owned-marker");
                return kind == 1 && count == data.Length && Encoding.Unicode.GetString(data) == owner + "\0";
            }
            finally { Array.Clear(data); }
        }
        private static void WriteDword(ProbeKey key, uint value)
        {
            byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            try { Native(RegSetValueExW(key, ValueName, 0, 4, bytes, 4), "write-inert-dword"); }
            finally { Array.Clear(bytes); }
        }
        private static uint ReadDword(ProbeKey key)
        {
            byte[] bytes = new byte[4]; uint count = 4;
            try
            {
                Native(RegQueryValueExW(key, ValueName, IntPtr.Zero, out uint kind, bytes, ref count), "read-inert-dword");
                if (kind != 4 || count != 4) throw new Stop("inert-dword-kind-size-mismatch", failed: true);
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            }
            finally { Array.Clear(bytes); }
        }
        private static void Counts(ProbeKey key, out uint subkeys, out uint values) => Native(RegQueryInfoKeyW(key,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out subkeys, IntPtr.Zero, IntPtr.Zero, out values,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "owned-topology-query");
        private void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= TimeSpan.FromSeconds(20)) throw new Stop("worker-cooperative-budget-expired");
        }
        private static void Native(int code, string stage) { if (code != 0) throw new Stop(stage, code); }
    }

    private sealed class ProbeKey : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ProbeKey() : base(true) { }
        internal int CloseChecked()
        {
            if (IsClosed || IsInvalid) return 0;
            int code = RegCloseKey(handle);
            if (code == 0) { SetHandleAsInvalid(); Dispose(); }
            return code;
        }
        protected override bool ReleaseHandle() => RegCloseKey(handle) == 0;
    }
    private sealed class ProbeTransaction : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ProbeTransaction() : base(true) { }
        internal int CloseChecked()
        {
            if (IsClosed || IsInvalid) return 0;
            bool ok = CloseHandle(handle); int code = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok) { SetHandleAsInvalid(); Dispose(); }
            return code;
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ProbeTransaction CreateTransaction(IntPtr security, IntPtr uow, uint options, uint isolationLevel, uint isolationFlags, uint timeout, string description);
    [DllImport("ktmw32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CommitTransaction(ProbeTransaction transaction);
    [DllImport("ktmw32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RollbackTransaction(ProbeTransaction transaction);
    [DllImport("ktmw32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTransactionInformation(ProbeTransaction transaction, out uint outcome, IntPtr isolationLevel, IntPtr isolationFlags, IntPtr timeout, uint bufferLength, IntPtr description);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(IntPtr root, string subkey, uint options, uint access, out ProbeKey result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(ProbeKey root, string subkey, uint options, uint access, out ProbeKey result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegCreateKeyExW(ProbeKey root, string subkey, uint reserved, IntPtr keyClass, uint options, uint access, IntPtr security, out ProbeKey result, out uint disposition);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyTransactedW(ProbeKey key, string? subkey, uint options, uint access, out ProbeKey result, ProbeTransaction transaction, IntPtr extended);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegCloseKey(IntPtr key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegSetValueExW(ProbeKey key, string name, uint reserved, uint kind, byte[] data, uint bytes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueExW(ProbeKey key, string name, IntPtr reserved, out uint kind, [Out] byte[] data, ref uint bytes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryInfoKeyW(ProbeKey key, IntPtr keyClass, IntPtr classLength, IntPtr reserved, out uint subkeys,
        IntPtr maxSubkeyLength, IntPtr maxClassLength, out uint values, IntPtr maxValueNameLength, IntPtr maxValueLength, IntPtr securityLength, IntPtr lastWrite);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegEnumKeyExW(ProbeKey key, uint index, [Out] char[] name, ref uint count, IntPtr reserved, IntPtr keyClass, IntPtr classCount, IntPtr lastWrite);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegDeleteKeyExW(ProbeKey key, string subkey, uint view, uint reserved);
}
