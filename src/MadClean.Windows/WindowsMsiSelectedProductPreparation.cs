using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>
/// Separate explicit exact-product metadata read. It shares the existing ARP/MSI owners;
/// it never changes a request policy, global inventory or Windows Installer state.
/// </summary>
public sealed class WindowsMsiSelectedProductPreparation : IMsiSelectedProductPreparationInventory
{
    private static int scheduledReads;
    public bool HasPendingNativeRead => Volatile.Read(ref scheduledReads) != 0 ||
        WindowsMsiRegistrationInventory.PendingRead || InstalledApplicationInventory.PendingRead;
    public async Task<CheckResult<MsiSelectedProductPreparation>> ReadAsync(MsiSelectedProductReadScope scope,
        long sourceGeneration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
            return Empty(CheckState.Unsupported, "The three-source selected-product read requires 64-bit Windows. No HKLM64 absence or native result was invented.");
        if (!MsiSelectedProductPreparationPolicy.IsFresh(scope, sourceGeneration, DateTimeOffset.UtcNow))
            return Empty(CheckState.Unknown, "The selected-product read selector is invalidated, stale or changed. No query started.");
        var ownership = new ScheduledOwnership();
        var worker = new Task<CheckResult<MsiSelectedProductPreparation>>(() =>
        {
            bool msiOwned = false, arpOwned = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!MsiSelectedProductPreparationPolicy.IsFresh(scope, sourceGeneration, DateTimeOffset.UtcNow))
                    return Empty(CheckState.Unknown, "The selected-product read selector changed before the worker started. No query ran.");
                if (!(msiOwned = WindowsMsiRegistrationInventory.TryBeginRead()))
                    return Empty(CheckState.Unknown, "An existing MSI worker is pending. No selected-product read started.");
                if (!(arpOwned = InstalledApplicationInventory.TryBeginRead()))
                    return Empty(CheckState.Unknown, "An existing registry worker is pending. No selected-product read started.");
                return new Reader(scope, sourceGeneration, token).Read();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return Empty(CheckState.Cancelled, "The selected-product worker observed cancellation. No completed preparation or action permission is claimed."); }
            catch (ReadScopeChanged)
            { return Empty(CheckState.Unknown, "The selection changed or expired during its read. No current target match is published."); }
            catch (ReadBudgetExceeded)
            { return Empty(CheckState.Unknown, "The cooperative selected-product read budget elapsed. Coverage remains incomplete; no action was attempted."); }
            catch (WindowsMsiRegistrationBudgetException)
            { return Empty(CheckState.Unknown, "The native MSI read budget elapsed. No complete context match or action permission was established."); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { return Empty(CheckState.Unsupported, "Required Windows metadata entry points are unavailable. No completed target read or action was substituted."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or System.Security.SecurityException or System.ComponentModel.Win32Exception or NotSupportedException)
            { return Empty(CheckState.Failed, "The selected-product metadata read could not complete. No provider text, global absence or action permission is inferred."); }
            finally
            {
                if (arpOwned) InstalledApplicationInventory.EndRead();
                if (msiOwned) WindowsMsiRegistrationInventory.EndRead();
                ownership.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
        // Allocate the task and its terminal observer before registering ownership. A
        // scheduled-but-unstarted task remains visible through timeout/cancellation.
        _ = worker.ContinueWith(value => { _ = value.Exception; ownership.Release(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        ownership.Register();
        try { worker.Start(TaskScheduler.Default); }
        catch (Exception)
        {
            // Created was never queued; a terminal task cannot begin later. Any other
            // state stays owned until its delegate/terminal continuation releases it.
            if (worker.Status == TaskStatus.Created || worker.IsCompleted) ownership.Release();
            return Empty(CheckState.Unknown, "The selected-product worker did not start conclusively. Any queued ownership remains visible; no completed query or action is claimed.");
        }
        try
        {
            var result = await worker.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!MsiSelectedProductPreparationPolicy.IsFresh(scope, sourceGeneration, DateTimeOffset.UtcNow))
                return Empty(CheckState.Unknown, "Selection invalidation or expiry won before publication. No current target match is shown.");
            return result;
        }
        catch (TimeoutException)
        { return Empty(CheckState.Unknown, "The caller stopped waiting for the selected-product read. Synchronous native work may continue under worker ownership and blocks new ARP/MSI reads; no force-close or action occurred."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Empty(CheckState.Cancelled, "The caller requested cancellation. This does not prove native calls stopped; their worker retains resources and shared gates until return."); }
    }
    private sealed class ScheduledOwnership
    {
        private int state;
        internal void Register()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) throw new InvalidOperationException();
            Interlocked.Increment(ref scheduledReads);
        }
        internal void Release()
        {
            if (Interlocked.CompareExchange(ref state, 2, 1) == 1) Interlocked.Decrement(ref scheduledReads);
        }
    }
    private static CheckResult<MsiSelectedProductPreparation> Empty(CheckState state, string message) =>
        CheckResult<MsiSelectedProductPreparation>.Empty(state, message);
    private sealed class ReadScopeChanged : Exception { }
    private sealed class ReadBudgetExceeded : Exception { }

    private sealed class Reader(MsiSelectedProductReadScope scope, long generation, CancellationToken token)
    {
        private const uint Query = 1, Enumerate = 8, OpenLink = 8;
        private static readonly string[] Components = ["Software", "Microsoft", "Windows", "CurrentVersion", "Uninstall"];
        private readonly Stopwatch watch = Stopwatch.StartNew();
        internal CheckResult<MsiSelectedProductPreparation> Read()
        {
            Check();
            using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true))
                if (impersonated != null) throw new NotSupportedException();
            var observations = Enum.GetValues<MsiArpProvenance>().Select(ReadSource).ToArray();
            Check();
            // The existing raw reader explicitly supports mask7. The original read scope
            // retains its original3/4 provenance hint; no old report or policy is changed.
            var native = new WindowsMsiRegistrationInventoryNative(scope.NativeReadScope, token).ReadObservations(7);
            Check();
            return MsiSelectedProductPreparationPolicy.Assess(scope, observations, native.Instances,
                native.EnumerationComplete, native.Issues.Count, generation, DateTimeOffset.UtcNow, token);
        }
        private void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= TimeSpan.FromSeconds(20)) throw new ReadBudgetExceeded();
            if (!MsiSelectedProductPreparationPolicy.IsFresh(scope, generation, DateTimeOffset.UtcNow)) throw new ReadScopeChanged();
        }
        private MsiSelectedArpLookup ReadSource(MsiArpProvenance source)
        {
            List<SafeRegistryHandle> handles = [];
            MsiSelectedArpLookup Unknown(int? code = null) =>
                new(source, MsiSelectedArpLookupState.Unknown, null, null, DateTimeOffset.UtcNow, code);
            MsiSelectedArpLookup Absent(int code) =>
                new(source, MsiSelectedArpLookupState.Absent, null, null, DateTimeOffset.UtcNow, code);
            try
            {
                Check();
                uint view = source == MsiArpProvenance.LocalMachine64 ? 0x100u : source == MsiArpProvenance.LocalMachine32 ? 0x200u : 0;
                int first = 0;
                SafeRegistryHandle anchor;
                int code;
                if (source == MsiArpProvenance.CurrentUserShared)
                    code = RegOpenCurrentUser(Query | Enumerate, out anchor);
                else
                {
                    code = RegOpenKeyExW(new nint(unchecked((int)0x80000002)), Components[0], OpenLink, Query | Enumerate | view, out anchor);
                    first = 1;
                }
                handles.Add(anchor); Check();
                if (source == MsiArpProvenance.CurrentUserShared && code != 0) return Unknown(code);
                if (code is 2 or 3) return Absent(code);
                if (code != 0) return Unknown(code);
                if (anchor.IsInvalid) return Unknown();
                if (!NoLink(anchor, out int? linkError)) return Unknown(linkError);
                for (int index = first; index <= Components.Length; index++)
                {
                    Check();
                    string component = index == Components.Length ? scope.ProductCode : Components[index];
                    code = RegOpenRelativeKeyExW(handles[^1], component, OpenLink, Query | Enumerate | view, out var next);
                    handles.Add(next); Check();
                    if (code is 2 or 3) return Absent(code);
                    if (code != 0) return Unknown(code);
                    if (next.IsInvalid) return Unknown();
                    if (!NoLink(next, out linkError)) return Unknown(linkError);
                }
                var key = handles[^1];
                bool? Flag(string field)
                {
                    var read = BoundedRegistrationMetadata.ReadFlag(key, field, Check, token);
                    return read.State == RegistrationMetadataState.Missing ? false : read.State == RegistrationMetadataState.Valid ? read.Value : null;
                }
                string Text(string field)
                {
                    var read = BoundedRegistrationMetadata.ReadText(key, field, Check, token);
                    return read.State == RegistrationMetadataState.Valid && read.Value != null ? read.Value : "Unknown";
                }
                bool? componentFlag = Flag("SystemComponent"), installer = Flag("WindowsInstaller"), restricted = Flag("NoRemove");
                string name = Text("DisplayName"), version = Text("DisplayVersion"), publisher = Text("Publisher");
                var normal = BoundedRegistrationMetadata.ProbeUninstallerName(key, "UninstallString", Check);
                var quiet = BoundedRegistrationMetadata.ProbeUninstallerName(key, "QuietUninstallString", Check);
                bool? present = normal.Exists == true || quiet.Exists == true ? true :
                    normal.Exists.HasValue && quiet.Exists.HasValue ? false : null;
                Check();
                var observed = DateTimeOffset.UtcNow;
                string identity = source == scope.OriginalSource ? scope.Application.Identity :
                    MsiSelectedProductPreparationPolicy.RegistrationIdentity(source, scope.ProductCode);
                string label = MsiSelectedProductPreparationPolicy.SourceLabel(source);
                var evidence = new InstalledAppRegistrationEvidence(identity, label, restricted, installer, present, scope.ProductCode, observed);
                var row = new InstalledApplication(identity, name, version, publisher, label, null,
                    "Exact-product registration metadata only; no action permission", evidence);
                return new(source, MsiSelectedArpLookupState.Present, row, componentFlag, observed, null);
            }
            finally { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
        }
        private bool NoLink(SafeRegistryHandle key, out int? error)
        {
            Check(); uint size = 0;
            int code = RegQueryTypeW(key, "SymbolicLinkValue", 0, out uint type, 0, ref size);
            Check();
            error = code is 0 or 2 or 234 ? null : code;
            return error == null && !((code is 0 or 234) && type == 6);
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int RegOpenKeyExW(nint root, string path, uint options, uint rights, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegOpenKeyExW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int RegOpenRelativeKeyExW(SafeRegistryHandle root, string path, uint options, uint rights, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int RegOpenCurrentUser(uint rights, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegQueryValueExW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int RegQueryTypeW(SafeRegistryHandle key, string name, nint reserved, out uint type, nint data, ref uint bytes);
    }
}
