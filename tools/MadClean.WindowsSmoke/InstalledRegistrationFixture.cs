using System.ComponentModel;
using System.Globalization;
using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static CheckResult<InstalledApplication>? InstalledRegistrations(SmokeReport report, CancellationToken token)
    {
        const string check = "installed-registration-contract";
        var inventory = new InstalledApplicationInventory();
        if (inventory.HasPendingNativeRead)
        {
            report.Unknown(check, "An earlier registration worker is pending; no second query or cancellation experiment was started. Native registration metadata remains unverified.");
            return null;
        }
        try
        {
            bool cancellationObserved;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    var stopped = inventory.ScanAsync(cancelled.Token).GetAwaiter().GetResult();
                    cancellationObserved = stopped.State == CheckState.Cancelled && stopped.Items.Count == 0;
                }
                catch (OperationCanceledException) { cancellationObserved = true; }
            }
            if (!cancellationObserved || inventory.HasPendingNativeRead)
            {
                report.Fail(check, "Pre-cancellation did not remain a cancelled/empty result or exception with no pending registration worker. No second query was started.");
                return null;
            }
            // Explicit actual CI-host metadata read, not a synthetic app list or ARP write.
            // No command value, executable, uninstall mechanism dispatch or plan is requested.
            var before = DateTimeOffset.UtcNow;
            var result = Query(ct => inventory.ScanAsync(ct), token);
            var after = DateTimeOffset.UtcNow;
            if (inventory.HasPendingNativeRead)
            {
                report.Unknown(check, $"Registration state={result.State}; preCancellationObserved=true; pendingWorker=true. The bounded caller returned while native work remains owned by its worker; no completed inventory proof or forced cancellation is claimed.");
                return null;
            }
            if (result.State is not (CheckState.Available or CheckState.Partial) || result.Items.Count == 0)
            {
                report.Unknown(check, $"Registration state={result.State}; itemCount={result.Items.Count}; preCancellationObserved=true; pendingWorker=false; actualMetadataVerified=false. Missing/empty/incomplete/unavailable metadata is not proof of no installed applications or completed coverage.");
                return null;
            }
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (result.Items.Count > 20_000 || result.Items.Any(app => !ValidInstalledRegistration(app, before, after, result.State) || !identities.Add(app.Identity)))
            {
                report.Fail(check, "Actual registration projection violated its bound, unique fixed-source identity, normalized local path, typed metadata/evidence or observation-time contract. All offending values, identifiers, paths and provider text are withheld.");
                return null;
            }
            report.Pass(check, $"Registration state={result.State}; itemCount={result.Items.Count}; fixedSourceProvenanceVerified=true; projectedSchemaValid=true; preCancellationObserved=true; pendingWorker=false; actualMetadataVerified=true. " +
                "This proves only the returned real registration metadata contract. Partial coverage remains incomplete; portable/unregistered/packaged applications may be absent. Null mechanism/restriction evidence stays unknown; registration hints never grant removal eligibility. No names, IDs/product codes, paths, times, flag values or provider messages exported; no uninstall strings read, ARP writes, action plans, handoffs or uninstall performed.");
            return result; // In-memory reuse only; the distinct selected MSI check owns its verdict.
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            report.Unknown(check, $"The bounded registration metadata fixture did not complete (code 0x{ex.HResult:X8}); actualMetadataVerified=false. A native worker may still own pending handles; no force-close, empty-inventory conclusion or metadata export occurred.");
            return null;
        }
    }

    private static bool ValidInstalledRegistration(InstalledApplication app, DateTimeOffset before, DateTimeOffset after, CheckState snapshotState)
    {
        if (app == null || !InstalledMetadataText(app.Identity, 1_024) || !InstalledMetadataText(app.Name, 1_024) ||
            !InstalledMetadataText(app.Version, 256) || !InstalledMetadataText(app.Publisher, 512) || app.RegistrationEvidence is not { } evidence)
            return false;
        string? prefix = app.Source switch
        {
            "HKCU / shared / Add or Remove Programs" => "HKCU/shared/",
            "HKLM / 64-bit / Add or Remove Programs" when Environment.Is64BitOperatingSystem => "HKLM/64/",
            "HKLM / 32-bit / Add or Remove Programs" => "HKLM/32/",
            _ => null
        };
        if (prefix == null) return false;
        prefix += @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
        if (!app.Identity.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string subkey = app.Identity[prefix.Length..];
        if (!InstalledMetadataText(subkey, 256) || subkey.Contains('\\') ||
            evidence.Identity != app.Identity || evidence.Source != app.Source ||
            ((!evidence.NoRemove.HasValue || !evidence.WindowsInstaller.HasValue || !evidence.HasRegisteredUninstaller.HasValue) && snapshotState != CheckState.Partial) ||
            evidence.ObservedUtc.Offset != TimeSpan.Zero || evidence.ObservedUtc < before || evidence.ObservedUtc > after ||
            app.InstallLocation != null && InstalledApplicationPaths.NormalizeLocalDirectory(app.InstallLocation) != app.InstallLocation)
            return false;
        string? expectedProduct = evidence.WindowsInstaller == true ? InstalledAppActionPlans.ProductCodeFromSubkey(subkey) : null;
        if (evidence.ProductCode != expectedProduct) return false;
        string expectedSupport = evidence.NoRemove == true ? InstalledApplicationSupport.Describe(false, false, true) :
            evidence.NoRemove.HasValue && evidence.WindowsInstaller.HasValue && evidence.HasRegisteredUninstaller.HasValue
            ? InstalledApplicationSupport.Describe(evidence.WindowsInstaller.Value, evidence.HasRegisteredUninstaller.Value, evidence.NoRemove.Value)
            : "Unknown";
        return app.UninstallSupport == expectedSupport;
    }

    private static bool InstalledMetadataText(string? text, int limit) => !string.IsNullOrWhiteSpace(text) && text.Length <= limit &&
        !text.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format);
}
