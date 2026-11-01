using System.ComponentModel;
using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void DefenderStatus(SmokeReport report, CancellationToken token)
    {
        const string check = "defender-status-contract";
        bool evidenceReported = false;
        try
        {
            // Explicit disposable-host read only. The existing production adapter uses fixed
            // Get-MpComputerStatus and Get-MpThreatDetection getters, not a scan/remediation,
            // signature update, preference change or elevation. Never export its returned values.
            var before = DateTimeOffset.UtcNow;
            var result = Query(ct => new WindowsInventory().DefenderAsync(ct), token);
            var after = DateTimeOffset.UtcNow;
            DefenderInspectionEvidence(result, before, after, report, token);
            evidenceReported = true;
            if (result.State is not (CheckState.Available or CheckState.Partial))
            {
                string reason = result.State switch
                {
                    CheckState.Unavailable => "The provider/cmdlets are unavailable; live status parsing remains unverified.",
                    CheckState.Unsupported => "This provider/platform is unsupported; live status parsing remains unverified.",
                    CheckState.Failed => "The bounded provider read failed or timed out; no successful live parse is claimed.",
                    CheckState.Cancelled => "The read did not complete; no live status result is trusted.",
                    _ => "No recognized completed provider result was returned; compatibility/coverage remains unknown."
                };
                report.Unknown(check, $"Defender state={result.State}; parsedActualResult=false; knownStatusFlagCount=0. {reason} No provider text, protection/settings values, detection IDs, dates or signature versions are exported.");
                return;
            }
            if (result.Items.Count != 1)
            {
                report.Fail(check, "A readable Defender result did not contain exactly one projected status object. No security data is exported.");
                return;
            }
            var data = result.Items[0];
            if (data == null || data.Mode == null || data.SignatureVersion == null || data.SignatureUpdated == null ||
                data.Findings == null || data.FindingsState is not ("Available" or "Failed") ||
                data.Findings.Any(finding => finding == null || finding.ThreatId == null || finding.ActionSuccess == null || finding.LastStatusChange == null))
            {
                report.Fail(check, "The real Defender projection violated its fixed status/history scalar schema. Offending fields and all actual security values are withheld.");
                return;
            }
            int knownFlags = (data.AntivirusEnabled.HasValue ? 1 : 0) + (data.RealTimeEnabled.HasValue ? 1 : 0) + (data.ServiceEnabled.HasValue ? 1 : 0);
            bool historyReadable = data.FindingsState == "Available";
            bool incomplete = knownFlags < 3 || !historyReadable;
            if ((result.State == CheckState.Available && incomplete) || (result.State == CheckState.Partial && !incomplete))
            {
                report.Fail(check, "The real Defender status/history completeness contradicts its Available/Partial classification. No actual flag/history values are exported.");
                return;
            }
            if (knownFlags == 0)
            {
                report.Unknown(check, $"Defender state={result.State}; projectedSchemaValid=true; parsedActualResult=false; knownStatusFlagCount=0; findingCount={data.Findings.Count}. A structured response supplied no known status flags, so live status coverage remains unverified.");
                return;
            }
            report.Pass(check, $"Defender state={result.State}; projectedSchemaValid=true; parsedActualResult=true; knownStatusFlagCount={knownFlags}; findingCount={data.Findings.Count}; historyReadable={(historyReadable ? "true" : "false")}. " +
                "This verifies the actual projected read-result contract only, including any stated Partial coverage. Known flags may be false; no protected/active/clean/signatures-current verdict is made. Empty history does not establish no threats. No modes, security flag values, versions, IDs, dates, paths or provider messages exported. Only fixed read-only getters requested; no scan, remediation, signature update, configuration change or elevation.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            report.Unknown(check, $"The bounded Defender read did not complete (code 0x{ex.HResult:X8}); parsedActualResult=false. No live status verification, protection verdict or raw security/provider data is exported.");
            if (!evidenceReported) report.Unknown("defender-inspection-evidence-contract", "The shared actual Defender read/projection did not complete; no second query or security-value export, no protection/health proof.");
        }
    }
}
