using System.Diagnostics;
using System.Security.Principal;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>A closed production boundary for separately selected, non-repair SFC verification.</summary>
public sealed class SfcVerificationProvider : ISfcVerificationProvider
{
    private static readonly object GlobalGate = new();
    private static readonly List<Session> Rooted = [];

    // No development entry point or configuration override. Before activation this
    // candidate also needs atomic shared servicing admission with DISM and the legacy
    // executor, cross-instance reconciliation, and independent Windows qualification.
    public bool ProductionExecutionValidated => false;
    public bool HasPendingWork { get { lock (GlobalGate) return Rooted.Any(owner => owner.HasPendingWork); } }
    public bool RequiresRetention { get { lock (GlobalGate) return Rooted.Any(owner => owner.RequiresRetention); } }

    public Task<ISfcVerificationSession> StartAsync(SfcVerificationIntent intent, CancellationToken prelaunchToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        // This must precede cancellation, platform/profile/token/native access,
        // queue admission and intent reservation. No SFC request is made in this build.
        if (!ProductionExecutionValidated)
            return Task.FromResult<ISfcVerificationSession>(new CompletedSession(intent,
                "SFC verification execution is not enabled in this build. No administrator query, executable identity check, elevation or client launch was requested."));
        return StartOwnedAsync(intent, prelaunchToken);
    }

    private static Task<ISfcVerificationSession> StartOwnedAsync(SfcVerificationIntent intent, CancellationToken token)
    {
        // Unreachable candidate only. This private local admission is deliberately
        // NOT represented as shared servicing exclusion or activation readiness.
        lock (GlobalGate)
        {
            Rooted.RemoveAll(owner => !owner.HasPendingWork && !owner.RequiresRetention);
            if (Rooted.Count != 0 || SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty)
                return Task.FromResult<ISfcVerificationSession>(new CompletedSession(intent,
                    "Earlier tool ownership is pending or uncertain. No additional SFC client was requested."));
            var owner = new Session(intent, token);
            var returned = Task.FromResult<ISfcVerificationSession>(owner);
            Rooted.EnsureCapacity(Rooted.Count + 1);
            Rooted.Add(owner);
            try { _ = Task.Run(owner.RunAsync); }
            catch { owner.BeforeWorkerFailed(); }
            return returned;
        }
    }

    private sealed class CompletedSession : ISfcVerificationSession
    {
        internal CompletedSession(SfcVerificationIntent intent, string message)
        {
            RequestId = intent.RequestId;
            var now = DateTimeOffset.UtcNow;
            Completion = Task.FromResult(new SfcVerificationResult(RequestId, CheckState.Unavailable, now, now, message,
                new() { ClientStartAttempted = false, ClientStartObserved = false, OwnershipReleased = true }));
        }
        public Guid RequestId { get; }
        public bool HasPendingWork => false;
        public bool RequiresRetention => false;
        public bool StopObservationRequested => false;
        public void RequestStopObservation() { }
        public Task<SfcVerificationResult> Completion { get; }
    }

    // Deliberately private: no test permission switch, fixture launch or other caller
    // can reach this candidate while the public immutable production gate is false.
    private sealed class Session(SfcVerificationIntent intent, CancellationToken token) : ISfcVerificationSession
    {
        private readonly object sync = new();
        private readonly TaskCompletionSource<SfcVerificationResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DateTimeOffset attempted = DateTimeOffset.UtcNow;
        private SystemToolTrustedLease? lease;
        private Process? child;
        private bool retained;
        private int stopState; // bit 1: request accepted; bit 2: publication boundary
        private bool? admin, startAttempted, started, exited, released;
        private int? processId, exitCode;
        private DateTimeOffset? startedUtc, exitedUtc;
        public Guid RequestId => intent.RequestId;
        public Task<SfcVerificationResult> Completion => completion.Task;
        public bool HasPendingWork => !completion.Task.IsCompleted;
        public bool RequiresRetention { get { lock (sync) return retained; } }
        public bool StopObservationRequested => (Volatile.Read(ref stopState) & 1) != 0;
        public void RequestStopObservation()
        {
            // Never take the lock held across native process creation or cleanup.
            while (true)
            {
                int observed = Volatile.Read(ref stopState);
                if ((observed & 2) != 0) return;
                if (Interlocked.CompareExchange(ref stopState, observed | 1, observed) == observed) return;
            }
        }
        private bool StopBeforeLaunch => token.IsCancellationRequested || StopObservationRequested || intent.IsInvalidated;

        internal void BeforeWorkerFailed()
        {
            lock (sync)
            {
                startAttempted = false; started = false; released = true;
                Publish("The owner could not be scheduled. No SFC client launch was attempted.");
            }
        }

