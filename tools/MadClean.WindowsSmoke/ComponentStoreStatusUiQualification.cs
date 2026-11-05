using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal sealed record ComponentStoreStatusUiObservation(bool? PrepareObserved, bool? AcknowledgementObserved,
    bool? ConfirmObserved, bool? ResultReturned, bool? FreshResultObserved, bool? Settled,
    ComponentStoreStatusResult? Result, int? ExceptionHResult);

/// <summary>Actual UI request in the separate authorized disposable D2 mode only; never ordinary smoke.</summary>
internal static class ComponentStoreStatusUiQualification
{
    private static readonly object Gate = new();
    private static Owner? rootedOwner;

    internal static Task<ComponentStoreStatusUiObservation> RunAsync(ComponentStoreCheckKind kind = ComponentStoreCheckKind.RecordedFlags)
    {
        // The caller additionally holds its source/run/owned-fixture authorization.
        // This harness check is not a production permission switch or a same-user security boundary.
        if (!AuthorizedMode(kind)) return Task.FromResult(new ComponentStoreStatusUiObservation(null, null, null, null, null, null, null, null));
        lock (Gate)
        {
            if (rootedOwner != null) return Task.FromResult(new ComponentStoreStatusUiObservation(null, null, null, null, null, null, null, null));
            var owner = new Owner(kind); rootedOwner = owner;
            try
            {
                owner.Thread = new Thread(owner.Run) { IsBackground = false, Name = "MadClean owned component-store UI qualification" };
                owner.Thread.SetApartmentState(ApartmentState.STA);
                owner.Thread.Start();
            }
            catch (Exception exception) { owner.BeforeStartFailed(exception.HResult); }
            return owner.Completion.Task;
        }
    }

    private static bool AuthorizedMode(ComponentStoreCheckKind kind) => Enum.IsDefined(kind) && OperatingSystem.IsWindows() && Environment.Is64BitProcess &&
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
        Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "MadGenius9/MadClean" &&
        Environment.GetEnvironmentVariable("GITHUB_REF") == "refs/heads/feat/phase-one-foundation" &&
        Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is "push" or "workflow_dispatch" &&
        Environment.GetEnvironmentVariable("GITHUB_JOB") == (kind == ComponentStoreCheckKind.ScanHealth ? "windows-component-store-scan" : "windows-component-store-status") &&
        Environment.GetEnvironmentVariable("MADCLEAN_WINDOWS_VALIDATION") == "1" &&
        Environment.GetEnvironmentVariable(kind == ComponentStoreCheckKind.ScanHealth ? "MADCLEAN_COMPONENT_STORE_SCAN_VALIDATION" : "MADCLEAN_COMPONENT_STORE_STATUS_VALIDATION") == "1";

    private sealed class Owner(ComponentStoreCheckKind kind)
    {
        internal readonly TaskCompletionSource<ComponentStoreStatusUiObservation> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Thread? Thread;
        private MadClean.App.App? application;
        private MainWindow? window;
        private MainViewModel? model;
        private bool? prepared, acknowledged, confirmed, resultReturned, freshResult, settled;
        private bool windowClosed, confirmAttempted;
        private int? exceptionHResult;
        private ComponentStoreStatusResult? actualResult;
        private DateTimeOffset? confirmBoundary;

        internal void BeforeStartFailed(int hresult)
        {
            exceptionHResult = hresult;
            Finish();
        }
        internal void Run()
        {
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                application = new MadClean.App.App(); application.InitializeComponent();
                application.StartupUri = new Uri("/MadClean;component/MainWindow.xaml", UriKind.Relative);
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.DispatcherUnhandledException += (_, args) =>
                {
                    exceptionHResult ??= args.Exception.HResult;
                    args.Handled = true;
                    // The active observer still owns settlement. Never shut down here.
                };
                application.Startup += async (_, _) => await ObserveAsync();
                application.Run();
            }
            catch (Exception exception) { exceptionHResult ??= exception.HResult; }
            // An unexpected dispatcher exit cannot release a still-pending operation.
            // This retains the own UI thread/model rather than inventing teardown or killing.
            while (Pending()) System.Threading.Thread.Sleep(100);
            Finish();
        }

