using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

// Explicit disposable-runner client-lifecycle qualification only. Never passive inventory.
internal static class SfcClientQualification
{
    private static Owner? rootedOwner;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private sealed class Report
    {
        public int FormatVersion => 1;
        public required string SourceCommit { get; init; }
        public required string RunId { get; init; }
        public required int RunAttempt { get; init; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset ObservedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "inconclusive";
        public string Stage { get; set; } = "Scope";
        public bool ProductionExecutionEnabled { get; set; }
        public bool? AdministratorObserved { get; set; }
        public bool? IdentityAccepted { get; set; }
        public DateTimeOffset? IdentityCheckedUtc { get; set; }
        public DateTimeOffset? IdentityExpiresUtc { get; set; }
        public bool? PreCancellationVerified { get; set; }
        public bool? CandidateAuthorizationTransferred { get; set; }
        public bool? CandidateAuthorizationReleased { get; set; }
        public bool? RequestMatched { get; set; }
        public string? ResultState { get; set; }
        public string Integrity => "Unknown";
        public DateTimeOffset? AttemptedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public bool? ClientStartAttempted { get; set; }
        public bool? ClientStartObserved { get; set; }
        public DateTimeOffset? ClientStartedUtc { get; set; }
        public bool? ClientExitObserved { get; set; }
        public int? ClientExitCode { get; set; }
        public DateTimeOffset? ClientExitedUtc { get; set; }
        public bool? StdoutDrained { get; set; }
        public bool? StderrDrained { get; set; }
        public bool? OwnershipReleased { get; set; }
        public bool? ProviderPending { get; set; }
        public bool? RequiresRetention { get; set; }
        public bool? CleanupConfirmed { get; set; }
    }

    private sealed class InvariantFailure : Exception { }

    private sealed class OwnedScope(string path, Guid id)
    {
        internal string Path { get; } = path;
        internal Guid Id { get; } = id;
        internal bool CreationAttempted { get; set; }
        internal bool MarkerCreated { get; set; }
        internal SfcClientFixtureAuthorization? Proof { get; set; }
    }

    internal static int Run(string repository)
    {
        if (rootedOwner != null || !Context(repository, out string source, out string run, out int attempt)) return 2;
        ISystemToolActions identityOwner = new SystemToolActionsProvider();
        ISfcVerificationProvider clientOwner = new SfcVerificationProvider();
        try { return RunOwned(repository, source, run, attempt); }
        catch (Exception) { return 2; } // Never print arbitrary setup/exception payloads.
        finally
        {
            while (identityOwner.HasPendingIdentityWork || clientOwner.HasPendingWork || clientOwner.RequiresRetention)
                Thread.Sleep(100);
        }
    }

    private static int RunOwned(string repository, string source, string run, int attempt)
    {
        var guards = SystemToolTrustNative.LockDirectoryChain(repository);
        try
        {
            string artifacts = Path.Combine(repository, "artifacts");
            Directory.CreateDirectory(artifacts); CheckAncestry(artifacts);
            string output = Path.Combine(artifacts, "sfc-client-validation");
            if (Directory.Exists(output) || File.Exists(output)) return 1;
            Directory.CreateDirectory(output); CheckAncestry(output);
            var owner = new Owner(repository, output,
                new Report { SourceCommit = source, RunId = run, RunAttempt = attempt });
            owner.Hold(guards); guards.Clear(); rootedOwner = owner;
            // No watchdog, bounded wait or process kill wraps the actual client.
            // A retained owner keeps this invocation alive until the disposable job expires.
            return owner.RunAsync().GetAwaiter().GetResult();
        }
        finally { foreach (var guard in guards.AsEnumerable().Reverse()) guard.Dispose(); }
    }

    private sealed class Owner(string repository, string output, Report report)
    {
        private readonly SfcVerificationProvider provider = new();
        private readonly ISystemToolActions identityProvider = new SystemToolActionsProvider();
        private readonly List<IDisposable> leases = [];
        private readonly List<OwnedScope> scopes = [];
        private ISfcVerificationSession? session;
        private OwnedScope? candidateScope;

        internal void Hold(IEnumerable<IDisposable> guards) => leases.AddRange(guards);

        private void Save()
        {
            report.ObservedUtc = DateTimeOffset.UtcNow;
            CheckAncestry(output);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            try
            {
                if (bytes.Length > 16_384) throw new InvalidDataException();
                string temporary = Path.Combine(output, "report.json.new");
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, Path.Combine(output, "report.json"), true);
            }
            finally { Array.Clear(bytes); }
        }

        private (SfcVerificationIntent Intent, OwnedScope Scope)? Prepare(SystemToolActionPreview identity)
        {
            var preview = SfcVerificationPolicy.CreatePreview(identity, DateTimeOffset.UtcNow).Items.SingleOrDefault();
            if (preview == null) return null;
            var intent = SfcVerificationPolicy.Confirm(preview, true, true, DateTimeOffset.UtcNow).Items.SingleOrDefault();
            if (intent == null) return null;
            var scope = new OwnedScope(Path.Combine(repository, "artifacts", "sfc-client-fixtures-" + intent.RequestId.ToString("N")), intent.RequestId);
            if (Directory.Exists(scope.Path) || File.Exists(scope.Path)) throw new InvariantFailure();
            scopes.Add(scope);
            scope.CreationAttempted = true;
            Directory.CreateDirectory(scope.Path); CheckAncestry(scope.Path);
            byte[] markerBytes = Encoding.ASCII.GetBytes(intent.RequestId.ToString("N"));
            try
            {
                using var marker = new FileStream(Path.Combine(scope.Path, ".madclean-sfc-client-owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                scope.MarkerCreated = true;
                marker.Write(markerBytes); marker.Flush(true);
            }
            finally { Array.Clear(markerBytes); }
            scope.Proof = SfcClientFixtureAuthorization.Open(repository, scope.Path, intent.RequestId);
            return (intent, scope);
        }

        internal async Task<int> RunAsync()
        {
            bool lifecycleObserved = false;
            try { lifecycleObserved = await ObserveAsync().ConfigureAwait(false); }
            catch (InvariantFailure) { report.State = "failed"; }
            catch (Exception) { /* Fixed partial observations only; no provider/exception output. */ }
            finally
            {
                report.ProviderPending = provider.HasPendingWork;
                report.RequiresRetention = provider.RequiresRetention || session?.RequiresRetention == true;
                if (report.ProviderPending == true || report.RequiresRetention == true || identityProvider.HasPendingIdentityWork)
                    try { Save(); } catch (Exception) { }
                // A static reference alone does not keep the CLR alive. Preserve
                // the actual identity/client/proof owners, including terminal uncertainty.
                while (identityProvider.HasPendingIdentityWork || provider.HasPendingWork || provider.RequiresRetention ||
                    session?.HasPendingWork == true || session?.RequiresRetention == true)
                    await Task.Delay(100).ConfigureAwait(false);
                if (session != null)
                    try { _ = await session.Completion.ConfigureAwait(false); } catch (Exception) { }
                report.ProviderPending = provider.HasPendingWork;
                report.RequiresRetention = provider.RequiresRetention || session?.RequiresRetention == true;
                string observationStage = report.Stage;
                try
                {
                    report.Stage = "Cleanup";
                    foreach (var scope in scopes)
                    {
                        scope.Proof?.Dispose();
                        if (scope.Proof != null && !scope.Proof.IsReleased) throw new InvalidDataException();
                        if (scope.CreationAttempted)
                        {
                            if (!scope.MarkerCreated) throw new InvalidDataException();
                            DeleteScope(scope.Path, scope.Id);
                        }
                    }
                    foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
                    leases.Clear();
                    report.CleanupConfirmed = true;
                }
                catch (Exception) { report.CleanupConfirmed = false; }
                if (candidateScope?.Proof is { } proof)
                    report.CandidateAuthorizationReleased = proof.IsReleased;
                if (report.State != "failed" && lifecycleObserved && report.CleanupConfirmed == true &&
                    report.CandidateAuthorizationReleased == true && report.ProviderPending == false && report.RequiresRetention == false)
                { report.State = "passed"; report.Stage = "Completed"; }
                else
                {
                    if (report.State != "failed") report.State = "inconclusive";
                    if (report.CleanupConfirmed == true) report.Stage = observationStage;
                }
                try { Save(); } catch (Exception) { report.State = "failed"; }
            }
            return report.State == "passed" ? 0 : report.State == "failed" ? 1 : 2;
        }

        private async Task<bool> ObserveAsync()
        {
            report.ProductionExecutionEnabled = provider.ProductionExecutionValidated;
            using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true))
            {
                if (impersonated != null) { report.AdministratorObserved = false; Save(); return false; }
            }
            using (var current = WindowsIdentity.GetCurrent())
                report.AdministratorObserved = new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator);
            Save();
            if (report.ProductionExecutionEnabled || identityProvider.ProductionExecutionValidated) throw new InvariantFailure();
            if (report.AdministratorObserved != true) return false;

            report.Stage = "Identity"; Save();
            var identities = await identityProvider.PrepareAsync(SystemToolOperation.SfcVerifyOnly, CancellationToken.None).ConfigureAwait(false);
            var accepted = SystemToolIdentityObservationPolicy.Evaluate(SystemToolOperation.SfcVerifyOnly, identities,
                Environment.GetFolderPath(Environment.SpecialFolder.System), DateTimeOffset.UtcNow, CancellationToken.None);
            var identity = accepted.IdentityObserved == true ? identities.Items.SingleOrDefault() : null;
            report.IdentityAccepted = identity != null && identity.ToolVersion != "Unknown" && !identity.CanLaunch &&
                identity.TrustState == SystemToolTrustState.MicrosoftSignatureVerified &&
                identity.Definition.Operation == SystemToolOperation.SfcVerifyOnly &&
                identity.CheckedUtc >= report.StartedUtc && identity.CheckedUtc.Offset == TimeSpan.Zero &&
                identity.ExpiresUtc.Offset == TimeSpan.Zero && identity.ExpiresUtc > DateTimeOffset.UtcNow &&
                identity.ExpiresUtc > identity.CheckedUtc && identity.ExpiresUtc - identity.CheckedUtc <= TimeSpan.FromMinutes(2);
            if (report.IdentityAccepted != true || identity == null) { Save(); return false; }
            report.IdentityCheckedUtc = identity.CheckedUtc; report.IdentityExpiresUtc = identity.ExpiresUtc;
            Save();

            report.Stage = "Precancel"; Save();
            var cancelled = Prepare(identity);
            if (cancelled == null) return false;
            var cancelledScope = cancelled.Value.Scope;
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                var stopped = await provider.StartCandidateAsync(cancelled.Value.Intent, cancelledScope.Proof!, stop.Token).ConfigureAwait(false);
                var result = await stopped.Completion.ConfigureAwait(false);
                report.PreCancellationVerified = result.RequestId == cancelled.Value.Intent.RequestId &&
                    result.State == CheckState.Cancelled && result.Integrity == SfcIntegrityAssessment.Unknown &&
                    result.Transport is { AdministratorObserved: null, ClientStartAttempted: false, ClientStartObserved: false,
                        ProcessId: null, ClientStartedUtc: null, ClientExitObserved: null, ClientExitCode: null, ClientExitedUtc: null,
                        StdoutDrained: null, StderrDrained: null, OwnershipReleased: true } &&
                    !stopped.HasPendingWork && !stopped.RequiresRetention && !provider.HasPendingWork && !provider.RequiresRetention &&
                    !cancelledScope.Proof!.WasTransferred && !cancelled.Value.Intent.IsDispatchReserved && cancelled.Value.Intent.IsInvalidated;
            }
            cancelledScope.Proof!.Dispose();
            if (!cancelledScope.Proof.IsReleased) report.PreCancellationVerified = false;
            Save();
            if (report.PreCancellationVerified != true) throw new InvariantFailure();

