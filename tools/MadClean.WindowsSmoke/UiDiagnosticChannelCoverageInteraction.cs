using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    public static bool DiagnosticChannelInteractionSettled { get; private set; } = true;
    // Run only after every capture. Actual passive event metadata is never exported here.
    private static async Task CheckDiagnosticChannelCoverageInteraction(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-diagnostic-channel-coverage-interaction";
        if (model.IsBusy || !model.ReadDiagnosticsCommand.CanExecute(null))
        { report.Unknown(check, "An earlier UI operation is unsettled; no event query or channel-completion proof was attempted."); return; }
        model.SelectedPage = 14; await Idle(window);
        var grid = Descendants<DataGrid>(window).Single(control => AutomationProperties.GetName(control) == "Application and System event query coverage");
        var details = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Selected event channel status, limits and retention uncertainty");
        var range = Descendants<ComboBox>(window).Single(control => AutomationProperties.GetName(control) == "Diagnostic evidence time range");
        var button = Descendants<Button>(window).Single(control => ReferenceEquals(control.Command, model.ReadDiagnosticsCommand));
        var status = Descendants<TextBlock>(window).Single(control =>
            control.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(MainViewModel.DiagnosticsMessage));
        if (!ReferenceEquals(grid.ItemsSource, model.DiagnosticChannelRows) || !grid.IsReadOnly || !details.IsReadOnly ||
            grid.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.SelectedDiagnosticChannel) ||
            details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.DiagnosticChannelDetails) ||
            range.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.SelectedDiagnosticRange))
            throw new SmokeAssertionException("ui-diagnostic-channel-binding");
        foreach (string path in new[] { nameof(DiagnosticChannelCoverage.UsableMatchingRecords), nameof(DiagnosticChannelCoverage.Truncated), nameof(DiagnosticChannelCoverage.WholeLogRecordCount) })
            if (!grid.Columns.OfType<DataGridTextColumn>().Any(column => column.Binding is Binding binding &&
                binding.Path?.Path == path && Equals(binding.TargetNullValue, "Unknown")))
                throw new SmokeAssertionException("ui-diagnostic-channel-unknown-cell-binding");
        var selectedRange = model.DiagnosticRanges.First(choice => choice != model.SelectedDiagnosticRange);
        range.SelectedItem = selectedRange; await Idle(window);
        if (model.SelectedDiagnosticRange != selectedRange || !DiagnosticInteractionReset(model) ||
            status.Text != model.DiagnosticsMessage || details.Text != model.DiagnosticChannelDetails)
            throw new SmokeAssertionException("ui-diagnostic-channel-initial-range-reset");
        var before = DateTimeOffset.UtcNow;
        button.BringIntoView(); await Idle(window);
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (!button.IsEnabled || peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new SmokeAssertionException("ui-diagnostic-channel-invoke");
        DiagnosticChannelInteractionSettled = false;
        invoke.Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var watch = Stopwatch.StartNew();
        while (model.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(60)) { await Task.Delay(40); await Idle(window); }
        if (model.IsBusy)
        {
            if (model.CancelCommand.CanExecute(null)) model.CancelCommand.Execute(null);
            report.Unknown(check, "The actual passive event query exceeded its bounded UI wait. Existing cancellation was requested; native/provider completion is unverified and no later query or capture is authorized.");
            return;
        }
        DiagnosticChannelInteractionSettled = true;
        await Idle(window);
        var after = DateTimeOffset.UtcNow;
        var rows = model.DiagnosticChannelRows.ToArray();
        if (rows.Length != 2 || rows.Count(row => row.Channel == "Application") != 1 || rows.Count(row => row.Channel == "System") != 1 ||
            rows.Any(row => row.Reason is DiagnosticChannelCoverageReason.NotChecked or DiagnosticChannelCoverageReason.NoCompletedReport or
                DiagnosticChannelCoverageReason.MissingResponse or DiagnosticChannelCoverageReason.AmbiguousResponse or DiagnosticChannelCoverageReason.MalformedResponse or DiagnosticChannelCoverageReason.UnknownStatus) ||
            !rows.Any(row => (row.State is CheckState.Available or CheckState.Partial) && row.Reason == DiagnosticChannelCoverageReason.Queried))
        { report.Unknown(check, "The real event query supplied no complete pair of source-qualified channel coverage rows with a genuine queried source; no fixture records or successful coverage were substituted."); return; }
        if (status.Text != model.DiagnosticsMessage || !DiagnosticInteractionWindow(model.DiagnosticsWindow, before, after, selectedRange.Days))
            throw new SmokeAssertionException("ui-diagnostic-channel-status-window");
        var activity = model.Activity.ToArray();
        foreach (var row in rows)
        {
            if (!Enum.IsDefined(row.State) || !Enum.IsDefined(row.Reason) || !Enum.IsDefined(row.RetentionState) ||
                row.UsableMatchingRecords is < 0 || row.RejectedRecords is < 0 || row.DuplicateRecords is < 0 || row.WholeLogRecordCount is < 0 ||
                string.IsNullOrWhiteSpace(row.Explanation) || row.Explanation.Length > 32768 ||
                (row.State is CheckState.Available or CheckState.Partial) && (row.UsableMatchingRecords == null || row.Truncated == null))
                throw new SmokeAssertionException("ui-diagnostic-channel-source-schema");
            grid.SelectedItem = row; await Idle(window);
            if (!ReferenceEquals(model.SelectedDiagnosticChannel, row) || !ReferenceEquals(grid.SelectedItem, row) ||
                details.Text != model.DiagnosticChannelDetails || !details.Text.StartsWith(row.Channel + " · " + row.State, StringComparison.Ordinal) ||
                !details.Text.Contains(row.Explanation, StringComparison.Ordinal) ||
                !details.Text.Contains("not matching-event bounds or proof of continuous coverage", StringComparison.Ordinal) ||
                !details.Text.Contains("No health or unique-incident conclusion", StringComparison.Ordinal) ||
                model.DiagnosticChannelRows.Count != rows.Length || !rows.Zip(model.DiagnosticChannelRows).All(pair => ReferenceEquals(pair.First, pair.Second)) ||
                !activity.SequenceEqual(model.Activity) || model.IsBusy)
                throw new SmokeAssertionException("ui-diagnostic-channel-selection-details");
            if (row.RejectedRecords == null && !details.Text.Contains("Rejected records: Unknown", StringComparison.Ordinal) ||
                row.DuplicateRecords == null && !details.Text.Contains("duplicate/ambiguous references omitted: Unknown", StringComparison.Ordinal))
                throw new SmokeAssertionException("ui-diagnostic-channel-null-not-zero");
        }
        var priorFindings = model.DiagnosticRows.ToArray();
        range.SelectedItem = model.DiagnosticRanges.First(choice => choice != selectedRange); await Idle(window);
        if (!DiagnosticInteractionReset(model) || !activity.SequenceEqual(model.Activity) || model.IsBusy ||
            status.Text != model.DiagnosticsMessage || details.Text != model.DiagnosticChannelDetails ||
            rows.Any(prior => model.DiagnosticChannelRows.Any(current => ReferenceEquals(prior, current))) ||
            !model.DiagnosticsAttempt.StartsWith("Not checked", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-diagnostic-channel-changed-range-reset");
        int queriedSources = rows.Count(row => row.Reason == DiagnosticChannelCoverageReason.Queried);
        int positiveSources = rows.Count(row => row.State is CheckState.Available or CheckState.Partial);
        report.Pass(check, $"actualChannelRows=2; queriedSourceCount={queriedSources}; positiveSourceCount={positiveSources}; selectedActualRows=2; sourceStates={string.Join(",", rows.Select(row => row.State))}; actualGridDetailsStatusBindings=true; selectionStartsNoQuery=true; changedRangeClearsCoverageAndSelection=true. The first explicit production passive Event Log cycle completed. Unknown counts were not treated as zero; no raw counts, source explanations, dates, providers, event records or identifiers exported. No WER, integrity command, repair, UAC, fixture injection or post-query screenshot occurred.");
        await CheckDiagnosticRangeChangeInFlight(window, model, range, rows, priorFindings, report);
    }

    private static async Task CheckDiagnosticRangeChangeInFlight(MainWindow window, MainViewModel model,
        ComboBox range, DiagnosticChannelCoverage[] priorChannels, DiagnosticFinding[] priorFindings, SmokeReport report)
    {
        const string check = "ui-diagnostic-range-change-in-flight";
        if (model.IsBusy || !model.ReadDiagnosticsCommand.CanExecute(null))
        { report.Unknown(check, "The previous cycle is unsettled; no additional event query or in-flight range proof attempted."); return; }
        var previousActivity = model.Activity.ToArray();
        DiagnosticChannelInteractionSettled = false;
        // This normal command executes synchronously to its first real provider await.
        // No fake provider, delay, injected result or private generation access is used.
        model.ReadDiagnosticsCommand.Execute(null);
        bool pendingObserved = model.IsBusy;
        if (!pendingObserved)
        {
            DiagnosticChannelInteractionSettled = true;
            report.Unknown(check, "The additional actual event read settled before an in-flight range change could be observed. No race-handling or WER qualification is inferred.");
            return;
        }
        Func<bool>? unchanged = null;
        bool rangeInteractionFailed = false;
        try
        {
            if (!model.IsBusy || !range.IsEnabled)
                throw new SmokeAssertionException("ui-diagnostic-range-change-control-disabled");
            var next = model.SelectedDiagnosticRange;
            var transientRange = model.DiagnosticRanges.First(choice => choice != next);
            range.SelectedItem = transientRange;
            range.SelectedItem = next; // Same days again must not revive the earlier request.
            var channels = model.DiagnosticChannelRows.ToArray();
            var selectedChannel = model.SelectedDiagnosticChannel;
            var message = model.DiagnosticsMessage;
            var windowText = model.DiagnosticsWindow;
            var attempt = model.DiagnosticsAttempt;
            var recurrence = model.DiagnosticRecurrence;
            var werMessage = model.WerMessage;
            var werWindow = model.WerWindow;
            var werCounts = model.WerCountSummary;
            bool reset = model.IsBusy && ReferenceEquals(model.SelectedDiagnosticRange, next) && DiagnosticInteractionReset(model) &&
                attempt.StartsWith("Not checked", StringComparison.Ordinal) &&
                windowText.StartsWith("Not checked", StringComparison.Ordinal) &&
                recurrence.StartsWith("Time range changed", StringComparison.Ordinal) &&
                model.DiagnosticFindingsView.IsEmpty && model.SelectedDiagnosticFinding == null &&
                model.WerRows.Count == 0 && model.WerCoverage.Count == 0 && model.SelectedWerObservation == null &&
                werMessage.StartsWith("Time range changed", StringComparison.Ordinal) &&
                werCounts.StartsWith("Store counts: not checked", StringComparison.Ordinal);
            unchanged = () => reset && ReferenceEquals(model.SelectedDiagnosticRange, next) && DiagnosticInteractionReset(model) &&
                channels.Length == model.DiagnosticChannelRows.Count && channels.Zip(model.DiagnosticChannelRows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
                ReferenceEquals(selectedChannel, model.SelectedDiagnosticChannel) && model.DiagnosticsMessage == message &&
                model.DiagnosticsWindow == windowText && model.DiagnosticsAttempt == attempt && model.DiagnosticRecurrence == recurrence &&
                model.DiagnosticFindingsView.IsEmpty && model.SelectedDiagnosticFinding == null &&
                model.WerRows.Count == 0 && model.WerCoverage.Count == 0 && model.SelectedWerObservation == null &&
                model.WerMessage == werMessage && model.WerWindow == werWindow && model.WerCountSummary == werCounts;
        }
        catch (Exception)
        {
            // Settle the actual query before reporting even a managed interaction error.
            // No provider exception, source value or user identifier is exported.
            rangeInteractionFailed = true;
        }
        var wait = Stopwatch.StartNew();
        while (model.IsBusy && wait.Elapsed < TimeSpan.FromSeconds(60)) { await Task.Delay(40); await Idle(window); }
        if (model.IsBusy)
        {
            if (model.CancelCommand.CanExecute(null)) model.CancelCommand.Execute(null);
            report.Unknown(check, "The actual event read remains pending after the bounded UI wait. Existing cancellation requested; no forced stop, late-result proof, WER query, later fixture or capture is authorized.");
            return;
        }
        DiagnosticChannelInteractionSettled = true;
        await Idle(window);
        var activity = model.Activity.ToArray();
        string? terminalOutcome = activity.Length == 0 ? null
            : activity[0].EndsWith("DiagnosticsReport · Cancelled", StringComparison.Ordinal) ? "Cancelled"
            : activity[0].EndsWith("DiagnosticsReport · Failed", StringComparison.Ordinal) ? "Failed" : null;
        bool expectedActivity = activity.Length == Math.Min(200, previousActivity.Length + 2) &&
            activity.Length >= 2 && terminalOutcome != null &&
            activity[1].EndsWith("DiagnosticsReport · Started", StringComparison.Ordinal) &&
            activity.Skip(2).SequenceEqual(previousActivity.Take(198));
        if (rangeInteractionFailed || unchanged?.Invoke() != true || !expectedActivity)
            throw new SmokeAssertionException("ui-diagnostic-range-change-stale-publication");
        foreach (var prior in priorChannels) model.SelectedDiagnosticChannel = prior;
        foreach (var prior in priorFindings) model.SelectedDiagnosticFinding = prior;
        await Idle(window);
        if (unchanged?.Invoke() != true || !activity.SequenceEqual(model.Activity) || model.IsBusy)
            throw new SmokeAssertionException("ui-diagnostic-range-change-stale-selection");
        report.Pass(check, $"actualPendingEventReadObserved=true; rangeControlEnabledWhileBusy=true; rangeChangedThroughControlWhileBusy=true; rangeChangedAwayAndBackWhileBusy=true; oldAdapterWaitSettled=true; terminalOutcome={terminalOutcome}; returnedResponseSuppressionObserved={terminalOutcome == "Cancelled"}; staleErrorSuppressionObserved={terminalOutcome == "Failed"}; newRangeUnknownStatePreserved=true; currentChannelReferencesPreserved=true; staleChannelReferencesRefused=true; priorFindingReferencesObserved={(priorFindings.Length > 0)}; existingWerResetStatePreserved=true; onlyExpectedCommandActivityObserved=true. Exactly one additional actual Event Log command was awaited. A Failed outcome qualifies stale-error suppression only, not returned-response suppression. Genuine prior finding references were refused when available; no WER read/race, native cancellation, provider counts, health, source values or post-query screenshots are qualified or exported.");
    }

    private static bool DiagnosticInteractionReset(MainViewModel model) => model.DiagnosticChannelRows.Count == 2 &&
        model.DiagnosticChannelRows.All(row => row.State == CheckState.Unknown && row.Reason == DiagnosticChannelCoverageReason.NoCompletedReport &&
            row.UsableMatchingRecords == null && row.Truncated == null && row.RejectedRecords == null && row.DuplicateRecords == null &&
            row.WholeLogRecordCount == null && row.OldestRetainedUtc == null && row.NewestRetainedUtc == null && row.RetentionState == DiagnosticRetentionState.Unknown) &&
        model.SelectedDiagnosticChannel is { } selected && model.DiagnosticChannelRows.Any(row => ReferenceEquals(row, selected)) &&
        model.DiagnosticRows.Count == 0 && model.DiagnosticsMessage.StartsWith("Time range changed", StringComparison.Ordinal);

    private static bool DiagnosticInteractionWindow(string value, DateTimeOffset before, DateTimeOffset after, int days)
    {
        const string prefix = "UTC window [";
        int ending = value.IndexOf("); local ", StringComparison.Ordinal);
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || ending < prefix.Length) return false;
        var pieces = value[prefix.Length..ending].Split(", ", StringSplitOptions.None);
        return pieces.Length == 2 && DateTimeOffset.TryParseExact(pieces[0], "u", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) &&
            DateTimeOffset.TryParseExact(pieces[1], "u", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) &&
            end >= before.AddSeconds(-1) && end <= after && end - start == TimeSpan.FromDays(days);
    }
}
