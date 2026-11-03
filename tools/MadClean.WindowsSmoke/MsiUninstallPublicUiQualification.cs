using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal sealed record MsiUninstallUiObservation(bool? InventoryRefreshObserved, bool? OwnedSelectionObserved,
    bool? PrepareObserved, bool? AcknowledgementObserved, bool? ConfirmObserved,
    bool? ResultReturned, bool? FreshResultObserved, bool? ClientObserverClaimed, bool? Settled,
    bool? WindowClosed, MsiUninstallRequestResult? Result, int? ExceptionHResult);

/// <summary>Actual public confirmation for one owned inert product, outside passive validation.</summary>
internal static class MsiUninstallPublicUiQualification
{
    private static readonly object Gate = new();
    private static Owner? rootedOwner;

    internal static Task<MsiUninstallUiObservation> RunAsync(string ownedProductCode,
        MsiUninstallFixtureAuthorization proof, Func<MsiUninstallFixtureClient, Task> observeConsent)
    {
        if (!AuthorizedMode() || proof == null || observeConsent == null ||
            !Guid.TryParseExact(ownedProductCode, "B", out var product) || product == Guid.Empty ||
            product.ToString("B").ToUpperInvariant() != ownedProductCode)
            return Task.FromResult(Empty());
        lock (Gate)
        {
            if (rootedOwner != null || MsiUninstallRequestLauncher.HasPendingRequest || MsiUninstallRequestLauncher.RequiresRetention)
                return Task.FromResult(Empty());
            var owner = new Owner(ownedProductCode, proof, observeConsent); rootedOwner = owner;
            try
            {
                owner.Thread = new Thread(owner.Run) { IsBackground = false, Name = "MadClean owned public MSI UI qualification" };
                owner.Thread.SetApartmentState(ApartmentState.STA);
                owner.Thread.Start();
            }
            catch (Exception exception) { owner.BeforeStartFailed(exception.HResult); }
            return owner.Completion.Task;
        }
    }

    private static MsiUninstallUiObservation Empty() => new(null, null, null, null, null, null, null, null, null, null, null, null);
    private static bool AuthorizedMode() => OperatingSystem.IsWindows() && Environment.Is64BitProcess &&
        MsiUninstallRequestLauncher.ProductionDispatchValidated &&
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
        Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "MadGenius9/MadClean" &&
        Environment.GetEnvironmentVariable("GITHUB_REF") == "refs/heads/feat/phase-one-foundation" &&
        Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is "push" or "workflow_dispatch" &&
        Environment.GetEnvironmentVariable("GITHUB_JOB") == "windows-msi-request" &&
        Environment.GetEnvironmentVariable("MADCLEAN_WINDOWS_VALIDATION") == "1" &&
        Environment.GetEnvironmentVariable("MADCLEAN_MSI_REQUEST_VALIDATION") == "1";

    private sealed class Owner(string ownedProductCode, MsiUninstallFixtureAuthorization proof,
        Func<MsiUninstallFixtureClient, Task> observeConsent)
    {
        internal readonly TaskCompletionSource<MsiUninstallUiObservation> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Thread? Thread;
        private readonly InstalledApplicationInventory inventoryOwnership = new();
        private MadClean.App.App? application;
        private MainWindow? window;
        private MainViewModel? model;
        private MsiUninstallRequestLauncher.PublicFixtureObservation? observer;
        private bool? refreshed, selected, prepared, acknowledged, confirmed, returned, fresh, observerClaimed, settled;
        private bool windowClosed, confirmAttempted;
        private DateTimeOffset? confirmBoundary;
        private MsiUninstallRequestResult? result;
        private int? exceptionHResult;

        internal void BeforeStartFailed(int hresult) { exceptionHResult = hresult; Finish(); }
        internal void Run()
        {
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                application = new MadClean.App.App(); application.InitializeComponent();
                application.StartupUri = new Uri("/MadClean;component/MainWindow.xaml", UriKind.Relative);
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.DispatcherUnhandledException += (_, args) =>
                { exceptionHResult ??= args.Exception.HResult; args.Handled = true; };
                application.Startup += async (_, _) => await ObserveAsync();
                application.Run();
            }
            catch (Exception exception) { exceptionHResult ??= exception.HResult; }
            // An unexpected dispatcher exit never abandons native/process ownership.
            while (Pending()) System.Threading.Thread.Sleep(100);
            Finish();
        }

