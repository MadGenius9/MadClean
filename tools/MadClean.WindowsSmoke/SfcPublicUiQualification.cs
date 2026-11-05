using System.Windows;
using System.Windows.Interop;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal sealed record SfcPublicUiObservation(bool? ControlBindingsObserved, bool? PrepareObserved,
    bool? AcknowledgementObserved, bool? ConfirmObserved, bool? ResultObserved, bool? FreshResultObserved,
    bool? Settled, bool? WindowClosed, SystemToolActionPreview? Identity, SfcVerificationPreview? Preview,
    SfcVerificationResult? Result, bool InvariantFailed);

/// <summary>One actual normal App request, in the separately authorized disposable job only.</summary>
internal static class SfcPublicUiQualification
{
    private static readonly object Gate = new();
    private static Owner? rootedOwner;

    internal static Task<SfcPublicUiObservation> RunAsync()
    {
        if (!AuthorizedMode()) return Task.FromResult(new SfcPublicUiObservation(null, null, null, null, null, null, null, null, null, null, null, false));
        lock (Gate)
        {
            if (rootedOwner != null) return Task.FromResult(new SfcPublicUiObservation(null, null, null, null, null, null, null, null, null, null, null, true));
            var owner = new Owner(); rootedOwner = owner;
            try
            {
                owner.Thread = new Thread(owner.Run) { IsBackground = false, Name = "MadClean owned SFC public UI qualification" };
                owner.Thread.SetApartmentState(ApartmentState.STA);
                owner.Thread.Start();
            }
            catch (Exception) { owner.BeforeStartFailed(); }
            return owner.Completion.Task;
        }
    }

    private static bool AuthorizedMode()
    {
        string? source = Environment.GetEnvironmentVariable("GITHUB_SHA");
        string? run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        string? attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        return source is { Length: 40 } && source.All(Uri.IsHexDigit) && run is { Length: > 0 and <= 32 } && run.All(char.IsAsciiDigit) &&
            int.TryParse(attempt, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsedAttempt) && parsedAttempt > 0 &&
            OperatingSystem.IsWindows() && Environment.Is64BitProcess &&
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
        Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "MadGenius9/MadClean" &&
        Environment.GetEnvironmentVariable("GITHUB_REF") == "refs/heads/feat/phase-one-foundation" &&
        Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is "push" or "workflow_dispatch" &&
        Environment.GetEnvironmentVariable("GITHUB_JOB") == "windows-sfc-client" &&
        Environment.GetEnvironmentVariable("MADCLEAN_WINDOWS_VALIDATION") == "1" &&
        Environment.GetEnvironmentVariable("MADCLEAN_SFC_CLIENT_VALIDATION") == "1" &&
        new SfcVerificationProvider().ProductionExecutionValidated;
    }

    private sealed class Owner
    {
        internal readonly TaskCompletionSource<SfcPublicUiObservation> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Thread? Thread;
        private readonly ISystemToolActions identityOwner = new SystemToolActionsProvider();
        private readonly ISfcVerificationProvider clientOwner = new SfcVerificationProvider();
        private MadClean.App.App? application;
        private MainWindow? window;
        private MainViewModel? model;
        private bool? bindings, prepared, acknowledged, confirmed, resultObserved, fresh, settled;
        private bool windowClosed, invariantFailed, expectedShutdown;
        private SystemToolActionPreview? actualIdentity;
        private SfcVerificationPreview? actualPreview;
        private SfcVerificationResult? actualResult;
        private DateTimeOffset? confirmBoundary;

