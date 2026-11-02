using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static bool DeviceDriverPropertiesCleanupConfirmed { get; set; } = true;

    // No fixture resources are created. False prevents later queries/owned-tree
    // teardown while an earlier provider worker or native set remains owned.
    private static bool DeviceDriverProperties(SmokeReport report, CancellationToken token)
    {
        const string check = "device-driver-properties-contract";
        DeviceDriverPropertiesCleanupConfirmed = false;
        bool Quiet() => !DeviceDriverInventory.HasPendingNativeQuery && !DeviceDriverInventory.RequiresRetention;
        if (!Quiet())
        {
            report.Unknown(check, "An earlier device-property worker or native release remains unconfirmed; no additional query started. ActualMetadataVerified=False; cleanupConfirmed=False.");
            return false;
        }
        try
        {
            DateTimeOffset before = DateTimeOffset.UtcNow;
            // Provider owns its bounded task/set through destruction; the caller
            // cannot kill the API call, close its set, or turn timeout into success.
            var result = new DeviceDriverInventory().ReadAsync(token).GetAwaiter().GetResult();
            DateTimeOffset after = DateTimeOffset.UtcNow;
            if (!Quiet())
            {
                report.Unknown(check, $"SourceState={result.State}; observedRows={result.Items.Count}; actualMetadataVerified=False; cleanupConfirmed=False. Native ownership remains pending or release was unconfirmed; later fixtures must stop.");
                return false;
            }
            if (!Enum.IsDefined(result.State) || result.Items.Count > DeviceDriverPropertyDecoder.MaximumObservations)
            {
                report.Fail(check, "Actual device-property source violated its fixed state/count contract; all provider values withheld.");
                return true;
            }
            var numbers = new HashSet<int>();
            int names = 0, descriptions = 0, providers = 0, versions = 0, dates = 0;
            foreach (var row in result.Items)
            {
                if (row == null || !DeviceDriverPropertyDecoder.IsConsistent(row) ||
                    row.ObservationNumber < 1 || row.ObservationNumber > DeviceDriverPropertyDecoder.MaximumObservations ||
                    !numbers.Add(row.ObservationNumber) || row.CheckedUtc.Offset != TimeSpan.Zero ||
                    row.CheckedUtc < before || row.CheckedUtc > after)
                {
                    report.Fail(check, "Actual device-property rows violated typed bounded field, unique ordinal or UTC query-interval qualification; no names, identifiers, property values or timestamps exported.");
                    return true;
                }
                if (row.Name.State == DeviceDriverFieldState.Available) names++;
                if (row.Description.State == DeviceDriverFieldState.Available) descriptions++;
                if (row.Provider.State == DeviceDriverFieldState.Available) providers++;
                if (row.InstalledVersion.State == DeviceDriverFieldState.Available) versions++;
                if (row.DriverDate.State == DeviceDriverFieldState.Available) dates++;
            }
            if (result.State == CheckState.Available &&
                (names != result.Items.Count || descriptions != result.Items.Count || providers != result.Items.Count ||
                 versions != result.Items.Count || dates != result.Items.Count))
            {
                report.Fail(check, "Incomplete device-property fields were promoted to Available source coverage; no field values exported.");
                return true;
            }
            string counts = $"SourceState={result.State}; observedRows={result.Items.Count}; nameAvailable={names}; descriptionAvailable={descriptions}; providerAvailable={providers}; installedVersionAvailable={versions}; driverDateAvailable={dates}; schemaQualified=True; cleanupConfirmed=True.";
            if (result.State is CheckState.Available or CheckState.Partial && result.Items.Count > 0 && versions > 0)
                report.Pass(check, counts + " ActualMetadataVerified=True. Real local present-device SetupAPI property projection returned at least one installed INF version; native ownership settled. INF metadata is not a loaded-binary version, signature/trust, device health, install time or update-offer match. No provider values, identities or timestamps exported; no installation or device change performed.");
            else
                report.Unknown(check, counts + " ActualMetadataVerified=False. Missing/denied/empty or incomplete native data does not establish positive installed-version compatibility or absence of devices; no success or health inferred.");
            return true;
        }
        catch (OperationCanceledException)
        {
            report.Unknown(check, "The actual device-property observation was cancelled; no positive metadata verification is claimed. Native ownership is checked separately; cancellation cannot interrupt a Windows API call.");
            return Quiet();
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            report.Unknown(check, "The actual bounded device-property observation was unavailable; no exception text, device values or inferred success exported. Native ownership is checked separately.");
            return Quiet();
        }
        finally { DeviceDriverPropertiesCleanupConfirmed = Quiet(); }
    }
}
