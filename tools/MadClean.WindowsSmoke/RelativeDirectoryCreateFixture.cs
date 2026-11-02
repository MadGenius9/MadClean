using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool RelativeDirectoryCreateBaseline(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "owned-relative-directory-create-control";
        try
        {
            var result = RelativeDirectoryCreateProbe.RunAsync(fixture, marker, token).GetAwaiter().GetResult();
            var data = result.Native;
            string Status(uint called, int value) => called == 1 ? "0x" + unchecked((uint)value).ToString("X8") : "unobserved";
            string detail = $"state={result.State}; stage={result.Stage}; apiReturn={result.ApiReturned?.ToString() ?? "unobserved"}; nativeStage={(data.Version == 1 ? data.Stage.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unobserved")}; setupWin32={(data.SetupErrorObserved == 1 ? data.SetupError.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unobserved")}; parent={Status(data.ParentCalled, data.ParentStatus)}; parentIo={Status(data.ParentIoObserved, data.ParentIoStatus)}; parentId={data.ParentMatched == 1}; create={Status(data.CreateCalled, data.CreateStatus)}; createIo={Status(data.CreateIoObserved, data.CreateIoStatus)}; created={data.CreatedHandle == 1}; structuralSD={data.DescriptorMatched == 1}; collision={Status(data.CollisionCalled, data.CollisionStatus)}; refused={data.CollisionRefused == 1}; parentBookend={data.ParentBookend == 1}; childBookend={data.ChildBookend == 1}; dispositionRequested={data.DispositionCalled == 1}; dispositionAccepted={data.DispositionSucceeded == 1}; nativeClosed={data.HandlesClosed == 1}; descriptorReleased={data.DescriptorReleased == 1}; absence={result.AbsenceObserved}; teardown={result.TeardownCompleted}; pending={RelativeDirectoryCreateProbe.HasPendingNativeProbe}; retained={RelativeDirectoryCreateProbe.RequiresFixtureRetention}. ";
            string message = detail + "Owned baseline/collision only; no reparse interference, effective parent ACL, history, durability or production creation proof. Creation, disposition acceptance, checked closure and observed absence are separate evidence. No profile/journal writer, elevation or gate change.";
            if (message.Length > 1000)
            {
                const string bounded = "The owned directory-control summary exceeded its bound; complete qualifications and cleanup must be reviewed. No production creation proof.";
                if (result.State == CheckState.Failed) report.Fail(check, bounded + " A known invariant failure remains failed.");
                else report.Unknown(check, bounded);
            }
            else if (result.State == CheckState.Failed) report.Fail(check, message);
            else if (result.State == CheckState.Available && result.TeardownCompleted && !RelativeDirectoryCreateProbe.RequiresFixtureRetention) report.Pass(check, message);
            else report.Unknown(check, message);
            return result.TeardownCompleted && !RelativeDirectoryCreateProbe.HasPendingNativeProbe && !RelativeDirectoryCreateProbe.RequiresFixtureRetention;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or OperationCanceledException or TimeoutException)
        { report.Unknown(check, "The owned SDK-native directory control did not establish checked teardown. All owned evidence remains retained; no production creation or servicing operation ran."); return false; }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RelativeDirectoryNativeResult
{
    internal uint Version, Stage, SetupErrorObserved, SetupError;
    internal uint ParentCalled; internal int ParentStatus, ParentIoStatus; internal uint ParentInformation, ParentIoObserved;
    internal uint ParentMatched;
    internal uint CreateCalled; internal int CreateStatus, CreateIoStatus; internal uint CreateInformation, CreateIoObserved;
    internal uint CreatedHandle, DescriptorMatched;
    internal uint CollisionCalled; internal int CollisionStatus, CollisionIoStatus; internal uint CollisionInformation, CollisionIoObserved;
    internal uint CollisionRefused, ParentBookend, ChildBookend;
    internal uint DispositionCalled, DispositionSucceeded, HandlesClosed, DescriptorReleased, Complete;
}
internal sealed record RelativeDirectoryCreateResult(CheckState State, string Stage,
    RelativeDirectoryNativeResult Native, bool AbsenceObserved, bool TeardownCompleted, bool? ApiReturned = null);

// Development-only. An unresolved native call owns every guard, descriptor, module and
// marshaled argument. The caller's wait/cancellation never releases or terminates that worker.
internal static class RelativeDirectoryCreateProbe
{
    private static int active, retain;
    private static Task<RelativeDirectoryCreateResult>? pending;
    private static readonly List<Worker> Rooted = [];
    internal static bool HasPendingNativeProbe => Volatile.Read(ref active) != 0;
    internal static bool RequiresFixtureRetention => Volatile.Read(ref retain) != 0;
    internal static Task<RelativeDirectoryCreateResult>? PendingCompletion => Volatile.Read(ref pending);
    internal static async Task<RelativeDirectoryCreateResult> RunAsync(string fixture, string marker, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || token.IsCancellationRequested)
            return new(CheckState.Unavailable, "windows-x64-owned-control-required", default, false, !RequiresFixtureRetention);
        if (RequiresFixtureRetention || Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return new(CheckState.Unknown, "previous-worker-or-evidence-retained", default, false, false);
        Volatile.Write(ref retain, 1);
        var owned = new Worker(fixture, marker, token);
        lock (Rooted) Rooted.Add(owned);
        var task = Task.Run(() =>
        {
            try
            {
                var result = owned.Run();
                if (result.TeardownCompleted) { lock (Rooted) Rooted.Remove(owned); Volatile.Write(ref retain, 0); }
                return result;
            }
            finally { Volatile.Write(ref active, 0); }
        }, CancellationToken.None);
        Volatile.Write(ref pending, task);
        _ = task.ContinueWith(value => { _ = value.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false); }
        catch (TimeoutException) { return new(CheckState.Unknown, "caller-wait-expired-worker-retains-all-inputs", default, false, false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(CheckState.Cancelled, "caller-cancelled-worker-retains-all-inputs", default, false, false); }
    }
    private sealed class Worker(string fixture, string marker, CancellationToken token)
    {
        private readonly List<Guard> guards = [];
        private nint module, descriptor;
        private Guard? parent, markerGuard, absenceGuard;
        private bool safeRelease = true, absence, teardown;
        private bool? apiReturned;
        private RelativeDirectoryNativeResult observed;
        private string stage = "initializing", root = "";
        private Baseline? baseline;
        internal RelativeDirectoryCreateResult Run()
        {
            try
            {
                token.ThrowIfCancellationRequested();
                root = Path.GetFullPath(fixture).TrimEnd(Path.DirectorySeparatorChar);
                string name = Path.GetFileName(root);
                const string prefix = "windows-validation-fixtures-";
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out var guid) || guid == Guid.Empty ||
                    Directory.GetParent(root)?.Name != "artifacts" || marker.Length != 32 || !marker.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new IOException();
                stage = "retain-owned-parent-chain";
                OpenChain(root); parent = guards[^1];
                markerGuard = Open(Path.Combine(root, ".madclean-fixture-owner"), directory: false);
                VerifyOwner(); token.ThrowIfCancellationRequested();
                string repository = Directory.GetParent(Directory.GetParent(root)!.FullName)!.FullName;
                string controlDirectory = Path.Combine(repository, "artifacts", "native-controls");
                OpenChain(controlDirectory);
                string dll = Path.Combine(controlDirectory, "ShortcutRenameControl.dll");
                var controlFile = Open(dll, directory: false);
                if (!GetFileSizeEx(controlFile, out long controlBytes) || controlBytes is < 1 or > 4_194_304) throw new IOException();
                stage = "load-owned-sdk-control";
                module = LoadLibraryExW(dll, 0, 0x800);
                if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var abi = Export<Version>("RelativeDirectoryCreateControlAbiVersion");
                var size = Export<Version>("RelativeDirectoryCreateControlResultBytes");
                if (abi() != 1 || size() != 120 || Marshal.SizeOf<RelativeDirectoryNativeResult>() != 120) throw new IOException();
                baseline = Export<Baseline>("OwnedRelativeDirectoryBaseline");
                stage = "prepare-explicit-creation-descriptor";
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    string sid = identity.User?.Value ?? throw new IOException();
                    string sddl = $"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)";
                    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out descriptor, out uint bytes) || descriptor == 0 || bytes is < 20 or > 4096)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    VerifyOwner(); token.ThrowIfCancellationRequested();
                    stage = "sdk-native-baseline-and-collision";
                    safeRelease = false;
                    apiReturned = baseline(parent!, root, (uint)root.Length, descriptor, bytes, out observed, 120);
                    safeRelease = observed.Version == 1 && observed.HandlesClosed == 1 && observed.DescriptorReleased == 1;
                }
                if (!safeRelease)
                    return new(observed.Version != 1 || observed.Complete == 1 || apiReturned == true ? CheckState.Failed : CheckState.Unknown,
                        "returned-native-ownership-release-unconfirmed", observed, false, false, apiReturned);
                stage = "independent-absence-and-owner-bookend";
                VerifyOwner(); CheckGuard(parent!, directory: true);
                absenceGuard = CreateFileW(Path.Combine(root, "relative-private-directory-baseline"), 0x80, 7, 0, 3, 0x02300000, 0);
                int absenceError = absenceGuard.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                absence = absenceGuard.IsInvalid && absenceError == 2;
                if (!absenceGuard.CloseChecked(out _)) { safeRelease = false; throw new IOException(); }
                absenceGuard = null;
                if (!absence) { safeRelease = false; return new(CheckState.Unknown, "owned-leaf-absence-unconfirmed", observed, false, false, apiReturned); }
                VerifyOwner();
                bool proof = apiReturned == true && observed.Complete == 1 && observed.ParentCalled == 1 && observed.ParentStatus == 0 && observed.ParentIoObserved == 1 && observed.ParentIoStatus == 0 && observed.ParentInformation == 1 &&
                    observed.ParentMatched == 1 && observed.CreateCalled == 1 && observed.CreateStatus == 0 && observed.CreateIoObserved == 1 && observed.CreateIoStatus == 0 && observed.CreateInformation == 2 &&
                    observed.CreatedHandle == 1 && observed.DescriptorMatched == 1 && observed.CollisionCalled == 1 && unchecked((uint)observed.CollisionStatus) == 0xC0000035 && observed.CollisionRefused == 1 &&
                    observed.ParentBookend == 1 && observed.ChildBookend == 1 && observed.DispositionCalled == 1 && observed.DispositionSucceeded == 1;
                stage = proof ? "baseline-collision-cleanup-observed-only" : "native-capability-unverified-with-absence-observed";
                bool contradiction = observed.Complete == 1 && !proof || apiReturned == true && observed.Complete != 1;
                if (contradiction) stage = "sdk-complete-result-contract-contradiction";
                Release();
                return new(contradiction ? CheckState.Failed : proof && teardown ? CheckState.Available : CheckState.Unknown, stage, observed, absence, teardown, apiReturned);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or System.Security.SecurityException or OperationCanceledException or BadImageFormatException)
            {
                if (safeRelease)
                {
                    try { Release(); } catch { /* Rooted ownership and sticky retention remain. */ }
                }
                return new(CheckState.Unknown, stage, observed, absence, teardown, apiReturned);
            }
        }
        private T Export<T>(string name) where T : Delegate
        { nint pointer = GetProcAddress(module, name); if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); return Marshal.GetDelegateForFunctionPointer<T>(pointer); }
        private void OpenChain(string path)
        {
            var names = new Stack<string>();
            for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            { if (names.Count >= 64) throw new IOException(); names.Push(current); }
            while (names.Count > 0) { token.ThrowIfCancellationRequested(); _ = Open(names.Pop(), directory: true); }
        }
        private void VerifyOwner()
        {
            if (markerGuard == null || !GetFileSizeEx(markerGuard, out long length) || length != 32 || !SetFilePointerEx(markerGuard, 0, out _, 0)) throw new IOException();
            byte[] data = new byte[33];
            try { if (!ReadFile(markerGuard, data, 33, out uint read, 0) || read != 32 || Encoding.ASCII.GetString(data, 0, 32) != marker) throw new IOException(); }
            finally { Array.Clear(data); }
        }
        private Guard Open(string path, bool directory)
        {
            var handle = CreateFileW(path, directory ? 0x20080u : 0x80000000u, 1, 0, 3, directory ? 0x02300000u : 0x00300000u, 0);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            // Root ownership before validation, so a failed checked close never falls back to GC.
            guards.Add(handle);
            CheckGuard(handle, directory); return handle;
        }
        private static void CheckGuard(Guard handle, bool directory)
        {
            if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            const uint denied = 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;
            if ((info.Attributes & denied) != 0 || ((info.Attributes & 0x10) != 0) != directory) throw new IOException();
        }
        private void Release()
        {
            if (!safeRelease) return;
            if (markerGuard != null) VerifyOwner();
            foreach (var handle in guards.AsEnumerable().Reverse())
                if (!handle.CloseChecked(out _)) { safeRelease = false; return; }
            if (descriptor != 0) { if (LocalFree(descriptor) != 0) { safeRelease = false; return; } descriptor = 0; }
            if (module != 0) { if (!FreeLibrary(module)) { safeRelease = false; return; } module = 0; }
            teardown = true;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal uint Attributes, Tag; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Baseline(Guard parent, [MarshalAs(UnmanagedType.LPWStr)] string path, uint characters, nint descriptor, uint descriptorBytes, out RelativeDirectoryNativeResult result, uint resultBytes);
    private sealed class Guard : SafeHandleZeroOrMinusOneIsInvalid
    {
        // Retained privately by the rooted worker after an unsuccessful explicit close.
        // Do not expose, retry or let SafeHandle finalization close this uncertain raw value.
        private nint uncertain;
        private int uncertainCode;
        public Guard() : base(true) { }
        internal bool CloseChecked(out int actualCode)
        {
            if (IsClosed || IsInvalid) { actualCode = uncertainCode; return uncertain == 0; }
            nint raw = handle;
            bool ok = CloseHandle(raw);
            actualCode = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok) { uncertain = raw; uncertainCode = actualCode; }
            // An observed error value of zero is still a failed BOOL result. Suppress every
            // future implicit/explicit close attempt irrespective of that numeric value.
            SetHandleAsInvalid(); Dispose();
            return ok;
        }
        protected override bool ReleaseHandle()
        {
            nint raw = handle;
            bool ok = CloseHandle(raw);
            if (!ok) { uncertain = raw; uncertainCode = Marshal.GetLastWin32Error(); }
            return ok;
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern Guard CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(Guard handle, uint informationClass, out AttributeTag info, uint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileSizeEx(Guard handle, out long bytes);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(Guard handle, [Out] byte[] data, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFilePointerEx(Guard handle, long offset, out long position, uint origin);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint LocalFree(nint value);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint bytes);
}
