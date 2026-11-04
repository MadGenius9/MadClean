using System.Diagnostics;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // This runs after every capture. Only normal identity-verification and preview
    // controls are invoked. No SFC client, intent, confirmation or execution API.
    private static async Task CheckSfcPreviewInteraction(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-sfc-preview-interaction";
        bool Quiet() => !model.IsBusy && !model.HasPendingSystemToolIdentity;
        void Require(bool condition, string code)
        { if (!condition) throw new SmokeAssertionException(code); }
        if (!Quiet())
        {
            report.Unknown(check, "identityWorkflowStarted=False; previewObserved=False; querySettled=False. Earlier ownership is pending; no identity or preview command was substituted.");
            return;
        }
        bool observationInterrupted = false;
        try
        {
            var host = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Main content");
            var page = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Windows integrity tools"));
            host.SelectedItem = page; await Idle(window);
            Require(model.SelectedPage == host.Items.IndexOf(page), "ui-sfc-preview-navigation");
            var selection = Descendants<ComboBox>(window).Single(control => AutomationProperties.GetName(control) == "Windows integrity operation selection");
            var verify = Descendants<Button>(window).Single(control => AutomationProperties.GetAutomationId(control) == "SystemToolVerifyIdentity");
            var prepare = Descendants<Button>(window).Single(control => AutomationProperties.GetAutomationId(control) == "SfcVerificationPreviewButton");
            var details = Descendants<TextBox>(window).Single(control => AutomationProperties.GetAutomationId(control) == "SfcVerificationPreviewDetails");
            var status = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetAutomationId(control) == "SfcVerificationPreviewMessage");
            Require(selection.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedSystemTool) &&
                ReferenceEquals(verify.Command, model.VerifySystemToolCommand) &&
                ReferenceEquals(prepare.Command, model.PrepareSfcVerificationPreviewCommand) &&
                details.IsReadOnly && details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SfcVerificationPreviewDetails) &&
                status.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SfcVerificationPreviewMessage),
                "ui-sfc-preview-control-bindings");
            var verifyOnly = selection.Items.OfType<SystemToolActionDefinition>().Single(item => item.Operation == SystemToolOperation.SfcVerifyOnly);
            selection.SelectedItem = verifyOnly; await Idle(window);
            Require(ReferenceEquals(model.SelectedSystemTool, verifyOnly) && model.SystemToolIdentityLastObservation == null &&
                model.SfcVerificationLastPreview == null && !prepare.IsEnabled && !model.PrepareSfcVerificationPreviewCommand.CanExecute(null),
                "ui-sfc-preview-unchecked-selection");
            verify.BringIntoView(); await Idle(window);
            Require(verify.IsVisible && verify.IsEnabled && model.VerifySystemToolCommand.CanExecute(null), "ui-sfc-preview-identity-disabled");
            var identityRequestedUtc = DateTimeOffset.UtcNow;
            await InvokeSfcPreviewControl(verify, window, "ui-sfc-preview-identity-invoke");
            var observation = Stopwatch.StartNew();
            while (!Quiet() && observation.Elapsed < TimeSpan.FromSeconds(55))
            { await Task.Delay(50); await Idle(window); }
            if (!Quiet())
            {
                observationInterrupted = true;
                if (model.CancelCommand.CanExecute(null)) model.CancelCommand.Execute(null);
                // Cancellation ends the caller's observation, never WinVerifyTrust.
                // Keep the real App/dispatcher alive until the actual owner returns.
                await DrainSfcIdentityOwnership(model);
            }
            await Idle(window);
            var identityObservedUtc = DateTimeOffset.UtcNow;
            var identity = model.SystemToolIdentityLastObservation;
            if (observationInterrupted || identity == null || identity.ToolVersion == "Unknown" ||
                identity.TrustState != SystemToolTrustState.MicrosoftSignatureVerified || identity.ExpiresUtc <= identityObservedUtc)
            {
                report.Unknown(check, $"identityAccepted={identity != null}; observationInterrupted={observationInterrupted}; querySettled={Quiet()}; previewObserved=False. The real identity result was unavailable, incomplete, interrupted or no longer fresh. No identity values or provider text are exported; no SFC client was requested.");
                return;
            }
            Require(identity.PreviewId != Guid.Empty && identity.Definition.Operation == SystemToolOperation.SfcVerifyOnly &&
                identity.CheckedUtc.Offset == TimeSpan.Zero && identity.CheckedUtc >= identityRequestedUtc && identity.CheckedUtc <= identityObservedUtc &&
                identity.ExpiresUtc.Offset == TimeSpan.Zero && identity.ExpiresUtc > identity.CheckedUtc &&
                identity.ExpiresUtc - identity.CheckedUtc <= TimeSpan.FromMinutes(2) && !identity.CanLaunch,
                "ui-sfc-preview-actual-identity");
            prepare.BringIntoView(); await Idle(window);
            if (DateTimeOffset.UtcNow >= identity.ExpiresUtc)
            {
                report.Unknown(check, "identityAccepted=True; previewObserved=False; querySettled=True; identityExpired=True. The real identity expired before preview invocation; no clock override or renewed identity was substituted.");
                return;
            }
            Require(prepare.IsVisible && prepare.IsEnabled && model.PrepareSfcVerificationPreviewCommand.CanExecute(null), "ui-sfc-preview-prepare-disabled");
            var previewActivity = model.Activity.ToArray();
            string queryStatus = model.Status;
            var previewRequestedUtc = DateTimeOffset.UtcNow;
            await InvokeSfcPreviewControl(prepare, window, "ui-sfc-preview-prepare-invoke");
            var previewObservedUtc = DateTimeOffset.UtcNow;
            var preview = model.SfcVerificationLastPreview ?? throw new SmokeAssertionException("ui-sfc-preview-missing-result");
            Require(preview.RequestId != Guid.Empty && preview.IdentityPreviewId == identity.PreviewId &&
                preview.ToolPath == identity.ToolPath && preview.ToolVersion == identity.ToolVersion && preview.Sha256 == identity.Sha256 &&
                preview.PreparedUtc.Offset == TimeSpan.Zero && preview.PreparedUtc >= previewRequestedUtc && preview.PreparedUtc <= previewObservedUtc &&
                preview.ExpiresUtc == identity.ExpiresUtc && preview.PreparedUtc < preview.ExpiresUtc &&
                preview.CommandPreview == "sfc.exe /verifyonly" && ReferenceEquals(model.SystemToolIdentityLastObservation, identity),
                "ui-sfc-preview-source-binding");
            Require(details.Text == model.SfcVerificationPreviewDetails && status.Text == model.SfcVerificationPreviewMessage &&
                details.Text.Contains(preview.CommandPreview, StringComparison.Ordinal) &&
                details.Text.Contains(preview.ToolPath, StringComparison.Ordinal) && details.Text.Contains(preview.ToolVersion, StringComparison.Ordinal) &&
                details.Text.Contains(preview.PreparedUtc.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                details.Text.Contains(identity.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                details.Text.Contains(preview.ExactEffect, StringComparison.Ordinal) && details.Text.Contains(preview.Permissions, StringComparison.Ordinal) &&
                details.Text.Contains(preview.Cancellation, StringComparison.Ordinal) && details.Text.Contains(preview.Limitations, StringComparison.Ordinal) &&
                status.Text.Contains("execution: disabled", StringComparison.Ordinal) &&
                status.Text.Contains("Integrity: not checked", StringComparison.Ordinal), "ui-sfc-preview-rendered-evidence");
            Require(Quiet() && model.Activity.SequenceEqual(previewActivity) && model.Status == queryStatus,
                "ui-sfc-preview-extra-query-activity");

            // Change the real selection, then return. Neither a prior identity nor
            // the historical preview may become current for the new selection.
            selection.SelectedItem = selection.Items.OfType<SystemToolActionDefinition>().Single(item => item.Operation == SystemToolOperation.DismCheckHealth);
            await Idle(window);
            Require(model.SystemToolIdentityLastObservation == null && model.SfcVerificationLastPreview == null &&
                !prepare.IsEnabled && !model.PrepareSfcVerificationPreviewCommand.CanExecute(null) &&
                details.Text == model.SfcVerificationPreviewDetails && status.Text == model.SfcVerificationPreviewMessage &&
                status.Text.StartsWith("Not prepared", StringComparison.Ordinal), "ui-sfc-preview-selection-invalidation");
            selection.SelectedItem = verifyOnly; await Idle(window);
            Require(model.SystemToolIdentityLastObservation == null && model.SfcVerificationLastPreview == null &&
                !prepare.IsEnabled && !model.PrepareSfcVerificationPreviewCommand.CanExecute(null) &&
                model.Activity.SequenceEqual(previewActivity) && model.Status == queryStatus && Quiet(), "ui-sfc-preview-stale-identity-revival");
            report.Pass(check, "identityAccepted=True; actualPreviewControlInvoked=True; sourceBindingVerified=True; originalExpiryPreserved=True; renderedScopeVerified=True; previewActivityUnchanged=True; selectionInvalidated=True; staleIdentityRefused=True; querySettled=True. Actual identity and read-only preview controls only. No SFC/DISM client, confirmation, intent, elevation, provider values or post-query screenshots. Timed expiry and refresh/cancellation race behavior remain separately unverified.");
        }
        finally
        {
            // An invariant exception after invocation must not let outer GUI
            // teardown exit the CLR beneath an in-flight native identity worker.
            await DrainSfcIdentityOwnership(model);
        }
    }

    private static async Task InvokeSfcPreviewControl(Button button, MainWindow window, string code)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new SmokeAssertionException(code);
        invoke.Invoke();
        await Idle(window);
    }

    private static async Task DrainSfcIdentityOwnership(MainViewModel model)
    {
        // Layout may be the invariant that failed. The dispatcher continues pumping
        // this await; never repeat layout work while retaining native ownership.
        while (model.IsBusy || model.HasPendingSystemToolIdentity)
            await Task.Delay(100);
    }
}
