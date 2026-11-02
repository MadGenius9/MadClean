using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal enum OwnedCleanupConfirmationState { Observed, Inconclusive, Failed, Pending, CancelledBeforeStart }
internal enum OwnedCleanupConfirmationCaseKind { Decline, Expiry, PreCancel, WrongSession, Confirm, Replay }
internal enum OwnedCleanupDecisionState { Declined, Expired, CancelledBeforeCommit, WrongSession, ReplayRejected, DispositionRequested, NativeRequestFailed }
internal sealed record OwnedCleanupConfirmationCase(OwnedCleanupConfirmationCaseKind Kind, OwnedCleanupDecisionState Decision,
    int DispositionCalls, bool? DispositionAccepted, int? DispositionNativeError, bool? DeletePendingObserved,
    bool? TargetCloseConfirmed, bool? NameAbsentObserved, int? NameProbeNativeError, bool? SourceUnchangedObserved,
    int RefusalAdditionalDispositionCalls);
internal sealed record OwnedCleanupConfirmationReport(OwnedCleanupConfirmationState State,
    IReadOnlyList<OwnedCleanupConfirmationCase> Cases, int TotalDispositionCalls, bool ControlsUnchanged,
    bool RootIdentitiesUnchanged, bool CleanupConfirmed, string Stage, int? NativeError)
{
    public bool ExecutionSupported => false;
}

// Fixed marked development scope only. An affirmative branch here is a protocol fixture,
// never evidence that a real user consented or that a client file is inactive/disposable.
internal static class OwnedCleanupConfirmationProtocol
{
    private static int active, retention;
    private static readonly List<object> RetainedOwnership = [];
    internal static bool HasPendingNativeProtocol => Volatile.Read(ref active) != 0;
    internal static bool RequiresRetention => Volatile.Read(ref retention) != 0;
    private const uint ReadAttributes = 0x80, NoFollow = 0x00200000, NoRecall = 0x00100000, Backup = 0x02000000;
    private const uint Unsafe = 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;

