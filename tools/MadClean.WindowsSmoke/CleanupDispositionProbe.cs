using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal enum OwnedCleanupDispositionState { Observed, Inconclusive, Failed, Pending, CancelledBeforeStart }
internal enum OwnedCleanupDispositionCaseKind { Normal, Readonly, Recent, Reparse, MultipleLinks, ChangedIdentity, ConflictingWriter }
// TargetCloseConfirmed concerns an actual DELETE-capable target handle only; null means none opened.
// Namespace observations are populated only by GetFileAttributesW, independently of open errors.
internal sealed record OwnedCleanupDispositionCase(OwnedCleanupDispositionCaseKind Kind, bool DispositionAttempted,
    bool? DispositionAccepted, int? DispositionNativeError, bool? DeletePendingObserved,
    bool? TargetCloseConfirmed, bool? NameAbsentObserved, int? NameProbeNativeError, bool PredicateRefused,
    int? TargetOpenNativeError = null);
internal sealed record OwnedCleanupDispositionReport(OwnedCleanupDispositionState State,
    IReadOnlyList<OwnedCleanupDispositionCase> Cases, bool SiblingUntouched, bool RootIdentitiesUnchanged,
    bool CleanupConfirmed, string Stage, int? NativeError)
{
    public bool ExecutionSupported => false;
}

// Development-only compatibility observation. There is no production scope, user-path API,
// deletion fallback, action capability or claim that sharing excludes every active/mapped use.
internal static class OwnedCleanupDispositionProbe
{
    private static int active, retention;
    private static readonly List<object> RetainedOwnership = [];
    internal static bool HasPendingNativeProbe => Volatile.Read(ref active) != 0;
    internal static bool RequiresRetention => Volatile.Read(ref retention) != 0;
    private const uint ReadAttributes = 0x80, Traverse = 0x20, Delete = 0x10000;
    private const uint NoFollow = 0x00200000, NoRecall = 0x00100000, Backup = 0x02000000;
    private const uint Unsafe = 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;
    private static readonly byte[] InertBytes = Encoding.ASCII.GetBytes("Owned disposition compatibility bytes; never real Temp.");

