using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IDefenderLifecycleInventory defenderLifecycleInventory = new WindowsDefenderLifecycleInventory();
    private Task<CheckResult<DefenderLifecycleReport>>? defenderLifecycleTask;
    private long defenderLifecycleGeneration;
    private DiagnosticRangeChoice selectedDefenderLifecycleRange = new("Last 7 days", 7);
    private DefenderLifecycleReport? defenderLifecycleReport;
    private DateTimeOffset? defenderLifecycleAttemptUtc, defenderLifecycleReceivedUtc;
    private string defenderLifecycleMessage = "Not checked. Read retained Defender scan-event metadata explicitly. No scan is started.";

    public IReadOnlyList<DiagnosticRangeChoice> DefenderLifecycleRanges { get; } =
        [new("Last 24 hours", 1), new("Last 7 days", 7), new("Last 30 days", 30)];
    public ObservableCollection<DefenderLifecycleEvent> DefenderLifecycleRows { get; } = [];
    public ICommand ReadDefenderLifecycleCommand { get; private set; } = null!;
    public bool HasPendingDefenderLifecycleRead => defenderLifecycleTask is { IsCompleted: false };
    public string DefenderLifecycleMessage { get => defenderLifecycleMessage; private set => Set(ref defenderLifecycleMessage, value); }
    public string DefenderLifecycleLimits => DefenderLifecyclePolicy.Limits;
    public DiagnosticRangeChoice SelectedDefenderLifecycleRange
    {
        get => selectedDefenderLifecycleRange;
        set
        {
            if (value == null || !DefenderLifecycleRanges.Any(choice => ReferenceEquals(choice, value)) || ReferenceEquals(value, selectedDefenderLifecycleRange)) return;
            // Invalidate before notifications, including an away/back range change.
            defenderLifecycleGeneration++;
            ResetDefenderLifecycle();
            Set(ref selectedDefenderLifecycleRange, value);
            DefenderLifecycleMessage = "Range changed. Read scan-event history to check this window; no query started automatically.";
            NotifyDefenderLifecycle();
        }
    }
    public string DefenderLifecycleDetails => defenderLifecycleReport is { } report
        ? $"Source: {DefenderLifecyclePolicy.Channel}\nProvider: {DefenderLifecyclePolicy.Provider}\n" +
          $"UTC query window (start included, end excluded): {report.StartUtc:O} to {report.EndUtc:O}\n" +
          $"Provider observation UTC: {report.ObservedUtc:O}; response received UTC: {defenderLifecycleReceivedUtc:O}\n" +
          $"Observed event records: {report.Records.Count}; capped: {report.Truncated}. These are event records, not a count of unique scans.\n" +
          "A finished event reports a historical lifecycle notification, not a clean result, complete coverage, or a scan requested by MadClean."
        : defenderLifecycleAttemptUtc is { } attempted
            ? $"Attempt started UTC: {attempted:O}. No accepted completed history is displayed. " +
              (HasPendingDefenderLifecycleRead ? "The query client is still owned; another read and normal close wait for it." : "Review the source status and retry explicitly.")
            : "No history window has been checked for this selection. Current protection status and reported quick/full timestamps above are independent sources.";

    private void InitializeDefenderLifecycle()
    {
        selectedDefenderLifecycleRange = DefenderLifecycleRanges.Single(choice => choice.Days == 7);
        ReadDefenderLifecycleCommand = new AsyncCommand(ReadDefenderLifecycle, () => !IsBusy && !HasPendingDefenderLifecycleRead);
    }
    private Task ReadDefenderLifecycle()
    {
        if (IsBusy || HasPendingDefenderLifecycleRead) return Task.CompletedTask;
        var generation = ++defenderLifecycleGeneration;
        int days = selectedDefenderLifecycleRange.Days;
        bool Current() => generation == defenderLifecycleGeneration && days == selectedDefenderLifecycleRange.Days;
        return RunDiscoveryAsync(ActivityArea.DefenderLifecycle, "Reading Defender scan-event history", async token =>
        {
            ResetDefenderLifecycle();
            defenderLifecycleAttemptUtc = DateTimeOffset.UtcNow;
            DefenderLifecycleMessage = "Reading bounded local lifecycle event metadata. No scan, remediation or settings change is requested.";
            NotifyDefenderLifecycle();
            var readTask = defenderLifecycleInventory.ReadAsync(days, token);
            defenderLifecycleTask = readTask;
            try
            {
                NotifyDefenderLifecycle();
                var result = await readTask;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                var received = DateTimeOffset.UtcNow;
                if (!DefenderLifecyclePolicy.ValidateResult(result, days, defenderLifecycleAttemptUtc.Value, received))
                {
                    DefenderLifecycleMessage = "Unknown · the response did not match this bounded source/window contract; no event records were accepted. Retry the explicit read.";
                    return CheckState.Unknown;
                }
                if (result.State == CheckState.Partial && result.Items.Count == 1)
                {
                    var report = result.Items[0];
                    foreach (var row in report.Records)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Current()) return CheckState.Cancelled;
                        DefenderLifecycleRows.Add(row);
                    }
                    if (!Current()) return CheckState.Cancelled;
                    defenderLifecycleReport = report;
                }
                defenderLifecycleReceivedUtc = received;
                DefenderLifecycleMessage = Stamp(result.State, result.Message);
                NotifyDefenderLifecycle();
                return result.State;
            }
            finally
            {
                // A display/notification failure cannot abandon the actual query
                // underneath the global busy owner. No engine scan is involved.
                try { await readTask; } catch { /* The first await or failure handler reports the attempt. */ }
                NotifyDefenderLifecycle();
            }
        }, error =>
        {
            if (!Current()) return;
            ResetDefenderLifecycle();
            DefenderLifecycleMessage = error + " No completed lifecycle history is claimed. This does not change Defender or cancel an engine scan.";
            NotifyDefenderLifecycle();
        });
    }
    private void ResetDefenderLifecycle()
    {
        defenderLifecycleReport = null;
        defenderLifecycleAttemptUtc = defenderLifecycleReceivedUtc = null;
        DefenderLifecycleRows.Clear();
    }
    private void NotifyDefenderLifecycle()
    {
        PropertyChanged?.Invoke(this, new(nameof(DefenderLifecycleDetails)));
        PropertyChanged?.Invoke(this, new(nameof(HasPendingDefenderLifecycleRead)));
        CommandManager.InvalidateRequerySuggested();
    }
    public void ExplainDefenderLifecycleCloseBlock()
    {
        Status = "The read-only Defender history query is still settling. Wait or cancel the query before closing; no engine scan was started.";
        NotifyDefenderLifecycle();
    }
}