        internal async Task RunAsync()
        {
            string? blocker = null;
            try
            {
                lock (sync) if (StopBeforeLaunch) { blocker = "Stopped before preparation. No SFC client was requested."; return; }
                if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                { blocker = "SFC verification requires a 64-bit Windows process. No client was requested."; return; }
                using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true))
                {
                    if (impersonated != null) { admin = false; blocker = "Impersonating callers are unsupported. No elevation or client was requested."; return; }
                }
                using (var current = WindowsIdentity.GetCurrent())
                    admin = new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator);
                if (admin != true) { blocker = "Already-effective administrator rights are required. No elevation or client was requested."; return; }
                lock (sync) if (StopBeforeLaunch) { blocker = "Stopped before executable qualification. No client was requested."; return; }
                lease = SystemToolTrustedLease.OpenSfc(token);
                if (!string.Equals(lease.ToolPath, intent.ToolPath, StringComparison.Ordinal) ||
                    !string.Equals(lease.ToolVersion, intent.ToolVersion, StringComparison.Ordinal) ||
                    !string.Equals(lease.Sha256, intent.Sha256, StringComparison.Ordinal) || lease.ToolVersion == "Unknown")
                { blocker = "The fresh held executable identity does not match this reviewed intent. Prepare a new preview; no client was requested."; return; }
                child = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = lease.ToolPath, UseShellExecute = false, CreateNoWindow = false,
                        WindowStyle = ProcessWindowStyle.Normal, WorkingDirectory = lease.SystemDirectory,
                        RedirectStandardInput = false, RedirectStandardOutput = false, RedirectStandardError = false
                    }
                };
                child.StartInfo.ArgumentList.Add("/verifyonly");
                ConfigureEnvironment(child.StartInfo, lease.SystemDirectory);
                lock (sync)
                {
                    if (StopBeforeLaunch || intent.TryReserveDispatch(DateTimeOffset.UtcNow, token) != SfcVerificationDispatchReservation.Reserved)
                    { blocker = "The reviewed intent was stopped, stale, invalidated or already reserved. No client was requested."; return; }
                    // Start may throw after possible dispatch: retain the exact Process
                    // and leases unless actual ownership/exit can be established.
                    startAttempted = true;
                    if (!child.Start()) { retained = true; return; }
                    started = true;
                    // Independent actual metadata observations. A failed getter must
                    // neither invent a timestamp nor lose ownership of this client.
                    try { startedUtc = new DateTimeOffset(child.StartTime.ToUniversalTime()); } catch { }
                    try { processId = child.Id; } catch { }
                }
                await ObserveActualExitAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { blocker = "Preparation was interrupted. No successful verification or SFC cancellation is claimed."; }
            catch (Exception) { /* Never export provider output, paths or exception text. */ }
            finally
            {
                if (started == true && exited != true && child != null)
                    await ObserveActualExitAsync().ConfigureAwait(false);
                lock (sync)
                {
                    if (startAttempted == null) { startAttempted = false; started = false; }
                    if (startAttempted == true && started != true || started == true && exited != true ||
                        SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty) retained = true;
                    if (!retained)
                    {
                        try { child?.Dispose(); child = null; lease?.Dispose(); lease = null; }
                        catch { retained = true; }
                    }
                    released = !retained;
                    Publish(blocker);
                }
                if (!RequiresRetention) { lock (GlobalGate) Rooted.Remove(this); }
            }
        }

        private async Task ObserveActualExitAsync()
        {
            // Observation stop never cancels this wait. Native observation failure
            // retains the original Process and every lease; no PID reopen or kill.
            while (exited != true)
            {
                try
                {
                    await child!.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    var code = child.ExitCode;
                    DateTimeOffset? actualExit = null;
                    try { actualExit = new DateTimeOffset(child.ExitTime.ToUniversalTime()); } catch { }
                    // Preserve the actual exit even if optional timing metadata is
                    // unavailable; never substitute the observation receipt clock.
                    lock (sync) { exited = true; exitCode = code; exitedUtc = actualExit; }
                }
                catch { lock (sync) retained = true; await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            }
        }

        private void Publish(string? blocker)
        {
            Interlocked.Or(ref stopState, 2);
            var transport = new SfcVerificationTransport
            {
                AdministratorObserved = admin, ClientStartAttempted = startAttempted, ClientStartObserved = started,
                ProcessId = processId, ClientStartedUtc = startedUtc, ClientExitObserved = exited,
                ClientExitCode = exitCode, ClientExitedUtc = exitedUtc, OwnershipReleased = released,
                StopObservationRequested = StopObservationRequested || token.IsCancellationRequested
            };
            var result = SfcVerificationPolicy.Assess(intent, transport, attempted, DateTimeOffset.UtcNow);
            if (blocker != null)
                result = result with { State = transport.StopObservationRequested ? CheckState.Cancelled : CheckState.Unavailable, Message = blocker };
            completion.TrySetResult(result);
        }
    }

    private static void ConfigureEnvironment(ProcessStartInfo info, string system)
    {
        // No inherited PATH, compatibility/profiler injection, shell settings,
        // credentials, user profile or user-controlled temporary directory.
        var windows = Path.GetDirectoryName(system) ?? throw new InvalidOperationException();
        info.Environment.Clear();
        info.Environment["SystemRoot"] = windows;
        info.Environment["windir"] = windows;
        info.Environment["PATH"] = system;
    }
}