    internal static async Task<OwnedCleanupDispositionReport> RunAsync(string outerGUIDroot, string marker, CancellationToken token)
    {
        static OwnedCleanupDispositionReport Empty(OwnedCleanupDispositionState state, string stage) => new(state, [], false, false, false, stage, null);
        if (token.IsCancellationRequested) return Empty(OwnedCleanupDispositionState.CancelledBeforeStart, "cancelled-before-start");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || RequiresRetention)
            return Empty(OwnedCleanupDispositionState.Inconclusive, "platform-or-prior-ownership-unavailable");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return Empty(OwnedCleanupDispositionState.Pending, "prior-worker-pending");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<OwnedCleanupDispositionReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            OwnedCleanupDispositionReport result;
            try { result = RunOwned(outerGUIDroot, marker, stop.Token); }
            catch (Exception) { Volatile.Write(ref retention, 1); result = Empty(OwnedCleanupDispositionState.Inconclusive, "worker-unexpected-failure"); }
            finally { stop.Dispose(); Volatile.Write(ref active, 0); }
            completion.TrySetResult(result);
        }, CancellationToken.None);
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return result with { State = OwnedCleanupDispositionState.Inconclusive, Stage = "stop-at-publication" };
            return result;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            Volatile.Write(ref retention, 1);
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return Empty(OwnedCleanupDispositionState.Pending, "caller-stopped-worker-still-owns-resources");
        }
    }

    private static OwnedCleanupDispositionReport RunOwned(string outer, string marker, CancellationToken token)
    {
        var watch = Stopwatch.StartNew(); var owned = new List<ProbeFile>(96); var guards = new List<Guard>(32);
        var cases = new List<OwnedCleanupDispositionCase>(7); var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string child = "", link = ""; bool created = false, linkCreated = false, sibling = false, roots = false, topology = false, cleanup = false;
        string stage = "validate-owned-scope"; int? nativeError = null; bool invariantFailure = false;
        bool readonlySet = false; string readonlyPath = "";
        void Check() { token.ThrowIfCancellationRequested(); if (watch.Elapsed >= TimeSpan.FromSeconds(35)) throw new ProbeUnavailable("cooperative-budget", null); }
        ProbeFile Open(string path, uint access, uint share = 3, uint flags = 0, uint creation = 3)
        {
            Check(); var file = CreateFileW(path, access, share, 0, creation, NoFollow | NoRecall | flags, 0);
            int code = file.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            owned.Add(file); if (file.IsInvalid) throw new ProbeUnavailable("owned-open-unavailable", code);
            return file;
        }
        void Close(ProbeFile file) { if (!file.CloseChecked(out int code)) throw new ProbeUnavailable("native-close-unconfirmed", code); }
        Stamp Observe(ProbeFile file, string path, bool directory)
        {
            Check();
            if (!GetIdentity(file, 18, out var id, 24) || !GetBasic(file, 0, out var basic, 40) || !GetStandard(file, 1, out var standard, 24))
                throw new ProbeUnavailable("metadata-query-unavailable", Marshal.GetLastWin32Error());
            var text = new StringBuilder(1024); uint length = GetFinalPathNameByHandleW(file, text, (uint)text.Capacity, 0);
            if (length == 0) throw new ProbeUnavailable("final-path-unavailable", Marshal.GetLastWin32Error());
            string actual = text.ToString();
            if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (length >= text.Capacity || !string.Equals(actual, path, StringComparison.OrdinalIgnoreCase) || id.Volume == 0 || id.Id == Guid.Empty ||
                (basic.Attributes & Unsafe) != 0 || ((basic.Attributes & 0x10) != 0) != directory || standard.Directory != directory ||
                standard.DeletePending || standard.Bytes < 0 || !directory && standard.Links == 0)
                throw new ProbeUnavailable("unsafe-object-or-path", null);
            return new(id.Volume, id.Id, standard.Bytes, standard.Links, basic.Created, basic.Written, basic.Changed, basic.Attributes);
        }
        void GuardChain(string path)
        {
            var chain = new Stack<string>();
            for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            { if (chain.Count >= 64) throw new ProbeUnavailable("ancestor-bound", null); chain.Push(current); }
            while (chain.Count > 0)
            {
                string ancestor = chain.Pop(); if (guards.Any(g => string.Equals(g.Path, ancestor, StringComparison.OrdinalIgnoreCase))) continue;
                var handle = Open(ancestor, ReadAttributes | Traverse, 3, Backup);
                guards.Add(new(ancestor, handle, Observe(handle, ancestor, true)));
            }
        }
        void RecheckRoots()
        {
            foreach (var guard in guards)
            { var current = Observe(guard.Handle, guard.Path, true); if (current.Volume != guard.Stamp.Volume || current.Id != guard.Stamp.Id) throw new ProbeInvariant("ancestor-identity-changed"); }
        }
        byte[] ReadOwned(string path)
        {
            var handle = Open(path, 0x80000000 | ReadAttributes, 3);
            byte[]? audit = null;
            try
            {
                var stamp = Observe(handle, path, false);
                if (stamp.Bytes is < 1 or > 128) throw new ProbeUnavailable("owned-audit-bound", null);
                byte[] buffer = new byte[(int)stamp.Bytes]; audit = buffer;
                try
                {
                    bool read = ReadFile(handle, buffer, (uint)buffer.Length, out uint count, 0);
                    if (!read) throw new ProbeUnavailable("owned-audit-read-unavailable", Marshal.GetLastWin32Error());
                    if (count != buffer.Length) throw new ProbeUnavailable("owned-audit-read-count-unexpected", null);
                    return buffer;
                }
                catch { Array.Clear(buffer); throw; }
            }
            finally
            {
                try { Close(handle); }
                catch { if (audit != null) Array.Clear(audit); throw; }
            }
        }
        void AssertBytes(string path, byte[] bytes)
        { byte[] actual = ReadOwned(path); try { if (!actual.AsSpan().SequenceEqual(bytes)) throw new ProbeInvariant("owned-control-bytes-changed"); } finally { Array.Clear(actual); } }
        void CreateOwned(string path, bool old = true)
        {
            var file = Open(path, 0x40000000 | ReadAttributes | 0x100, 0, 0, 1);
            try
            {
                bool wrote = WriteFile(file, InertBytes, (uint)InertBytes.Length, out uint written, 0);
                if (!wrote) throw new ProbeUnavailable("owned-create-write-unavailable", Marshal.GetLastWin32Error());
                if (written != InertBytes.Length) throw new ProbeUnavailable("owned-create-write-count-unexpected", null);
                if (old)
                {
                    long time = DateTime.UtcNow.AddDays(-10).ToFileTimeUtc();
                    if (!SetFileTime(file, ref time, 0, ref time)) throw new ProbeUnavailable("owned-old-stamp-unavailable", Marshal.GetLastWin32Error());
                }
                if (!FlushFileBuffers(file)) throw new ProbeUnavailable("owned-flush-unavailable", Marshal.GetLastWin32Error());
            }
            finally { Close(file); }
            expected.Add(path, (byte[])InertBytes.Clone());
        }
        Stamp ReadStamp(string path)
        { var file = Open(path, ReadAttributes); try { return Observe(file, path, false); } finally { Close(file); } }
        bool RefusedByPolicy(Stamp stamp) => stamp.Links != 1 || (stamp.Attributes & (1u | 2u | 4u | Unsafe)) != 0 ||
            stamp.Written <= 0 || DateTime.UtcNow - DateTime.FromFileTimeUtc(stamp.Written) < TimeSpan.FromDays(7);
        void Refusal(OwnedCleanupDispositionCaseKind kind, string path)
        {
            bool refused = RefusedByPolicy(ReadStamp(path));
            if (!refused) throw new ProbeUnavailable("owned-refusal-predicate-not-established", null);
            AssertBytes(path, expected[path]);
            cases.Add(new(kind, false, null, null, null, null, null, null, true));
        }
        try
        {
            Check(); ValidateOuter(outer, marker);
            GuardChain(outer);
            string markerPath = Path.Combine(outer, ".madclean-fixture-owner"); byte[] markerBytes = ReadOwned(markerPath);
            try { if (Encoding.ASCII.GetString(markerBytes) != marker) throw new ProbeUnavailable("owner-marker-mismatch", null); }
            finally { Array.Clear(markerBytes); }
            child = Path.Combine(outer, "cleanup-disposition-probe");
            if (child.Length > 220) throw new ProbeUnavailable("fresh-child-path-bound", null);
            if (!CreateDirectoryW(child, 0))
            {
                int creationCode = Marshal.GetLastWin32Error(); Volatile.Write(ref retention, 1);
                throw new ProbeUnavailable("fresh-child-unavailable", creationCode);
            }
            created = true; GuardChain(child);
            string childMarker = Path.Combine(child, ".probe-owner");
            var markerHandle = Open(childMarker, 0x40000000, 0, 0, 1); byte[] ownerBytes = Encoding.ASCII.GetBytes(marker);
            try
            {
                if (!WriteFile(markerHandle, ownerBytes, (uint)ownerBytes.Length, out var n, 0)) throw new ProbeUnavailable("child-marker-write-unavailable", Marshal.GetLastWin32Error());
                if (n != ownerBytes.Length) throw new ProbeUnavailable("child-marker-write-count-unexpected", null);
                if (!FlushFileBuffers(markerHandle)) throw new ProbeUnavailable("child-marker-flush-unavailable", Marshal.GetLastWin32Error());
            }
            finally { Array.Clear(ownerBytes); Close(markerHandle); }
            expected.Add(childMarker, Encoding.ASCII.GetBytes(marker));
            string normal = Path.Combine(child, "normal.tmp"), control = Path.Combine(child, "outside-control.tmp");
            CreateOwned(normal); CreateOwned(control); var controlStamp = ReadStamp(control);
            stage = "normal-classic-disposition"; var historical = ReadStamp(normal);
            var target = Open(normal, ReadAttributes | Delete, 0);
            bool accepted; int? dispositionError = null; bool? pending = null; bool closed;
            try
            {
                var current = Observe(target, normal, false); RecheckRoots(); Check();
                if (current != historical || RefusedByPolicy(current)) throw new ProbeUnavailable("normal-precommit-evidence-changed", null);
                if (Marshal.SizeOf<Disposition>() != 1) throw new ProbeInvariant("disposition-abi-not-one-byte");
                var value = new Disposition { DeleteFile = 1 };
                accepted = SetFileInformationByHandle(target, 4, ref value, 1);
                if (!accepted) dispositionError = Marshal.GetLastWin32Error();
                if (GetStandard(target, 1, out var standard, 24)) pending = standard.DeletePending;
            }
            finally { closed = target.CloseChecked(out int code); if (!closed) throw new ProbeUnavailable("target-close-unconfirmed", code); }
            uint attributes = GetFileAttributesW(normal); int? nameCode = attributes == uint.MaxValue ? Marshal.GetLastWin32Error() : null;
            bool? absent = attributes != uint.MaxValue ? false : nameCode == 2 ? true : null;
            cases.Add(new(OwnedCleanupDispositionCaseKind.Normal, true, accepted, dispositionError, pending, closed, absent, nameCode, false));
            if (accepted && absent == true) { Array.Clear(expected[normal]); expected.Remove(normal); }
            else if (!accepted) AssertBytes(normal, expected[normal]);
            else throw new ProbeUnavailable("accepted-disposition-name-absence-unconfirmed", nameCode);
            AssertBytes(control, expected[control]);

            stage = "readonly-refusal"; readonlyPath = Path.Combine(child, "readonly.tmp"); CreateOwned(readonlyPath);
            uint readonlyOriginal = ReadStamp(readonlyPath).Attributes;
            if (!SetFileAttributesW(readonlyPath, readonlyOriginal | 1u)) throw new ProbeUnavailable("readonly-control-setup-unavailable", Marshal.GetLastWin32Error());
            readonlySet = true; Refusal(OwnedCleanupDispositionCaseKind.Readonly, readonlyPath);
            if (!SetFileAttributesW(readonlyPath, readonlyOriginal)) throw new ProbeUnavailable("readonly-control-restoration-unavailable", Marshal.GetLastWin32Error());
            if (ReadStamp(readonlyPath).Attributes != readonlyOriginal) throw new ProbeUnavailable("readonly-control-restoration-unconfirmed", null);
            readonlySet = false;
            stage = "recent-refusal"; string recent = Path.Combine(child, "recent.tmp"); CreateOwned(recent, false); Refusal(OwnedCleanupDispositionCaseKind.Recent, recent);
            stage = "multiple-links-refusal"; string multi = Path.Combine(child, "hardlink.tmp"), alias = Path.Combine(child, "hardlink-alias.tmp"); CreateOwned(multi);
            if (!CreateHardLinkW(alias, multi, 0)) throw new ProbeUnavailable("hardlink-control-unavailable", Marshal.GetLastWin32Error());
            expected.Add(alias, (byte[])InertBytes.Clone()); Refusal(OwnedCleanupDispositionCaseKind.MultipleLinks, multi);
            stage = "changed-identity-refusal"; string changed = Path.Combine(child, "changed.tmp"), moved = Path.Combine(child, "changed-original.tmp"); CreateOwned(changed);
            var oldIdentity = ReadStamp(changed);
            if (!MoveFileExW(changed, moved, 0)) throw new ProbeUnavailable("owned-replacement-rename-unavailable", Marshal.GetLastWin32Error());
            expected.Add(moved, expected[changed]); expected.Remove(changed); CreateOwned(changed);
            var replacement = Open(changed, ReadAttributes | Delete, 0);
            bool identityChanged;
            try { var observed = Observe(replacement, changed, false); identityChanged = observed.Volume != oldIdentity.Volume || observed.Id != oldIdentity.Id; }
            finally { Close(replacement); }
            if (!identityChanged) throw new ProbeUnavailable("changed-object-control-not-established", null);
            AssertBytes(changed, expected[changed]); AssertBytes(moved, expected[moved]);
            cases.Add(new(OwnedCleanupDispositionCaseKind.ChangedIdentity, false, null, null, null, true, null, null, true));
            stage = "writer-sharing-refusal"; string writerPath = Path.Combine(child, "writer.tmp"); CreateOwned(writerPath);
            var writer = Open(writerPath, 0x40000000, 0); int writerError; bool writerRefused;
            try
            {
                var denied = CreateFileW(writerPath, ReadAttributes | Delete, 0, 0, 3, NoFollow | NoRecall, 0); owned.Add(denied);
                writerError = denied.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                writerRefused = denied.IsInvalid && writerError is 32 or 33;
                Close(denied);
            }
            finally { Close(writer); }
            if (!writerRefused) throw new ProbeUnavailable("writer-sharing-refusal-unconfirmed", writerError);
            AssertBytes(writerPath, expected[writerPath]);
            cases.Add(new(OwnedCleanupDispositionCaseKind.ConflictingWriter, false, null, null, null, null, null, null, true, writerError));

            stage = "reparse-ancestor-refusal"; link = Path.Combine(child, "redirect-source");
            if (CreateSymbolicLinkW(link, child, 3))
            {
                linkCreated = true;
                var redirected = Open(link, ReadAttributes | Traverse, 3, Backup);
                bool reparse, rejected = false;
                try
                {
                    if (!GetBasic(redirected, 0, out var b, 40)) throw new ProbeUnavailable("reparse-control-query-unavailable", Marshal.GetLastWin32Error());
                    reparse = (b.Attributes & 0x400) != 0;
                    try { _ = Observe(redirected, link, true); }
                    catch (ProbeUnavailable failure) when (failure.Stage == "unsafe-object-or-path") { rejected = true; }
                }
                finally { Close(redirected); }
                if (!reparse || !rejected) throw new ProbeUnavailable("reparse-control-not-established", null);
                // The same root-to-leaf predicate stops at this link before any target lookup.
                cases.Add(new(OwnedCleanupDispositionCaseKind.Reparse, false, null, null, null, null, null, null, true));
                uint linkAttributes = GetFileAttributesW(link);
                if (!string.Equals(new DirectoryInfo(link).LinkTarget, child, StringComparison.OrdinalIgnoreCase) || linkAttributes == uint.MaxValue || (linkAttributes & 0x400) == 0)
                    throw new ProbeUnavailable("owned-link-teardown-identity-unconfirmed", null);
                Directory.Delete(link, false); linkCreated = false;
            }
            else { nativeError = Marshal.GetLastWin32Error(); cases.Add(new(OwnedCleanupDispositionCaseKind.Reparse, false, null, null, null, null, null, null, false)); }
            Check(); RecheckRoots(); roots = true;
            if (ReadStamp(control) != controlStamp) throw new ProbeInvariant("outside-control-stamp-changed");
            foreach (var pair in expected) AssertBytes(pair.Key, pair.Value);
            sibling = true;
            var allowed = expected.Keys.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int entries = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(child))
                if (++entries > 16 || !allowed.Contains(Path.GetFileName(path)) || (GetFileAttributesW(path) & (0x10u | Unsafe)) != 0)
                    throw new ProbeUnavailable("owned-topology-audit-unconfirmed", null);
            if (entries != expected.Count) throw new ProbeUnavailable("owned-topology-count-unconfirmed", null);
            topology = true;
            stage = "owned-classic-observations-complete";
        }
        catch (ProbeInvariant failure) { invariantFailure = true; stage = failure.Stage; }
        catch (ProbeUnavailable failure) { stage = failure.Stage; nativeError = failure.NativeCode; }
        catch (OperationCanceledException) { stage = "worker-stop-after-start"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        { stage = "owned-prerequisite-or-observation-unavailable"; }
        finally
        {
            bool closes = true;
            for (int i = owned.Count - 1; i >= 0; i--)
                if (!owned[i].CloseChecked(out _)) closes = false;
            cleanup = closes && !RequiresRetention && !linkCreated && !readonlySet && (!created || roots && sibling && topology);
            if (!cleanup)
            {
                Volatile.Write(ref retention, 1);
                lock (RetainedOwnership) { RetainedOwnership.Add(owned); RetainedOwnership.Add(guards); }
            }
            foreach (var bytes in expected.Values) Array.Clear(bytes);
        }
        var normalCase = cases.FirstOrDefault(c => c.Kind == OwnedCleanupDispositionCaseKind.Normal);
        bool allObserved = cases.Count == 7 && normalCase?.DispositionAccepted == true && normalCase.NameAbsentObserved == true &&
            cases.Where(c => c.Kind != OwnedCleanupDispositionCaseKind.Normal).All(c => c.PredicateRefused && !c.DispositionAttempted);
        return new(invariantFailure ? OwnedCleanupDispositionState.Failed : allObserved && cleanup ? OwnedCleanupDispositionState.Observed : OwnedCleanupDispositionState.Inconclusive,
            Array.AsReadOnly(cases.ToArray()), sibling, roots, cleanup, stage, nativeError);
    }

    private static void ValidateOuter(string root, string marker)
    {
        string repository = Path.GetFullPath(Environment.CurrentDirectory), prefix = "windows-validation-fixtures-";
        if (root == null || root.Length is < 4 or > 200 || root != Path.GetFullPath(root) || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\' ||
            root.Contains('/') || root.IndexOf(':', 2) >= 0 || root.Any(char.IsControl) || new DriveInfo(root[..3]).DriveType != DriveType.Fixed ||
            !Guid.TryParseExact(marker, "N", out var owner) || owner == Guid.Empty || marker != owner.ToString("N")) throw new ProbeUnavailable("owned-root-syntax-unavailable", null);
        string name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), Path.Combine(repository, "artifacts"), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty || name[prefix.Length..] != id.ToString("N"))
            throw new ProbeUnavailable("owned-root-topology-unavailable", null);
    }
    private sealed record Stamp(ulong Volume, Guid Id, long Bytes, uint Links, long Created, long Written, long Changed, uint Attributes);
    private sealed record Guard(string Path, ProbeFile Handle, Stamp Stamp);
    private sealed class ProbeUnavailable(string stage, int? code) : Exception("Owned probe observation unavailable.") { internal string Stage => stage; internal int? NativeCode => code; }
    private sealed class ProbeInvariant(string stage) : Exception("Owned probe invariant failed.") { internal string Stage => stage; }
    private sealed class ProbeFile : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ProbeFile() : base(true) { }
        internal bool CloseChecked(out int error)
        {
            error = 0; if (IsInvalid || IsClosed) return true;
            bool success = CloseHandle(handle); error = success ? 0 : Marshal.GetLastWin32Error();
            // Never retry a potentially reused raw value. Preserve the wrapper/uncertainty,
            // suppress finalizer retry, and prevent further I/O after an unconfirmed close.
            SetHandleAsInvalid();
            if (!success) { Volatile.Write(ref retention, 1); lock (RetainedOwnership) RetainedOwnership.Add(this); }
            Dispose(); return success;
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Identity { internal ulong Volume; internal Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct Basic { internal long Created, Accessed, Written, Changed; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Standard { internal long Allocation, Bytes; internal uint Links; [MarshalAs(UnmanagedType.U1)] internal bool DeletePending; [MarshalAs(UnmanagedType.U1)] internal bool Directory; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Disposition { internal byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ProbeFile CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileAttributesW(string path, uint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileExW(string source, string destination, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string path, string existing, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CreateSymbolicLinkW(string path, string target, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdentity(ProbeFile handle, int kind, out Identity identity, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBasic(ProbeFile handle, int kind, out Basic basic, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(ProbeFile handle, int kind, out Standard standard, uint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(ProbeFile handle, int kind, ref Disposition value, uint bytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFinalPathNameByHandleW(ProbeFile handle, StringBuilder buffer, uint length, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteFile(ProbeFile handle, byte[] buffer, uint count, out uint written, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(ProbeFile handle, [Out] byte[] buffer, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileTime(ProbeFile handle, ref long creation, nint access, ref long write);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushFileBuffers(ProbeFile handle);
}
