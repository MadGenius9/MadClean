using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IDeviceDriverInventory deviceDriverInventory = new DeviceDriverInventory();
    private Task<CheckResult<DeviceDriverObservation>>? deviceDriverReadTask;
    private DeviceDriverObservation? selectedDeviceDriver;
    private CheckResult<DeviceDriverObservation>? deviceDriverSnapshot;
    private long deviceDriverGeneration;
    private DateTimeOffset? deviceDriverAttemptUtc, deviceDriverResultUtc, deviceDriverInterruptedUtc;
    private CheckState deviceDriverSourceState = CheckState.Unknown;
    private bool deviceDriverReading;
    private string deviceDriverMessage = "Not checked. Read present-device driver metadata explicitly; this does not check for updates.";
    private const string DeviceDriverLimits = "Source: local present-device SetupAPI metadata only, at most 512 observations. Observation numbers are display rows, not persistent identifiers or a unique physical-device count. Bounded hardware and compatible IDs stay in memory; only their states/counts are displayed here. No device-instance-ID or serial-number property is requested. Reported provider, INF-derived version and INF driver date do not establish the loaded binary, package authenticity, compatibility, installation time or version ordering. The separate driver prerequisite review can compare literal reported IDs from the two current sources. Names/dates are not joined; correspondence is not exact-device/OEM compatibility, update eligibility or installation permission. No download, installation, elevation or restart is performed.";
    public ObservableCollection<DeviceDriverObservation> DeviceDriverRows { get; } = [];
    public ICommand ReadDeviceDriversCommand { get; private set; } = null!;
    public string DeviceDriverMessage { get => deviceDriverMessage; private set => Set(ref deviceDriverMessage, value); }
    public bool HasPendingDeviceDriverRead => deviceDriverReadTask is { IsCompleted: false } || DeviceDriverInventory.HasPendingNativeQuery || DeviceDriverInventory.RequiresRetention;
    public DeviceDriverObservation? SelectedDeviceDriver
    {
        get => selectedDeviceDriver;
        set
        {
            if (value != null && !DeviceDriverRows.Any(row => ReferenceEquals(row, value))) return;
            Set(ref selectedDeviceDriver, value);
            PropertyChanged?.Invoke(this, new(nameof(DeviceDriverDetails)));
        }
    }
    public string DeviceDriverQueryDetails => deviceDriverAttemptUtc == null
        ? "Not checked in this session. Present-device metadata is separate from Windows Update offers."
        : $"Attempt started UTC: {deviceDriverAttemptUtc.Value:u}\n" +
            (deviceDriverReading ? "No completed metadata result is claimed."
                : deviceDriverResultUtc is { } completed
                    ? $"Provider result received UTC: {completed:u}\nSource coverage: {deviceDriverSourceState}; displayed metadata observations: {DeviceDriverRows.Count}. Empty results do not establish absent hardware or current drivers."
                    : $"Attempt interruption observed UTC: {deviceDriverInterruptedUtc?.ToString("u") ?? "Unknown"}. No completed source result is claimed.") +
            (HasPendingDeviceDriverRead ? "\nNative work or cleanup ownership remains pending/unconfirmed. Another read is blocked; cancellation does not prove native calls stopped." : "");
    public string DeviceDriverDetails
    {
        get
        {
            if (selectedDeviceDriver is not { } row) return "Select an actual observation to inspect and copy each field's source and state. Unknown/missing/denied metadata is not an absent device or an up-to-date result.\n\n" + DeviceDriverLimits;
            string display = row.Name.State == DeviceDriverFieldState.Available ? row.Name.Value!
                : row.Description.State == DeviceDriverFieldState.Available ? row.Description.Value! + " (description fallback for display only)" : "Unknown display name";
            return $"Observation {row.ObservationNumber}: {display}\nObserved UTC: {row.CheckedUtc:u}\n\n" +
                DriverField("Friendly name", "DEVPKEY_Device_FriendlyName", row.Name) + "\n" +
                DriverField("Device description", "DEVPKEY_Device_DeviceDesc", row.Description) + "\n" +
                DriverField("Reported driver provider", "DEVPKEY_Device_DriverProvider", row.Provider) + "\n" +
                DriverField("Reported INF-derived driver version", "DEVPKEY_Device_DriverVersion", row.InstalledVersion) + "\n" +
                $"Reported INF driver date · DEVPKEY_Device_DriverDate\nState: {row.DriverDate.State}; value: {row.DriverDate.Value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown"}; native code: {DriverNativeCode(row.DriverDate.NativeError)}\n\n" +
                DriverIdentifiers("Reported hardware IDs", "DEVPKEY_Device_HardwareIds", row.HardwareIds) + "\n" +
                DriverIdentifiers("Reported compatible IDs", "DEVPKEY_Device_CompatibleIds", row.CompatibleIds) + "\n\n" + DeviceDriverLimits;
        }
    }
    private static string DriverField(string label, string source, DeviceDriverText field) =>
        $"{label} · {source}\nState: {field.State}; value: {field.Value ?? "Unknown"}; native code: {DriverNativeCode(field.NativeError)}";
    private static string DriverIdentifiers(string label, string source, DeviceDriverIdentifiers field) =>
        $"{label} · {source}\nState: {field.State}; reported identifier count: {field.Values.Count}; native code: {DriverNativeCode(field.NativeError)}. Values are withheld from this display and logs; a reported ID is not a unique physical-device identity.";
    private static string DriverNativeCode(int? code) => code.HasValue ? $"0x{unchecked((uint)code.Value):X8}" : "not reported";
    private void InitializeDeviceDriverInventory() => ReadDeviceDriversCommand = new AsyncCommand(ReadDeviceDrivers,
        () => !IsBusy && !HasPendingDeviceDriverRead);
    private Task ReadDeviceDrivers()
    {
        long generation = -1;
        bool Current() => generation == deviceDriverGeneration;
        return RunDiscoveryAsync(ActivityArea.DeviceDrivers, "Reading present-device driver metadata", async token =>
        {
            if (HasPendingDeviceDriverRead)
            { DeviceDriverMessage = "A native metadata worker or cleanup uncertainty remains; no additional query started."; return CheckState.Unknown; }
            generation = ++deviceDriverGeneration;
            deviceDriverSnapshot = null;
            InvalidateDriverUpdateReview();
            DeviceDriverRows.Clear(); SelectedDeviceDriver = null;
            deviceDriverAttemptUtc = DateTimeOffset.UtcNow; deviceDriverResultUtc = null; deviceDriverInterruptedUtc = null;
            deviceDriverReading = true; deviceDriverSourceState = CheckState.Unknown;
            DeviceDriverMessage = "Reading bounded local present-device properties only. No update check or installation is requested.";
            NotifyDeviceDriverQuery();
            deviceDriverReadTask = deviceDriverInventory.ReadAsync(token);
            var result = await deviceDriverReadTask;
            token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            var received = DateTimeOffset.UtcNow;
            deviceDriverReading = false; deviceDriverResultUtc = received;
            deviceDriverSourceState = Enum.IsDefined(result.State) ? result.State : CheckState.Unknown;
            if (result.Items == null || result.Items.Count > 512 || result.Items.Any(row => !DeviceDriverPropertyDecoder.IsConsistent(row, received) || row.CheckedUtc < deviceDriverAttemptUtc) ||
                result.Items.Select(row => row.ObservationNumber).Distinct().Count() != result.Items.Count)
            {
                deviceDriverSourceState = CheckState.Unknown;
                DeviceDriverMessage = "The returned driver metadata was inconsistent or exceeded its bound; no rows were trusted. Retry the explicit local read.";
                NotifyDeviceDriverQuery(); return CheckState.Unknown;
            }
            if (result.State is CheckState.Available or CheckState.Partial)
            {
                foreach (var row in result.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Current()) return CheckState.Cancelled;
                    DeviceDriverRows.Add(row);
                }
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                deviceDriverSnapshot = result; // Only a fully published, current source can inform explicit review.
            }
            DeviceDriverMessage = Stamp(deviceDriverSourceState, result.Message) + " This is present-device metadata, not an update, trust or compatibility result.";
            NotifyDeviceDriverQuery();
            return deviceDriverSourceState;
        }, error =>
        {
            if (!Current()) return;
            deviceDriverSnapshot = null;
            InvalidateDriverUpdateReview();
            DeviceDriverRows.Clear(); SelectedDeviceDriver = null;
            deviceDriverReading = false; deviceDriverResultUtc = null; deviceDriverInterruptedUtc = DateTimeOffset.UtcNow; deviceDriverSourceState = CheckState.Unknown;
            DeviceDriverMessage = error + " No completed present-device metadata is claimed; a synchronous query or cleanup may still finish under worker ownership.";
            NotifyDeviceDriverQuery();
        });
    }
    private void NotifyDeviceDriverQuery()
    {
        PropertyChanged?.Invoke(this, new(nameof(DeviceDriverQueryDetails)));
        PropertyChanged?.Invoke(this, new(nameof(DeviceDriverDetails)));
        PropertyChanged?.Invoke(this, new(nameof(HasPendingDeviceDriverRead)));
        CommandManager.InvalidateRequerySuggested();
    }
}
