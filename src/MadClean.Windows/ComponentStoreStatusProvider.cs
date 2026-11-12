using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>One explicitly selected component-store check. Never ordinary inventory, elevation or repair.</summary>
public sealed class ComponentStoreStatusProvider : IComponentStoreStatusProvider
{
    private static readonly object GlobalGate = new();
    private static readonly List<Session> Rooted = [];
    // Fixed backend qualified at source 2e0c0160 / evidence c93b0960; no runtime override.
    public bool ProductionCheckValidated => IsCheckKindValidated(ComponentStoreCheckKind.RecordedFlags);
    public bool IsCheckKindValidated(ComponentStoreCheckKind kind) => kind switch
    {
        ComponentStoreCheckKind.RecordedFlags => true,
        // Backend qualified at 88038b6c / 6b5970a1; public UI is separately qualified. No runtime override.
        ComponentStoreCheckKind.ScanHealth => true,
        _ => false
    };
    public bool HasPendingWork { get { lock (GlobalGate) return SystemToolServicingAdmission.HasPendingWork || Rooted.Any(session => session.HasPendingWork); } }
    public bool RequiresRetention { get { lock (GlobalGate) return SystemToolServicingAdmission.RequiresRetention || SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty || Rooted.Any(session => session.RequiresRetention); } }

    public Task<IComponentStoreStatusSession> StartAsync(ComponentStoreStatusIntent intent, CancellationToken prelaunchToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        // Immutable production gate: no profile/admin/token/native/path access and no host launch.
        if (!IsCheckKindValidated(intent.Kind))
            return Task.FromResult<IComponentStoreStatusSession>(new CompletedSession(intent, CheckState.Unavailable,
                "This selected component-store check kind is not enabled in this build. No administrator query, Windows host or operation was requested.",
                new(null, false, null, null, true) { HostStartAttempted = false }));
        return StartOwnedAsync(intent, null, prelaunchToken);
    }

