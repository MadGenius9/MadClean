using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal enum OwnedCleanupMappedDispositionState
{ Observed, CounterexampleObserved, ControlledOpenRefusalObserved, Inconclusive, Failed, Pending, CancelledBeforeStart }
internal sealed record OwnedCleanupMappedDispositionReport(OwnedCleanupMappedDispositionState State,
    bool? SourceCloseConfirmed, bool? ViewWriteRestoreBeforeObserved, bool? TargetOpenSucceeded, int? TargetOpenNativeError,
    bool? DispositionAccepted, int? DispositionNativeError, bool? DeletePendingObserved, bool? TargetCloseConfirmed,
    bool? NameAbsentWithViewObserved, int? NameWithViewNativeError, bool? ViewWriteRestoreAfterObserved,
    bool? ViewUnmapConfirmed, int? UnmapNativeError, bool? MappingCloseConfirmed,
    bool? NameAbsentAfterUnmapObserved, int? NameAfterUnmapNativeError,
    bool ControlUnchanged, bool RootIdentitiesUnchanged, bool CleanupConfirmed, string Stage, int? NativeError)
{
    public bool ExecutionSupported => false;
}

// One fixed development experiment, never an action engine. Mapping observations do not
// establish inactivity or allocation reclamation. There is no production scope or fallback.
internal static class OwnedCleanupMappedDispositionProbe
{
    private static int active, retention;
    private static readonly List<object> RetainedOwnership = [];
    internal static bool HasPendingNativeProbe => Volatile.Read(ref active) != 0;
    internal static bool RequiresRetention => Volatile.Read(ref retention) != 0;
    private const uint ReadAttributes = 0x80, NoFollow = 0x00200000, NoRecall = 0x00100000, Backup = 0x02000000;
    private const uint Unsafe = 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;

