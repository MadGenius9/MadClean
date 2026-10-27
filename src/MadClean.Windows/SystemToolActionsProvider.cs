using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit identity previews; the production launch gate is deliberately closed and has no runtime override.</summary>
public sealed class SystemToolActionsProvider : ISystemToolActions
{
    private static readonly SemaphoreSlim IdentityWorker = new(1, 1);
    private static readonly SemaphoreSlim ActionWorker = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SystemToolActionPreview> previews = new();
    private static volatile bool monitoringBecameUncertain;
    public bool ProductionExecutionValidated => false;

    public async Task<CheckResult<SystemToolActionPreview>> PrepareAsync(SystemToolOperation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var definition = SystemToolActionsCatalog.Find(operation);
        if (definition == null) return CheckResult<SystemToolActionPreview>.Empty(CheckState.Unsupported, "This system-tool operation is not in the fixed catalogue.");
        if (!OperatingSystem.IsWindows()) return PreviewFailure(definition, CheckState.Unsupported, SystemToolTrustState.Unavailable,
            "Windows identity checks were not attempted on this platform. Execution remains disabled.");
        if (!IdentityWorker.Wait(0)) return PreviewFailure(definition, CheckState.Unavailable, SystemToolTrustState.Unknown,
            "A previous native signature check is still finishing. No additional identity worker or tool process was started; retry after it returns.");
        var work = Task.Run(() =>
        {
            try
            {
                using var identity = SystemToolTrustedLease.Open(definition, token);
                token.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                var preview = new SystemToolActionPreview(Guid.NewGuid(), definition, identity.SystemDirectory, identity.ToolPath,
                    identity.ToolVersion, identity.Sha256, SystemToolTrustState.MicrosoftSignatureVerified, identity.SignatureDetail,
                    now, now.AddMinutes(2), ProductionExecutionValidated && identity.ToolVersion != "Unknown",
                    "Production execution is disabled pending separate disposable-Windows and manual validation. Checking a signature does not run DISM/SFC or establish system health.");
                return new CheckResult<SystemToolActionPreview>(identity.ToolVersion == "Unknown" ? CheckState.Partial : CheckState.Available,
                    "Read-only tool identity preview obtained. No servicing command, elevation, repair or restart was requested.", [preview]);
            }
            catch (SystemToolTrustFailure failure) { return PreviewFailure(definition, failure.State, failure.Trust, failure.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or
                System.Security.SecurityException or System.Security.Cryptography.CryptographicException or System.Formats.Asn1.AsnContentException)
            {
                return PreviewFailure(definition, CheckState.Unavailable, SystemToolTrustState.Unknown,
                    $"System-tool identity could not be established (code 0x{ex.HResult:X8}). No validation was bypassed and no tool was launched.");
            }
            finally { IdentityWorker.Release(); }
        }, CancellationToken.None);
        // Cancellation/timeout bounds the caller, not WinVerifyTrust's in-flight native network work.
        // The worker retains and eventually releases its file leases and singleton slot itself.
        _ = work.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        CheckResult<SystemToolActionPreview> result;
        try { result = await work.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return PreviewFailure(definition, CheckState.Unavailable, SystemToolTrustState.Unknown,
                "The identity-check wait timed out. A native signature/revocation check may still finish; its leases and singleton worker remain owned until it returns. No servicing tool was started.");
        }
        token.ThrowIfCancellationRequested();
        foreach (var pair in previews.Where(pair => pair.Value.ExpiresUtc <= DateTimeOffset.UtcNow)) previews.TryRemove(pair.Key, out _);
        foreach (var preview in result.Items) previews[preview.PreviewId] = preview;
        while (previews.Count > 8) previews.TryRemove(previews.OrderBy(pair => pair.Value.CheckedUtc).First().Key, out _);
        return result;
    }

    public async Task<SystemToolRunRecord> LaunchAsync(SystemToolLaunchRequest request, CancellationToken observationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = SystemToolActionsCatalog.Find(request.Operation);
        // This read-only property is deliberately not configurable or injectable.
        // No native identity/elevation/process/log operation is reached through today's production API.
        if (!ProductionExecutionValidated)
            return NotStarted(request.Operation, "Production execution is disabled. No environment flag, consent checkbox or request descriptor enables it.");
        if (!OperatingSystem.IsWindows() || definition == null || !previews.TryRemove(request.PreviewId, out var preview))
            return NotStarted(request.Operation, "A fresh supported Windows identity preview is required; no tool was launched.");
        var reasons = SystemToolActionsPolicy.ValidateLaunch(preview, request, DateTimeOffset.UtcNow, ProductionExecutionValidated);
        if (reasons.Count != 0) return NotStarted(request.Operation, string.Join(" ", reasons));
        if (observationToken.IsCancellationRequested) return NotStarted(request.Operation, "The request was cancelled before launch; no tool was started.");
        if (monitoringBecameUncertain || !ActionWorker.Wait(0)) return NotStarted(request.Operation,
            "Another system operation is active or its monitoring became uncertain. Do not start overlapping servicing; inspect Windows and its logs first.");
        try
        {
            if (monitoringBecameUncertain) return NotStarted(request.Operation,
                "A preceding operation lost monitoring; inspect Windows and its logs before any further servicing.");
            return await Task.Run(() => RunValidatedFutureOperation(definition, preview, request, observationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally { ActionWorker.Release(); }
    }

    // This path is compiled for review but is unreachable while ProductionExecutionValidated is false.
    // Enabling it requires another code review and Windows runtime gates, not a runtime setting.
    private static async Task<SystemToolRunRecord> RunValidatedFutureOperation(SystemToolActionDefinition definition,
        SystemToolActionPreview preview, SystemToolLaunchRequest request, CancellationToken observationToken)
    {
        var id = Guid.NewGuid(); var started = DateTimeOffset.UtcNow;
        var record = new SystemToolRunRecord(id, request.Operation, SystemToolRunState.NotStarted, started, null, null, null,
            false, false, "Exact selected launch preparation; no client started yet.", definition.WindowsLogReference);
        try
        {
            observationToken.ThrowIfCancellationRequested();
            using var identity = SystemToolTrustedLease.Open(definition, observationToken);
            if (identity.Sha256 != preview.Sha256 || identity.ToolPath != preview.ToolPath || identity.ToolVersion != preview.ToolVersion)
                return record with { State = SystemToolRunState.Unavailable, EndedUtc = DateTimeOffset.UtcNow, Outcome = "Tool identity changed after preview; no launch was requested. Verify and review again." };
            using var log = SystemToolActionPrivateLog.Create(id);
            log.Append(record, identity);
            observationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(identity.ToolPath)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = identity.SystemDirectory,
                WindowStyle = ProcessWindowStyle.Normal, CreateNoWindow = false, ErrorDialog = false
            };
            foreach (string argument in definition.Arguments) start.ArgumentList.Add(argument);
            record = record with { ElevationRequested = true, Outcome = "Windows was asked for visible consent to start the exact selected Microsoft tool. This is not proof that a UAC prompt appeared or an operation completed." };
            log.Append(record, identity);
            Process? process;
            try { process = Process.Start(start); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                record = record with { State = SystemToolRunState.ConsentDenied, EndedUtc = DateTimeOffset.UtcNow, Outcome = "Windows reported cancellation of the elevation/launch consent request. No servicing completion is claimed." };
                log.Append(record, identity); return record;
            }
            if (process == null)
            {
                monitoringBecameUncertain = true;
                record = record with { State = SystemToolRunState.MonitoringLost, EndedUtc = DateTimeOffset.UtcNow, Outcome = SystemToolActionsPolicy.DescribeLostMonitoring() };
                log.Append(record, identity); return record;
            }
            using (process)
            {
                record = record with { State = SystemToolRunState.Launched, ProcessId = process.Id, Outcome = "A servicing client process was observed. No integrity or repair assessment has been obtained." };
                log.Append(record, identity);
                try
                {
                    await process.WaitForExitAsync(observationToken).ConfigureAwait(false);
                    record = record with { State = SystemToolRunState.ClientExited, EndedUtc = DateTimeOffset.UtcNow,
                        ClientExitCode = process.ExitCode, Outcome = SystemToolActionsPolicy.DescribeClientExit(request.Operation, process.ExitCode) };
                }
                catch (OperationCanceledException)
                {
                    monitoringBecameUncertain = true;
                    record = record with { State = SystemToolRunState.MonitoringLost, StopMonitoringRequested = true,
                        EndedUtc = DateTimeOffset.UtcNow, Outcome = SystemToolActionsPolicy.DescribeLostMonitoring() };
                }
                log.Append(record, identity);
            }
            return record;
        }
        catch (OperationCanceledException)
        {
            return record with { EndedUtc = DateTimeOffset.UtcNow, Outcome = "Cancelled before a servicing client was confirmed. No tool cancellation, rollback or healthy state is claimed." };
        }
        catch (Exception ex) when (ex is SystemToolTrustFailure or IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException or System.Formats.Asn1.AsnContentException)
        {
            if (record.ElevationRequested) monitoringBecameUncertain = true;
            return record with { State = record.ElevationRequested ? SystemToolRunState.MonitoringLost : SystemToolRunState.Unavailable,
                EndedUtc = DateTimeOffset.UtcNow,
                Outcome = record.ElevationRequested ? SystemToolActionsPolicy.DescribeLostMonitoring() : $"Preparation/logging/identity failed (code 0x{ex.HResult:X8}); no launch was requested." };
        }
    }
    private static CheckResult<SystemToolActionPreview> PreviewFailure(SystemToolActionDefinition definition, CheckState state,
        SystemToolTrustState trust, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        return new(state, reason, [new(Guid.NewGuid(), definition, "Unknown", "Unknown", "Unknown", "", trust,
            reason, now, now.AddMinutes(2), false, "Execution remains disabled; an unchecked or failed identity is never permission to run a system tool.")]);
    }
    private static SystemToolRunRecord NotStarted(SystemToolOperation operation, string reason) => new(Guid.NewGuid(), operation,
        SystemToolRunState.Unavailable, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, false, false, reason,
        SystemToolActionsCatalog.Find(operation)?.WindowsLogReference ?? "Unknown operation; no log source selected.");
}

internal sealed class SystemToolActionPrivateLog : IDisposable
{
    private readonly FileStream file;
    private readonly List<Microsoft.Win32.SafeHandles.SafeFileHandle> directories;
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    private SystemToolActionPrivateLog(FileStream file, List<Microsoft.Win32.SafeHandles.SafeFileHandle> directories)
    { this.file = file; this.directories = directories; }
    internal static SystemToolActionPrivateLog Create(Guid executionId)
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(root)) throw new IOException("A fixed local private log directory is required.");
        List<Microsoft.Win32.SafeHandles.SafeFileHandle> guards = [];
        try
        {
            guards.AddRange(SystemToolTrustNative.LockDirectoryChain(root));
            foreach (string part in new[] { "MadClean", "system-tool-actions" })
            {
                root = Path.Combine(root, part); Directory.CreateDirectory(root);
                guards.AddRange(SystemToolTrustNative.LockDirectoryChain(root));
            }
            if (Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly).Take(129).Count() >= 128)
                throw new IOException("Private execution-record retention reached its 128-file bound; review/archive records manually before another launch.");
            // No overwrite, selected destination or fallback outside the private application folder.
            var file = new FileStream(Path.Combine(root, executionId.ToString("N") + ".jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            return new(file, guards);
        }
        catch { foreach (var guard in guards.AsEnumerable().Reverse()) guard.Dispose(); throw; }
    }
    internal void Append(SystemToolRunRecord record, SystemToolTrustedLease identity)
    {
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1, record,
            identity = new { identity.ToolPath, identity.ToolVersion, identity.Sha256 },
            arguments = SystemToolActionsCatalog.Find(record.Operation)?.Arguments,
            outputCapture = "Not captured: visible elevated console. Inspect actual protected Windows log. No integrity/repair verdict is inferred."
        }, Options);
        if (line.Length > 16_384 || file.Length + line.Length + 1 > 131_072)
            throw new IOException("The bounded execution metadata record is full.");
        file.Write(line); file.WriteByte((byte)'\n'); file.Flush(flushToDisk: true);
    }
    public void Dispose()
    {
        file.Dispose();
        foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
    }
}