    internal Task<IComponentStoreStatusSession> StartCandidateAsync(ComponentStoreStatusIntent intent,
        ComponentStoreStatusFixtureAuthorization authorization, CancellationToken prelaunchToken)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(authorization);
        return StartOwnedAsync(intent, authorization, prelaunchToken);
    }

    private static Task<IComponentStoreStatusSession> StartOwnedAsync(ComponentStoreStatusIntent intent,
        ComponentStoreStatusFixtureAuthorization? authorization, CancellationToken prelaunchToken)
    {
        if (prelaunchToken.IsCancellationRequested)
        {
            intent.Invalidate();
            return Task.FromResult<IComponentStoreStatusSession>(new CompletedSession(intent, CheckState.Cancelled,
                "Cancelled before host preparation. No host launch was attempted.",
                new(null, false, null, null, true) { HostStartAttempted = false }));
        }
        lock (GlobalGate)
        {
            Rooted.RemoveAll(session => !session.HasPendingWork && !session.RequiresRetention);
            if (SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty)
                return Task.FromResult<IComponentStoreStatusSession>(new CompletedSession(intent, CheckState.Unavailable,
                    "Shared fixed-tool identity ownership is uncertain. No additional host or identity query was requested.", null));
            if (Rooted.Any(session => session.HasPendingWork || session.RequiresRetention))
                return Task.FromResult<IComponentStoreStatusSession>(new CompletedSession(intent, CheckState.Unavailable,
                    "An earlier component-store owner is pending or retained. No additional host was requested.", null));
            // Allocate the managed owner/result and root capacity before acquiring
            // a shared slot or transferring any fixture proof ownership.
            var owner = new Session(intent, authorization, prelaunchToken);
            var returned = Task.FromResult<IComponentStoreStatusSession>(owner);
            Rooted.EnsureCapacity(Rooted.Count + 1);
            var operation = intent.Kind switch
            {
                ComponentStoreCheckKind.RecordedFlags => SystemToolOperation.DismCheckHealth,
                ComponentStoreCheckKind.ScanHealth => SystemToolOperation.DismScanHealth,
                _ => (SystemToolOperation)(-1)
            };
            var admission = SystemToolServicingAdmission.TryAcquire(operation, intent.RequestId);
            if (admission == null)
                return Task.FromResult<IComponentStoreStatusSession>(new CompletedSession(intent, CheckState.Unavailable,
                    "A cooperating owner or unresolved operation history blocks admission, or this check kind is unsupported. No host or identity query was requested.", null));
            owner.AttachAdmission(admission);
            bool proofTransferred;
            try { proofTransferred = authorization == null || authorization.TransferToWorker(intent.RequestId, intent.Kind); }
            catch
            {
                // A transfer exception cannot establish which proof owner remains.
                // Root that exact reference and retain the shared slot, without
                // attempting to close a possibly foreign/current proof owner.
                Rooted.Add(owner);
                owner.BeforeWorkerFailed(proofOwnershipUncertain: true);
                return returned;
            }
            if (!proofTransferred)
            {
                // A refused transfer leaves the fixture with its existing owner;
                // only this newly acquired, never-dispatched slot is released.
                admission.Complete(ownershipReleased: true);
                return Task.FromResult<IComponentStoreStatusSession>(new CompletedSession(intent, CheckState.Unavailable,
                    "The exact source-bound disposable-CI authorization was not transferred. No host was requested.", null));
            }
            Rooted.Add(owner);
            try { _ = Task.Run(owner.RunAsync); }
            catch { owner.BeforeWorkerFailed(); }
            return returned;
        }
    }

    private sealed class CompletedSession : IComponentStoreStatusSession
    {
        internal CompletedSession(ComponentStoreStatusIntent intent, CheckState state, string message, ComponentStoreStatusTransport? transport)
        {
            RequestId = intent.RequestId; var now = DateTimeOffset.UtcNow;
            Completion = Task.FromResult(new ComponentStoreStatusResult(intent.RequestId, state, ComponentStoreRecordedFlag.Unknown, now, now, message)
                { Kind = intent.Kind, Transport = transport ?? new(null, null, null, null, null) });
        }
        public Guid RequestId { get; }
        public bool HasPendingWork => false;
        public bool RequiresRetention => false;
        public bool StopRequested => false;
        public void RequestStop() { }
        public Task<ComponentStoreStatusResult> Completion { get; }
    }

    private sealed class Session(ComponentStoreStatusIntent intent, ComponentStoreStatusFixtureAuthorization? authorization,
        CancellationToken prelaunchToken) : IComponentStoreStatusSession
    {
        private readonly object sync = new();
        private readonly TaskCompletionSource<ComponentStoreStatusResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DateTimeOffset attempted = DateTimeOffset.UtcNow;
        private SystemToolTrustedLease? lease;
        private SystemToolAdmissionLease? admission;
        internal void AttachAdmission(SystemToolAdmissionLease value) => admission = value;
        private PrivateCancellationEvent? cancel;
        private Process? child;
        private Task<DrainResult>? stdout, stderr;
        private Task<BodyWriteResult>? input;
        private bool inputAttempted;
        private int stopState; // bit1 request accepted; bit2 final publication boundary
        private bool signalAttempted, retained, startAmbiguous;
        private bool? admin, hostStart, hostExit, ownership, startAttempted, stopSignal;
        private int? exitCode, stopNativeError;
        private SystemToolIdentityDiagnostic? identity;
        public Guid RequestId => intent.RequestId;
        public bool HasPendingWork => !completion.Task.IsCompleted;
        public bool RequiresRetention => Volatile.Read(ref retained);
        public bool StopRequested => (Volatile.Read(ref stopState) & 1) != 0;
        public Task<ComponentStoreStatusResult> Completion => completion.Task;
        public void RequestStop()
        {
            while (true)
            {
                int observed = Volatile.Read(ref stopState);
                if ((observed & 2) != 0 && !RequiresRetention) return;
                if (Interlocked.CompareExchange(ref stopState, observed | 1, observed) == observed) break;
            }
            // Never block the UI on token, trust, process-start or native cleanup calls.
            // The owner observes the latch at each prelaunch boundary and after Start returns.
            if (!Monitor.TryEnter(sync)) return;
            try { SignalLatchedLocked(); }
            finally { Monitor.Exit(sync); }
        }
        private void SignalLatchedLocked()
        {
            if (StopRequested && cancel is { Ready: true } && !signalAttempted)
            {
                signalAttempted = true;
                stopSignal = cancel.Signal(out int? error); stopNativeError = error;
            }
        }
        private bool StopBeforeLaunch => prelaunchToken.IsCancellationRequested || StopRequested || intent.IsInvalidated;
        internal void BeforeWorkerFailed(bool proofOwnershipUncertain = false)
        {
            lock (sync)
            {
                if (proofOwnershipUncertain) { retained = true; ownership = false; }
                else
                {
                    try { authorization?.ReleaseFromWorker(); ownership = true; }
                    catch { retained = true; ownership = false; }
                }
                admission?.Complete(ownershipReleased: ownership == true);
                completion.TrySetResult(new(RequestId, CheckState.Unknown, ComponentStoreRecordedFlag.Unknown, attempted, DateTimeOffset.UtcNow,
                    "The owner could not be scheduled. No host launch was attempted; any uncertain proof remains retained.")
                    { Kind = intent.Kind, Transport = Transport(null, null) });
            }
        }
        internal async Task RunAsync()
        {
            ComponentStoreStatusEvidence? evidence = null;
            DrainResult? output = null, error = null;
            string? stoppedBeforeLaunch = null;
            try
            {
                if (StopBeforeLaunch) { stoppedBeforeLaunch = "Stopped before host preparation; no host was requested."; return; }
                if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                { stoppedBeforeLaunch = "This fixed host requires a 64-bit Windows process; no host was requested."; return; }
                using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true))
                {
                    if (impersonated != null) { admin = false; stoppedBeforeLaunch = "An impersonating caller is unsupported. No host was requested."; return; }
                }
                using (var current = WindowsIdentity.GetCurrent())
                {
                    admin = new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator);
                    if (admin != true) { stoppedBeforeLaunch = "The current caller is not effectively elevated. No elevation or host was requested."; return; }
                    if (StopBeforeLaunch) { stoppedBeforeLaunch = "Stopped before fixed host identity qualification."; return; }
                    lease = SystemToolTrustedLease.OpenWindowsPowerShell(prelaunchToken);
                    identity = lease.SignatureDiagnostic with { Stage = SystemToolIdentityStage.Completed, Reason = SystemToolIdentityReason.Observed };
                    if (StopBeforeLaunch) { stoppedBeforeLaunch = "Stopped before private cancellation channel creation."; return; }
                    lock (sync)
                    {
                        cancel = new PrivateCancellationEvent();
                        cancel.Initialize(current.User?.Value ?? throw new InvalidOperationException());
                        SignalLatchedLocked();
                    }
                }
                string body = ComponentStoreStatusHost.Body(RequestId, cancel.Name, intent.Kind);
                string encoded = ComponentStoreStatusHost.EncodedBootstrap;
                if (lease.ToolPath.Length + encoded.Length + 128 >= 32767) throw new InvalidOperationException();
                child = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = lease.ToolPath, UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardInputEncoding = new UTF8Encoding(false, true),
                        StandardOutputEncoding = new UTF8Encoding(false, false), StandardErrorEncoding = new UTF8Encoding(false, false),
                        WorkingDirectory = lease.SystemDirectory
                    }
                };
                foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }) child.StartInfo.ArgumentList.Add(argument);
                ConfigureEnvironment(child.StartInfo, lease.SystemDirectory);
                lock (sync)
                {
                    if (StopBeforeLaunch) { stoppedBeforeLaunch = "Stopped before actual host dispatch; no host was requested."; return; }
                    var reservation = intent.TryReserveDispatch(DateTimeOffset.UtcNow, prelaunchToken);
                    if (reservation != ComponentStoreStatusDispatchReservation.Reserved)
                    { stoppedBeforeLaunch = "The reviewed one-shot request was stale, cancelled, invalidated or already reserved. No host was requested."; return; }
                    if (StopBeforeLaunch) { stoppedBeforeLaunch = "Stopped at the prelaunch boundary; no host was requested."; return; }
                    startAttempted = true;
                    try { hostStart = child.Start(); SignalLatchedLocked(); }
                    catch { startAmbiguous = true; retained = true; throw; }
                }
                if (hostStart != true) { stoppedBeforeLaunch = "The process API returned without a started host. No component-store result is claimed."; return; }
                // No cancellation token or timeout terminates the process or servicing. The rooted
                // owner continues draining and observing actual exit, including after a stop request.
                stdout = Drain(child.StandardOutput, ComponentStoreStatusParser.MaximumCharacters, retain: true);
                stderr = Drain(child.StandardError, 0, retain: false);
                var ownedWriter = child.StandardInput;
                inputAttempted = true;
                input = WriteBody(ownedWriter, body);
                var inputResult = await input.ConfigureAwait(false);
                if (!inputResult.Closed) { lock (sync) retained = true; }
                await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                hostExit = true; exitCode = child.ExitCode;
                output = await stdout.ConfigureAwait(false); error = await stderr.ConfigureAwait(false);
                if (inputResult.Written && inputResult.Closed && !output.Exceeded && output.Completed)
                {
                    var parsed = ComponentStoreStatusParser.Parse(output.Text, RequestId, intent.Kind);
                    if (parsed.Accepted) evidence = parsed.Evidence;
                }
            }
            catch (SystemToolTrustFailure failure) { identity = failure.Diagnostic; }
            catch (OperationCanceledException) { stoppedBeforeLaunch = "Cancelled before a host was observed. No completed Windows check is claimed."; }
            catch (Exception) { /* Fixed nullable observations, never provider/exception text. */ }
            finally
            {
                // Establish every available drain even when setup failed part-way. A started
                // bootstrap with no writer must receive EOF before waiting for its actual exit.
                if (hostStart == true && child != null)
                {
                    if (stdout == null) { try { stdout = Drain(child.StandardOutput, ComponentStoreStatusParser.MaximumCharacters, retain: true); } catch { lock (sync) retained = true; } }
                    if (stderr == null) { try { stderr = Drain(child.StandardError, 0, retain: false); } catch { lock (sync) retained = true; } }
                    if (!inputAttempted)
                    {
                        try { child.StandardInput.Close(); } catch { lock (sync) retained = true; }
                    }
                    else if (input == null) { lock (sync) retained = true; } // unknown write ownership is never force-closed
                }
                // The write and its stream-close remain owned even after a stop request.
                if (input != null) { try { var written = await input.ConfigureAwait(false); if (!written.Closed) { lock (sync) retained = true; } } catch { lock (sync) retained = true; } }
                // A known started process remains owned even if a drain/exit observation raised.
                if (hostStart == true && hostExit != true && child != null)
                {
                    try { await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); hostExit = true; exitCode = child.ExitCode; }
                    catch { lock (sync) retained = true; }
                }
                if (stdout != null && output == null) { try { output = await stdout.ConfigureAwait(false); } catch { lock (sync) retained = true; } }
                if (stderr != null && error == null) { try { error = await stderr.ConfigureAwait(false); } catch { lock (sync) retained = true; } }
                lock (sync)
                {
                    if (startAttempted == null) { startAttempted = false; hostStart = false; }
                    if (hostStart != true && (StopRequested || prelaunchToken.IsCancellationRequested)) intent.Invalidate();
                    if (SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty) retained = true;
                    if (hostStart == true && hostExit != true || startAmbiguous) retained = true;
                    if (!retained)
                    {
                        try
                        {
                            child?.Dispose();
                            if (cancel != null && !cancel.Release()) retained = true;
                            if (!retained) { lease?.Dispose(); lease = null; authorization?.ReleaseFromWorker(); }
                        }
                        catch { retained = true; }
                    }
                    ownership = !retained;
                    // Caller cancellation/publication never releases admission.
                    // Complete only after actual process/drain/proof/lease cleanup;
                    // any retained ownership permanently blocks cooperating routes.
                    admission?.Complete(ownershipReleased: ownership == true);
                    var transport = Transport(output, error);
                    Interlocked.Or(ref stopState, 2);
                    var result = ComponentStoreStatusPolicy.Assess(intent, evidence, transport, attempted, DateTimeOffset.UtcNow, StopRequested);
                    if (stoppedBeforeLaunch != null && evidence == null)
                        result = result with { State = StopRequested || prelaunchToken.IsCancellationRequested ? CheckState.Cancelled : CheckState.Unavailable, Message = stoppedBeforeLaunch };
                    completion.TrySetResult(result);
                }
                if (!RequiresRetention) { lock (GlobalGate) Rooted.Remove(this); }
            }
        }
        private ComponentStoreStatusTransport Transport(DrainResult? output, DrainResult? error) =>
            new(admin, hostStart, hostExit, exitCode, ownership)
            {
                HostStartAttempted = startAttempted, HostIdentity = identity,
                StdoutDrained = output?.Completed, StderrDrained = error?.Completed,
                OutputBoundExceeded = output?.Exceeded, StderrCharacterCount = error?.Count,
                StopSignalSucceeded = stopSignal, StopSignalNativeError = stopNativeError
            };
    }

    private sealed record BodyWriteResult(bool Written, bool Closed);
    private static async Task<BodyWriteResult> WriteBody(StreamWriter writer, string body)
    {
        bool returned = false;
        try { await writer.WriteAsync(body).ConfigureAwait(false); await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false); returned = true; }
        catch { /* No raw script/transport exception text leaves this private pipe owner. */ }
        bool closed = false;
        try { writer.Close(); closed = true; } catch { }
        return new(returned, closed);
    }

    private sealed record DrainResult(string Text, int? Count, bool Exceeded, bool Completed);
    private static async Task<DrainResult> Drain(StreamReader reader, int bound, bool retain)
    {
        var text = new StringBuilder(Math.Min(bound, 4096)); char[] buffer = new char[4096];
        int? count = 0; bool exceeded = false;
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) return new(text.ToString(), count, exceeded, true);
                count = count is { } measured && measured <= int.MaxValue - read ? measured + read : null;
                if (retain)
                {
                    if (count is { } within && within <= bound) text.Append(buffer, 0, read);
                    else { exceeded = true; text.Clear(); }
                }
            }
        }
        catch { return new("", count, exceeded, false); }
        finally { Array.Clear(buffer); }
    }
    private static void ConfigureEnvironment(ProcessStartInfo info, string system)
    {
        string? temp = Environment.GetEnvironmentVariable("TEMP"), tmp = Environment.GetEnvironmentVariable("TMP");
        info.Environment.Clear();
        string windows = Path.GetDirectoryName(system)!;
        info.Environment["SystemRoot"] = windows; info.Environment["windir"] = windows; info.Environment["PATH"] = system;
        if (temp != null && Path.IsPathFullyQualified(temp)) info.Environment["TEMP"] = temp;
        if (tmp != null && Path.IsPathFullyQualified(tmp)) info.Environment["TMP"] = tmp;
        info.Environment["PSModulePath"] = Path.Combine(system, @"WindowsPowerShell\v1.0\Modules");
    }

    private sealed class PrivateCancellationEvent
    {
        internal string Name { get; } = ComponentStoreStatusHost.EventPrefix + Guid.NewGuid().ToString("N");
        private nint handle, creationDescriptor, copiedDescriptor;
        private bool handleClosed, creationFreed, copiedFreed, uncertain;
        internal bool Ready { get; private set; }
        internal void Initialize(string sid)
        {
            string sddl = $"O:{sid}D:P(A;;0x001F0003;;;{sid})(A;;0x001F0003;;;SY)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out creationDescriptor, out _) || creationDescriptor == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = creationDescriptor };
            handle = CreateEventW(ref attributes, true, false, Name);
            int createError = Marshal.GetLastWin32Error();
            if (handle == 0 || createError == 183) throw new Win32Exception(createError);
            uint status = GetSecurityInfo(handle, 6, 5, 0, 0, 0, 0, out copiedDescriptor);
            if (status != 0 || copiedDescriptor == 0 || !IsValidSecurityDescriptor(copiedDescriptor)) throw new IOException();
            uint length = GetSecurityDescriptorLength(copiedDescriptor);
            if (length is < 20 or > 4096) throw new IOException();
            byte[] bytes = new byte[length];
            try
            {
                Marshal.Copy(copiedDescriptor, bytes, 0, bytes.Length);
                var security = new RawSecurityDescriptor(bytes, 0);
                if (security.Owner?.Value != sid || (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 || security.DiscretionaryAcl is not { Count: 2 } acl)
                    throw new IOException();
                bool user = false, system = false;
                foreach (var entry in acl)
                {
                    if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed || ace.AccessMask != 0x001F0003 || ace.AceFlags != AceFlags.None) throw new IOException();
                    if (ace.SecurityIdentifier.Value == sid && !user) user = true;
                    else if (ace.SecurityIdentifier.Value == "S-1-5-18" && !system) system = true;
                    else throw new IOException();
                }
                if (!user || !system) throw new IOException();
            }
            finally { Array.Clear(bytes); }
            copiedFreed = true; if (LocalFree(copiedDescriptor) != 0) { uncertain = true; throw new IOException(); } copiedDescriptor = 0;
            creationFreed = true; if (LocalFree(creationDescriptor) != 0) { uncertain = true; throw new IOException(); } creationDescriptor = 0;
            Ready = true;
        }
        internal bool Signal(out int? error)
        {
            if (handle == 0 || handleClosed || uncertain) { error = null; return false; }
            bool returned = SetEvent(handle); error = returned ? null : Marshal.GetLastWin32Error(); return returned;
        }
        internal bool Release()
        {
            if (handle != 0 && !handleClosed) { handleClosed = true; if (!CloseHandle(handle)) uncertain = true; else handle = 0; }
            if (copiedDescriptor != 0 && !copiedFreed) { copiedFreed = true; if (LocalFree(copiedDescriptor) != 0) uncertain = true; else copiedDescriptor = 0; }
            if (creationDescriptor != 0 && !creationFreed) { creationFreed = true; if (LocalFree(creationDescriptor) != 0) uncertain = true; else creationDescriptor = 0; }
            Ready = false; return !uncertain;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal uint Length; internal nint Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateEventW(ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initial, string name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetEvent(nint handle);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint LocalFree(nint memory);
    [DllImport("advapi32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSecurityInfo(nint handle, uint objectType, uint information, nint owner, nint group, nint dacl, nint sacl, out nint descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsValidSecurityDescriptor(nint descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetSecurityDescriptorLength(nint descriptor);
}