    internal static async Task<OwnedCleanupMappedDispositionReport> RunAsync(string outerGUIDroot, string marker, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Empty(OwnedCleanupMappedDispositionState.CancelledBeforeStart, "cancelled-before-start");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || RequiresRetention)
            return Empty(OwnedCleanupMappedDispositionState.Inconclusive, "platform-or-prior-ownership-unavailable");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return Empty(OwnedCleanupMappedDispositionState.Pending, "prior-worker-pending");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<OwnedCleanupMappedDispositionReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            OwnedCleanupMappedDispositionReport result;
            try { result = new Worker(outerGUIDroot, marker, stop.Token).Run(); }
            catch (Exception) { Retain(); result = Empty(OwnedCleanupMappedDispositionState.Inconclusive, "worker-unexpected-failure"); }
            finally { stop.Dispose(); Volatile.Write(ref active, 0); }
            completion.TrySetResult(result);
        }, CancellationToken.None);
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            return token.IsCancellationRequested ? result with { State = OwnedCleanupMappedDispositionState.Inconclusive, Stage = "stop-at-publication" } : result;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            Retain(); try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return Empty(OwnedCleanupMappedDispositionState.Pending, "caller-stopped-worker-still-owns-resources");
        }
    }
    private static void Retain() => Volatile.Write(ref retention, 1);
    private static OwnedCleanupMappedDispositionReport Empty(OwnedCleanupMappedDispositionState state, string stage) =>
        new(state, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, false, false, stage, null);

    private sealed class Worker(string outer, string marker, CancellationToken token)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<OwnedHandle> handles = [];
        private readonly List<Guard> guards = [];
        private readonly byte[] bytes = Enumerable.Range(0, 64).Select(i => (byte)(i + 17)).ToArray();
        private string child = "", sourcePath = "", controlPath = "", markerPath = "", stage = "validate-owned-scope";
        private int? nativeError;
        private OwnedHandle? source, mapping, target;
        private nint view;
        private bool created, unmapAttempted, viewRestoreUncertain, controlUnchanged, rootsUnchanged, topologyConfirmed, cleanup;
        private bool? sourceClosed, beforeView, targetOpened, accepted, deletePending, targetClosed, absentLive, afterView, unmapped, mappingClosed, absentFinal;
        private int? openError, dispositionError, liveNameError, unmapError, finalNameError;
        private Stamp? controlStamp, sourceStamp;

        internal OwnedCleanupMappedDispositionReport Run()
        {
            bool failed = false;
            try { Execute(); }
            catch (Invariant failure) { failed = true; stage = failure.Stage; }
            catch (Unavailable failure) { stage = failure.Stage; nativeError = failure.Code; }
            catch (OperationCanceledException) { stage = "worker-stop-after-start"; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException or System.Security.SecurityException)
            { stage = "owned-prerequisite-or-observation-unavailable"; }
            finally
            {
                // The caller never owns these pointers/handles. A stalled synchronous native
                // call remains in this worker; no caller can unmap or close underneath it.
                try { ReleaseMapping(); }
                catch (Unavailable failure) { nativeError = failure.Code; stage = failure.Stage; Retain(); }
                if (!RequiresRetention)
                {
                    try
                    {
                        CloseTargetAndSource();
                        if (created) AuditAfterRelease();
                    }
                    catch (Invariant failure) { failed = true; stage = failure.Stage; Retain(); }
                    catch (Exception) { stage = "owned-final-audit-unconfirmed"; Retain(); }
                }
                if (!RequiresRetention)
                {
                    for (int i = handles.Count - 1; i >= 0; i--)
                        if (!handles[i].CloseChecked(out _)) Retain();
                }
                cleanup = !RequiresRetention && view == 0 && (!created || controlUnchanged && rootsUnchanged && topologyConfirmed);
                if (!cleanup) { Retain(); lock (RetainedOwnership) RetainedOwnership.Add(this); }
                Array.Clear(bytes);
            }
            var state = OwnedCleanupMappedDispositionState.Inconclusive;
            if (failed) state = OwnedCleanupMappedDispositionState.Failed;
            else if (cleanup && sourceClosed == true && beforeView == true && afterView == true)
            {
                if (targetOpened == false && openError is 32 or 33) state = OwnedCleanupMappedDispositionState.ControlledOpenRefusalObserved;
                else if (accepted == true && targetClosed == true && absentLive == true) state = OwnedCleanupMappedDispositionState.CounterexampleObserved;
                else if (accepted == true && targetClosed == true && absentLive.HasValue && absentFinal.HasValue) state = OwnedCleanupMappedDispositionState.Observed;
            }
            return new(state, sourceClosed, beforeView, targetOpened, openError, accepted, dispositionError, deletePending, targetClosed,
                absentLive, liveNameError, afterView, unmapped, unmapError, mappingClosed, absentFinal, finalNameError,
                controlUnchanged, rootsUnchanged, cleanup, stage, nativeError);
        }

        private void Execute()
        {
            Check(); ValidateScope(); GuardChain(outer);
            byte[] owner = ReadBounded(Path.Combine(outer, ".madclean-fixture-owner"));
            try { if (Encoding.ASCII.GetString(owner) != marker) throw new Unavailable("owner-marker-mismatch", null); }
            finally { Array.Clear(owner); }
            child = Path.Combine(outer, "cleanup-mapped-disposition-probe");
            if (child.Length > 220) throw new Unavailable("child-path-bound", null);
            if (!CreateDirectoryW(child, 0)) { int error = Marshal.GetLastWin32Error(); Retain(); throw new Unavailable("fresh-child-unavailable", error); }
            created = true; GuardChain(child);
            sourcePath = Path.Combine(child, "mapped-source.tmp"); controlPath = Path.Combine(child, "outside-control.tmp"); markerPath = Path.Combine(child, ".probe-owner");
            byte[] ownedMarker = Encoding.ASCII.GetBytes(marker);
            try { WriteNew(markerPath, ownedMarker); } finally { Array.Clear(ownedMarker); }
            WriteNew(sourcePath, bytes); WriteNew(controlPath, bytes);
            sourceStamp = ReadStamp(sourcePath); controlStamp = ReadStamp(controlPath);
            stage = "create-owned-mapping";
            source = Open(sourcePath, 0xC0000000 | ReadAttributes, 7);
            if (Observe(source, sourcePath, false) != sourceStamp) throw new Unavailable("source-before-mapping-changed", null);
            mapping = CreateFileMappingW(source, 0, 4, 0, 0, null);
            int? mappingError = mapping.IsInvalid ? Marshal.GetLastWin32Error() : null; handles.Add(mapping);
            if (mapping.IsInvalid) throw new Unavailable("mapping-create-unavailable", mappingError);
            view = MapViewOfFile(mapping, 2, 0, 0, 64);
            if (view == 0) throw new Unavailable("view-create-unavailable", Marshal.GetLastWin32Error());
            CloseSource(); // No managed mapping/FileStream retains this original file handle.
            stage = "live-view-before-delete-open"; beforeView = WitnessView();
            Check(); RecheckRoots();
            stage = "exclusive-delete-open-with-live-view";
            target = CreateFileW(sourcePath, ReadAttributes | 0x10000, 0, 0, 3, NoFollow | NoRecall, 0);
            openError = target.IsInvalid ? Marshal.GetLastWin32Error() : null; handles.Add(target);
            targetOpened = !target.IsInvalid;
            // Actual open failures remain separate from namespace observations below.
            if (targetOpened.Value)
            {
                var current = Observe(target, sourcePath, false);
                // Own mapped writes deliberately restore bytes; Windows need not update mapped
                // write timestamps automatically. Identity/type/link/size checks are separate.
                if (current.Volume != sourceStamp.Volume || current.Id != sourceStamp.Id || current.Bytes != 64 || current.Links != 1 ||
                    (current.Attributes & (1u | 2u | 4u | Unsafe)) != 0) throw new Unavailable("target-object-not-exact-owned-source", null);
                RecheckRoots(); Check();
                if (Marshal.SizeOf<Disposition>() != 1) throw new Invariant("disposition-abi-not-one-byte");
                var disposition = new Disposition { DeleteFile = 1 };
                accepted = SetFileInformationByHandle(target, 4, ref disposition, 1);
                if (accepted == false) dispositionError = Marshal.GetLastWin32Error();
                if (GetStandard(target, 1, out var standard, 24)) deletePending = standard.DeletePending;
                CloseTarget();
            }
            (absentLive, liveNameError) = ProbeName();
            stage = "live-view-after-request"; afterView = WitnessView();
            stage = "release-owned-mapping"; ReleaseMapping();
            (absentFinal, finalNameError) = ProbeName();
            stage = "owned-mapped-observations-complete";
        }

        // No pointer is directly dereferenced. Both APIs receive only GetCurrentProcess's
        // pseudo handle and byte zero of this worker's own nonexecuting 64-byte writable view.
        private bool WitnessView()
        {
            Check();
            if (view == 0 || unmapAttempted || viewRestoreUncertain || sourceClosed != true) throw new Unavailable("view-witness-precondition-unavailable", null);
            byte[] one = new byte[1], changed = [(byte)(bytes[0] ^ 0x5A)], original = [bytes[0]];
            bool mayHaveWritten = false, restored = false, witnessed = false;
            try
            {
                ReadView(one);
                if (one[0] != bytes[0]) throw new Invariant("owned-live-view-original-byte-changed");
                mayHaveWritten = true;
                WriteView(changed); ReadView(one);
                if (one[0] != changed[0]) throw new Invariant("owned-live-view-write-not-observed");
                witnessed = true;
            }
            finally
            {
                try
                {
                    if (mayHaveWritten)
                    {
                        try
                        {
                            WriteView(original); ReadView(one);
                            if (one[0] != original[0]) throw new Unavailable("view-byte-restoration-unconfirmed", null);
                            if (!FlushViewOfFile(view, 1)) throw new Unavailable("restored-view-flush-unavailable", Marshal.GetLastWin32Error());
                            restored = true;
                        }
                        catch { viewRestoreUncertain = true; Retain(); throw; }
                    }
                }
                finally { Array.Clear(one); Array.Clear(changed); Array.Clear(original); }
            }
            return witnessed && restored;
        }
        private void ReadView(byte[] one)
        {
            if (!ReadProcessMemory(GetCurrentProcess(), view, one, 1, out nuint read)) throw new Unavailable("view-read-unavailable", Marshal.GetLastWin32Error());
            if (read != 1) throw new Unavailable("view-read-count-unexpected", null);
        }
        private void WriteView(byte[] one)
        {
            if (!WriteProcessMemory(GetCurrentProcess(), view, one, 1, out nuint written)) throw new Unavailable("view-write-unavailable", Marshal.GetLastWin32Error());
            if (written != 1) throw new Unavailable("view-write-count-unexpected", null);
        }
        private void ReleaseMapping()
        {
            // A failed restore/flush or native close invalidates further release assumptions.
            // Keep the original view/map and remaining leases rooted; neither this finally
            // path nor a later call retries or disposes underneath uncertain ownership.
            if (viewRestoreUncertain || sourceClosed == false || targetClosed == false || mappingClosed == false)
            { Retain(); return; }
            if (view != 0 && !unmapAttempted)
            {
                unmapAttempted = true;
                unmapped = UnmapViewOfFile(view);
                if (unmapped == false) { unmapError = Marshal.GetLastWin32Error(); Retain(); throw new Unavailable("view-unmap-unconfirmed", unmapError); }
                view = 0;
            }
            if (view != 0) return; // Failed unmap is never retried; mapping/leases remain owned.
            if (mapping is { IsInvalid: false, IsClosed: false })
            {
                mappingClosed = mapping.CloseChecked(out int code);
                if (mappingClosed == false) throw new Unavailable("mapping-close-unconfirmed", code);
            }
        }
        private void CloseTargetAndSource()
        {
            CloseTarget(); CloseSource();
        }
        private void CloseTarget()
        {
            if (target is not { IsInvalid: false, IsClosed: false }) return;
            targetClosed = target.CloseChecked(out int code);
            if (targetClosed == false) throw new Unavailable("target-close-unconfirmed", code);
        }
        private void CloseSource()
        {
            if (source is not { IsInvalid: false, IsClosed: false }) return;
            sourceClosed = source.CloseChecked(out int code);
            if (sourceClosed == false) throw new Unavailable("source-close-unconfirmed", code);
        }
        private (bool? Absent, int? Code) ProbeName()
        {
            uint attributes = GetFileAttributesW(sourcePath);
            if (attributes != uint.MaxValue) return (false, null);
            int code = Marshal.GetLastWin32Error(); return (code == 2 ? true : null, code);
        }
        private void AuditAfterRelease()
        {
            if (view != 0 || viewRestoreUncertain || mapping is { IsInvalid: false, IsClosed: false }) throw new Unavailable("audit-mapping-not-released", null);
            if (sourceStamp == null || controlStamp == null || sourcePath.Length == 0 || controlPath.Length == 0 || markerPath.Length == 0)
                throw new Unavailable("owned-audit-baseline-unavailable", null);
            RecheckRoots(); rootsUnchanged = true;
            if (ReadStamp(controlPath) != controlStamp) throw new Invariant("outside-control-identity-or-stamp-changed");
            AssertBytes(controlPath, bytes);
            byte[] owner = Encoding.ASCII.GetBytes(marker);
            try { AssertBytes(markerPath, owner); } finally { Array.Clear(owner); }
            var (absent, _) = ProbeName();
            if (!absent.HasValue) throw new Unavailable("final-source-name-unobserved", null);
            if (absent.Value)
            { if (accepted != true) throw new Invariant("source-absent-without-accepted-disposition"); }
            else
            {
                var current = ReadStamp(sourcePath);
                if (current.Volume != sourceStamp?.Volume || current.Id != sourceStamp?.Id || current.Bytes != 64 || current.Links != 1)
                    throw new Invariant("remaining-source-object-changed");
                AssertBytes(sourcePath, bytes);
            }
            controlUnchanged = true;
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { controlPath, markerPath };
            if (!absent.Value) expected.Add(sourcePath);
            int count = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(child))
                if (++count > 3 || !expected.Contains(path) || (GetFileAttributesW(path) & (0x10u | Unsafe)) != 0) throw new Unavailable("owned-topology-unconfirmed", null);
            if (count != expected.Count) throw new Unavailable("owned-topology-count-unconfirmed", null);
            topologyConfirmed = true;
        }
        private void Check()
        { token.ThrowIfCancellationRequested(); if (watch.Elapsed >= TimeSpan.FromSeconds(35)) throw new Unavailable("cooperative-budget", null); }
        private OwnedHandle Open(string path, uint access, uint share = 3, uint flags = 0, uint creation = 3)
        {
            Check(); var handle = CreateFileW(path, access, share, 0, creation, NoFollow | NoRecall | flags, 0); handles.Add(handle);
            if (handle.IsInvalid) throw new Unavailable("owned-open-unavailable", Marshal.GetLastWin32Error()); return handle;
        }
        private static bool Close(OwnedHandle handle)
        { if (!handle.CloseChecked(out int code)) throw new Unavailable("native-close-unconfirmed", code); return true; }
        private void WriteNew(string path, byte[] content)
        {
            var handle = Open(path, 0x40000000 | ReadAttributes | 0x100, 0, 0, 1);
            try
            {
                if (!WriteFile(handle, content, (uint)content.Length, out uint written, 0)) throw new Unavailable("owned-write-unavailable", Marshal.GetLastWin32Error());
                if (written != content.Length) throw new Unavailable("owned-write-count-unexpected", null);
                long old = DateTime.UtcNow.AddDays(-10).ToFileTimeUtc();
                if (!SetFileTime(handle, ref old, 0, ref old)) throw new Unavailable("owned-old-stamp-unavailable", Marshal.GetLastWin32Error());
                if (!FlushFileBuffers(handle)) throw new Unavailable("owned-file-flush-unavailable", Marshal.GetLastWin32Error());
            }
            finally { Close(handle); }
        }
        private byte[] ReadBounded(string path)
        {
            var handle = Open(path, 0x80000000 | ReadAttributes); byte[]? result = null;
            try
            {
                var stamp = Observe(handle, path, false);
                if (stamp.Bytes is < 1 or > 128) throw new Unavailable("owned-read-bound", null);
                result = new byte[(int)stamp.Bytes];
                if (!ReadFile(handle, result, (uint)result.Length, out var read, 0)) throw new Unavailable("owned-read-unavailable", Marshal.GetLastWin32Error());
                if (read != result.Length) throw new Unavailable("owned-read-count-unexpected", null);
                return result;
            }
            catch { if (result != null) Array.Clear(result); throw; }
            finally { try { Close(handle); } catch { if (result != null) Array.Clear(result); throw; } }
        }
        private void AssertBytes(string path, byte[] expected)
        { byte[] actual = ReadBounded(path); try { if (!actual.AsSpan().SequenceEqual(expected)) throw new Invariant("owned-control-bytes-changed"); } finally { Array.Clear(actual); } }
        private Stamp ReadStamp(string path)
        { var handle = Open(path, ReadAttributes); try { return Observe(handle, path, false); } finally { Close(handle); } }
        private Stamp Observe(OwnedHandle handle, string path, bool directory)
        {
            if (!GetIdentity(handle, 18, out var id, 24) || !GetBasic(handle, 0, out var basic, 40) || !GetStandard(handle, 1, out var size, 24))
                throw new Unavailable("metadata-unavailable", Marshal.GetLastWin32Error());
            var text = new StringBuilder(1024); uint length = GetFinalPathNameByHandleW(handle, text, 1024, 0);
            if (length == 0) throw new Unavailable("final-path-unavailable", Marshal.GetLastWin32Error());
            string actual = text.ToString(); if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (length >= 1024 || !string.Equals(actual, path, StringComparison.OrdinalIgnoreCase) || id.Volume == 0 || id.Id == Guid.Empty ||
                (basic.Attributes & Unsafe) != 0 || ((basic.Attributes & 0x10) != 0) != directory || size.Directory != directory || size.DeletePending ||
                size.Bytes < 0 || !directory && size.Links != 1) throw new Unavailable("unsafe-object-or-path", null);
            return new(id.Volume, id.Id, size.Bytes, size.Links, basic.Created, basic.Written, basic.Changed, basic.Attributes);
        }
        private void GuardChain(string path)
        {
            var paths = new Stack<string>();
            for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            { if (paths.Count >= 64) throw new Unavailable("ancestor-bound", null); paths.Push(current); }
            while (paths.Count > 0)
            {
                string current = paths.Pop(); if (guards.Any(g => string.Equals(g.Path, current, StringComparison.OrdinalIgnoreCase))) continue;
                var handle = Open(current, ReadAttributes | 0x20, 3, Backup); guards.Add(new(current, handle, Observe(handle, current, true)));
            }
        }
        private void RecheckRoots()
        {
            foreach (var guard in guards)
            { var current = Observe(guard.Handle, guard.Path, true); if (current.Volume != guard.Stamp.Volume || current.Id != guard.Stamp.Id) throw new Invariant("root-identity-changed"); }
        }
        private void ValidateScope()
        {
            string repository = Path.GetFullPath(Environment.CurrentDirectory), prefix = "windows-validation-fixtures-";
            if (outer == null || outer.Length is < 4 or > 200 || outer != Path.GetFullPath(outer) || !char.IsAsciiLetter(outer[0]) || outer[1] != ':' || outer[2] != '\\' ||
                outer.Contains('/') || outer.IndexOf(':', 2) >= 0 || outer.Any(char.IsControl) || new DriveInfo(outer[..3]).DriveType != DriveType.Fixed ||
                !Guid.TryParseExact(marker, "N", out var owner) || owner == Guid.Empty || marker != owner.ToString("N")) throw new Unavailable("owned-scope-syntax-unavailable", null);
            string name = Path.GetFileName(outer);
            if (!string.Equals(Path.GetDirectoryName(outer), Path.Combine(repository, "artifacts"), StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty || name[prefix.Length..] != id.ToString("N")) throw new Unavailable("owned-scope-topology-unavailable", null);
        }
    }

    private sealed record Stamp(ulong Volume, Guid Id, long Bytes, uint Links, long Created, long Written, long Changed, uint Attributes);
    private sealed record Guard(string Path, OwnedHandle Handle, Stamp Stamp);
    private sealed class Unavailable(string stage, int? code) : Exception("Owned mapped observation unavailable.") { internal string Stage => stage; internal int? Code => code; }
    private sealed class Invariant(string stage) : Exception("Owned mapped invariant failed.") { internal string Stage => stage; }
    private sealed class OwnedHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public OwnedHandle() : base(true) { }
        internal bool CloseChecked(out int code)
        {
            code = 0; if (IsInvalid || IsClosed) return true;
            bool success = CloseHandle(handle); code = success ? 0 : Marshal.GetLastWin32Error(); SetHandleAsInvalid(); Dispose();
            if (!success) { Retain(); lock (RetainedOwnership) RetainedOwnership.Add(this); }
            return success;
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Identity { internal ulong Volume; internal Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct Basic { internal long Created, Accessed, Written, Changed; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Standard { internal long Allocation, Bytes; internal uint Links; [MarshalAs(UnmanagedType.U1)] internal bool DeletePending; [MarshalAs(UnmanagedType.U1)] internal bool Directory; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Disposition { internal byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern OwnedHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern OwnedHandle CreateFileMappingW(OwnedHandle source, nint security, uint protection, uint high, uint low, string? name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint MapViewOfFile(OwnedHandle mapping, uint access, uint high, uint low, nuint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnmapViewOfFile(nint view);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushViewOfFile(nint view, nuint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint count, out nuint read);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint count, out nuint written);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdentity(OwnedHandle handle, int kind, out Identity identity, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBasic(OwnedHandle handle, int kind, out Basic basic, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(OwnedHandle handle, int kind, out Standard standard, uint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(OwnedHandle handle, int kind, ref Disposition disposition, uint bytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFinalPathNameByHandleW(OwnedHandle handle, StringBuilder text, uint length, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(OwnedHandle handle, [Out] byte[] buffer, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteFile(OwnedHandle handle, byte[] buffer, uint count, out uint written, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileTime(OwnedHandle handle, ref long created, nint accessed, ref long written);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushFileBuffers(OwnedHandle handle);
}
