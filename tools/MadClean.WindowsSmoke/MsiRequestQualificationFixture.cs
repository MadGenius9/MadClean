using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

// Deliberately separate from the passive harness. This mode can change only its fresh
// inert per-user product. It is not an uninstall testing switch in the production app.
internal static class MsiRequestQualificationFixture
{
    private const string DirectoryPrefix = "msi-request-validation-";
    private const string MarkerName = ".madclean-msi-owner";
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("MadClean inert MSI qualification payload\r\n");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static Owner? rootedOwner;

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
        public bool? PackageBuilt { get; set; }
        public bool? DatabaseVerified { get; set; }
        public bool? PreexistingCodeAbsent { get; set; }
        public bool? InstallReturned { get; set; }
        public int? InstallCode { get; set; }
        public bool? InstalledContextObserved { get; set; }
        public bool? PayloadObserved { get; set; }
        public bool? VisibleConsentObserved { get; set; }
        public bool? ClientStarted { get; set; }
        public bool? ClientExited { get; set; }
        public int? ClientExitCode { get; set; }
        public bool? PostCodeAbsent { get; set; }
        public bool? PayloadAbsent { get; set; }
        public string? SelectionSourceState { get; set; }
        public string? BindingResultState { get; set; }
        public string? BindingReportState { get; set; }
        public string? BindingReason { get; set; }
        public int? BindingChangedFields { get; set; }
        public bool? SelectionNoRemove { get; set; }
        public bool? BindingNoRemove { get; set; }
        public int? BindingInstanceCount { get; set; }
        public int? ExpectedMask { get; set; }
        public int? ObservedContext { get; set; }
        public string? CurrentSnapshotState { get; set; }
        public bool? PreviewPrepared { get; set; }
        public string? ClientRequestState { get; set; }
        public string? ToolIdentityStage { get; set; }
        public string? ToolIdentityReason { get; set; }
        public int? ToolNativeTrustResult { get; set; }
        public int? ToolFileTrustResult { get; set; }
        public int? ToolCatalogTrustResult { get; set; }
        public int? ToolCleanupTrustResult { get; set; }
        public string? DatabaseOperation { get; set; }
        public string? FailureKind { get; set; }
        public int? FailureCode { get; set; }
        public bool? WorkerSettled { get; set; }
        public bool? OwnedTeardown { get; set; }
    }

    internal static int Run(string repository)
    {
        if (!QualifiedContext(repository, out var source, out var run, out int attempt) || rootedOwner != null) return 2;
        var guards = SystemToolTrustNative.LockDirectoryChain(repository);
        try
        {
            string artifacts = Path.Combine(repository, "artifacts");
            Directory.CreateDirectory(artifacts); CheckAncestry(artifacts);
            string output = Path.Combine(artifacts, "msi-request-validation");
            if (Directory.Exists(output) || File.Exists(output)) return 1;
            Directory.CreateDirectory(output); CheckAncestry(output);
            var owner = new Owner(repository, output, new Report { SourceCommit = source, RunId = run, RunAttempt = attempt });
            owner.Hold(guards); guards.Clear(); rootedOwner = owner;
            // No process-deadline kill or Environment.Exit. A pending installer/native call
            // remains owned; the isolated GitHub job's outer VM lifetime is independent.
            return owner.RunAsync().GetAwaiter().GetResult();
        }
        finally { foreach (var guard in guards.AsEnumerable().Reverse()) guard.Dispose(); }
    }

    private sealed class Owner(string repository, string output, Report report)
    {
        private readonly Guid product = Guid.NewGuid();
        private readonly object reportWriteGate = new();
        private readonly List<IDisposable> leases = [];
        private bool installationRequested, resourcesReleased;
        private string scope = "", target = "", package = "";
        internal void Hold(IEnumerable<IDisposable> owned) => leases.AddRange(owned);
        private void Save()
        {
            lock (reportWriteGate)
            {
            report.ObservedUtc = DateTimeOffset.UtcNow;
            CheckAncestry(output);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            if (bytes.Length > 8192) throw new InvalidDataException();
            string temporary = Path.Combine(output, "report.json.new");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, Path.Combine(output, "report.json"), true);
            }
        }
        internal async Task<int> RunAsync()
        {
            try
            {
                scope = Path.Combine(repository, "artifacts", DirectoryPrefix + product.ToString("N"));
                target = Path.Combine(scope, "target"); package = Path.Combine(scope, "source", "fixture.msi");
                if (scope.Length > 180 || Directory.Exists(scope) || File.Exists(scope)) throw new InvalidDataException();
                Directory.CreateDirectory(scope); Directory.CreateDirectory(target); Directory.CreateDirectory(Path.GetDirectoryName(package)!);
                File.WriteAllText(Path.Combine(scope, MarkerName), product.ToString("N"), new ASCIIEncoding());
                CheckAncestry(scope);
                foreach (var handle in SystemToolTrustNative.LockDirectoryChain(scope)) leases.Add(handle);
                leases.Add(new FileStream(Path.Combine(scope, MarkerName), FileMode.Open, FileAccess.Read, FileShare.Read));
                Save();
                var initial = Contexts(product);
                report.PreexistingCodeAbsent = initial.Count == 0;
                if (initial.Count != 0) { report.State = "failed"; report.Stage = "ProductCollision"; Save(); return 1; }
                File.WriteAllBytes(Path.Combine(scope, "source", "fixture.txt"), Payload);
                report.Stage = "BuildOwnedDatabase"; Save();
                using (var database = new OwnedMsiDatabase(package, product, target, Payload.Length))
                { report.PackageBuilt = true; database.Verify(product, target); report.DatabaseVerified = true; }
                report.DatabaseOperation = OwnedMsiDatabase.LastOperation;
                if (OwnedMsiDatabase.RequiresRetention) throw new InvalidOperationException();
                leases.Add(new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read));
                leases.Add(new FileStream(Path.Combine(scope, "source", "fixture.txt"), FileMode.Open, FileAccess.Read, FileShare.Read));
                report.Stage = "InstallOwnedProduct"; Save();
                installationRequested = true;
                // Setup UI suppression applies only to this process's freshly authored inert
                // fixture package, never the separately launched visible uninstall client.
                await Task.Run(() =>
                {
                    uint previous = MsiSetInternalUI(2, 0);
                    if (previous == 0) throw new InvalidOperationException();
                    try
                    {
                        uint result = MsiInstallProductW(package, "ALLUSERS=\"\" REBOOT=ReallySuppress");
                        report.InstallCode = unchecked((int)result); report.InstallReturned = true;
                    }
                    finally { if (MsiSetInternalUI(previous, 0) == 0) throw new InvalidOperationException(); }
                });
                if (report.InstallCode != 0) { report.Stage = "InstallUnverified"; Save(); return 2; }
                var installed = Contexts(product);
                report.InstalledContextObserved = installed.Count == 1 && installed[0] == 2;
                report.PayloadObserved = ObservePayload(Path.Combine(target, "fixture.txt"));
                if (report.InstalledContextObserved != true || report.PayloadObserved != true) { report.Stage = "InstalledEvidenceUnknown"; Save(); return 2; }
                report.Stage = "PrepareRequest"; Save();
                using var authorization = MsiUninstallFixtureAuthorization.Open(repository, scope, product);
                var snapshot = await new InstalledApplicationInventory().ScanAsync(CancellationToken.None);
                report.SelectionSourceState = Enum.IsDefined(snapshot.State) ? snapshot.State.ToString() : null; Save();
                var selected = snapshot.Items.SingleOrDefault(row => row.RegistrationEvidence?.ProductCode == product.ToString("B").ToUpperInvariant());
                report.SelectionNoRemove = selected?.RegistrationEvidence?.NoRemove; Save();
                if (selected == null || selected.Name != OwnedMsiDatabase.ProductName || selected.Version != OwnedMsiDatabase.ProductVersion || selected.Publisher != OwnedMsiDatabase.Publisher)
                { report.Stage = "SelectionUnknown"; Save(); return 2; }
                var scopeResult = MsiActionBindingPolicy.CreateScope(snapshot, selected, DateTimeOffset.UtcNow);
                if (scopeResult.Items.Count != 1) { report.Stage = "SelectionUnknown"; Save(); return 2; }
                report.ExpectedMask = scopeResult.Items[0].ExpectedContextMask; Save();
                var binding = await new WindowsMsiActionBindingInventory().VerifyAsync(scopeResult.Items[0], CancellationToken.None);
                report.BindingResultState = Enum.IsDefined(binding.State) ? binding.State.ToString() : null;
                if (binding.Items.Count == 1)
                {
                    var actualBinding = binding.Items[0];
                    report.BindingReportState = Enum.IsDefined(actualBinding.State) ? actualBinding.State.ToString() : null;
                    report.BindingReason = Enum.IsDefined(actualBinding.Reason) ? actualBinding.Reason.ToString() : null;
                    report.BindingChangedFields = actualBinding.ChangedFields is { } changed ? (int)changed : null;
                    report.BindingNoRemove = actualBinding.NoRemove;
                    report.BindingInstanceCount = actualBinding.Instances?.Count;
                    report.ExpectedMask = actualBinding.ExpectedContextMask;
                    if (actualBinding.Instances is { Count: 1 } instances && Enum.IsDefined(instances[0].Context)) report.ObservedContext = (int)instances[0].Context;
                    report.CurrentSnapshotState = Enum.IsDefined(binding.Items[0].CurrentSnapshotState) ? binding.Items[0].CurrentSnapshotState.ToString() : null;
                }
                Save();
                var previews = MsiUninstallRequestPolicy.CreatePreview(scopeResult.Items[0], binding, 1, DateTimeOffset.UtcNow);
                report.PreviewPrepared = previews.Items.Count == 1; Save();
                if (previews.Items.Count != 1) { report.Stage = "SelectionUnknown"; Save(); return 2; }
                report.Stage = "VisibleRequest"; Save();
                var resultObservation = await new MsiUninstallRequestLauncher().RequestOwnedFixtureAsync(previews.Items[0].Intent, true, 1, authorization,
                    client => ObserveConsent(client), CancellationToken.None);
                report.ClientRequestState = Enum.IsDefined(resultObservation.State) ? resultObservation.State.ToString() : null;
                if (resultObservation.ClientStartObserved.HasValue) report.ClientStarted = resultObservation.ClientStartObserved;
                else if (resultObservation.State is MsiUninstallRequestState.ClientStarted or MsiUninstallRequestState.ClientExited) report.ClientStarted = true;
                if (resultObservation.ToolIdentity is { } identity)
                {
                    report.ToolIdentityStage = Enum.IsDefined(identity.Stage) ? identity.Stage.ToString() : null;
                    report.ToolIdentityReason = Enum.IsDefined(identity.Reason) ? identity.Reason.ToString() : null;
                    report.ToolNativeTrustResult = identity.NativeTrustResult; report.ToolFileTrustResult = identity.FileNativeTrustResult;
                    report.ToolCatalogTrustResult = identity.CatalogNativeTrustResult; report.ToolCleanupTrustResult = identity.TrustCleanupNativeResult;
                }
                report.ClientExited = resultObservation.ExitedUtc.HasValue;
                report.ClientExitCode = resultObservation.ExitCode;
                report.WorkerSettled = !MsiUninstallRequestLauncher.HasPendingRequest && !MsiUninstallRequestLauncher.RequiresRetention;
                if (report.WorkerSettled != true || report.ClientExited != true) { report.Stage = "RequestUnsettled"; Save(); return 2; }
                report.Stage = "ObservePostState";
                report.PostCodeAbsent = Contexts(product).Count == 0;
                report.PayloadAbsent = ObserveAbsent(Path.Combine(target, "fixture.txt"), scope, product);
                if (report.ClientExitCode == 1641) report.State = "failed";
                if (report.VisibleConsentObserved != true || report.ClientExitCode != 0 || report.PostCodeAbsent != true || report.PayloadAbsent != true)
                { report.Stage = "PostStateUnverified"; Save(); return report.State == "failed" ? 1 : 2; }
                authorization.Dispose();
                Release();
                if (!resourcesReleased) { report.Stage = "OwnershipUnverified"; Save(); return 2; }
                CheckAncestry(scope); VerifyMarker(scope, product);
                // These are the exact fresh owned files after native/installer ownership has
                // settled and this product's registration and payload absence were observed.
                DeleteOwnedScope(scope, product); report.OwnedTeardown = !Directory.Exists(scope);
                report.Stage = "Complete";
                report.State = report.OwnedTeardown == true ? "passed" : "inconclusive";
                Save(); return report.State == "passed" ? 0 : 2;
            }
            catch (Exception exception)
            {
                report.DatabaseOperation = OwnedMsiDatabase.LastOperation;
                if (exception is OwnedMsiDatabaseInvariantException || OwnedMsiDatabase.HasKnownInvariantFailure) report.State = "failed";
                report.FailureKind = exception is OwnedMsiDatabaseInvariantException ? "OwnedInvariantFailed" : exception is Win32Exception ? "NativeWin32" : exception is InvalidDataException or InvalidOperationException ? "OwnedEvidenceUnavailable" : "ObservationUnavailable";
                report.FailureCode = exception is Win32Exception native ? native.NativeErrorCode : null;
                // Fixed labels only; no product identity, path, MSI output or exception text.
                if (report.State != "failed") report.State = "inconclusive";
                try { Save(); } catch { }
                return report.State == "failed" ? 1 : 2;
            }
            finally
            {
                if (!installationRequested) Release();
                // Any uncertain installed/request state retains source, marker and scope.
                // The host VM eventually expires; this is not MadClean rollback/recovery.
            }
        }
        private Task ObserveConsent(MsiUninstallFixtureClient client) => Task.Run(async () =>
        {
            var started = DateTimeOffset.UtcNow;
            bool authored = false;
            report.ClientStarted = true; report.WorkerSettled = false; Save();
            if (client.ProcessId is not int processId) { report.Stage = "VisibleConsentUnverified"; Save(); return; }
            while (client.HasExited == false && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(90))
            {
                foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, processId)))
                {
                    if (client.HasExited != false) break;
                    string title = window.Current.Name;
                    if (title == OwnedMsiDatabase.DialogTitle && VisibleAndEnabled(window))
                    {
                        var button = window.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, "Remove")));
                        if (button != null && VisibleAndEnabled(button) && client.HasExited == false && button.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern))
                        { if (client.TryInvokeIfRunning(() => ((InvokePattern)pattern).Invoke())) { authored = true; report.VisibleConsentObserved = true; Save(); return; } }
                    }
                    else if (title == "Windows Installer" && VisibleAndEnabled(window))
                    {
                        // The generic /x confirmation is accepted only for this exact live
                        // borrowed client, with exactly the known confirmation text. The own
                        // authored fixed Remove dialog is still required for qualification.
                        var texts = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                        bool ownText = texts.Cast<AutomationElement>().Any(item => item.Current.Name == "Are you sure you want to uninstall this product?");
                        var yes = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "6"));
                        if (ownText && yes != null && VisibleAndEnabled(yes) && client.HasExited == false && yes.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern))
                            client.TryInvokeIfRunning(() => ((InvokePattern)pattern).Invoke());
                    }
                }
                await Task.Delay(100);
            }
            if (!authored) { report.VisibleConsentObserved = false; report.Stage = "VisibleConsentUnverified"; Save(); }
        });
        private void Release()
        {
            if (OwnedMsiDatabase.RequiresRetention || MsiUninstallRequestLauncher.HasPendingRequest || MsiUninstallRequestLauncher.RequiresRetention) return;
            foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
            leases.Clear(); resourcesReleased = true;
        }
    }

    private static bool VisibleAndEnabled(AutomationElement element)
    {
        var current = element.Current; var rectangle = current.BoundingRectangle;
        return !current.IsOffscreen && current.IsEnabled && !rectangle.IsEmpty &&
            double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) && rectangle.Width > 0 && rectangle.Height > 0;
    }
    private static bool QualifiedContext(string repository, out string source, out string run, out int attempt)
    {
        source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? ""; run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? ""; attempt = 0;
        string? workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        return Path.IsPathFullyQualified(repository) && !string.IsNullOrWhiteSpace(workspace) && Path.IsPathFullyQualified(workspace) && OperatingSystem.IsWindows() && Environment.Is64BitProcess &&
            Environment.GetEnvironmentVariable("MADCLEAN_MSI_REQUEST_VALIDATION") == "1" &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "MadGenius9/MadClean" &&
            Environment.GetEnvironmentVariable("GITHUB_REF") == "refs/heads/feat/phase-one-foundation" &&
            Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is "push" or "workflow_dispatch" &&
            source.Length == 40 && source.All(Uri.IsHexDigit) && run.Length is > 0 and <= 32 && run.All(char.IsAsciiDigit) &&
            int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), out attempt) && attempt > 0 &&
            Path.GetFullPath(repository).Equals(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase);
    }
    private static void CheckAncestry(string path)
    {
        var parents = new Stack<DirectoryInfo>();
        for (DirectoryInfo? item = new(path); item != null; item = item.Parent) parents.Push(item);
        while (parents.Count != 0)
            if ((parents.Pop().Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }
    private static void VerifyMarker(string scope, Guid product)
    { if (File.ReadAllText(Path.Combine(scope, MarkerName), Encoding.ASCII) != product.ToString("N")) throw new InvalidDataException(); }
    private static void DeleteOwnedScope(string scope, Guid product)
    {
        CheckAncestry(scope); VerifyMarker(scope, product);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { Path.Combine(scope, MarkerName), Path.Combine(scope, "source"), Path.Combine(scope, "target"),
            Path.Combine(scope, "source", "fixture.msi"), Path.Combine(scope, "source", "fixture.txt") };
        var entries = new List<string>();
        var pending = new Stack<string>(); pending.Push(scope);
        while (pending.Count != 0)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (entries.Count >= 5 || !expected.Contains(entry)) throw new InvalidDataException();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                entries.Add(entry);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        if (entries.Count is not (4 or 5) || expected.Where(path => path != Path.Combine(scope, "target")).Any(path => !entries.Contains(path, StringComparer.OrdinalIgnoreCase))) throw new InvalidDataException();
        File.Delete(Path.Combine(scope, "source", "fixture.txt")); File.Delete(Path.Combine(scope, "source", "fixture.msi"));
        File.Delete(Path.Combine(scope, MarkerName));
        Directory.Delete(Path.Combine(scope, "source"), false);
        if (entries.Contains(Path.Combine(scope, "target"), StringComparer.OrdinalIgnoreCase)) Directory.Delete(Path.Combine(scope, "target"), false);
        Directory.Delete(scope, false);
    }
    private static bool ObservePayload(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[Payload.Length + 1]; int count = file.Read(bytes);
        return count == Payload.Length && bytes.AsSpan(0, count).SequenceEqual(Payload) && file.ReadByte() == -1;
    }
    private static bool ObserveAbsent(string path, string scope, Guid product)
    {
        if (!string.Equals(path, Path.Combine(scope, "target", "fixture.txt"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        CheckAncestry(scope); VerifyMarker(scope, product);
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException)
        {
            // The fixed target itself may have disappeared during supported uninstall.
            // The marked held scope still exists; do not infer absence from access errors.
            try
            {
                var attributes = File.GetAttributes(Path.Combine(scope, "target"));
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) throw new InvalidDataException();
                return false;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
        }
    }
    private static List<uint> Contexts(Guid product)
    {
        var contexts = new List<uint>();
        for (uint index = 0; index < 4; index++)
        {
            var found = new StringBuilder(39);
            uint code = MsiEnumProductsExW(product.ToString("B").ToUpperInvariant(), 0, 7, index, found, out uint context, 0, 0);
            if (code == 259) return contexts;
            if (code != 0 || !Guid.TryParseExact(found.ToString(), "B", out var actual) || actual != product || context is not (1 or 2 or 4) || contexts.Contains(context))
                throw new InvalidDataException();
            contexts.Add(context);
        }
        throw new InvalidDataException();
    }
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiInstallProductW(string package, string properties);
    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiSetInternalUI(uint level, nint window);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint MsiEnumProductsExW(string product, nint sid, uint context, uint index, StringBuilder code, out uint installedContext, nint installedSid, nint sidLength);
}
