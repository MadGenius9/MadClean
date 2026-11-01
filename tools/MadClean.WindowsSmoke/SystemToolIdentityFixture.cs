using System.ComponentModel;
using System.Globalization;
using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void SystemToolIdentities(SmokeReport report, CancellationToken token)
    {
        const string check = "system-tool-identity-contract";
        ISystemToolActions provider = new SystemToolActionsProvider();
        SystemToolOperation[] operations = [SystemToolOperation.DismCheckHealth, SystemToolOperation.SfcVerifyOnly];
        var outcomes = new List<string>();
        int observed = 0, gatedRequests = 0, unknownVersions = 0;
        if (provider.ProductionExecutionValidated)
        {
            report.Fail(check, "The production execution gate unexpectedly reports enabled; no preparation or launch request was called.");
            return;
        }
        try
        {
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                foreach (var operation in operations)
                {
                    bool stopped = false;
                    try { provider.PrepareAsync(operation, cancelled.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { stopped = true; }
                    if (!stopped)
                    {
                        report.Fail(check, "A pre-cancelled fixed identity preparation did not throw cancellation; no actual identity or launch request followed.");
                        return;
                    }
                }
            }
            // Independent OS-provided directory, never an environment/path/request override.
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!Path.IsPathFullyQualified(system))
            {
                report.Unknown(check, "The independent OS system directory was unavailable; no actual identity or launch request started. Pre-cancellation observed; identity support remains unverified.");
                return;
            }
            system = Path.TrimEndingDirectorySeparator(system);
            foreach (var operation in operations)
            {
                token.ThrowIfCancellationRequested();
                if (provider.ProductionExecutionValidated)
                {
                    report.Fail(check, "The production gate changed unexpectedly; no further preparation or launch request was called.");
                    return;
                }
                var before = DateTimeOffset.UtcNow;
                // The provider owns its 45-second wait, singleton trust worker and file leases.
                // Never force-close a pending WinVerifyTrust/certificate/revocation operation.
                var result = provider.PrepareAsync(operation, token).GetAwaiter().GetResult();
                var after = DateTimeOffset.UtcNow;
                var assessed = SystemToolIdentityObservationPolicy.Evaluate(operation, result, system, after, token);
                var trust = result.Items.Count == 1 ? result.Items[0].TrustState : SystemToolTrustState.Unknown;
                var diagnostic = result.Items.Count == 1 ? result.Items[0].Diagnostic : null;
                string stage = diagnostic != null && Enum.IsDefined(diagnostic.Stage) ? diagnostic.Stage.ToString() : "Unknown";
                string reason = diagnostic != null && Enum.IsDefined(diagnostic.Reason) ? diagnostic.Reason.ToString() : "Unknown";
                string nativeResult = diagnostic?.NativeTrustResult is int observedNativeResult
                    ? "0x" + unchecked((uint)observedNativeResult).ToString("X8") : "none";
                // Returned fixed enum/code metadata only. Never derive a cause or trust from a
                // diagnostic, parse Message/TrustDetail, or change positive identity requirements.
                outcomes.Add($"{operation}:state={result.State}/trust={trust}/policyState={assessed.State}/identityObserved={(assessed.IdentityObserved == true ? "true" : "unknown")}/stage={stage}/reason={reason}/nativeResult={nativeResult}");
                if (result.State is not (CheckState.Available or CheckState.Partial) || trust != SystemToolTrustState.MicrosoftSignatureVerified || assessed.IdentityObserved != true)
                    continue;
                var definition = SystemToolActionsCatalog.Find(operation)!;
                if (result.Items.Count != 1 || assessed.State != result.State)
                {
                    report.Fail(check, "A positive fixed identity source contradicted its single-preview/coverage contract; no launch request called.");
                    return;
                }
                var preview = result.Items[0];
                bool unknownVersion = preview.ToolVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
                if (!SameToolDefinition(preview.Definition, definition) || preview.PreviewId == Guid.Empty ||
                    !string.Equals(preview.SystemDirectory, system, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(preview.ToolPath, Path.Combine(system, definition.FileName), StringComparison.OrdinalIgnoreCase) ||
                    !SystemToolActionsPolicy.ValidHash(preview.Sha256) || !BoundedIdentityText(preview.ToolVersion, 128) ||
                    !BoundedIdentityText(preview.Blocker, 4096) || !BoundedIdentityText(preview.TrustDetail, 4096) || preview.CanLaunch ||
                    preview.CheckedUtc.Offset != TimeSpan.Zero || preview.CheckedUtc < before || preview.CheckedUtc > after ||
                    preview.ExpiresUtc.Offset != TimeSpan.Zero || preview.ExpiresUtc <= after || preview.ExpiresUtc <= preview.CheckedUtc ||
                    preview.ExpiresUtc - preview.CheckedUtc > TimeSpan.FromMinutes(2) ||
                    assessed.CheckedUtc != preview.CheckedUtc || assessed.ExpiresUtc != preview.ExpiresUtc || unknownVersion && result.State != CheckState.Partial)
                {
                    report.Fail(check, "A positive identity preview violated exact catalog/path/hash/version/time/disabled-launch bounds. Actual paths, hashes, versions and certificate values withheld; no launch request called.");
                    return;
                }
                observed++; if (unknownVersion) unknownVersions++;
                if (provider.ProductionExecutionValidated)
                {
                    report.Fail(check, "The gate unexpectedly enabled before a genuine-preview request; LaunchAsync was not called.");
                    return;
                }
                var request = new SystemToolLaunchRequest(preview.PreviewId, operation, preview.Sha256,
                    UserSelectedOperation: true, EffectsAndElevationAcknowledged: true);
                var blocked = provider.LaunchAsync(request, token).GetAwaiter().GetResult();
                if (blocked.State != SystemToolRunState.Unavailable || blocked.Operation != operation || blocked.ElevationRequested ||
                    blocked.ProcessId != null || blocked.ClientExitCode != null || blocked.StopMonitoringRequested)
                {
                    report.Fail(check, "The closed production gate did not return an Unavailable/non-elevated/process-free record for the genuine checked preview. No servicing outcome inferred.");
                    return;
                }
                gatedRequests++;
            }
            if (provider.ProductionExecutionValidated)
            {
                report.Fail(check, "The production execution gate unexpectedly enabled; no successful disabled-gate verdict is claimed and no further request was called.");
                return;
            }
            if (observed != operations.Length || gatedRequests != operations.Length)
            {
                report.Unknown(check, $"preCancellationObserved=true; fixedIdentityCount={observed}; blockedGenuineRequestCount={gatedRequests}; productionExecutionEnabled=false; " +
                    string.Join("; ", outcomes) + ". Both genuine identities were not established. Certificate/revocation/catalog or pending-provider gaps remain unverified; no bypass, forced closure or servicing command occurred. No paths, hashes, versions, certificate values or provider messages exported.");
                return;
            }
            report.Pass(check, $"fixedIdentityCount=2; unknownVersionCount={unknownVersions}; blockedGenuineRequestCount=2; preCancellationObserved=true; catalogPathSchemaVerified=true; productionExecutionEnabled=false; " +
                string.Join("; ", outcomes) + ". Only actual OS-file identity previews and hard-gated acknowledged requests were checked. No DISM/SFC command, elevation or repair ran; no paths/hashes/versions/certificate data exported. Signature checks may use normal Windows certificate/cache infrastructure. No dynamic zero-log-write instrumentation or Windows health/action-readiness claim.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException)
        {
            report.Unknown(check, $"Fixed tool identity fixture did not complete (code 0x{ex.HResult:X8}); native trust resources may remain worker-owned. No forced closure, command, elevation or repair requested; no provider values exported.");
        }
    }

    private static bool SameToolDefinition(SystemToolActionDefinition actual, SystemToolActionDefinition expected) => actual != null &&
        actual.Operation == expected.Operation && actual.Title == expected.Title && actual.FileName == expected.FileName &&
        actual.Arguments != null && actual.Arguments.SequenceEqual(expected.Arguments) && actual.Kind == expected.Kind &&
        actual.Effect == expected.Effect && actual.Elevation == expected.Elevation && actual.Network == expected.Network &&
        actual.Cancellation == expected.Cancellation && actual.Reboot == expected.Reboot && actual.WindowsLogReference == expected.WindowsLogReference;

    private static bool BoundedIdentityText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        value == value.Trim() && !value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
}
