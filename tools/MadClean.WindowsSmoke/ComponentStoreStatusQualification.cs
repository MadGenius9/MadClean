using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

// Separate explicit non-repair qualification. Ordinary inventory never calls this mode.
internal static class ComponentStoreStatusQualification
{
    private static Owner? rootedOwner;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed class Report
    {
        public int FormatVersion { get; } = 2;
        public required string SourceCommit { get; init; }
        public required string RunId { get; init; }
        public required int RunAttempt { get; init; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset ObservedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "inconclusive";
        public string Stage { get; set; } = "OwnedSetup";
        public bool? AdministratorObserved { get; set; }
        public bool? PrecancelNoHostObserved { get; set; }
        public string? PrecancelState { get; set; }
        public bool? PrecancelAuthorizationTransferred { get; set; }
        public bool? CandidateAuthorizationTransferred { get; set; }
        public bool? CandidateAuthorizationReleased { get; set; }
        public string? ResultState { get; set; }
        public string? RecordedFlag { get; set; }
        public bool? RequestMatched { get; set; }
        public bool? TransportConsistent { get; set; }
        public bool? EvidenceConsistent { get; set; }
        public bool? SessionStopRequested { get; set; }
        public bool? SessionPending { get; set; }
        public bool? ProviderPending { get; set; }
        public bool? SessionRetention { get; set; }
        public bool? ProviderRetention { get; set; }
        public TransportFacts? Transport { get; set; }
        public EvidenceFacts? Evidence { get; set; }
        public UiFacts? Ui { get; set; }
        public bool? OwnedTeardown { get; set; }
        public int? FixtureExceptionHResult { get; set; }
    }
    private sealed record TransportFacts(bool? AdministratorObserved, bool? HostStartAttempted, bool? HostStartObserved,
        bool? HostExitObserved, int? HostExitCode, bool? OwnershipReleased, bool? StdoutDrained, bool? StderrDrained,
        bool? OutputBoundExceeded, int? StderrCharacterCount, bool? StopSignalSucceeded, int? StopSignalNativeError,
        string? IdentityStage, string? IdentityReason, int? NativeTrustResult);
    private sealed record EvidenceFacts(string Stage, string Outcome, bool? HostQualified, bool? ModuleBound,
        bool? CmdletQualified, bool? OutputContractQualified, bool? InvocationStarted, string? PipelineState,
        bool? EndInvokeReturned, bool? CancelEventObserved, bool? BeginStopCalled, bool? EndStopReturned,
        int? OutputCount, int? ErrorCount, int? WarningCount, string? HealthState, bool? PowerShellDisposed, bool? RunspaceClosed,
        bool? CancelHandleDisposed, int? ManagedExceptionHResult);

    private sealed record UiFacts(bool? PrepareObserved, bool? AcknowledgementObserved, bool? ConfirmObserved,
        bool? ResultReturned, bool? FreshResultObserved, bool? Settled, bool? TransportConsistent,
        bool? EvidenceConsistent, string? ResultState, string? RecordedFlag, TransportFacts? Transport,
        EvidenceFacts? Evidence, int? ExceptionHResult);

    private static TransportFacts ProjectTransport(ComponentStoreStatusTransport transport) =>
        new(transport.AdministratorObserved, transport.HostStartAttempted, transport.HostStartObserved,
            transport.HostExitObserved, transport.HostExitCode, transport.OwnershipReleased, transport.StdoutDrained,
            transport.StderrDrained, transport.OutputBoundExceeded, transport.StderrCharacterCount, transport.StopSignalSucceeded,
            transport.StopSignalNativeError, transport.HostIdentity?.Stage.ToString(), transport.HostIdentity?.Reason.ToString(), transport.HostIdentity?.NativeTrustResult);

    private static EvidenceFacts? ProjectEvidence(ComponentStoreStatusEvidence? evidence) => evidence == null ? null :
        new(evidence.Stage.ToString(), evidence.Outcome.ToString(), evidence.HostQualified, evidence.ModuleBound,
            evidence.CmdletQualified, evidence.OutputContractQualified, evidence.InvocationStarted, evidence.PipelineState?.ToString(),
            evidence.EndInvokeReturned, evidence.CancelEventObserved, evidence.BeginStopCalled, evidence.EndStopReturned,
            evidence.OutputCount, evidence.ErrorCount, evidence.WarningCount, evidence.HealthState, evidence.PowerShellDisposed, evidence.RunspaceClosed,
            evidence.CancelHandleDisposed, evidence.ManagedExceptionHResult);

    internal static int Run(string repository)
    {
        if (rootedOwner != null || !Context(repository, out string source, out string run, out int attempt)) return 2;
        var repositoryGuards = SystemToolTrustNative.LockDirectoryChain(repository);
        try
        {
            string artifacts = Path.Combine(repository, "artifacts");
            Directory.CreateDirectory(artifacts); CheckAncestry(artifacts);
            string output = Path.Combine(artifacts, "component-store-status-validation");
            if (Directory.Exists(output) || File.Exists(output)) return 1;
            Directory.CreateDirectory(output); CheckAncestry(output);
            var owner = new Owner(repository, output, new Report { SourceCommit = source, RunId = run, RunAttempt = attempt });
            owner.Hold(repositoryGuards); repositoryGuards.Clear(); rootedOwner = owner;
            // No local watchdog, process kill or Environment.Exit wraps this mode. A hung
            // native/host operation stays owned; only the disposable job's outer VM expires.
            return owner.RunAsync().GetAwaiter().GetResult();
        }
        finally { foreach (var guard in repositoryGuards.AsEnumerable().Reverse()) guard.Dispose(); }
    }

    private sealed class Owner(string repository, string output, Report report)
    {
        private readonly object reportGate = new();
        private readonly ComponentStoreStatusProvider provider = new();
        private readonly List<IDisposable> leases = [];
        private readonly List<(string Path, Guid Id, ComponentStoreStatusFixtureAuthorization Proof)> scopes = [];
        private IComponentStoreStatusSession? session;
        internal void Hold(IEnumerable<IDisposable> guards) => leases.AddRange(guards);
        private void Save()
        {
            lock (reportGate)
            {
                report.ObservedUtc = DateTimeOffset.UtcNow; CheckAncestry(output);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
                if (bytes.Length > 8192) throw new InvalidDataException();
                string temporary = Path.Combine(output, "report.json.new");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
                File.Move(temporary, Path.Combine(output, "report.json"), true);
            }
        }
        private (ComponentStoreStatusIntent Intent, ComponentStoreStatusFixtureAuthorization Proof) Prepare()
        {
            var preview = ComponentStoreStatusPolicy.CreatePreview(DateTimeOffset.UtcNow).Items.Single();
            var intent = ComponentStoreStatusPolicy.Confirm(preview, true, DateTimeOffset.UtcNow).Items.Single();
            string scope = Path.Combine(repository, "artifacts", "component-store-status-fixtures-" + intent.RequestId.ToString("N"));
            if (Directory.Exists(scope) || File.Exists(scope)) throw new InvalidDataException();
            Directory.CreateDirectory(scope); CheckAncestry(scope);
            using (var marker = new FileStream(Path.Combine(scope, ".madclean-component-store-owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { marker.Write(Encoding.ASCII.GetBytes(intent.RequestId.ToString("N"))); marker.Flush(true); }
            var proof = ComponentStoreStatusFixtureAuthorization.Open(repository, scope, intent.RequestId);
            scopes.Add((scope, intent.RequestId, proof));
            return (intent, proof);
        }
        internal async Task<int> RunAsync()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    report.AdministratorObserved = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                Save();
                if (report.AdministratorObserved != true) { report.Stage = "AdministratorUnavailable"; Save(); return 2; }
                report.Stage = "Precancel"; Save();
                var cancelled = Prepare();
                using var stop = new CancellationTokenSource(); stop.Cancel();
                var cancelledSession = await provider.StartCandidateAsync(cancelled.Intent, cancelled.Proof, stop.Token);
                var cancelledResult = await cancelledSession.Completion;
                report.PrecancelState = Enum.IsDefined(cancelledResult.State) ? cancelledResult.State.ToString() : null;
                report.PrecancelAuthorizationTransferred = cancelled.Proof.WasTransferred;
                report.PrecancelNoHostObserved = cancelledResult.State == CheckState.Cancelled &&
                    cancelledResult.Transport.HostStartAttempted == false && cancelledResult.Transport.HostStartObserved == false &&
                    cancelledResult.Transport.HostExitObserved == null && cancelledResult.Transport.HostExitCode == null &&
                    !cancelledSession.HasPendingWork && !cancelledSession.RequiresRetention && !provider.HasPendingWork && !provider.RequiresRetention &&
                    !cancelled.Proof.WasTransferred && !cancelled.Intent.IsDispatchReserved && cancelled.Intent.IsInvalidated;
                cancelled.Proof.Dispose(); Save();
                if (report.PrecancelNoHostObserved != true) { report.State = "failed"; report.Stage = "PrecancelInvariantFailed"; Save(); return 1; }
                var candidate = Prepare();
                report.Stage = "Candidate"; Save();
                session = await provider.StartCandidateAsync(candidate.Intent, candidate.Proof, CancellationToken.None);
                report.CandidateAuthorizationTransferred = candidate.Proof.WasTransferred; Save();
                var observed = await session.Completion;
                report.RequestMatched = observed.RequestId == candidate.Intent.RequestId;
                if (report.RequestMatched == false) report.State = "failed";
                report.ResultState = Enum.IsDefined(observed.State) ? observed.State.ToString() : null;
                report.RecordedFlag = Enum.IsDefined(observed.Flag) ? observed.Flag.ToString() : null;
                report.TransportConsistent = ComponentStoreStatusPolicy.IsTransportConsistent(observed.Transport);
                report.SessionStopRequested = session.StopRequested; report.SessionPending = session.HasPendingWork;
                report.ProviderPending = provider.HasPendingWork; report.SessionRetention = session.RequiresRetention; report.ProviderRetention = provider.RequiresRetention;
                if (!candidate.Proof.WasTransferred && !session.HasPendingWork && !session.RequiresRetention && !provider.HasPendingWork && !provider.RequiresRetention) candidate.Proof.Dispose();
                report.CandidateAuthorizationReleased = candidate.Proof.IsReleased;
                var transport = observed.Transport;
                report.Transport = ProjectTransport(transport);
                if (observed.Evidence is { } evidence)
                {
                    report.EvidenceConsistent = ComponentStoreStatusParser.IsConsistent(evidence, candidate.Intent.RequestId);
                    report.Evidence = ProjectEvidence(evidence);
                }
                report.Stage = "Observed"; Save();
                bool settled = !session.HasPendingWork && !session.RequiresRetention && !provider.HasPendingWork && !provider.RequiresRetention && candidate.Proof.IsReleased;
                if (!settled) { report.Stage = "OwnershipUnsettled"; Save(); return 2; }
                report.Stage = "UiActual"; Save();
                var ui = await ComponentStoreStatusUiQualification.RunAsync();
                var uiResult = ui.Result;
                report.Ui = new(ui.PrepareObserved, ui.AcknowledgementObserved, ui.ConfirmObserved,
                    ui.ResultReturned, ui.FreshResultObserved, ui.Settled,
                    uiResult == null ? null : ComponentStoreStatusPolicy.IsTransportConsistent(uiResult.Transport),
                    uiResult?.Evidence is { } uiEvidence ? ComponentStoreStatusParser.IsConsistent(uiEvidence, uiResult.RequestId) : null,
                    uiResult?.State.ToString(), uiResult?.Flag.ToString(),
                    uiResult == null ? null : ProjectTransport(uiResult.Transport), ProjectEvidence(uiResult?.Evidence), ui.ExceptionHResult);
                Save();
                if (ui.Settled != true) { report.Stage = "UiUnsettled"; Save(); return 2; }
                foreach (var scope in scopes) scope.Proof.Dispose();
                foreach (var scope in scopes)
                {
                    if (!scope.Proof.IsReleased) throw new InvalidOperationException();
                    DeleteScope(scope.Path, scope.Id);
                }
                foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); leases.Clear(); report.OwnedTeardown = true;
                bool qualified = report.PrecancelNoHostObserved == true && report.CandidateAuthorizationTransferred == true &&
                    report.RequestMatched == true && report.TransportConsistent == true && report.EvidenceConsistent == true &&
                    observed.State == CheckState.Available && observed.Flag != ComponentStoreRecordedFlag.Unknown &&
                    !session.StopRequested && transport.AdministratorObserved == true && transport.HostStartObserved == true &&
                    transport.HostStartAttempted == true && transport.HostExitObserved == true && transport.HostExitCode == 0 && transport.OwnershipReleased == true &&
                    transport.StdoutDrained == true && transport.StderrDrained == true && transport.OutputBoundExceeded == false && transport.StderrCharacterCount == 0 &&
                    transport.StopSignalSucceeded == null && transport.StopSignalNativeError == null &&
                    transport.HostIdentity is { Stage: SystemToolIdentityStage.Completed, Reason: SystemToolIdentityReason.Observed, NativeTrustResult: 0 } &&
                    observed.Evidence is { Stage: ComponentStoreStatusStage.Completed, Outcome: ComponentStoreStatusOutcome.Completed,
                        HostQualified: true, ModuleBound: true, CmdletQualified: true, OutputContractQualified: true, InvocationStarted: true,
                        PipelineState: ComponentStorePipelineState.Completed, EndInvokeReturned: true, CancelEventObserved: false, BeginStopCalled: false,
                        EndStopReturned: null, OutputCount: 1, ErrorCount: 0, WarningCount: 0, ManagedExceptionHResult: null,
                        PowerShellDisposed: true, RunspaceClosed: true, CancelHandleDisposed: true };
                qualified = qualified && ui.PrepareObserved == true && ui.AcknowledgementObserved == true && ui.ConfirmObserved == true &&
                    ui.ResultReturned == true && ui.FreshResultObserved == true && ui.Settled == true && ui.ExceptionHResult == null &&
                    uiResult is { State: CheckState.Available } && uiResult.Flag != ComponentStoreRecordedFlag.Unknown &&
                    report.Ui.TransportConsistent == true && report.Ui.EvidenceConsistent == true &&
                    uiResult.Transport is { AdministratorObserved: true, HostStartAttempted: true, HostStartObserved: true,
                        HostExitObserved: true, HostExitCode: 0, OwnershipReleased: true, StdoutDrained: true, StderrDrained: true,
                        OutputBoundExceeded: false, StderrCharacterCount: 0, StopSignalSucceeded: null, StopSignalNativeError: null,
                        HostIdentity: { Stage: SystemToolIdentityStage.Completed, Reason: SystemToolIdentityReason.Observed, NativeTrustResult: 0 } } &&
                    uiResult.Evidence is { Stage: ComponentStoreStatusStage.Completed, Outcome: ComponentStoreStatusOutcome.Completed,
                        HostQualified: true, ModuleBound: true, CmdletQualified: true, OutputContractQualified: true, InvocationStarted: true,
                        PipelineState: ComponentStorePipelineState.Completed, EndInvokeReturned: true, CancelEventObserved: false, BeginStopCalled: false,
                        EndStopReturned: null, OutputCount: 1, ErrorCount: 0, WarningCount: 0, ManagedExceptionHResult: null,
                        PowerShellDisposed: true, RunspaceClosed: true, CancelHandleDisposed: true };
                if (report.State != "failed") report.State = qualified ? "passed" : "inconclusive";
                report.Stage = "Complete"; Save();
                return report.State == "failed" ? 1 : qualified ? 0 : 2;
            }
            catch (Exception exception)
            {
                report.FixtureExceptionHResult = exception.HResult;
                if (report.State != "failed") report.State = "inconclusive";
                try { Save(); } catch { }
                return report.State == "failed" ? 1 : 2;
            }
            // Uncertain host/pipeline/proof ownership is retained in rootedOwner. There is
            // no process termination, event replay or cleanup beneath an active worker.
        }
    }
    private static void DeleteScope(string scope, Guid id)
    {
        CheckAncestry(scope);
        string marker = Path.Combine(scope, ".madclean-component-store-owner");
        string[] entries = Directory.GetFileSystemEntries(scope);
        if (entries.Length != 1 || entries[0] != marker || (File.GetAttributes(marker) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
            File.ReadAllText(marker, Encoding.ASCII) != id.ToString("N")) throw new InvalidDataException();
        File.Delete(marker); Directory.Delete(scope, false);
    }
    private static void CheckAncestry(string path)
    {
        var parents = new Stack<DirectoryInfo>();
        for (DirectoryInfo? item = new(path); item != null; item = item.Parent) parents.Push(item);
        while (parents.Count != 0) if ((parents.Pop().Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }
    private static bool Context(string repository, out string source, out string run, out int attempt)
    {
        source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? ""; run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? ""; attempt = 0;
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        return OperatingSystem.IsWindows() && Environment.Is64BitProcess && Path.IsPathFullyQualified(repository) &&
            !string.IsNullOrWhiteSpace(workspace) && Path.IsPathFullyQualified(workspace) &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "MadGenius9/MadClean" && Environment.GetEnvironmentVariable("GITHUB_REF") == "refs/heads/feat/phase-one-foundation" &&
            Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is "push" or "workflow_dispatch" && Environment.GetEnvironmentVariable("GITHUB_JOB") == "windows-component-store-status" &&
            Environment.GetEnvironmentVariable("MADCLEAN_COMPONENT_STORE_STATUS_VALIDATION") == "1" &&
            source.Length == 40 && source.All(Uri.IsHexDigit) && run.Length is > 0 and <= 32 && run.All(char.IsAsciiDigit) &&
            int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), out attempt) && attempt > 0 &&
            string.Equals(Path.GetFullPath(repository), Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase);
    }
}
