using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Controls;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // This explicit actual-host lifecycle runs after ALL images and metadata-search
    // workflows. Never capture or export the populated inspection controls.
    private static async Task CheckInspectionEvidence(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-inspection-evidence";
        if (model.IsBusy || !model.ScanCommand.CanExecute(null))
        {
            report.Unknown(check, "An earlier UI workflow is still busy; no local inspection started or lifecycle completion inferred.");
            return;
        }
        model.SelectedPage = 14;
        await Idle(window);
        var grid = Descendants<DataGrid>(window).Single(control => AutomationProperties.GetName(control) == "Local inspection evidence and source states");
        var details = Descendants<TextBox>(window).Single(control => AutomationProperties.GetName(control) == "Inspection source, time, uncertainty and impact");
        var integrity = model.InspectionEvidenceRows.Where(row => !IsOrdinaryInspection(row)).ToArray();
        AssertUnattemptedIntegrity(model, integrity);
        var before = DateTimeOffset.UtcNow;
        model.ScanCommand.Execute(null); // Local inventory only; never UpdateCommand.
        if (!await WaitForLocalInspection(window, model))
        {
            report.Unknown(check, "Actual local inventory exceeded the bounded UI wait. Production cancellation requested; worker completion unverified, no forced closure or subsequent query/capture.");
            return;
        }
        var after = DateTimeOffset.UtcNow;
        AssertUnattemptedIntegrity(model, integrity);
        var first = model.InspectionEvidenceRows.Where(IsOrdinaryInspection).ToArray();
        if (!ValidCompletedEvidence(first, before, after))
            throw new SmokeAssertionException("ui-inspection-evidence-completion-schema");
        int actualPositive = first.Count(row => row.State is CheckState.Available or CheckState.Partial);
        if (actualPositive == 0)
        {
            report.Unknown(check, "Actual local inventory returned no Available/Partial storage or Defender evidence. Lifecycle source proof remains unavailable; no sample records, source values or later captures substituted.");
            return;
        }
        var activity = model.Activity.ToArray();
        foreach (var row in first)
        {
            grid.SelectedItem = row;
            await Idle(window);
            if (!ReferenceEquals(model.SelectedInspectionEvidence, row) || !ReferenceEquals(grid.SelectedItem, row) ||
                !ReferenceEquals(grid.ItemsSource, model.InspectionEvidenceRows) || !details.IsReadOnly ||
                details.Text != model.InspectionEvidenceDetails || !details.Text.Contains(row.Source, StringComparison.Ordinal) ||
                details.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path != nameof(MainViewModel.InspectionEvidenceDetails) ||
                !model.Activity.SequenceEqual(activity) || model.IsBusy || !model.InspectionEvidenceRows.Where(IsOrdinaryInspection).SequenceEqual(first))
                throw new SmokeAssertionException("ui-inspection-evidence-selection-no-query");
            AssertUnattemptedIntegrity(model, integrity);
        }
        if (!model.ScanCommand.CanExecute(null)) throw new SmokeAssertionException("ui-inspection-evidence-rescan-disabled");
        var secondBefore = DateTimeOffset.UtcNow;
        model.ScanCommand.Execute(null);
        AssertUnattemptedIntegrity(model, integrity);
        var pending = model.InspectionEvidenceRows.Where(IsOrdinaryInspection).ToArray();
        bool resetObserved = pending.Length == 2 && pending.All(row => row.State == CheckState.Unknown && row.CompletedUtc == null &&
            row.AttemptedUtc.HasValue && row.AttemptedUtc.Value >= secondBefore && row.AttemptedUtc.Value > first[0].AttemptedUtc) &&
            pending[0].AttemptedUtc == pending[1].AttemptedUtc;
        // Exercise preservation with a genuinely completed source while another local
        // provider is still pending; the synchronous reset snapshot alone cannot prove it.
        var completionWait = Stopwatch.StartNew();
        while (model.IsBusy && !model.InspectionEvidenceRows.Where(IsOrdinaryInspection).Any(row => row.CompletedUtc != null) &&
            completionWait.Elapsed < TimeSpan.FromSeconds(15))
        { await Task.Delay(25); await Idle(window); }
        var alreadyCompleted = model.InspectionEvidenceRows.Where(IsOrdinaryInspection).Where(row => row.CompletedUtc != null).ToArray();
        bool cancellationRequested = model.CancelCommand.CanExecute(null);
        if (cancellationRequested) model.CancelCommand.Execute(null);
        if (!await WaitForLocalInspection(window, model))
        {
            report.Unknown(check, "Second actual local inspection did not finish within the bounded cancellation wait; no forced worker cleanup, subsequent query or screenshots attempted.");
            return;
        }
        AssertUnattemptedIntegrity(model, integrity);
        var second = model.InspectionEvidenceRows.Where(IsOrdinaryInspection).ToArray();
        if (!ValidCompletedEvidence(second, secondBefore, DateTimeOffset.UtcNow) ||
            second.Any(row => row.AttemptedUtc <= first[0].AttemptedUtc))
            throw new SmokeAssertionException("ui-inspection-evidence-cancel-terminal-schema");
        if (resetObserved && second.Any(row => row.AttemptedUtc != pending[0].AttemptedUtc))
            throw new SmokeAssertionException("ui-inspection-evidence-attempt-changed");
        if (alreadyCompleted.Any(completed => !second.Contains(completed)))
            throw new SmokeAssertionException("ui-inspection-evidence-completed-result-lost");
        string states = string.Join(",", second.Select(row => row.Area + ":" + row.State));
        bool preservationExercised = alreadyCompleted.Length > 0 && cancellationRequested;
        string detail = $"initialPositiveSourceCount={actualPositive}; selectedActualRows=2; selectionStartsNoQuery=true; resetPendingObserved={resetObserved}; cancellationRequested={cancellationRequested}; completedBeforeCancelCount={alreadyCompleted.Length}; preservationExercised={preservationExercised}; terminalRowCount={second.Length}; terminalStates={states}; noPendingOrPriorAttemptRows=true; threeIntegritySourcesRemainUnattempted=true. " +
            "Two explicit local production inspections only. Completed-source preservation is proved only when exercised before a live cancellation. No captures after data loading; no flags, capacities, provider messages, dates or source values exported. Available is source evidence, not health or action permission.";
        if (resetObserved && preservationExercised) report.Pass(check, detail); else report.Unknown(check, detail);
    }

    private static bool IsOrdinaryInspection(DiagnosticInspectionEvidenceEntry row) =>
        row.Area is DiagnosticEvidenceArea.Storage or DiagnosticEvidenceArea.Defender;

    private static void AssertUnattemptedIntegrity(MainViewModel model, DiagnosticInspectionEvidenceEntry[] original)
    {
        var current = model.InspectionEvidenceRows.Where(row => !IsOrdinaryInspection(row)).ToArray();
        if (model.InspectionEvidenceRows.Count != 5 || current.Length != 3 || original.Length != 3 ||
            current.Select(row => row.Area).Distinct().Count() != 3 ||
            current.Any(row => row.Area is not (DiagnosticEvidenceArea.ComponentStoreRecordedFlags or
                DiagnosticEvidenceArea.ComponentStoreScan or DiagnosticEvidenceArea.SystemFiles) ||
                row.State != CheckState.Unknown || row.AttemptedUtc != null || row.CompletedUtc != null) ||
            current.Where((row, index) => !ReferenceEquals(row, original[index])).Any())
            throw new SmokeAssertionException("ui-inspection-integrity-sources-remain-unattempted");
    }

    private static bool ValidCompletedEvidence(DiagnosticInspectionEvidenceEntry[] rows, DateTimeOffset before, DateTimeOffset after) =>
        rows.Length == 2 && rows.Select(row => row.Area).Distinct().Count() == 2 && rows[0].AttemptedUtc == rows[1].AttemptedUtc &&
        rows.All(row => IsOrdinaryInspection(row) && Enum.IsDefined(row.Area) && Enum.IsDefined(row.State) && row.AttemptedUtc is { } attempt && row.CompletedUtc is { } completed &&
            attempt.Offset == TimeSpan.Zero && completed.Offset == TimeSpan.Zero && attempt >= before && completed >= attempt && completed <= after &&
            row.Source == (row.Area == DiagnosticEvidenceArea.Storage ? "System.IO.DriveInfo / TotalSize and AvailableFreeSpace" : "Microsoft Defender / Get-MpComputerStatus") &&
            new[] { row.Observation, row.Coverage, row.Uncertainty, row.Impact, row.NextStep }.All(text => !string.IsNullOrWhiteSpace(text) && text.Length <= 32768));

    private static async Task<bool> WaitForLocalInspection(MainWindow window, MainViewModel model)
    {
        var watch = Stopwatch.StartNew();
        while (model.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(70)) { await Task.Delay(50); await Idle(window); }
        if (!model.IsBusy) return true;
        if (model.CancelCommand.CanExecute(null)) model.CancelCommand.Execute(null);
        var cancellationWait = Stopwatch.StartNew();
        while (model.IsBusy && cancellationWait.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(50); await Idle(window); }
        return false; // A timed-out attempt cannot become a positive proof through late completion.
    }
}