        private async Task ObserveAsync()
        {
            try
            {
                await Idle();
                window = application!.MainWindow as MainWindow ?? throw new SmokeAssertionException("msi-ui-window");
                window.Closed += (_, _) => windowClosed = true;
                window.Width = 1100; window.Height = 850;
                await Idle();
                model = window.DataContext as MainViewModel ?? throw new SmokeAssertionException("msi-ui-model");
                Require(!Pending() && model.MsiUninstallLastResult == null && model.InstalledApplications.Count == 0 &&
                    model.Activity.Count == 0 && model.Disks.Count == 0 && model.CleanupRows.Count == 0 &&
                    model.StartupRows.Count == 0 && model.SecurityRows.Count == 0 && model.AppRows.Count == 0 &&
                    model.SystemRows.Count == 0 && model.PackagedApplicationRows.Count == 0 &&
                    model.DiagnosticRows.Count == 0 && model.WerRows.Count == 0,
                    "msi-ui-fresh-neutral-model");
                var host = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Main content");
                host.SelectedItem = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Installed apps"));
                await Idle();
                var registrations = Descendants<TabControl>(window).Single(control =>
                    control.Items.OfType<TabItem>().Any(item => Equals(item.Header, "Desktop registrations")));
                registrations.SelectedItem = registrations.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Desktop registrations"));
                await Idle();
                var refresh = Descendants<Button>(window).Single(button => Equals(button.Content, "Refresh registrations") &&
                    ReferenceEquals(button.Command, model.RefreshInstalledApplicationsCommand));
                await Invoke(refresh, "msi-ui-refresh");
                await WaitForSettlement();
                refreshed = !Pending();
                var ownRow = model.InstalledApplications.SingleOrDefault(row => row.RegistrationEvidence?.ProductCode == ownedProductCode &&
                    row.Name == OwnedMsiDatabase.ProductName && row.Version == OwnedMsiDatabase.ProductVersion && row.Publisher == OwnedMsiDatabase.Publisher)
                    ?? throw new SmokeAssertionException("msi-ui-owned-actual-registration");
                var grid = Find<DataGrid>("Loaded installed-app registrations");
                Require(ReferenceEquals(grid.ItemsSource, model.InstalledApplicationsView) && grid.Items.Contains(ownRow), "msi-ui-actual-loaded-view");
                grid.ScrollIntoView(ownRow); grid.SelectedItem = ownRow;
                await Idle();
                selected = ReferenceEquals(grid.SelectedItem, ownRow) && ReferenceEquals(model.SelectedInstalledApplication, ownRow) && grid.SelectedItems.Count == 1;
                Require(selected == true, "msi-ui-owned-real-selection");
                var prepare = Find<Button>("Prepare exact selected MSI uninstall request");
                var confirm = Find<Button>("Confirm exact selected Windows Installer uninstall request");
                var acknowledgement = Find<CheckBox>("Acknowledge exact selected MSI request preview");
                var preview = Find<TextBox>("Exact selected MSI product, arguments, effects and limitations");
                Require(ReferenceEquals(prepare.Command, model.PrepareMsiUninstallRequestCommand) &&
                    ReferenceEquals(confirm.Command, model.ConfirmMsiUninstallRequestCommand) && !confirm.IsEnabled &&
                    acknowledgement.IsChecked == false && !model.MsiUninstallAcknowledged && preview.IsReadOnly,
                    "msi-ui-actual-prepare-bindings");
                await Invoke(prepare, "msi-ui-prepare");
                await WaitForSettlement();
                prepared = preview.Text == model.MsiUninstallPreviewDetails &&
                    preview.Text.Contains("msiexec.exe /x " + ownedProductCode + " /qf /norestart", StringComparison.Ordinal) &&
                    model.CanAcknowledgeMsiUninstallRequest && model.MsiUninstallLastResult == null &&
                    ReferenceEquals(model.SelectedInstalledApplication, ownRow) && !confirm.IsEnabled;
                Require(prepared == true, "msi-ui-exact-preview-only");
                acknowledgement.BringIntoView(); await Idle();
                Require(acknowledgement.IsVisible && acknowledgement.IsEnabled, "msi-ui-ack-enabled");
                var peer = UIElementAutomationPeer.CreatePeerForElement(acknowledgement) ?? new CheckBoxAutomationPeer(acknowledgement);
                if (peer.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
                    throw new SmokeAssertionException("msi-ui-ack-pattern");
                toggle.Toggle(); await Idle();
                acknowledged = acknowledgement.IsChecked == true && model.MsiUninstallAcknowledged &&
                    model.MsiUninstallLastResult == null && !Pending();
                Require(acknowledged == true, "msi-ui-actual-ack");
                observer = MsiUninstallRequestLauncher.RegisterOwnedPublicFixtureObserver(proof, observeConsent);
                await Invoke(confirm, "msi-ui-confirm", true);
                await WaitForSettlement();
                ObserveResult();
                var history = Find<TextBox>("Actual MSI client result and unchecked registration status");
                var status = Find<TextBlock>("Selected MSI uninstall request status");
                Require(history.IsReadOnly && history.Text == model.MsiUninstallResultDetails && status.Text == model.MsiUninstallMessage &&
                    !model.MsiUninstallAcknowledged, "msi-ui-actual-result-bindings");
            }
            catch (Exception exception) { exceptionHResult ??= exception.HResult; }
            finally
            {
                // Even an invariant failure after Confirm must wait for the actual public
                // request and native registry workers. No deadline/kill or early shutdown.
                await WaitForSettlement();
                if (confirmAttempted) ObserveResult();
                observer?.Dispose();
                if (window != null && !windowClosed) window.Close();
                await WaitForSettlement();
                settled = returned == true && fresh == true && !Pending() && windowClosed;
                application!.Shutdown(exceptionHResult == null ? 0 : 1);
            }
        }

        private void ObserveResult()
        {
            result = model?.MsiUninstallLastResult;
            returned = result != null;
            observerClaimed = observer?.ClaimedRequestId != null;
            fresh = result != null && observer?.ClaimedRequestId == result.RequestId && confirmBoundary is { } boundary &&
                result.RequestId != Guid.Empty && result.RequestedUtc >= boundary && result.RequestedUtc <= DateTimeOffset.UtcNow;
        }
        private bool Pending() => inventoryOwnership.HasPendingNativeRead || MsiUninstallRequestLauncher.HasPendingRequest ||
            MsiUninstallRequestLauncher.RequiresRetention || model != null && (model.IsBusy || model.HasPendingMsiUninstallRequest);
        private async Task WaitForSettlement() { while (Pending()) { await Task.Delay(50); await Idle(); } }
        private async Task Idle() => await application!.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        private async Task Invoke(Button button, string code, bool isConfirmation = false)
        {
            button.BringIntoView(); await Idle();
            Require(button.IsVisible && button.IsEnabled && button.Command?.CanExecute(null) == true, code + "-enabled");
            var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new SmokeAssertionException(code + "-pattern");
            if (isConfirmation) { confirmBoundary = DateTimeOffset.UtcNow; confirmAttempted = true; }
            invoke.Invoke();
            if (isConfirmation) confirmed = true;
            await Idle();
        }
        private T Find<T>(string name) where T : DependencyObject => Descendants<T>(window!).Single(control => AutomationProperties.GetName(control) == name);
        private static void Require(bool condition, string code) { if (!condition) throw new SmokeAssertionException(code); }
        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T value) yield return value;
                foreach (var descendant in Descendants<T>(child)) yield return descendant;
            }
        }
        private void Finish()
        {
            if (Pending()) return;
            observer?.Dispose();
            settled = returned == true && fresh == true && windowClosed;
            lock (Gate) if (ReferenceEquals(rootedOwner, this)) rootedOwner = null;
            Completion.TrySetResult(new(refreshed, selected, prepared, acknowledged, confirmed, returned, fresh,
                observerClaimed, settled, window == null ? null : windowClosed, result, exceptionHResult));
        }
    }
}
