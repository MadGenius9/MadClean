using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>
/// Selected registered-product request, distinct from the read-only binding report.
/// A single owner retains native preparation and the actual started process until exit.
/// No process ID is reopened and no cancellation path terminates Windows Installer.
/// </summary>
public sealed class MsiUninstallRequestLauncher
{
    private static readonly object Gate = new();
    private static Owner? rootedOwner;
    private static Task<MsiUninstallRequestResult>? pending;
    private static int retained;

    // Source qualification changes only after independent owned Windows evidence and review.
    // There is no configuration, environment or UI switch for this production boundary.
    public static bool ProductionDispatchValidated => false;
    public static bool IsProductionContextQualified(MsiRegistrationContext context) =>
        ProductionDispatchValidated && context == MsiRegistrationContext.UserUnmanaged;
    public static bool HasPendingRequest { get { lock (Gate) return rootedOwner != null; } }
    public static bool RequiresRetention => Volatile.Read(ref retained) != 0;
    internal static Task<MsiUninstallRequestResult>? PendingCompletion { get { lock (Gate) return pending; } }

    public Task<MsiUninstallRequestResult> RequestAsync(MsiUninstallRequestIntent intent,
        bool userConfirmed, long sourceGeneration, CancellationToken token)
    {
        var requested = DateTimeOffset.UtcNow;
        if (!ProductionDispatchValidated)
            return Task.FromResult(Blocked(intent, requested,
                "Selected Windows Installer dispatch is not yet production-qualified. No identity query, process or uninstall was requested."));
        if (intent == null || !IsProductionContextQualified(intent.Context))
            return Task.FromResult(Blocked(intent, requested,
                "This Windows Installer context has not been separately qualified for dispatch. No installer started."));
        return Begin(intent, userConfirmed, sourceGeneration, requested, null, null, token);
    }

    internal Task<MsiUninstallRequestResult> RequestOwnedFixtureAsync(MsiUninstallRequestIntent intent,
        bool userConfirmed, long sourceGeneration, MsiUninstallFixtureAuthorization authorization,
        Func<MsiUninstallFixtureClient, Task>? interaction, CancellationToken token)
    {
        var requested = DateTimeOffset.UtcNow;
        if (authorization == null)
            return Task.FromResult(Blocked(intent, requested,
                "No exact owned qualification authorization was supplied. No installer started."));
        return Begin(intent, userConfirmed, sourceGeneration, requested, authorization, interaction, token);
    }

