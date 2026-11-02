using System.Diagnostics;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    public static bool DeviceDriverInteractionSettled { get; private set; } = true;

    // Last UI workflow, after every capture. No fixture files or injected rows are
    // used; a pending/uncertain native owner blocks all later native fixtures.
    private static async Task CheckDeviceDriverInventoryInteraction(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-device-driver-inventory-interaction";
        bool Quiet() => !model.IsBusy && !model.HasPendingDeviceDriverRead &&
            !DeviceDriverInventory.HasPendingNativeQuery && !DeviceDriverInventory.RequiresRetention;
        void Require(bool condition, string code)
        { if (!condition) throw new SmokeAssertionException(code); }
        if (!Quiet())
        {
            DeviceDriverInteractionSettled = false;
            report.Unknown(check, "Earlier UI/native ownership is unsettled; no device-property command or additional query started. Later native fixtures must stop.");
            return;
        }
        model.SelectedPage = 4; await Idle(window);
        var categories = Descendants<TabControl>(window).Single(control =>
            control.Items.OfType<TabItem>().Any(item => Equals(item.Header, "Drivers")));
        categories.SelectedItem = categories.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Drivers"));
        await Idle(window);
        var panel = Descendants<Expander>(window).Single(control => AutomationProperties.GetName(control) == "Present-device driver metadata panel");
        panel.IsExpanded = true; await Idle(window);
        var button = Descendants<Button>(window).Single(control => AutomationProperties.GetName(control) == "Read actual present-device driver metadata");
        var grid = Descendants<DataGrid>(window).Single(control => AutomationProperties.GetName(control) == "Actual present-device driver observations");
        var details = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Selected device driver reported fields, states and limitations");
        var query = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Present-device metadata attempt and source coverage");
        var status = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetName(control) == "Present-device driver metadata status");
        Require(ReferenceEquals(button.Command, model.ReadDeviceDriversCommand) && ReferenceEquals(grid.ItemsSource, model.DeviceDriverRows) &&
            grid.IsReadOnly && details.IsReadOnly && query.IsReadOnly &&
            grid.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedDeviceDriver) &&
            details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.DeviceDriverDetails) &&
            query.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.DeviceDriverQueryDetails) &&
            status.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.DeviceDriverMessage),
            "ui-device-driver-control-bindings");
        Require(model.DeviceDriverRows.Count == 0 && grid.Items.Count == 0 && model.SelectedDeviceDriver == null &&
            model.DeviceDriverMessage.StartsWith("Not checked", StringComparison.Ordinal) &&
            model.DeviceDriverQueryDetails.StartsWith("Not checked", StringComparison.Ordinal), "ui-device-driver-not-initially-unchecked");
        var offers = model.DriverOffers.ToArray();
        var systemRows = model.SystemRows.ToArray();
        var firmwareOffers = model.FirmwareOffers.ToArray();
        var appRows = model.AppRows.ToArray();
        var earlierActivity = model.Activity.ToArray();
        string offerStatus = model.DriverOffersMessage, offerQuery = model.DriverOfferQueryDetails;
        bool OffersUnchanged() => model.DriverOffers.Count == offers.Length &&
            offers.Zip(model.DriverOffers).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            model.DriverOffersMessage == offerStatus && model.DriverOfferQueryDetails == offerQuery &&
            model.SystemRows.Count == systemRows.Length && systemRows.Zip(model.SystemRows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            model.FirmwareOffers.Count == firmwareOffers.Length && firmwareOffers.Zip(model.FirmwareOffers).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            model.AppRows.Count == appRows.Length && appRows.Zip(model.AppRows).All(pair => ReferenceEquals(pair.First, pair.Second));
        button.BringIntoView(); await Idle(window);
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        Require(button.IsVisible && button.IsEnabled && model.ReadDeviceDriversCommand.CanExecute(null), "ui-device-driver-read-command-disabled");
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new SmokeAssertionException("ui-device-driver-invoke-unavailable");
        DateTimeOffset before = DateTimeOffset.UtcNow;
        DeviceDriverInteractionSettled = false;
        invoke.Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var watch = Stopwatch.StartNew();
        while (!Quiet() && watch.Elapsed < TimeSpan.FromSeconds(45))
        { await Task.Delay(40); await Idle(window); }
        if (!Quiet())
        {
            bool requested = model.CancelCommand.CanExecute(null);
            if (requested) model.CancelCommand.Execute(null);
            report.Unknown(check, $"cancellationRequested={requested}; querySettled=False. The explicit production read exceeded its bounded wait or retained native cleanup uncertainty; no further query, force-close or metadata success is claimed.");
            return;
        }
        DeviceDriverInteractionSettled = true;
        await Idle(window);
        DateTimeOffset after = DateTimeOffset.UtcNow;
        Require(OffersUnchanged() && status.Text == model.DeviceDriverMessage && query.Text == model.DeviceDriverQueryDetails &&
            details.Text == model.DeviceDriverDetails && grid.Items.Count == model.DeviceDriverRows.Count,
            "ui-device-driver-source-display");
        Require(model.Activity.Count == Math.Min(200, earlierActivity.Length + 2) &&
            model.Activity.Skip(2).SequenceEqual(earlierActivity.Take(198)) &&
            model.Activity[1].EndsWith("   DeviceDrivers · Started", StringComparison.Ordinal) &&
            Enum.GetNames<ActivityOutcome>().Where(value => value != nameof(ActivityOutcome.Started)).Any(value =>
                model.Activity[0].EndsWith("   DeviceDrivers · " + value, StringComparison.Ordinal)), "ui-device-driver-own-query-activity");
        var rows = model.DeviceDriverRows.ToArray();
        Require(rows.Length <= DeviceDriverPropertyDecoder.MaximumObservations &&
            rows.All(row => DeviceDriverPropertyDecoder.IsConsistent(row, after) && row.CheckedUtc >= before && row.CheckedUtc <= after) &&
            rows.Select(row => row.ObservationNumber).Distinct().Count() == rows.Length,
            "ui-device-driver-actual-row-contract");
        var selected = rows.FirstOrDefault(row => row.InstalledVersion.State == DeviceDriverFieldState.Available);
        bool acceptedCoverage = status.Text.StartsWith("Available", StringComparison.Ordinal) || status.Text.StartsWith("Partial", StringComparison.Ordinal);
        if (!acceptedCoverage || selected == null)
        {
            report.Unknown(check, $"observedRows={rows.Length}; querySettled=True; populatedVersionSelectionVerified=False. The actual production query supplied no accepted installed-version row. Missing, denied, unavailable or empty evidence remains inconclusive; no sample values or successful metadata were substituted.");
            return;
        }
        string sourceQuery = query.Text, sourceStatus = status.Text;
        var activity = model.Activity.ToArray();
        bool SnapshotUnchanged() => Quiet() && OffersUnchanged() && query.Text == sourceQuery && status.Text == sourceStatus &&
            model.DeviceDriverRows.Count == rows.Length && rows.Zip(model.DeviceDriverRows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            activity.SequenceEqual(model.Activity);
        grid.SelectedItem = selected; await Idle(window);
        Require(ReferenceEquals(model.SelectedDeviceDriver, selected) && ReferenceEquals(grid.SelectedItem, selected) &&
            details.Text == model.DeviceDriverDetails && details.Text.StartsWith($"Observation {selected.ObservationNumber}: ", StringComparison.Ordinal) &&
            details.Text.Contains($"Observed UTC: {selected.CheckedUtc:u}", StringComparison.Ordinal) && SnapshotUnchanged(),
            "ui-device-driver-actual-selection");
        foreach (var field in new[] { ("DEVPKEY_Device_FriendlyName", selected.Name), ("DEVPKEY_Device_DeviceDesc", selected.Description),
            ("DEVPKEY_Device_DriverProvider", selected.Provider), ("DEVPKEY_Device_DriverVersion", selected.InstalledVersion) })
        {
            string code = field.Item2.NativeError is { } error ? $"0x{unchecked((uint)error):X8}" : "not reported";
            Require(details.Text.Contains(field.Item1 + "\nState: " + field.Item2.State + "; value: " +
                (field.Item2.Value ?? "Unknown") + "; native code: " + code, StringComparison.Ordinal), "ui-device-driver-field-state-display");
        }
        string dateCode = selected.DriverDate.NativeError is { } dateError ? $"0x{unchecked((uint)dateError):X8}" : "not reported";
        Require(details.Text.Contains("DEVPKEY_Device_DriverDate\nState: " + selected.DriverDate.State + "; value: " +
            (selected.DriverDate.Value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown") + "; native code: " + dateCode, StringComparison.Ordinal) &&
            details.Text.Contains("not establish the loaded binary", StringComparison.Ordinal) &&
            details.Text.Contains("No matching to Windows Update", StringComparison.Ordinal), "ui-device-driver-date-and-limits");
        grid.SelectedItem = null; await Idle(window);
        Require(model.SelectedDeviceDriver == null && grid.SelectedItem == null && details.Text == model.DeviceDriverDetails &&
            details.Text.StartsWith("Select an actual observation", StringComparison.Ordinal) && SnapshotUnchanged(),
            "ui-device-driver-clear-selection");
        panel.IsExpanded = false; await Idle(window); panel.IsExpanded = true; await Idle(window);
        Require(model.SelectedDeviceDriver == null && SnapshotUnchanged() && model.ReadDeviceDriversCommand.CanExecute(null),
            "ui-device-driver-panel-started-query");
        report.Pass(check, $"observedRows={rows.Length}; actualInstalledVersionSelection=True; fieldStatesAndSourcesVerified=True; selectionClearVerified=True; originalRowsRetained=True; noExtraQueryActivity=True; separateWuaOffersUnchanged=True; querySettled=True. One explicit actual production button invocation supplied real SetupAPI metadata. No device names, values, identities, timestamps, raw errors or loaded-data screenshots exported; no update, install, elevation or restart occurred. Cancellation/hot-plug and physical keyboard behavior remain unverified.");
    }
}
