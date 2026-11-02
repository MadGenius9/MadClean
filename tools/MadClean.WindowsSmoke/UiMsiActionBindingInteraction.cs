using System.Diagnostics;
using System.Globalization;
using System.Windows;
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
    public static bool MsiActionBindingInteractionSettled { get; private set; } = true;

    private static async Task CheckMsiActionBindingInteraction(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-msi-action-binding-interaction";
        var native = new WindowsMsiActionBindingInventory(); // Constructor performs no query.
        if (model.IsBusy || native.HasPendingNativeRead)
        {
            MsiActionBindingInteractionSettled = false;
            report.Unknown(check, "An earlier UI/shared ARP or MSI worker is unsettled; no selection/query or continuation proof was attempted.");
            return;
        }
        model.SelectedPage = 6; await Idle(window);
        var sources = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Installed application sources");
        sources.SelectedIndex = 0; await Idle(window);
        var grid = Descendants<DataGrid>(window).Single(control => AutomationProperties.GetName(control) == "Loaded installed-app registrations");
        var button = Descendants<Button>(window).Single(control => AutomationProperties.GetName(control) == "Recheck selected MSI current-user and machine binding");
        var status = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetName(control) == "Selected MSI context-binding status");
        var details = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Selected MSI fresh restrictions, contexts and binding limitations");
        if (!ReferenceEquals(grid.ItemsSource, model.InstalledApplicationsView) ||
            grid.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.SelectedInstalledApplication) ||
            !ReferenceEquals(button.Command, model.RecheckMsiActionBindingCommand) || !details.IsReadOnly ||
            details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.MsiActionBindingDetails) ||
            status.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.MsiActionBindingMessage))
            throw new SmokeAssertionException("ui-msi-binding-control-bindings");
        var now = DateTimeOffset.UtcNow;
        var selected = grid.Items.OfType<InstalledApplication>().FirstOrDefault(app =>
            app.RegistrationEvidence is { WindowsInstaller: true, ProductCode: not null } evidence &&
            evidence.ObservedUtc != default && evidence.ObservedUtc.Offset == TimeSpan.Zero && evidence.ObservedUtc <= now &&
            now - evidence.ObservedUtc <= MsiRegistrationReview.ScopeLifetime &&
            model.InstalledApplications.Any(row => ReferenceEquals(row, app)));
        if (selected == null)
        {
            report.Unknown(check, "The previously loaded actual registration rows contain no fresh eligible MSI selection; no refresh, sample registration, private data injection or guessed identifier was substituted.");
            return;
        }
        var desktopRows = model.InstalledApplications.ToArray();
        string baseline = model.ApplicationBaselineMessage, inventoryMessage = model.InstalledMessage;
        bool HistoryUnchanged() => model.InstalledApplications.Count == desktopRows.Length &&
            desktopRows.Zip(model.InstalledApplications).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            model.ApplicationBaselineMessage == baseline && model.InstalledMessage == inventoryMessage;
        grid.SelectedItem = selected; await Idle(window);
        if (!ReferenceEquals(model.SelectedInstalledApplication, selected) || !ReferenceEquals(grid.SelectedItem, selected) ||
            !model.RecheckMsiActionBindingCommand.CanExecute(null) || !HistoryUnchanged())
            throw new SmokeAssertionException("ui-msi-binding-actual-selection");
        button.BringIntoView(); await Idle(window);
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (!button.IsEnabled || peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new SmokeAssertionException("ui-msi-binding-invoke");
        var before = DateTimeOffset.UtcNow;
        MsiActionBindingInteractionSettled = false;
        invoke.Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var watch = Stopwatch.StartNew();
        while ((model.IsBusy || native.HasPendingNativeRead) && watch.Elapsed < TimeSpan.FromSeconds(60))
        { await Task.Delay(40); await Idle(window); }
        if (model.IsBusy || native.HasPendingNativeRead)
        {
            bool requested = model.CancelCommand.CanExecute(null);
            if (requested) model.CancelCommand.Execute(null);
            report.Unknown(check, $"cancellationRequested={requested}; querySettled=false. The real selected binding query exceeded the bounded wait; synchronous completion is unverified and later native fixtures must not start. No force-abort was used.");
            return;
        }
        MsiActionBindingInteractionSettled = true;
        await Idle(window);
        var after = DateTimeOffset.UtcNow;
        if (!HistoryUnchanged() || !ReferenceEquals(model.SelectedInstalledApplication, selected) ||
            status.Text != model.MsiActionBindingMessage || details.Text != model.MsiActionBindingDetails)
            throw new SmokeAssertionException("ui-msi-binding-display-history");
        string text = details.Text;
        bool bound = text.StartsWith("Selection/context review: Bound\n", StringComparison.Ordinal);
        bool blocked = text.StartsWith("Selection/context review: Blocked\n", StringComparison.Ordinal);
        var contexts = text.Split('\n').Where(line => Enum.GetNames<MsiRegistrationContext>().Any(context => line.StartsWith(context + " · ", StringComparison.Ordinal))).ToArray();
        if ((!bound && !blocked) || !text.Contains("Current-user/machine enumeration complete: True", StringComparison.Ordinal) || contexts.Length is < 1 or > 3)
        {
            report.Unknown(check, "Actual selected read returned no accepted complete Bound/Blocked current-user/machine display with genuine context observations. Missing coverage stays Unknown; no populated data or success was fabricated.");
            return;
        }
        if (!(bound ? status.Text.StartsWith("Available", StringComparison.Ordinal) || status.Text.StartsWith("Partial", StringComparison.Ordinal)
                    : status.Text.StartsWith("Unavailable", StringComparison.Ordinal)) ||
            !MsiBindingInteractionTime(text, before, after) ||
            !text.Contains("CanExecute is always false", StringComparison.Ordinal) ||
            !text.Contains("No global absence is established", StringComparison.Ordinal) ||
            !text.Contains("non-atomic", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-msi-binding-current-evidence");
        var activity = model.Activity.ToArray();
        grid.SelectedItem = null; await Idle(window);
        if (model.SelectedInstalledApplication != null || grid.SelectedItem != null || model.RecheckMsiActionBindingCommand.CanExecute(null) ||
            !model.MsiActionBindingMessage.StartsWith("Not checked for this selection", StringComparison.Ordinal) ||
            model.MsiActionBindingDetails.StartsWith("Selection/context review:", StringComparison.Ordinal) ||
            details.Text != model.MsiActionBindingDetails || status.Text != model.MsiActionBindingMessage ||
            !activity.SequenceEqual(model.Activity) || !HistoryUnchanged() || model.IsBusy || native.HasPendingNativeRead)
            throw new SmokeAssertionException("ui-msi-binding-clear-invalidation");
        report.Pass(check, $"bindingState={(bound ? "Bound" : "Blocked")}; observedContextCount={contexts.Length}; enumerationComplete=true; actualSelectionAndDetailsBindings=true; selectionClearInvalidates=true; noAdditionalQueryActivity=true; visibleDesktopRowsAndBaselineMessageUnchanged=true; querySettled=true; canExecute=false. Exactly one explicit production binding workflow used the previously loaded genuine selection and fresh native ARP/MSI reads. No raw metadata, identities, flags, values or source text exported; no fixture injection, refresh command, update, uninstall, process, UAC, journal write or later screenshot occurred. No private baseline-object or global-absence claim is made.");
    }

    private static bool MsiBindingInteractionTime(string text, DateTimeOffset before, DateTimeOffset after)
    {
        const string prefix = "Observed UTC: ";
        string? line = text.Split('\n').SingleOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return line != null && DateTimeOffset.TryParseExact(line[prefix.Length..], "u", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var observed) && observed >= before.AddSeconds(-1) && observed <= after;
    }
}