        private async Task ObserveAsync()
        {
            try
            {
                await application!.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window = application.MainWindow as MainWindow ?? throw new SmokeAssertionException("component-ui-startup-window");
                window.Closed += (_, _) => windowClosed = true;
                window.Width = 1100; window.Height = 850;
                await Idle();
                model = window.DataContext as MainViewModel ?? throw new SmokeAssertionException("component-ui-data-context");
                Require(!model.IsBusy && !model.HasPendingComponentStoreStatus && !model.HasPendingMsiUninstallRequest &&
                    model.ComponentStoreLastResult == null && model.Activity.Count == 0 && model.Disks.Count == 0 &&
                    model.CleanupRows.Count == 0 && model.StartupRows.Count == 0 && model.SecurityRows.Count == 0 &&
                    model.AppRows.Count == 0 && model.SystemRows.Count == 0 && model.InstalledApplications.Count == 0 &&
                    model.PackagedApplicationRows.Count == 0 && model.DiagnosticRows.Count == 0 && model.WerRows.Count == 0,
                    "component-ui-fresh-neutral-model");
                var host = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Main content");
                host.SelectedItem = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Windows Diagnostics"));
                await Idle();
                var prepare = Find<Button>(kind == ComponentStoreCheckKind.ScanHealth ? "Prepare explicit fresh component-store scan" : "Prepare explicit recorded component-store status check");
                var confirm = Find<Button>("Confirm explicit recorded component-store status check");
                var acknowledgement = Find<CheckBox>("Acknowledge recorded component-store check scope and Windows log writes");
                var preview = Find<TextBox>("Exact component-store status command, permissions and side effects");
                Require(ReferenceEquals(prepare.Command, kind == ComponentStoreCheckKind.ScanHealth ? model.PrepareComponentStoreScanCommand : model.PrepareComponentStoreStatusCommand) &&
                    ReferenceEquals(confirm.Command, model.ConfirmComponentStoreStatusCommand) && !confirm.IsEnabled &&
                    !model.ComponentStoreAcknowledged && acknowledgement.IsChecked == false && preview.IsReadOnly,
                    "component-ui-actual-control-bindings");
                await Invoke(prepare, "component-ui-prepare");
                prepared = preview.Text == model.ComponentStorePreviewDetails &&
                    preview.Text.Contains(kind == ComponentStoreCheckKind.ScanHealth ? @"Dism\Repair-WindowsImage -Online -ScanHealth -NoRestart -ErrorAction Stop" : @"Dism\Repair-WindowsImage -Online -CheckHealth -NoRestart -ErrorAction Stop", StringComparison.Ordinal) &&
                    model.ComponentStoreSelectedKind == kind &&
                    AutomationProperties.GetName(confirm) == model.ComponentStoreConfirmAutomationName &&
                    AutomationProperties.GetName(acknowledgement) == model.ComponentStoreAcknowledgementAutomationName &&
                    model.CanAcknowledgeComponentStoreStatus && model.ComponentStoreLastResult == null && !confirm.IsEnabled &&
                    !model.HasPendingComponentStoreStatus && model.Activity.Count == 0;
                Require(prepared == true, "component-ui-fixed-preview-only");
                acknowledgement.BringIntoView(); await Idle();
                Require(acknowledgement.IsVisible && acknowledgement.IsEnabled, "component-ui-ack-enabled");
                var togglePeer = UIElementAutomationPeer.CreatePeerForElement(acknowledgement) ?? new CheckBoxAutomationPeer(acknowledgement);
                if (togglePeer.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
                    throw new SmokeAssertionException("component-ui-ack-toggle-pattern");
                toggle.Toggle(); await Idle();
                acknowledged = model.ComponentStoreAcknowledged && acknowledgement.IsChecked == true &&
                    model.ComponentStoreLastResult == null && model.Activity.Count == 0 && !model.HasPendingComponentStoreStatus;
                Require(acknowledged == true, "component-ui-real-acknowledgement");
                if (!model.ConfirmComponentStoreStatusCommand.CanExecute(null) || !confirm.IsEnabled)
                { confirmed = false; return; }
                await Invoke(confirm, "component-ui-confirm", isConfirmation: true);
                // Invoke queues the real AsyncCommand. The idle turn observes its actual start
                // or completed result without issuing a direct provider call.
                await WaitForSettlement();
                actualResult = model.ComponentStoreLastResult;
                resultReturned = actualResult != null;
                freshResult = actualResult != null && actualResult.Kind == kind && confirmBoundary is { } actualBoundary && actualResult.RequestId != Guid.Empty &&
                    actualResult.AttemptedUtc >= actualBoundary && actualResult.AttemptedUtc <= DateTimeOffset.UtcNow;
                var status = Find<TextBlock>(model.ComponentStoreStatusAutomationName);
                var assessment = Find<TextBlock>(model.ComponentStoreAssessmentAutomationName);
                var history = Find<TextBox>("Historical component-store pipeline, reported flags and transport observations");
                Require(status.Text == model.ComponentStoreMessage && assessment.Text == model.ComponentStoreCurrentAssessment &&
                    history.IsReadOnly && history.Text == model.ComponentStoreLastObservation && !model.ComponentStoreAcknowledged,
                    "component-ui-returned-result-bindings");
                await CheckSessionEvidence();
            }
            catch (Exception exception) { exceptionHResult ??= exception.HResult; }
            finally
            {
                // Even a UI invariant/dispatcher error after Confirm must retain the actual
                // owner until it settles. No deadline, watchdog, stop, kill or early shutdown.
                await WaitForSettlement();
                if (confirmAttempted && model != null)
                {
                    actualResult ??= model.ComponentStoreLastResult;
                    resultReturned ??= actualResult != null;
                    freshResult ??= actualResult != null && actualResult.Kind == kind && confirmBoundary is { } actualBoundary && actualResult.RequestId != Guid.Empty &&
                        actualResult.AttemptedUtc >= actualBoundary && actualResult.AttemptedUtc <= DateTimeOffset.UtcNow;
                }
                if (window != null && !windowClosed) window.Close();
                await WaitForSettlement();
                settled = resultReturned == true && freshResult == true && !Pending() && windowClosed;
                application!.Shutdown(exceptionHResult == null ? 0 : 1);
            }
        }

        // Additional fresh-source invariant only: selecting the result produced by the
        // normal request above must never manufacture an observation or dispatch work.
        private async Task CheckSessionEvidence()
        {
            Require(model != null && actualResult != null && actualResult.CompletedUtc != null &&
                ReferenceEquals(model.ComponentStoreLastResult, actualResult), "component-ui-session-actual-result");
            var observed = actualResult!;
            var area = (kind == ComponentStoreCheckKind.ScanHealth ? DiagnosticEvidenceArea.ComponentStoreScan : DiagnosticEvidenceArea.ComponentStoreRecordedFlags);
            var expectedSource = (kind == ComponentStoreCheckKind.ScanHealth ? "Microsoft DISM / Repair-WindowsImage ScanHealth" : "Microsoft DISM / Repair-WindowsImage CheckHealth (recorded flags)");
            var activity = model!.Activity.ToArray();
            var rows = model.InspectionEvidenceRows.ToArray();
            Require(rows.Length == 5 && rows.Select(row => row.Area).Distinct().Count() == 5 &&
                rows.Where(row => row.Area != area).All(row => row.State == CheckState.Unknown &&
                    row.AttemptedUtc == null && row.CompletedUtc == null), "component-ui-session-source-isolation");
            var row = rows.Single(entry => entry.Area == area);
            Require(row.State == observed.State && row.AttemptedUtc == observed.AttemptedUtc &&
                row.CompletedUtc == observed.CompletedUtc && row.Source == expectedSource &&
                new[] { row.Title, row.Observation, row.Source, row.Coverage, row.Uncertainty, row.Impact, row.NextStep }
                    .All(value => !string.IsNullOrWhiteSpace(value)), "component-ui-session-source-time-state");
            var host = Descendants<TabControl>(window!).Single(control => AutomationProperties.GetName(control) == "Main content");
            host.SelectedItem = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Windows Diagnostics"));
            await Idle();
            var grid = Descendants<DataGrid>(window!).Single(control => AutomationProperties.GetName(control) == "Local inspection evidence and source states");
            var details = Descendants<TextBox>(window!).Single(control => AutomationProperties.GetName(control) == "Inspection source, time, uncertainty and impact");
            Require(ReferenceEquals(grid.ItemsSource, model.InspectionEvidenceRows), "component-ui-session-grid-source");
            grid.SelectedItem = row;
            await Idle();
            Require(ReferenceEquals(grid.SelectedItem, row) && ReferenceEquals(model.SelectedInspectionEvidence, row) &&
                details.IsReadOnly && details.Text == model.InspectionEvidenceDetails &&
                details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.InspectionEvidenceDetails) &&
                details.Text.Contains(expectedSource, StringComparison.Ordinal) &&
                details.Text.Contains(observed.AttemptedUtc.ToString("u"), StringComparison.Ordinal) &&
                details.Text.Contains(observed.CompletedUtc!.Value.ToString("u"), StringComparison.Ordinal) &&
                model.Activity.SequenceEqual(activity) && !(Pending()) &&
                ReferenceEquals(model.ComponentStoreLastResult, observed) &&
                model.InspectionEvidenceRows.Count == rows.Length &&
                model.InspectionEvidenceRows.Where((entry, index) => !ReferenceEquals(entry, rows[index])).Any() == false,
                "component-ui-session-selection-no-query");
        }

        private bool Pending() => model != null && (model.IsBusy || model.HasPendingComponentStoreStatus || model.HasPendingMsiUninstallRequest);
        private async Task WaitForSettlement()
        {
            while (Pending()) { await Task.Delay(50); await Idle(); }
        }
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
        private T Find<T>(string name) where T : DependencyObject =>
            Descendants<T>(window!).Single(control => AutomationProperties.GetName(control) == name);
        private static void Require(bool condition, string code) { if (!condition) throw new SmokeAssertionException(code); }
        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T value) yield return value;
                foreach (var valueBelow in Descendants<T>(child)) yield return valueBelow;
            }
        }
        private void Finish()
        {
            // A retained/pending owner never reaches this publication path.
            if (Pending()) return;
            settled = resultReturned == true && freshResult == true && windowClosed;
            lock (Gate) if (ReferenceEquals(rootedOwner, this)) rootedOwner = null;
            Completion.TrySetResult(new(prepared, acknowledged, confirmed, resultReturned, freshResult, settled, actualResult, exceptionHResult));
        }
    }
}