    internal static async Task<OwnedCleanupConfirmationReport> RunAsync(string outerGUIDroot, string marker, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Empty(OwnedCleanupConfirmationState.CancelledBeforeStart, "cancelled-before-start");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || RequiresRetention)
            return Empty(OwnedCleanupConfirmationState.Inconclusive, "platform-or-prior-ownership-unavailable");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return Empty(OwnedCleanupConfirmationState.Pending, "prior-worker-pending");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<OwnedCleanupConfirmationReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            OwnedCleanupConfirmationReport result;
            try { result = new Worker(outerGUIDroot, marker, stop.Token).Run(); }
            catch (Exception) { Retain(); result = Empty(OwnedCleanupConfirmationState.Inconclusive, "worker-unexpected-failure"); }
            finally { stop.Dispose(); Volatile.Write(ref active, 0); }
            completion.TrySetResult(result);
        }, CancellationToken.None);
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            return token.IsCancellationRequested ? result with { State = OwnedCleanupConfirmationState.Inconclusive, Stage = "stop-at-publication" } : result;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            Retain(); try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return Empty(OwnedCleanupConfirmationState.Pending, "caller-stopped-worker-still-owns-resources");
        }
    }
    private static void Retain() => Volatile.Write(ref retention, 1);
    private static OwnedCleanupConfirmationReport Empty(OwnedCleanupConfirmationState state, string stage) => new(state, [], 0, false, false, false, stage, null);

    private sealed class Preview(Session owner, TimeSpan lifetime)
    {
        internal Session Owner { get; } = owner;
        private readonly long prepared = Stopwatch.GetTimestamp();
        internal bool Expired => Stopwatch.GetElapsedTime(prepared) >= lifetime;
    }
    private sealed class Session
    {
        private readonly Worker owner;
        private readonly ProtocolHandle target;
        private readonly Stamp expected;
        private readonly Preview preview;
        private bool consumed;
        internal string Path { get; }
        internal int Calls { get; private set; }
        internal bool? Accepted { get; private set; }
        internal int? NativeError { get; private set; }
        internal bool? Pending { get; private set; }
        internal bool? Closed { get; private set; }
        internal Stamp Expected => expected;
        internal Session(Worker owner, string path, ProtocolHandle target, Stamp expected, TimeSpan lifetime)
        { this.owner = owner; Path = path; this.target = target; this.expected = expected; preview = new(this, lifetime); }
        internal Preview PreparedPreview => preview;
        internal OwnedCleanupDecisionState Decide(Preview selected, bool affirmative, CancellationToken decisionToken)
        {
            // Reference/session binding precedes any native query. The private preview is
            // not a path, serializable grant, caller-supplied identity or public execution token.
            if (!ReferenceEquals(selected, preview) || !ReferenceEquals(selected.Owner, this)) return OwnedCleanupDecisionState.WrongSession;
            if (consumed || Closed.HasValue) return OwnedCleanupDecisionState.ReplayRejected;
            consumed = true; // Every terminal decision, including decline/expiry/stop, is one shot.
            if (decisionToken.IsCancellationRequested) return OwnedCleanupDecisionState.CancelledBeforeCommit;
            if (!affirmative) return OwnedCleanupDecisionState.Declined;
            if (preview.Expired) return OwnedCleanupDecisionState.Expired;
            owner.Check(); owner.RecheckRoots();
            if (owner.Observe(target, Path, false) != expected) throw new Unavailable("held-source-evidence-changed", null);
            if (decisionToken.IsCancellationRequested) return OwnedCleanupDecisionState.CancelledBeforeCommit;
            if (preview.Expired) return OwnedCleanupDecisionState.Expired;
            if (Marshal.SizeOf<Disposition>() != 1) throw new Invariant("disposition-abi-not-one-byte");
            var request = new Disposition { DeleteFile = 1 };
            Calls++; owner.TotalCalls++;
            Accepted = SetFileInformationByHandle(target, 4, ref request, 1);
            if (Accepted == false) NativeError = Marshal.GetLastWin32Error();
            if (GetStandard(target, 1, out var standard, 24)) Pending = standard.DeletePending;
            return Accepted == true ? OwnedCleanupDecisionState.DispositionRequested : OwnedCleanupDecisionState.NativeRequestFailed;
        }
        internal void Close()
        {
            consumed = true;
            if (Closed.HasValue) return;
            Closed = target.CloseChecked(out int code);
            if (Closed == false) throw new Unavailable("target-close-unconfirmed", code);
        }
    }

    private sealed class Worker(string outer, string marker, CancellationToken token)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<ProtocolHandle> handles = [];
        private readonly List<Guard> guards = [];
        private readonly List<Session> sessions = [];
        private readonly List<OwnedCleanupConfirmationCase> cases = [];
        private readonly byte[] bytes = Encoding.ASCII.GetBytes("Inert owned two-step cleanup protocol bytes, never client data.");
        private string child = "", control = "", childMarker = "", stage = "validate-owned-scope";
        private bool created, controls, roots, topology, cleanup;
        private int? nativeError;
        private Stamp? controlStamp;
        internal int TotalCalls { get; set; }

        internal OwnedCleanupConfirmationReport Run()
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
                if (!RequiresRetention)
                {
                    try
                    {
                        foreach (var session in sessions) session.Close();
                        if (created) AuditFinal();
                    }
                    catch (Invariant failure) { failed = true; stage = failure.Stage; Retain(); }
                    catch (Exception) { stage = "owned-cleanup-or-audit-unconfirmed"; Retain(); }
                }
                if (!RequiresRetention)
                    for (int i = handles.Count - 1; i >= 0; i--) if (!handles[i].CloseChecked(out _)) Retain();
                cleanup = !RequiresRetention && (!created || controls && roots && topology);
                if (!cleanup) { Retain(); lock (RetainedOwnership) RetainedOwnership.Add(this); }
                Array.Clear(bytes);
            }
            bool observed = cases.Count == 6 && TotalCalls == 2 && cases.All(c =>
                c.Kind is OwnedCleanupConfirmationCaseKind.Confirm or OwnedCleanupConfirmationCaseKind.Replay
                    ? c.DispositionCalls == 1 && c.DispositionAccepted == true && c.TargetCloseConfirmed == true && c.NameAbsentObserved == true && c.RefusalAdditionalDispositionCalls == 0
                    : c.DispositionCalls == 0 && c.SourceUnchangedObserved == true && c.RefusalAdditionalDispositionCalls == 0);
            return new(failed ? OwnedCleanupConfirmationState.Failed : observed && cleanup ? OwnedCleanupConfirmationState.Observed : OwnedCleanupConfirmationState.Inconclusive,
                Array.AsReadOnly(cases.ToArray()), TotalCalls, controls, roots, cleanup, stage, nativeError);
        }
        private void Execute()
        {
            Check(); ValidateScope(); GuardChain(outer);
            byte[] owner = ReadBounded(Path.Combine(outer, ".madclean-fixture-owner"));
            try { if (Encoding.ASCII.GetString(owner) != marker) throw new Unavailable("owner-marker-mismatch", null); }
            finally { Array.Clear(owner); }
            child = Path.Combine(outer, "cleanup-confirmation-protocol");
            if (child.Length > 220) throw new Unavailable("child-path-bound", null);
            if (!CreateDirectoryW(child, 0)) { int code = Marshal.GetLastWin32Error(); Retain(); throw new Unavailable("fresh-child-unavailable", code); }
            created = true; GuardChain(child);
            childMarker = Path.Combine(child, ".probe-owner"); control = Path.Combine(child, "outside-control.tmp");
            byte[] markerBytes = Encoding.ASCII.GetBytes(marker);
            try { WriteNew(childMarker, markerBytes); } finally { Array.Clear(markerBytes); }
            WriteNew(control, bytes); controlStamp = ReadStamp(control);

            stage = "decline-private-preview";
            var decline = Prepare("decline.tmp");
            var declined = decline.Decide(decline.PreparedPreview, false, token); Check();
            if (declined != OwnedCleanupDecisionState.Declined || decline.Calls != 0) throw new Invariant("decline-invoked-disposition");
            AddCase(OwnedCleanupConfirmationCaseKind.Decline, decline, declined);

            stage = "expired-private-preview";
            var expiry = Prepare("expiry.tmp", TimeSpan.FromMilliseconds(100));
            Thread.Sleep(150); // Actual monotonic elapsed time; fixed developer-only wait, no fake clock.
            var expired = expiry.Decide(expiry.PreparedPreview, true, token); Check();
            if (expired != OwnedCleanupDecisionState.Expired || expiry.Calls != 0) throw new Invariant("expired-preview-invoked-disposition");
            AddCase(OwnedCleanupConfirmationCaseKind.Expiry, expiry, expired);

            stage = "precancel-private-decision";
            var cancelled = Prepare("cancel.tmp"); OwnedCleanupDecisionState stopped;
            using (var stop = new CancellationTokenSource()) { stop.Cancel(); stopped = cancelled.Decide(cancelled.PreparedPreview, true, stop.Token); }
            Check();
            if (stopped != OwnedCleanupDecisionState.CancelledBeforeCommit || cancelled.Calls != 0) throw new Invariant("precancel-decision-invoked-disposition");
            AddCase(OwnedCleanupConfirmationCaseKind.PreCancel, cancelled, stopped);

            stage = "wrong-private-session";
            var sender = Prepare("wrong-sender.tmp"); var receiver = Prepare("wrong-receiver.tmp");
            var wrong = receiver.Decide(sender.PreparedPreview, true, token); Check();
            if (wrong != OwnedCleanupDecisionState.WrongSession || sender.Calls != 0 || receiver.Calls != 0) throw new Invariant("foreign-preview-invoked-disposition");
            sender.Close(); if (!AuditUnchanged(sender)) throw new Invariant("foreign-preview-source-changed");
            AddCase(OwnedCleanupConfirmationCaseKind.WrongSession, receiver, wrong);

            stage = "affirmative-private-confirmation";
            var confirm = Prepare("confirm.tmp");
            var confirmed = confirm.Decide(confirm.PreparedPreview, true, token);
            AddCase(OwnedCleanupConfirmationCaseKind.Confirm, confirm, confirmed);

            stage = "replay-consumed-private-preview";
            var replay = Prepare("replay.tmp");
            var first = replay.Decide(replay.PreparedPreview, true, token); Check(); int callsBefore = replay.Calls;
            var second = replay.Decide(replay.PreparedPreview, true, token); Check(); int added = replay.Calls - callsBefore;
            if (second != OwnedCleanupDecisionState.ReplayRejected || added != 0) throw new Invariant("replay-invoked-disposition");
            if (first is not (OwnedCleanupDecisionState.DispositionRequested or OwnedCleanupDecisionState.NativeRequestFailed)) throw new Invariant("initial-replay-decision-invalid");
            AddCase(OwnedCleanupConfirmationCaseKind.Replay, replay, second, added);
            stage = "owned-two-step-observations-complete";
        }
        private Session Prepare(string leaf, TimeSpan? lifetime = null)
        {
            Check();
            // Called only with fixed literals above; no exported preparation/path API exists.
            if (leaf is not ("decline.tmp" or "expiry.tmp" or "cancel.tmp" or "wrong-sender.tmp" or "wrong-receiver.tmp" or "confirm.tmp" or "replay.tmp"))
                throw new Unavailable("fixed-source-required", null);
            string path = Path.Combine(child, leaf); WriteNew(path, bytes); var expected = ReadStamp(path);
            var target = Open(path, ReadAttributes | 0x10000, 0);
            var current = Observe(target, path, false);
            if (current != expected || current.Links != 1 || (current.Attributes & (1u | 2u | 4u | Unsafe)) != 0 ||
                current.Written <= 0 || DateTime.UtcNow - DateTime.FromFileTimeUtc(current.Written) < TimeSpan.FromDays(7))
                throw new Unavailable("owned-preview-evidence-unavailable", null);
            RecheckRoots();
            var session = new Session(this, path, target, current, lifetime ?? TimeSpan.FromSeconds(5)); sessions.Add(session); return session;
        }
        private void AddCase(OwnedCleanupConfirmationCaseKind kind, Session session, OwnedCleanupDecisionState decision, int additional = 0)
        {
            session.Close();
            var (absent, code) = ProbeName(session.Path);
            bool? unchanged = absent == false ? AuditUnchanged(session) : null;
            if (session.Calls == 0 && (absent != false || unchanged != true)) throw new Invariant("refused-source-not-unchanged");
            cases.Add(new(kind, decision, session.Calls, session.Accepted, session.NativeError, session.Pending, session.Closed, absent, code, unchanged, additional));
        }
        private bool AuditUnchanged(Session session)
        { if (ReadStamp(session.Path) != session.Expected) return false; AssertBytes(session.Path, bytes); return true; }
        private void AuditFinal()
        {
            if (controlStamp == null || childMarker.Length == 0) throw new Unavailable("owned-final-baseline-unavailable", null);
            RecheckRoots(); roots = true;
            if (ReadStamp(control) != controlStamp) throw new Invariant("outside-control-identity-or-stamp-changed");
            AssertBytes(control, bytes);
            byte[] owner = Encoding.ASCII.GetBytes(marker);
            try { AssertBytes(childMarker, owner); } finally { Array.Clear(owner); }
            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { childMarker, control };
            foreach (var session in sessions)
            {
                var (absent, _) = ProbeName(session.Path);
                if (!absent.HasValue) throw new Unavailable("source-name-unobserved", null);
                if (absent == true) { if (session.Accepted != true) throw new Invariant("source-absent-without-accepted-disposition"); }
                else
                {
                    if (session.Calls == 0 && !AuditUnchanged(session)) throw new Invariant("refused-source-identity-changed");
                    if (session.Calls != 0) AssertBytes(session.Path, bytes);
                    expectedPaths.Add(session.Path);
                }
            }
            controls = true; int count = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(child))
                if (++count > 12 || !expectedPaths.Contains(path) || (GetFileAttributesW(path) & (0x10u | Unsafe)) != 0) throw new Unavailable("owned-topology-unconfirmed", null);
            if (count != expectedPaths.Count) throw new Unavailable("owned-topology-count-unconfirmed", null);
            topology = true;
        }
        internal void Check() { token.ThrowIfCancellationRequested(); if (watch.Elapsed >= TimeSpan.FromSeconds(35)) throw new Unavailable("cooperative-budget", null); }
        private ProtocolHandle Open(string path, uint access, uint share = 3, uint flags = 0, uint creation = 3)
        {
            Check(); var handle = CreateFileW(path, access, share, 0, creation, NoFollow | NoRecall | flags, 0);
            int? error = handle.IsInvalid ? Marshal.GetLastWin32Error() : null; handles.Add(handle);
            if (handle.IsInvalid) throw new Unavailable("owned-open-unavailable", error); return handle;
        }
        private static void Close(ProtocolHandle handle) { if (!handle.CloseChecked(out int code)) throw new Unavailable("native-close-unconfirmed", code); }
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
                if (!ReadFile(handle, result, (uint)result.Length, out uint read, 0)) throw new Unavailable("owned-read-unavailable", Marshal.GetLastWin32Error());
                if (read != result.Length) throw new Unavailable("owned-read-count-unexpected", null); return result;
            }
            catch { if (result != null) Array.Clear(result); throw; }
            finally { try { Close(handle); } catch { if (result != null) Array.Clear(result); throw; } }
        }
        private void AssertBytes(string path, byte[] expected)
        { byte[] actual = ReadBounded(path); try { if (!actual.AsSpan().SequenceEqual(expected)) throw new Invariant("owned-control-bytes-changed"); } finally { Array.Clear(actual); } }
        private Stamp ReadStamp(string path) { var handle = Open(path, ReadAttributes); try { return Observe(handle, path, false); } finally { Close(handle); } }
        internal Stamp Observe(ProtocolHandle handle, string path, bool directory)
        {
            if (!GetIdentity(handle, 18, out var id, 24) || !GetBasic(handle, 0, out var basic, 40) || !GetStandard(handle, 1, out var size, 24)) throw new Unavailable("metadata-unavailable", Marshal.GetLastWin32Error());
            var text = new StringBuilder(1024); uint length = GetFinalPathNameByHandleW(handle, text, 1024, 0);
            if (length == 0) throw new Unavailable("final-path-unavailable", Marshal.GetLastWin32Error());
            string actual = text.ToString(); if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (length >= 1024 || !string.Equals(actual, path, StringComparison.OrdinalIgnoreCase) || id.Volume == 0 || id.Id == Guid.Empty ||
                (basic.Attributes & Unsafe) != 0 || ((basic.Attributes & 0x10) != 0) != directory || size.Directory != directory || size.DeletePending || size.Bytes < 0 || !directory && size.Links != 1)
                throw new Unavailable("unsafe-object-or-path", null);
            return new(id.Volume, id.Id, size.Bytes, size.Links, basic.Created, basic.Written, basic.Changed, basic.Attributes);
        }
        private void GuardChain(string path)
        {
            var paths = new Stack<string>(); for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            { if (paths.Count >= 64) throw new Unavailable("ancestor-bound", null); paths.Push(current); }
            while (paths.Count > 0)
            {
                string current = paths.Pop(); if (guards.Any(g => string.Equals(g.Path, current, StringComparison.OrdinalIgnoreCase))) continue;
                var handle = Open(current, ReadAttributes | 0x20, 3, Backup); guards.Add(new(current, handle, Observe(handle, current, true)));
            }
        }
        internal void RecheckRoots()
        { foreach (var guard in guards) { var current = Observe(guard.Handle, guard.Path, true); if (current.Volume != guard.Stamp.Volume || current.Id != guard.Stamp.Id) throw new Invariant("root-identity-changed"); } }
        private static (bool? Absent, int? Code) ProbeName(string path)
        { uint attributes = GetFileAttributesW(path); if (attributes != uint.MaxValue) return (false, null); int code = Marshal.GetLastWin32Error(); return (code == 2 ? true : null, code); }
        private void ValidateScope()
        {
            string repository = Path.GetFullPath(Environment.CurrentDirectory), prefix = "windows-validation-fixtures-";
            if (outer == null || outer.Length is < 4 or > 200 || outer != Path.GetFullPath(outer) || !char.IsAsciiLetter(outer[0]) || outer[1] != ':' || outer[2] != '\\' ||
                outer.Contains('/') || outer.IndexOf(':', 2) >= 0 || outer.Any(char.IsControl) || new DriveInfo(outer[..3]).DriveType != DriveType.Fixed ||
                !Guid.TryParseExact(marker, "N", out var owner) || owner == Guid.Empty || marker != owner.ToString("N")) throw new Unavailable("owned-scope-syntax-unavailable", null);
            string name = Path.GetFileName(outer);
            if (!string.Equals(Path.GetDirectoryName(outer), Path.Combine(repository, "artifacts"), StringComparison.OrdinalIgnoreCase) || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty || name[prefix.Length..] != id.ToString("N")) throw new Unavailable("owned-scope-topology-unavailable", null);
        }
    }
    private sealed record Stamp(ulong Volume, Guid Id, long Bytes, uint Links, long Created, long Written, long Changed, uint Attributes);
    private sealed record Guard(string Path, ProtocolHandle Handle, Stamp Stamp);
    private sealed class Unavailable(string stage, int? code) : Exception("Owned confirmation observation unavailable.") { internal string Stage => stage; internal int? Code => code; }
    private sealed class Invariant(string stage) : Exception("Owned confirmation invariant failed.") { internal string Stage => stage; }
    private sealed class ProtocolHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ProtocolHandle() : base(true) { }
        internal bool CloseChecked(out int code)
        {
            code = 0; if (IsInvalid || IsClosed) return true;
            bool success = CloseHandle(handle); code = success ? 0 : Marshal.GetLastWin32Error(); SetHandleAsInvalid(); Dispose();
            if (!success) { Retain(); lock (RetainedOwnership) RetainedOwnership.Add(this); } return success;
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Identity { internal ulong Volume; internal Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct Basic { internal long Created, Accessed, Written, Changed; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Standard { internal long Allocation, Bytes; internal uint Links; [MarshalAs(UnmanagedType.U1)] internal bool DeletePending; [MarshalAs(UnmanagedType.U1)] internal bool Directory; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Disposition { internal byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern ProtocolHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdentity(ProtocolHandle handle, int kind, out Identity identity, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBasic(ProtocolHandle handle, int kind, out Basic basic, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(ProtocolHandle handle, int kind, out Standard standard, uint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(ProtocolHandle handle, int kind, ref Disposition disposition, uint bytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetFinalPathNameByHandleW(ProtocolHandle handle, StringBuilder text, uint length, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(ProtocolHandle handle, [Out] byte[] buffer, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteFile(ProtocolHandle handle, byte[] buffer, uint count, out uint written, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileTime(ProtocolHandle handle, ref long created, nint accessed, ref long written);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushFileBuffers(ProtocolHandle handle);
}

internal static partial class NativeFixtures
{
    // Fixed numeric enum values and per-call observations only. No identity/path/content exports.
    // c=calls,a=accepted,e=disposition error,p=pending,h=checked target close,n=name absent,
    // g=name-query error,u=unchanged source,r=additional calls from refused replay.
    private static string CleanupConfirmationCaseFacts(IReadOnlyList<OwnedCleanupConfirmationCase> cases)
    {
        static string Flag(bool? value) => value == true ? "1" : value == false ? "0" : "?";
        return string.Join(";", cases.Take(6).Select(c => $"{(int)c.Kind}:{(int)c.Decision},c{c.DispositionCalls},a{Flag(c.DispositionAccepted)},e{c.DispositionNativeError},p{Flag(c.DeletePendingObserved)},h{Flag(c.TargetCloseConfirmed)},n{Flag(c.NameAbsentObserved)},g{c.NameProbeNativeError},u{Flag(c.SourceUnchangedObserved)},r{c.RefusalAdditionalDispositionCalls}"));
    }

    private static bool CleanupConfirmationProtocolFixture(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "cleanup-confirmation-owned-protocol";
        try
        {
            using var stop = new CancellationTokenSource(); stop.Cancel();
            var pre = OwnedCleanupConfirmationProtocol.RunAsync(fixture, marker, stop.Token).GetAwaiter().GetResult();
            if (pre.State != OwnedCleanupConfirmationState.CancelledBeforeStart || pre.TotalDispositionCalls != 0)
                throw new SmokeAssertionException("cleanup-confirmation-precancel-started");
            var observed = Query(ct => OwnedCleanupConfirmationProtocol.RunAsync(fixture, marker, ct), token);
            string caseFacts = CleanupConfirmationCaseFacts(observed.Cases);
            if (OwnedCleanupConfirmationProtocol.HasPendingNativeProtocol || OwnedCleanupConfirmationProtocol.RequiresRetention || !observed.CleanupConfirmed)
            {
                if (observed.State == OwnedCleanupConfirmationState.Failed) report.Fail(check, "An owned protocol invariant failed and resources/tree must be retained. No user confirmation or client action is established.");
                else report.Unknown(check, $"stage={observed.Stage}; nativeCode={observed.NativeError}; dispositionCalls={observed.TotalDispositionCalls}; caseFacts={caseFacts}. Owned confirmation resources or native work remain uncertain; retain the full marked tree and stop later fixture mutation. No client action was authorized.");
                return false;
            }
            if (observed.State == OwnedCleanupConfirmationState.Failed)
                report.Fail(check, "An owned confirmation protocol invariant failed; caseFacts=" + caseFacts + ". No user consent, inactivity or deletion eligibility is established.");
            else if (observed.State == OwnedCleanupConfirmationState.Observed)
                report.Pass(check, $"developerProtocolOnly=true; actualCases={observed.Cases.Count}; dispositionCalls={observed.TotalDispositionCalls}; declineExpiryCancelWrongSessionZeroCalls=true; replayAdditionalCalls=0; exactHeldIdentityBound=true; affirmativeRequestsAndCheckedClosesAndNameAbsenceObserved=true; preCancelBeforeStart=true; controlBytesRootIdsTopologyAndCleanupConfirmed=true; caseFacts={caseFacts}; ExecutionSupported=false. These fixed developer decisions are not real UI/user consent, inactivity, durable recovery or production cleanup permission.");
            else report.Unknown(check, $"stage={observed.Stage}; nativeCode={observed.NativeError}; actualCases={observed.Cases.Count}; dispositionCalls={observed.TotalDispositionCalls}; cleanupConfirmed={observed.CleanupConfirmed}; caseFacts={caseFacts}; ExecutionSupported=false. The developer-only protocol was incomplete; no client result was substituted.");
            return true;
        }
        catch (SmokeAssertionException failure) { report.Fail(check, "Owned confirmation invariant " + failure.Code + " failed; no client action is established."); return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or TimeoutException or OperationCanceledException or InvalidOperationException)
        { report.Unknown(check, "The owned confirmation protocol could not complete; retain the marked tree and pending worker ownership. No production action ran."); return false; }
    }
}
