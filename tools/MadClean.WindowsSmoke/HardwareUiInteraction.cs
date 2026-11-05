using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // After all screenshots. Actual metadata stays in memory and is never exported.
    private static async Task CheckHardwareInventoryInteraction(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-hardware-inventory-interaction";
        void Require(bool condition, string code)
        { if (!condition) throw new SmokeAssertionException(code); }
        bool Quiet() => !model.IsBusy && !model.HasPendingHardwareQuery;
        if (!Quiet())
        {
            report.Unknown(check, "queryStarted=False; querySettled=False. Earlier UI work remains pending; no hardware query or compatibility result was substituted.");
            return;
        }
        try
        {
            var host = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Main content");
            var page = host.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Hardware Upgrades"));
            host.SelectedItem = page; await Idle(window);
            Require(model.SelectedPage == host.Items.IndexOf(page), "ui-hardware-navigation");
            var read = Descendants<Button>(window).Single(button => AutomationProperties.GetAutomationId(button) == "HardwareReadInventory");
            var grid = Descendants<DataGrid>(window).Single(control => AutomationProperties.GetName(control) == "Detected hardware facts");
            var details = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Selected hardware fact and source");
            var status = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetAutomationId(control) == "HardwareQueryStatus");
            var attempt = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetAutomationId(control) == "HardwareQueryAttemptUtc");
            var completed = Descendants<TextBlock>(window).Single(control => AutomationProperties.GetAutomationId(control) == "HardwareQueryCompletedUtc");
            Require(ReferenceEquals(read.Command, model.ReadHardwareCommand) && ReferenceEquals(grid.ItemsSource, model.HardwareFacts) &&
                grid.IsReadOnly && details.IsReadOnly &&
                grid.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.SelectedHardwareFact) &&
                details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.HardwareFactDetails) &&
                status.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.HardwareMessage) &&
                attempt.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.HardwareAttemptedUtc) &&
                completed.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.HardwareCompletedUtc),
                "ui-hardware-control-bindings");
            Require(model.HardwareLastObservation == null && model.HardwareAttemptedUtc == null && model.HardwareCompletedUtc == null &&
                model.HardwareFacts.Count == 0 && grid.Items.Count == 0 && model.SelectedHardwareFact == null,
                "ui-hardware-initial-state");
            var earlierActivity = model.Activity.ToArray();
            var firstRequestedUtc = DateTimeOffset.UtcNow;
            if (!await InvokeHardwareRead(read, window, model))
            {
                report.Unknown(check, "queryStarted=True; observationInterrupted=True; querySettled=True. The bounded hardware wait ended; cancellation and actual query-owner return were observed. No populated result was promoted afterward.");
                return;
            }
            var first = model.HardwareLastObservation;
            if (first == null || first.State is not (CheckState.Available or CheckState.Partial) || first.Items.Count == 0)
            {
                report.Unknown(check, $"queryStarted=True; querySettled=True; resultState={first?.State.ToString() ?? "Unknown"}; projectedFacts={first?.Items.Count ?? 0}; populatedSelectionVerified=False. Actual unavailable/empty/incomplete provider evidence is not replaced by samples or upgrade claims.");
                return;
            }
            var firstRows = model.HardwareFacts.ToArray();
            int firstClasses = ValidateHardwareUiResult(first, firstRows);
            Require(CurrentHardwareReceipt(model, firstRequestedUtc) && SameHardwareRows(first.Items, firstRows) &&
                grid.Items.Count == firstRows.Length && status.Text == model.HardwareMessage &&
                attempt.Text.Contains(model.HardwareAttemptedUtc!.Value.ToString("O"), StringComparison.Ordinal) &&
                completed.Text.Contains(model.HardwareCompletedUtc!.Value.ToString("O"), StringComparison.Ordinal), "ui-hardware-first-receipt");
            Require(HardwareOnlyActivity(model.Activity, earlierActivity, 2) && NoHardwareOnlineResult(model), "ui-hardware-first-query-scope");
            Require(model.ReadManufacturerServiceFactsCommand.CanExecute(null) ==
                (HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(firstRows) != null), "ui-hardware-manufacturer-eligibility");
            var firstAttempt = model.HardwareAttemptedUtc;
            var firstCompleted = model.HardwareCompletedUtc;
            var selected = firstRows.FirstOrDefault(fact => fact.Value != "Unknown") ?? firstRows[0];
            var selectionActivity = model.Activity.ToArray();
            grid.SelectedItem = null; await Idle(window);
            Require(model.SelectedHardwareFact == null && details.Text == model.HardwareFactDetails &&
                selectionActivity.SequenceEqual(model.Activity), "ui-hardware-clear-selection");
            grid.ScrollIntoView(selected); grid.SelectedItem = selected; await Idle(window);
            Require(ReferenceEquals(model.SelectedHardwareFact, selected) && ReferenceEquals(grid.SelectedItem, selected) &&
                details.Text == model.HardwareFactDetails && details.Text.Contains(selected.Property, StringComparison.Ordinal) &&
                details.Text.Contains(selected.Value, StringComparison.Ordinal) && details.Text.Contains(selected.Source, StringComparison.Ordinal) &&
                details.Text.Contains(model.HardwareAttemptTime, StringComparison.Ordinal) &&
                details.Text.Contains("firmware-reported", StringComparison.Ordinal) &&
                details.Text.Contains("do not establish manufacturer-supported upgrades", StringComparison.Ordinal) &&
                ReferenceEquals(model.HardwareLastObservation, first) && model.HardwareAttemptedUtc == firstAttempt &&
                model.HardwareCompletedUtc == firstCompleted && selectionActivity.SequenceEqual(model.Activity), "ui-hardware-real-selection");

            // Observe the real refresh reset while its provider is pending. Nothing
            // injects an artificial result, row, clock or race into the production UI.
            bool refreshCleared = false;
            void ObserveClear()
            {
                if (model.IsBusy && model.HardwareLastObservation == null && model.HardwareFacts.Count == 0 &&
                    model.SelectedHardwareFact == null && model.HardwareCompletedUtc == null)
                    refreshCleared = true;
            }
            PropertyChangedEventHandler changed = (_, _) => ObserveClear();
            System.Collections.Specialized.NotifyCollectionChangedEventHandler rowsChanged = (_, _) => ObserveClear();
            model.PropertyChanged += changed; model.HardwareFacts.CollectionChanged += rowsChanged;
            var refreshRequestedUtc = DateTimeOffset.UtcNow;
            bool refreshReturned;
            try { refreshReturned = await InvokeHardwareRead(read, window, model); }
            finally { model.PropertyChanged -= changed; model.HardwareFacts.CollectionChanged -= rowsChanged; }
            if (!refreshReturned)
            {
                report.Unknown(check, "firstPopulatedSelection=True; refreshStarted=True; observationInterrupted=True; querySettled=True. The second actual read was interrupted; no current refresh or stale-selection proof is promoted.");
                return;
            }
            var refreshed = model.HardwareLastObservation;
            if (refreshed == null || refreshed.State is not (CheckState.Available or CheckState.Partial) || refreshed.Items.Count == 0)
            {
                report.Unknown(check, $"firstPopulatedSelection=True; refreshCleared={refreshCleared}; querySettled=True; resultState={refreshed?.State.ToString() ?? "Unknown"}; projectedFacts={refreshed?.Items.Count ?? 0}. The new actual provider result did not supply populated current evidence; prior facts remain historical only.");
                return;
            }
            var currentRows = model.HardwareFacts.ToArray();
            int currentClasses = ValidateHardwareUiResult(refreshed, currentRows);
            Require(refreshCleared && !ReferenceEquals(first, refreshed) && CurrentHardwareReceipt(model, refreshRequestedUtc) &&
                model.HardwareAttemptedUtc >= firstCompleted && SameHardwareRows(refreshed.Items, currentRows) &&
                !currentRows.Any(row => ReferenceEquals(row, selected)) && grid.Items.Count == currentRows.Length &&
                status.Text == model.HardwareMessage && HardwareOnlyActivity(model.Activity, earlierActivity, 4) && NoHardwareOnlineResult(model),
                "ui-hardware-refresh-source");
            Require(model.ReadManufacturerServiceFactsCommand.CanExecute(null) ==
                (HardwareIdentitySnapshotPolicy.FindCompleteKnownPc(currentRows) != null), "ui-hardware-refreshed-eligibility");
            var current = currentRows[0];
            grid.ScrollIntoView(current); grid.SelectedItem = current; await Idle(window);
            Require(ReferenceEquals(model.SelectedHardwareFact, current) && details.Text == model.HardwareFactDetails &&
                details.Text.Contains(current.Source, StringComparison.Ordinal), "ui-hardware-refresh-selection");
            grid.SelectedItem = null; await Idle(window);
            string noSelection = model.HardwareFactDetails;
            var beforeStaleSelection = model.Activity.ToArray();
            // This is the previously observed genuine row, not constructed data.
            model.SelectedHardwareFact = selected; await Idle(window);
            Require(model.SelectedHardwareFact == null && grid.SelectedItem == null && details.Text == noSelection &&
                !ReferenceEquals(model.SelectedHardwareFact, selected) && ReferenceEquals(model.HardwareLastObservation, refreshed) &&
                SameHardwareRows(currentRows, model.HardwareFacts) && beforeStaleSelection.SequenceEqual(model.Activity) && Quiet(),
                "ui-hardware-stale-selection-refusal");
            report.Pass(check, $"firstFacts={firstRows.Length}; firstSourceClasses={firstClasses}; currentFacts={currentRows.Length}; currentSourceClasses={currentClasses}; resultState={refreshed.State}; actualSelectionDisplayed=True; refreshCleared=True; refreshFresh=True; staleSelectionRefused=True; manufacturerEligibilityScoped=True; noOnlineQueries=True; querySettled=True. Actual local provider observations and normal controls only; values/identities and post-query screenshots withheld. Provider observations do not verify physical fit, manufacturer limits or upgrade compatibility; hosted-VM observations are not physical-PC proof. Cancellation races and physical input remain separately unverified.");
        }
        finally
        {
            // Keep the real dispatcher alive for normal query cleanup without
            // repeating layout work that may have caused an invariant exception.
            while (model.IsBusy || model.HasPendingHardwareQuery) await Task.Delay(100);
        }
    }

    private static async Task<bool> InvokeHardwareRead(Button button, MainWindow window, MainViewModel model)
    {
        button.BringIntoView(); await Idle(window);
        if (!button.IsVisible || !button.IsEnabled || !model.ReadHardwareCommand.CanExecute(null))
            throw new SmokeAssertionException("ui-hardware-read-disabled");
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new SmokeAssertionException("ui-hardware-invoke-unavailable");
        invoke.Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var watch = Stopwatch.StartNew();
        while ((model.IsBusy || model.HasPendingHardwareQuery) && watch.Elapsed < TimeSpan.FromSeconds(100))
        { await Task.Delay(50); await Idle(window); }
        bool interrupted = model.IsBusy || model.HasPendingHardwareQuery;
        if (interrupted && model.CancelCommand.CanExecute(null)) model.CancelCommand.Execute(null);
        while (model.IsBusy || model.HasPendingHardwareQuery) await Task.Delay(100);
        await Idle(window);
        return !interrupted;
    }

    private static bool CurrentHardwareReceipt(MainViewModel model, DateTimeOffset requestedUtc)
        => model.HardwareAttemptedUtc is { } attempted && attempted.Offset == TimeSpan.Zero && attempted >= requestedUtc &&
            model.HardwareCompletedUtc is { } completed && completed.Offset == TimeSpan.Zero && completed >= attempted && completed <= DateTimeOffset.UtcNow;

    private static bool SameHardwareRows(IEnumerable<HardwareFact> first, IEnumerable<HardwareFact> second)
    {
        var before = first.ToArray(); var after = second.ToArray();
        return before.Length == after.Length && before.Zip(after).All(pair => ReferenceEquals(pair.First, pair.Second));
    }

    private static int ValidateHardwareUiResult(CheckResult<HardwareFact> result, HardwareFact[] rows)
    {
        if (rows.Length < 1 || rows.Length > NativeFixtures.HardwareMetadataFactLimit || result.Items.Count != rows.Length)
            throw new SmokeAssertionException("ui-hardware-fact-count");
        var properties = new HashSet<(string Class, string Component, string Property)>();
        var completion = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var fact in rows)
        {
            if (!NativeFixtures.TryHardwareMetadata(fact, out string sourceClass, out string sourceProperty) ||
                !properties.Add((sourceClass, fact.Component, sourceProperty)) ||
                completion.TryGetValue(sourceClass, out bool complete) && complete != fact.SourceEnumerationComplete)
                throw new SmokeAssertionException("ui-hardware-source-schema");
            completion[sourceClass] = fact.SourceEnumerationComplete;
        }
        return completion.Count;
    }

    private static bool NoHardwareOnlineResult(MainViewModel model) => model.ManufacturerServiceRows.Count == 0 &&
        model.ManufacturerServiceCoverage.Count == 0 && model.SelectedManufacturerServiceFact == null;

    private static bool HardwareOnlyActivity(IEnumerable<string> actual, string[] before, int added)
    {
        var records = actual.ToArray();
        return records.Length == Math.Min(200, before.Length + added) && records.Skip(added).SequenceEqual(before.Take(200 - added)) &&
            records.Take(added).All(record => record.Contains("   HardwareInventory · ", StringComparison.Ordinal)) &&
            records.Take(added).Count(record => record.EndsWith(" · Started", StringComparison.Ordinal)) == added / 2;
    }
}