    private static Task<MsiUninstallRequestResult> Begin(MsiUninstallRequestIntent intent, bool confirmed,
        long generation, DateTimeOffset requested, MsiUninstallFixtureAuthorization? authorization,
        Func<MsiUninstallFixtureClient, Task>? interaction, CancellationToken token)
    {
        if (intent == null || !confirmed || token.IsCancellationRequested || intent.IsInvalidated || intent.IsDispatchReserved)
            return Task.FromResult(Blocked(intent, requested,
                "No current explicitly confirmed request is available. No installer started."));
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return Task.FromResult(Blocked(intent, requested, "Selected Windows Installer requests require supported 64-bit Windows. No installer started."));
        lock (Gate)
        {
            if (rootedOwner != null || RequiresRetention)
                return Task.FromResult(Blocked(intent, requested,
                    "A previous request or uncertain ownership remains. No additional request started."));
            // Allocate the owner before transferring a caller's proof: a construction
            // failure must leave that caller able to release its still-untransferred leases.
            var owner = new Owner(intent, confirmed, generation, requested, authorization, interaction, token);
            if (authorization != null && !authorization.TransferToWorker(intent))
                return Task.FromResult(Blocked(intent, requested,
                    "The exact owned qualification product was not established. No installer started."));
            rootedOwner = owner;
            try
            {
                var task = Task.Run(owner.RunAsync, CancellationToken.None);
                pending = task;
                _ = task.ContinueWith(value => { _ = value.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }
            catch
            {
                // Never abandon a possibly queued owner or release its proof from the caller.
                Volatile.Write(ref retained, 1);
                return Task.FromResult(new MsiUninstallRequestResult(intent.RequestId, MsiUninstallRequestState.MonitoringUnknown,
                    requested, null, null, null, null,
                    "Request ownership could not be scheduled conclusively. No process start or completion is claimed; another request is blocked."));
            }
        }
    }

    private static MsiUninstallRequestResult Blocked(MsiUninstallRequestIntent? intent, DateTimeOffset requested, string message) =>
        new(intent?.RequestId ?? Guid.Empty, MsiUninstallRequestState.Blocked, requested, null, null, null, null, message);

    private sealed class Owner(MsiUninstallRequestIntent intent, bool confirmed, long generation,
        DateTimeOffset requested, MsiUninstallFixtureAuthorization? authorization,
        Func<MsiUninstallFixtureClient, Task>? interaction, CancellationToken token)
    {
        private readonly WindowsMsiActionBindingInventory binding = new();
        private SystemToolTrustedLease? lease;
        private Process? process;
        private SafeProcessHandle? actualProcessHandle;
        private bool processStarted, processExitObserved, uncertain, startAttempted;
        private DateTimeOffset? startedUtc, exitedUtc;
        private int? processId, exitCode;
        private bool? clientStartObserved;
        private SystemToolIdentityDiagnostic? toolIdentity;

        internal async Task<MsiUninstallRequestResult> RunAsync()
        {
            MsiUninstallRequestResult result;
            try { result = await ExecuteAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (!intent.IsDispatchReserved)
            {
                intent.Invalidate();
                result = Blocked(intent, requested,
                    "Cancellation was observed before dispatch. No installer request was sent; any pending metadata remains owned until it returns.");
            }
            catch (SystemToolTrustFailure failure)
            {
                toolIdentity = failure.Diagnostic;
                string code = failure.Diagnostic.NativeTrustResult is { } value
                    ? "0x" + unchecked((uint)value).ToString("X8") : "unobserved";
                result = Blocked(intent, requested,
                    $"The fixed system Windows Installer executable could not be qualified (stage {failure.Diagnostic.Stage}, reason {failure.Diagnostic.Reason}, native trust result {code}). Review Windows Installer availability and Windows certificate-validation access, then retry; no fallback or installer request was used.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or
                InvalidOperationException or ArgumentException or System.Security.SecurityException or NotSupportedException)
            {
                if (processStarted || startAttempted && exception is not Win32Exception)
                {
                    uncertain = true;
                    result = Result(MsiUninstallRequestState.MonitoringUnknown,
                        "The owned request could not be observed conclusively. Windows Installer may continue; no cancellation, rollback or completed uninstall is claimed. Ownership is retained and another request is blocked.");
                }
                else result = Blocked(intent, requested,
                    "Preparation or the direct Windows process start failed. No alternate command, elevated caller, silent mode or retry was used; no completed uninstall is claimed.");
            }
            catch (Exception)
            {
                uncertain = true;
                result = Result(MsiUninstallRequestState.MonitoringUnknown,
                    "An unexpected request observation failed. No completion or cancellation is claimed; all request ownership remains retained.");
            }

            // Diagnostics are actual observations and survive later cleanup uncertainty.
            result = result with { ToolIdentity = toolIdentity, ClientStartObserved = clientStartObserved };
            if (SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty)
                uncertain = true;
            // A bounded metadata caller may finish before its synchronous native worker.
            // Its lease remains independent; never start another request or close beneath it.
            while (!uncertain && binding.HasPendingNativeRead)
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            if (processStarted && !processExitObserved) uncertain = true;
            if (!uncertain)
            {
                try
                {
                    process?.Dispose(); actualProcessHandle = null;
                    lease?.Dispose(); lease = null;
                    authorization?.ReleaseFromWorker();
                }
                catch (Exception) { uncertain = true; }
            }
            lock (Gate)
            {
                if (uncertain) Volatile.Write(ref retained, 1);
                else if (ReferenceEquals(rootedOwner, this)) { rootedOwner = null; pending = null; }
            }
            return uncertain ? result with
            {
                State = MsiUninstallRequestState.MonitoringUnknown,
                Message = "Actual client facts shown separately remain historical. Request/resource ownership is uncertain and retained; another request is blocked. Do not infer cancellation, uninstall completion, rollback or a safe restart."
            } : result;
        }

        private async Task<MsiUninstallRequestResult> ExecuteAsync()
        {
            token.ThrowIfCancellationRequested();
            if (Impersonating()) return Blocked(intent, requested,
                "An impersonated caller is unsupported. Windows Installer must be requested as the same current caller; no alternate credentials were used.");
            if (intent.IsInvalidated || intent.IsDispatchReserved || generation != intent.SourceGeneration)
                return Blocked(intent, requested, "The selected request changed before preparation. No installer started.");
            lease = SystemToolTrustedLease.OpenWindowsInstaller(token);
            toolIdentity = lease.SignatureDiagnostic with
            {
                Stage = SystemToolIdentityStage.Completed, Reason = SystemToolIdentityReason.Observed,
                EmbeddedVersionState = lease.EmbeddedVersionObservation.State,
                EmbeddedVersionResourceCount = lease.EmbeddedVersionObservation.ResourceCount,
                EmbeddedOriginalNameCount = lease.EmbeddedVersionObservation.OriginalFilenameCount,
                EmbeddedVersionFailureSite = lease.EmbeddedVersionObservation.FailureSite,
                DeclaredRootLength = lease.EmbeddedVersionObservation.DeclaredRootLength,
                ResourceBlobLength = lease.EmbeddedVersionObservation.ResourceBlobLength,
                RootTrailingBytesZero = lease.EmbeddedVersionObservation.RootTrailingBytesZero
            };
            if (lease.EmbeddedVersionObservation.State != SystemToolEmbeddedVersionState.Matched ||
                string.IsNullOrWhiteSpace(lease.ToolVersion) || lease.ToolVersion == "Unknown" ||
                !SystemToolActionsPolicy.ValidHash(lease.Sha256))
                return Blocked(intent, requested,
                    "The held fixed Windows Installer identity is incomplete. No fallback or installer request was used.");
            token.ThrowIfCancellationRequested();
            var fresh = await binding.VerifyAsync(intent.Scope, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (binding.HasPendingNativeRead || MsiUninstallRequestPolicy.ValidateFreshBinding(intent, fresh, DateTimeOffset.UtcNow, token).Count != 0)
                return Blocked(intent, requested,
                    "Fresh complete current-user/machine selection evidence did not qualify one unchanged unrestricted installed context. No installer started; refresh and review the selected registration again.");
            if (Impersonating()) return Blocked(intent, requested,
                "The caller context changed before dispatch. No installer started.");
            if (!Guid.TryParseExact(intent.ProductCode, "B", out var code) || code == Guid.Empty ||
                code.ToString("B").ToUpperInvariant() != intent.ProductCode ||
                !intent.Arguments.SequenceEqual(new[] { "/x", intent.ProductCode, "/norestart" }))
                return Blocked(intent, requested, "The fixed ProductCode request descriptor was inconsistent. No installer started.");
            var start = new ProcessStartInfo
            {
                FileName = lease.ToolPath, WorkingDirectory = lease.SystemDirectory,
                UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Normal,
                ErrorDialog = false
            };
            start.ArgumentList.Add("/x"); start.ArgumentList.Add(intent.ProductCode); start.ArgumentList.Add("/norestart");
            process = new Process { StartInfo = start };
            var reservation = intent.TryReserveDispatch(confirmed, generation, DateTimeOffset.UtcNow, token);
            if (reservation != MsiUninstallDispatchReservation.Reserved)
                return Blocked(intent, requested,
                    "The one-shot request was not committed at the final dispatch boundary. No installer started.");
            // No await, shell, user input, additional query or cancellation claim separates
            // reservation from this exact direct process-start attempt.
            startAttempted = true;
            bool returnedStarted = process.Start();
            clientStartObserved = returnedStarted;
            if (!returnedStarted) return Blocked(intent, requested,
                "The direct process start returned no new client. No alternate request or retry was used.");
            processStarted = true;
            actualProcessHandle = process.SafeHandle;
            if (actualProcessHandle.IsInvalid || actualProcessHandle.IsClosed) throw new InvalidOperationException();
            try { processId = process.Id; } catch (Exception) { }
            try { startedUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); } catch (Exception) { }
            bool interactionFailed = false;
            if (interaction != null)
            {
                try { await interaction(new MsiUninstallFixtureClient(process)).ConfigureAwait(false); }
                catch (Exception) { interactionFailed = true; }
            }
            // User cancellation after the committed boundary belongs to the visible Windows
            // Installer UI. A token, timeout or closing a wait never terminates its process.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (!process.HasExited) throw new InvalidOperationException();
            processExitObserved = true; exitedUtc = DateTimeOffset.UtcNow;
            try { exitCode = process.ExitCode; } catch (Exception) { }
            var result = Result(MsiUninstallRequestState.ClientExited,
                interactionFailed
                    ? "The actual Windows Installer client exited, but the owned qualification interaction did not complete. No positive qualification or completed uninstall is inferred. Refresh registration evidence separately."
                    : "The actual Windows Installer client exited. Its code describes the client/engine result only; package-defined effects, current registration absence, leftovers and completed uninstall remain separately unverified. Refresh registrations explicitly. No restart was requested by MadClean.");
            return result with { ExitDisposition = exitCode is { } value ? MsiUninstallRequestPolicy.ClassifyExit(value) : null };
        }
        private MsiUninstallRequestResult Result(MsiUninstallRequestState state, string message) =>
            new(intent.RequestId, state, requested, startedUtc, exitedUtc, processId, exitCode, message);
        private static bool Impersonating()
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            return identity != null;
        }
    }
}

/// <summary>Borrowed actual-client facts for the exact owned fixture; never an executable/PID reopening API.</summary>
internal sealed class MsiUninstallFixtureClient
{
    private readonly Process process;
    internal MsiUninstallFixtureClient(Process process) => this.process = process;
    internal int? ProcessId { get { try { return process.Id; } catch (Exception) { return null; } } }
    internal bool? HasExited { get { try { return process.HasExited; } catch (Exception) { return null; } } }
    internal bool TryInvokeIfRunning(Action action)
    {
        if (HasExited != false) return false;
        action(); return true;
    }
}
