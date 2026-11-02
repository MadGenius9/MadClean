using System.Diagnostics;
using System.IO;
using MadClean.Core;
using MadClean.Windows;
using Microsoft.Win32.SafeHandles;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool SystemToolProcessObservationCleanupConfirmed { get; set; } = true;
    // Bounded retained ownership only when actual exit cannot be established. No disposal/closed
    // handle is used as proof that a child stopped, and no second fixture invocation is permitted.
    private static readonly List<Process> UnconfirmedInertChildren = [];
    private static InertApphostLease? UnconfirmedInertApphost;

    private static void SystemToolProcessObservationFixture(string fixture, string marker, SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-process-observation-fixture";
        var children = new List<InertOwnedChild>(4);
        InertApphostLease? apphost = null;
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        bool zero = false, nonzero = false, stoppedBefore = false, stoppedLive = false, exitedWithStop = false, invalidUnavailable = false;
        bool participantSetupAttempted = false;
        bool previousUnconfirmed = UnconfirmedInertChildren.Count != 0 || UnconfirmedInertApphost != null;
        string stage = "initializing";
        try
        {
            token.ThrowIfCancellationRequested();
            if (UnconfirmedInertChildren.Count != 0 || UnconfirmedInertApphost != null || !InertProcessChild.IsValidFixture(repository, fixture, marker))
                throw new InertObservationUnavailable("owned-fixture-or-previous-child-unavailable");
            apphost = InertApphostLease.Open();
            SystemToolProcessObservationCleanupConfirmed = false;
            participantSetupAttempted = true;
            var first = StartInert("zero", repository, fixture, marker, apphost.Path, children);
            WaitInertReady(first, token);
            var observedZero = SystemToolProcessObserver.ObserveAsync(first.Process, token).GetAwaiter().GetResult();
            RequireInertExit(first, observedZero, 0); zero = true;
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                var exited = SystemToolProcessObserver.ObserveAsync(first.Process, stop.Token).GetAwaiter().GetResult();
                RequireInertExit(first, exited, 0);
                if (!exited.StopRequested) throw new SmokeAssertionException("inert-exited-stop-request-not-preserved");
                exitedWithStop = true;
            }
            var second = StartInert("nonzero", repository, fixture, marker, apphost.Path, children);
            WaitInertReady(second, token);
            var observedNonzero = SystemToolProcessObserver.ObserveAsync(second.Process, token).GetAwaiter().GetResult();
            RequireInertExit(second, observedNonzero, 23); nonzero = true;

            var pre = StartInert("wait", repository, fixture, marker, apphost.Path, children);
            WaitInertReady(pre, token);
            if (pre.Process.HasExited) throw new InertObservationUnavailable("pre-cancel-child-exited-before-observation");
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                var stopped = SystemToolProcessObserver.ObserveAsync(pre.Process, stop.Token).GetAwaiter().GetResult();
                RequireInertStopped(pre, stopped); stoppedBefore = true;
            }
            ReleaseInert(pre);
            if (!pre.Process.WaitForExit(12_000)) throw new InertObservationUnavailable("pre-cancel-child-exit-unconfirmed");
            if (pre.Process.ExitCode != 0) throw new InertObservationUnavailable("pre-cancel-child-self-deadline-or-invalid-signal");

            token.ThrowIfCancellationRequested();
            var live = StartInert("wait", repository, fixture, marker, apphost.Path, children);
            WaitInertReady(live, token);
            if (live.Process.HasExited) throw new InertObservationUnavailable("live-stop-child-exited-before-observation");
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                stop.CancelAfter(TimeSpan.FromMilliseconds(150));
                var stopped = SystemToolProcessObserver.ObserveAsync(live.Process, stop.Token).GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                RequireInertStopped(live, stopped); stoppedLive = true;
            }
            ReleaseInert(live);
            if (!live.Process.WaitForExit(12_000)) throw new InertObservationUnavailable("live-stop-child-exit-unconfirmed");
            if (live.Process.ExitCode != 0) throw new InertObservationUnavailable("live-stop-child-self-deadline-or-invalid-signal");
            using (var neverStarted = new Process())
            {
                var unavailable = SystemToolProcessObserver.ObserveAsync(neverStarted, token).GetAwaiter().GetResult();
                if (unavailable.State != ClientProcessObservationState.Unavailable || unavailable.ExitCode != null || unavailable.ObservedProcessId != null)
                    throw new SmokeAssertionException("inert-unstarted-process-unavailable");
                invalidUnavailable = true;
            }
            stage = "inert-cases-observed";
        }
        catch (SmokeAssertionException ex)
        {
            report.Fail(check, "Inert borrowed-process invariant " + ex.Code + " failed; no system servicing or tool health is validated.");
            stage = "fixed-invariant-failed";
        }
        catch (Exception ex) when (ex is InertObservationUnavailable or IOException or UnauthorizedAccessException or InvalidOperationException or
            OperationCanceledException or System.ComponentModel.Win32Exception or ArgumentException or System.Security.SecurityException or SystemToolTrustFailure)
        { stage = ex is InertObservationUnavailable safe ? safe.Stage : "inert-prerequisite-or-observation-unavailable"; }
        finally
        {
            bool exitedAndAudited = !previousUnconfirmed && (!participantSetupAttempted || children.Count > 0);
            foreach (var child in children)
            {
                bool exited = false;
                try
                {
                    // Parent cancellation stops observation, not child ownership. A fixed owned
                    // release and bounded wait allow natural exit; there is never a Kill call.
                    if (!child.Process.HasExited && child.Mode == "wait") ReleaseInert(child);
                    exited = child.Process.WaitForExit(12_000) && child.Process.HasExited;
                    if (!exited || !AuditInertChild(repository, fixture, marker, child)) exitedAndAudited = false;
                }
                catch (Exception) { exitedAndAudited = false; }
                if (exited)
                {
                    try { child.Process.Dispose(); }
                    catch (Exception) { exitedAndAudited = false; if (UnconfirmedInertChildren.Count < 4) UnconfirmedInertChildren.Add(child.Process); }
                }
                else if (UnconfirmedInertChildren.Count < 4) UnconfirmedInertChildren.Add(child.Process);
            }
            if (exitedAndAudited)
            {
                try { apphost?.Dispose(); apphost = null; }
                catch (Exception) { exitedAndAudited = false; }
            }
            SystemToolProcessObservationCleanupConfirmed = exitedAndAudited;
            if (!exitedAndAudited && apphost != null) { UnconfirmedInertApphost = apphost; apphost = null; }
        }
        bool complete = zero && nonzero && stoppedBefore && stoppedLive && exitedWithStop && invalidUnavailable && SystemToolProcessObservationCleanupConfirmed;
        if (complete)
            report.Pass(check, "inertChildren=4; actualExit0=true; actualExit23=true; precancelStoppedWhileAlive=true; liveStopWhileAlive=true; releaseThenExit0=true; exitedWithStopPreserved=true; borrowedOwnershipRetained=true; invalidProcessUnavailable=true; allActualExitsAndOwnedFilesAudited=true. This tests observation of this existing harness's fixed inert child modes only: no servicing executable, UAC, repair, installation, forced termination or tool-health conclusion occurred.");
        else if (stage != "fixed-invariant-failed")
            report.Unknown(check, $"stage={stage}; actualExit0={zero}; actualExit23={nonzero}; precancelAliveObserved={stoppedBefore}; liveStopAliveObserved={stoppedLive}; exitedWithStopPreserved={exitedWithStop}; invalidProcessUnavailable={invalidUnavailable}; cleanupConfirmed={SystemToolProcessObservationCleanupConfirmed}. No observer-stop was treated as process exit; retain the complete marked fixture tree if any exit or ownership audit is unconfirmed.");
    }

    private sealed class InertObservationUnavailable(string stage) : Exception("Inert process observation is incomplete.")
    { internal string Stage { get; } = stage; }
    private sealed record InertOwnedChild(Process Process, string Directory, string Mode, string Repository, string Owner);
    private static InertOwnedChild StartInert(string mode, string repository, string fixture, string marker,
        string apphost, List<InertOwnedChild> children, string? preparedChild = null)
    {
        if (children.Count >= 4 || mode is not ("zero" or "nonzero" or "wait" or "journal-contention" or "journal-read") || !InertProcessChild.IsValidFixture(repository, fixture, marker))
            throw new InertObservationUnavailable("inert-start-scope-invalid");
        string child = preparedChild ?? PrepareInertChild(repository, fixture, marker);
        if (!InertProcessChild.IsValidChild(repository, child, marker, requireOwnerOnly: true) ||
            !string.Equals(Path.GetDirectoryName(child), fixture, StringComparison.OrdinalIgnoreCase))
            throw new InertObservationUnavailable("inert-child-ownership-invalid");
        var info = new ProcessStartInfo(apphost) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repository };
        info.ArgumentList.Add(InertProcessChild.Switch); info.ArgumentList.Add(mode); info.ArgumentList.Add(child); info.ArgumentList.Add(marker);
        var process = new Process { StartInfo = info };
        var result = new InertOwnedChild(process, child, mode, repository, marker);
        children.Add(result); // Retain ownership even if Start throws with an uncertain association.
        if (!process.Start()) throw new InertObservationUnavailable("inert-start-not-established");
        return result;
    }
    private static string PrepareInertChild(string repository, string fixture, string marker)
    {
        if (!InertProcessChild.IsValidFixture(repository, fixture, marker)) throw new InertObservationUnavailable("inert-prepare-scope-invalid");
        string child = Path.Combine(fixture, "inert-process-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(child)) throw new InertObservationUnavailable("inert-child-already-existed");
        Directory.CreateDirectory(child);
        InertProcessChild.WriteNew(Path.Combine(child, InertProcessChild.OwnerName), marker);
        if (!InertProcessChild.IsValidChild(repository, child, marker, requireOwnerOnly: true))
            throw new InertObservationUnavailable("inert-child-ownership-invalid");
        return child;
    }
    private static void WaitInertReady(InertOwnedChild child, CancellationToken token)
    {
        var budget = Stopwatch.StartNew();
        string path = Path.Combine(child.Directory, InertProcessChild.ReadyName);
        while (budget.Elapsed < TimeSpan.FromSeconds(5))
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try { if (InertProcessChild.ReadExact(path, "ready")) return; }
                catch (IOException) { } // The child's CreateNew writer may still be closing.
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 32 or 33) { }
            }
            if (child.Process.HasExited) throw new InertObservationUnavailable("inert-child-exited-without-ready");
            Thread.Sleep(20);
        }
        throw new InertObservationUnavailable("inert-ready-not-observed");
    }
    private static void RequireInertExit(InertOwnedChild child, ClientProcessObservation observation, int expected)
    {
        if (observation.State != ClientProcessObservationState.Exited) throw new InertObservationUnavailable("actual-client-exit-unavailable");
        if (observation.State != ClientProcessObservationState.Exited || observation.Reason != ClientProcessObservationReason.ClientExited ||
            observation.ExitCode != expected || observation.ObservedProcessId != child.Process.Id ||
            !child.Process.HasExited || child.Process.ExitCode != expected || child.Process.SafeHandle.IsClosed ||
            observation.ObservationStartedUtc == null || observation.ObservationCompletedUtc == null)
            throw new SmokeAssertionException("inert-actual-exit-contract");
    }
    private static void RequireInertStopped(InertOwnedChild child, ClientProcessObservation observation)
    {
        if (observation.State == ClientProcessObservationState.Exited || child.Process.HasExited)
            throw new InertObservationUnavailable("inert-self-exit-raced-stop-observation");
        if (observation.State != ClientProcessObservationState.ObservationStopped || observation.Reason != ClientProcessObservationReason.StopRequested ||
            !observation.StopRequested || observation.ExitCode != null || observation.ObservedProcessId != child.Process.Id || child.Process.SafeHandle.IsClosed)
            throw new SmokeAssertionException("inert-stop-observation-contract");
    }
    private static void ReleaseInert(InertOwnedChild child)
    {
        if (!InertProcessChild.IsValidChild(child.Repository, child.Directory, child.Owner))
            throw new InertObservationUnavailable("inert-release-ownership-invalid");
        string path = Path.Combine(child.Directory, InertProcessChild.ReleaseName);
        if (File.Exists(path))
        {
            if (!InertProcessChild.ReadExact(path, "release")) throw new InertObservationUnavailable("inert-release-marker-invalid");
            return;
        }
        InertProcessChild.WriteNew(path, "release");
    }
    private static bool AuditInertChild(string repository, string fixture, string marker, InertOwnedChild child) =>
        InertProcessChild.IsValidFixture(repository, fixture, marker) && InertProcessChild.IsValidChild(repository, child.Directory, marker) &&
        InertProcessChild.ReadExact(Path.Combine(child.Directory, InertProcessChild.ReadyName), "ready") &&
        Directory.EnumerateFileSystemEntries(child.Directory).Count() == (child.Mode == "wait" ? 3 : 2) &&
        (child.Mode == "wait" ? InertProcessChild.ReadExact(Path.Combine(child.Directory, InertProcessChild.ReleaseName), "release")
            : !File.Exists(Path.Combine(child.Directory, InertProcessChild.ReleaseName)));

    private sealed class InertApphostLease : IDisposable
    {
        internal string Path { get; }
        private readonly List<SafeFileHandle> directories;
        private readonly SafeFileHandle file;
        private InertApphostLease(string path, List<SafeFileHandle> directories, SafeFileHandle file)
        { Path = path; this.directories = directories; this.file = file; }
        internal static InertApphostLease Open()
        {
            string expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "MadClean.WindowsSmoke.exe"));
            if (!string.Equals(Environment.ProcessPath, expected, StringComparison.OrdinalIgnoreCase))
                throw new InertObservationUnavailable("exact-current-smoke-apphost-unavailable");
            var directories = SystemToolTrustNative.LockDirectoryChain(System.IO.Path.GetDirectoryName(expected)!);
            SafeFileHandle? file = null;
            try
            {
                file = SystemToolTrustNative.OpenFile(expected);
                SystemToolTrustNative.ValidateAttributes(file, false); SystemToolTrustNative.ValidateFinalPath(file, expected);
                if (RandomAccess.GetLength(file) is <= 0 or > 67108864) throw new InertObservationUnavailable("inert-apphost-size-unavailable");
                return new(expected, directories, file);
            }
            catch { file?.Dispose(); foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose(); throw; }
        }
        public void Dispose() { file.Dispose(); foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose(); }
    }
}