        internal void BeforeStartFailed() { invariantFailed = true; Finish(); }
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
                    invariantFailed = true;
                    args.Handled = true;
                    // Keep the real observer/dispatcher alive for owned settlement.
                };
                application.Startup += async (_, _) => await ObserveAsync();
                application.Run();
                if (!expectedShutdown) invariantFailed = true;
            }
            catch (Exception) { invariantFailed = true; }
            // A departed dispatcher cannot resume its model continuations. Observe
            // the actual shared owners, not a possibly latched model.IsBusy flag.
            while (NativePending()) System.Threading.Thread.Sleep(100);
            Finish();
        }

        private async Task ObserveAsync()
        {
            try
            {
                await Idle();
                window = application!.MainWindow as MainWindow ?? throw new SmokeAssertionException("sfc-public-startup-window");
                window.Closed += (_, _) => windowClosed = true;
                window.Width = 1100; window.Height = 850;
                await Idle();
                model = window.DataContext as MainViewModel ?? throw new SmokeAssertionException("sfc-public-data-context");
                Require(!ModelPending() && model.SfcVerificationLastResult == null && model.SystemToolIdentityLastObservation == null &&
                    model.SfcVerificationLastPreview == null && model.Activity.Count == 0 && model.Disks.Count == 0 &&
                    model.CleanupRows.Count == 0 && model.StartupRows.Count == 0 && model.SecurityRows.Count == 0 &&
                    model.AppRows.Count == 0 && model.SystemRows.Count == 0 && model.InstalledApplications.Count == 0 &&
                    model.PackagedApplicationRows.Count == 0 && model.DiagnosticRows.Count == 0 && model.WerRows.Count == 0,
                    "sfc-public-neutral-model");
                var host = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Main content");
                host.SelectedItem = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Windows integrity tools"));
                await Idle();
                var selection = Descendants<ComboBox>(window).Single(control => AutomationProperties.GetName(control) == "Windows integrity operation selection");
                var verify = Find<Button>("SystemToolVerifyIdentity");
                var prepare = Find<Button>("SfcVerificationPreviewButton");
                var acknowledge = Find<CheckBox>("SfcVerificationAcknowledgement");
                var confirm = Find<Button>("SfcVerificationConfirm");
                var details = Find<TextBox>("SfcVerificationPreviewDetails");
                var status = Find<TextBlock>("SfcVerificationStatus");
                var assessment = Find<TextBlock>("SfcVerificationAssessment");
                var history = Find<TextBox>("SfcVerificationObservation");
                bindings = selection.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedSystemTool) &&
                    ReferenceEquals(verify.Command, model.VerifySystemToolCommand) && ReferenceEquals(prepare.Command, model.PrepareSfcVerificationPreviewCommand) &&
                    ReferenceEquals(confirm.Command, model.ConfirmSfcVerificationCommand) && !confirm.IsEnabled && !acknowledge.IsChecked.GetValueOrDefault() &&
                    !model.SfcVerificationAcknowledged && acknowledge.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SfcVerificationAcknowledged) &&
                    details.IsReadOnly && details.Text == model.SfcVerificationPreviewDetails &&
                    history.IsReadOnly && history.Text == model.SfcVerificationLastObservation &&
                    status.Text == model.SfcVerificationMessage && assessment.Text == model.SfcVerificationAssessment;
                Require(bindings == true, "sfc-public-control-bindings");
                selection.SelectedItem = selection.Items.OfType<SystemToolActionDefinition>().Single(item => item.Operation == SystemToolOperation.SfcVerifyOnly);
                await Idle();
                Require(model.SystemToolIdentityLastObservation == null && model.SfcVerificationLastPreview == null && !confirm.IsEnabled && !prepare.IsEnabled,
                    "sfc-public-unchecked-selection");
                var identityBoundary = DateTimeOffset.UtcNow;
                await Invoke(verify, "sfc-public-identity");
                await WaitForSettlement();
                actualIdentity = model.SystemToolIdentityLastObservation;
                if (actualIdentity == null || actualIdentity.ToolVersion == "Unknown" || actualIdentity.ExpiresUtc <= DateTimeOffset.UtcNow) return;
                Require(actualIdentity.Definition.Operation == SystemToolOperation.SfcVerifyOnly && !actualIdentity.CanLaunch &&
                    actualIdentity.TrustState == SystemToolTrustState.MicrosoftSignatureVerified && actualIdentity.CheckedUtc >= identityBoundary &&
                    actualIdentity.CheckedUtc.Offset == TimeSpan.Zero && actualIdentity.ExpiresUtc.Offset == TimeSpan.Zero,
                    "sfc-public-actual-identity");
                var activity = model.Activity.ToArray();
                await Invoke(prepare, "sfc-public-prepare");
                actualPreview = model.SfcVerificationLastPreview;
                prepared = actualPreview != null && actualPreview.IdentityPreviewId == actualIdentity.PreviewId &&
                    actualPreview.ToolPath == actualIdentity.ToolPath && actualPreview.ToolVersion == actualIdentity.ToolVersion && actualPreview.Sha256 == actualIdentity.Sha256 &&
                    actualPreview.ExpiresUtc == actualIdentity.ExpiresUtc && actualPreview.PreparedUtc >= actualIdentity.CheckedUtc &&
                    actualPreview.PreparedUtc < actualPreview.ExpiresUtc && actualPreview.CommandPreview == "sfc.exe /verifyonly" &&
                    details.Text == model.SfcVerificationPreviewDetails && details.Text.Contains(actualPreview.CommandPreview, StringComparison.Ordinal) &&
                    !confirm.IsEnabled && !model.SfcVerificationAcknowledged && !ModelPending() && model.Activity.SequenceEqual(activity);
                Require(prepared == true, "sfc-public-identity-bound-preview");
                acknowledge.BringIntoView(); await Idle();
                Require(acknowledge.IsVisible && acknowledge.IsEnabled && model.CanAcknowledgeSfcVerification, "sfc-public-ack-enabled");
                var togglePeer = UIElementAutomationPeer.CreatePeerForElement(acknowledge) ?? new CheckBoxAutomationPeer(acknowledge);
                if (togglePeer.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle) throw new SmokeAssertionException("sfc-public-toggle-pattern");
                toggle.Toggle(); await Idle();
                acknowledged = model.SfcVerificationAcknowledged && acknowledge.IsChecked == true && model.SfcVerificationLastResult == null &&
                    !ModelPending() && model.Activity.SequenceEqual(activity);
                Require(acknowledged == true, "sfc-public-actual-ack");
                if (!model.SfcVerificationExecutionValidated || !confirm.IsEnabled || !model.ConfirmSfcVerificationCommand.CanExecute(null))
                { confirmed = false; return; }
                await Invoke(confirm, "sfc-public-confirm", confirmation: true);
                await WaitForSettlement();
                actualResult = model.SfcVerificationLastResult;
                resultObserved = actualResult != null;
                fresh = actualResult != null && actualPreview != null && confirmBoundary is { } boundary &&
                    actualResult.RequestId == actualPreview.RequestId && actualResult.AttemptedUtc >= boundary &&
                    actualResult.AttemptedUtc <= DateTimeOffset.UtcNow && actualResult.Integrity == SfcIntegrityAssessment.Unknown;
                Require(status.Text == model.SfcVerificationMessage && assessment.Text == model.SfcVerificationAssessment &&
                    history.Text == model.SfcVerificationLastObservation && history.IsReadOnly && !model.SfcVerificationAcknowledged &&
                    assessment.Text.Contains("Unknown", StringComparison.Ordinal), "sfc-public-result-bindings");
                await CheckSessionEvidence();
            }
            catch (Exception) { invariantFailed = true; }
            finally
            {
                // No layout or dispatcher call is repeated during the actual-owner
                // drain, even if such a call caused the original invariant failure.
                await WaitForSettlement();
                if (confirmed == true && model != null)
                {
                    actualResult ??= model.SfcVerificationLastResult;
                    resultObserved ??= actualResult != null;
                    fresh ??= actualResult != null && actualPreview != null && confirmBoundary is { } boundary &&
                        actualResult.RequestId == actualPreview.RequestId && actualResult.AttemptedUtc >= boundary &&
                        actualResult.AttemptedUtc <= DateTimeOffset.UtcNow && actualResult.Integrity == SfcIntegrityAssessment.Unknown;
                }
                try { if (window != null && !windowClosed) window.Close(); }
                catch (Exception) { invariantFailed = true; }
                await WaitForSettlement();
                settled = !ModelPending() && !NativePending();
                expectedShutdown = true;
                application!.Shutdown(invariantFailed ? 1 : 0);
            }
        }

        // Additional fresh-source invariant only: selecting the result produced by the
        // normal request above must never manufacture an observation or dispatch work.
        private async Task CheckSessionEvidence()
        {
            Require(model != null && actualResult != null && actualResult.CompletedUtc != null &&
                ReferenceEquals(model.SfcVerificationLastResult, actualResult), "sfc-public-session-actual-result");
            var observed = actualResult!;
            var area = DiagnosticEvidenceArea.SystemFiles;
            var expectedSource = "Microsoft SFC / sfc.exe /verifyonly client";
            var activity = model!.Activity.ToArray();
            var rows = model.InspectionEvidenceRows.ToArray();
            Require(rows.Length == 5 && rows.Select(row => row.Area).Distinct().Count() == 5 &&
                rows.Where(row => row.Area != area).All(row => row.State == CheckState.Unknown &&
                    row.AttemptedUtc == null && row.CompletedUtc == null), "sfc-public-session-source-isolation");
            var row = rows.Single(entry => entry.Area == area);
            Require(row.State == observed.State && row.AttemptedUtc == observed.AttemptedUtc &&
                row.CompletedUtc == observed.CompletedUtc && row.Source == expectedSource &&
                new[] { row.Title, row.Observation, row.Source, row.Coverage, row.Uncertainty, row.Impact, row.NextStep }
                    .All(value => !string.IsNullOrWhiteSpace(value)), "sfc-public-session-source-time-state");
            Require(observed.Integrity == SfcIntegrityAssessment.Unknown &&
                row.Uncertainty.Contains("Integrity remains Unknown", StringComparison.Ordinal) &&
                (row.State != CheckState.Available || row.Observation.Contains("integrity remains Unknown", StringComparison.Ordinal)),
                "sfc-public-session-integrity-unknown");
            var host = Descendants<TabControl>(window!).Single(control => AutomationProperties.GetName(control) == "Main content");
            host.SelectedItem = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Windows Diagnostics"));
            await Idle();
            var grid = Descendants<DataGrid>(window!).Single(control => AutomationProperties.GetName(control) == "Local inspection evidence and source states");
            var details = Descendants<TextBox>(window!).Single(control => AutomationProperties.GetName(control) == "Inspection source, time, uncertainty and impact");
            Require(ReferenceEquals(grid.ItemsSource, model.InspectionEvidenceRows), "sfc-public-session-grid-source");
            grid.SelectedItem = row;
            await Idle();
            Require(ReferenceEquals(grid.SelectedItem, row) && ReferenceEquals(model.SelectedInspectionEvidence, row) &&
                details.IsReadOnly && details.Text == model.InspectionEvidenceDetails &&
                details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.InspectionEvidenceDetails) &&
                details.Text.Contains(expectedSource, StringComparison.Ordinal) &&
                details.Text.Contains(observed.AttemptedUtc.ToString("u"), StringComparison.Ordinal) &&
                details.Text.Contains(observed.CompletedUtc!.Value.ToString("u"), StringComparison.Ordinal) &&
                model.Activity.SequenceEqual(activity) && !(ModelPending() || NativePending()) &&
                ReferenceEquals(model.SfcVerificationLastResult, observed) &&
                model.InspectionEvidenceRows.Count == rows.Length &&
                model.InspectionEvidenceRows.Where((entry, index) => !ReferenceEquals(entry, rows[index])).Any() == false,
                "sfc-public-session-selection-no-query");
        }

        private bool NativePending() => identityOwner.HasPendingIdentityWork || clientOwner.HasPendingWork || clientOwner.RequiresRetention;
        private bool ModelPending() => model != null && (model.IsBusy || model.HasPendingSystemToolIdentity || model.HasPendingSfcVerification ||
            model.HasPendingComponentStoreStatus || model.HasPendingMsiUninstallRequest);
        private async Task WaitForSettlement()
        { while (ModelPending() || NativePending()) await Task.Delay(100); }
        private async Task Idle() => await application!.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        private async Task Invoke(Button button, string code, bool confirmation = false)
        {
            button.BringIntoView(); await Idle();
            Require(button.IsVisible && button.IsEnabled && button.Command?.CanExecute(null) == true, code + "-enabled");
            var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new SmokeAssertionException(code + "-pattern");
            if (confirmation) confirmBoundary = DateTimeOffset.UtcNow;
            invoke.Invoke();
            if (confirmation) confirmed = true;
            await Idle();
        }
        private T Find<T>(string id) where T : DependencyObject => Descendants<T>(window!).Single(control => AutomationProperties.GetAutomationId(control) == id);
        private static void Require(bool condition, string code) { if (!condition) throw new SmokeAssertionException(code); }
        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T value) yield return value;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        private void Finish()
        {
            if (NativePending()) return;
            settled ??= !ModelPending() && !NativePending();
            Completion.TrySetResult(new(bindings, prepared, acknowledged, confirmed, resultObserved, fresh, settled, windowClosed,
                actualIdentity, actualPreview, actualResult, invariantFailed));
        }
    }
}