            var prepared = Prepare(identity);
            if (prepared == null) return false;
            var candidate = prepared.Value;
            candidateScope = candidate.Scope;
            report.Stage = "Client"; Save();
            session = await provider.StartCandidateAsync(candidate.Intent, candidate.Scope.Proof!, CancellationToken.None).ConfigureAwait(false);
            report.CandidateAuthorizationTransferred = candidate.Scope.Proof!.WasTransferred;
            report.ProviderPending = provider.HasPendingWork;
            report.RequiresRetention = provider.RequiresRetention || session.RequiresRetention;
            Save();
            var observed = await session.Completion.ConfigureAwait(false);
            var transport = observed.Transport;
            report.RequestMatched = observed.RequestId == candidate.Intent.RequestId && session.RequestId == candidate.Intent.RequestId;
            report.ResultState = Enum.IsDefined(observed.State) ? observed.State.ToString() : null;
            report.AttemptedUtc = observed.AttemptedUtc; report.CompletedUtc = observed.CompletedUtc;
            report.AdministratorObserved = transport.AdministratorObserved;
            report.ClientStartAttempted = transport.ClientStartAttempted; report.ClientStartObserved = transport.ClientStartObserved;
            report.ClientStartedUtc = transport.ClientStartedUtc; report.ClientExitObserved = transport.ClientExitObserved;
            report.ClientExitCode = transport.ClientExitCode; report.ClientExitedUtc = transport.ClientExitedUtc;
            report.StdoutDrained = transport.StdoutDrained; report.StderrDrained = transport.StderrDrained;
            report.OwnershipReleased = transport.OwnershipReleased;
            report.CandidateAuthorizationReleased = candidate.Scope.Proof.IsReleased;
            report.ProviderPending = provider.HasPendingWork;
            report.RequiresRetention = provider.RequiresRetention || session.RequiresRetention;
            Save();
            if (report.RequestMatched != true || observed.Integrity != SfcIntegrityAssessment.Unknown || !Enum.IsDefined(observed.State) ||
                !SfcVerificationPolicy.IsTransportConsistent(transport) || observed.State == CheckState.Available &&
                SfcVerificationPolicy.Assess(candidate.Intent, transport, observed.AttemptedUtc, observed.CompletedUtc).State != CheckState.Available)
                throw new InvariantFailure();
            // Zero exit is a conservative lifecycle criterion, never a Windows
            // integrity verdict. No localized output or exit-code health mapping.
            return observed.State == CheckState.Available && candidate.Intent.IsDispatchReserved &&
                report.PreCancellationVerified == true && report.CandidateAuthorizationTransferred == true &&
                report.CandidateAuthorizationReleased == true && !session.StopObservationRequested &&
                !session.HasPendingWork && !session.RequiresRetention && !provider.HasPendingWork && !provider.RequiresRetention &&
                transport is { AdministratorObserved: true, ClientStartAttempted: true, ClientStartObserved: true,
                    ClientExitObserved: true, ClientExitCode: 0, StdoutDrained: true, StderrDrained: true,
                    OwnershipReleased: true, StopObservationRequested: false } &&
                observed.AttemptedUtc >= candidate.Intent.PreparedUtc && observed.AttemptedUtc >= report.StartedUtc &&
                transport.ClientStartedUtc is { } start && start.Offset == TimeSpan.Zero && start >= observed.AttemptedUtc &&
                start < identity.ExpiresUtc && transport.ClientExitedUtc is { } exit && exit.Offset == TimeSpan.Zero && exit >= start &&
                observed.CompletedUtc is { } end && end.Offset == TimeSpan.Zero && end >= exit && end <= DateTimeOffset.UtcNow;
        }
    }

    private static void DeleteScope(string path, Guid id)
    {
        CheckAncestry(path);
        string marker = Path.Combine(path, ".madclean-sfc-client-owner");
        string[] entries = Directory.EnumerateFileSystemEntries(path).Take(2).ToArray();
        if (entries.Length != 1 || !string.Equals(entries[0], marker, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(marker) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new InvalidDataException();
        byte[] bytes = new byte[32];
        try
        {
            using (var file = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length != 32) throw new InvalidDataException();
                file.ReadExactly(bytes);
                if (file.ReadByte() != -1 || Encoding.ASCII.GetString(bytes) != id.ToString("N")) throw new InvalidDataException();
            }
        }
        finally { Array.Clear(bytes); }
        File.Delete(marker); Directory.Delete(path, false);
        if (File.Exists(path) || Directory.Exists(path)) throw new InvalidDataException();
    }

    private static void CheckAncestry(string path)
    {
        for (DirectoryInfo? item = new(path); item != null; item = item.Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }

    private static bool Context(string repository, out string source, out string run, out int attempt)
    {
        source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "";
        run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? ""; attempt = 0;
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        return OperatingSystem.IsWindows() && Environment.Is64BitProcess && Path.IsPathFullyQualified(repository) &&
            repository.Length >= 4 && char.IsAsciiLetter(repository[0]) && repository[1] == ':' && repository[2] == '\\' &&
            !string.IsNullOrWhiteSpace(workspace) && Path.IsPathFullyQualified(workspace) &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "MadGenius9/MadClean" && Environment.GetEnvironmentVariable("GITHUB_REF") == "refs/heads/feat/phase-one-foundation" &&
            Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is "push" or "workflow_dispatch" && Environment.GetEnvironmentVariable("GITHUB_JOB") == "windows-sfc-client" &&
            Environment.GetEnvironmentVariable("MADCLEAN_SFC_CLIENT_VALIDATION") == "1" && source.Length == 40 && source.All(Uri.IsHexDigit) &&
            run.Length is > 0 and <= 32 && run.All(char.IsAsciiDigit) &&
            int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out attempt) && attempt > 0 &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), StringComparison.OrdinalIgnoreCase);
    }
}
