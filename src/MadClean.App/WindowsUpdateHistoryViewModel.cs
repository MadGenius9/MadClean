using System.Collections.ObjectModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

/// <summary>Display projection of one recorded attempt, never a current installation verdict.</summary>
public sealed class WindowsUpdateHistoryRow(WindowsUpdateHistoryEntry entry)
{
    public WindowsUpdateHistoryEntry Entry { get; } = entry;
    public int Record => Entry.Index + 1;
    public string Title => Text(Entry.Title);
    public string RecordedUtc => Value(Entry.DateUtc, time => time.ToString("O"));
    public string Operation => Value(Entry.Operation, value => value switch
    { 1 => "Recorded installation", 2 => "Recorded uninstallation", _ => "Unknown" });
    public string RecordedResult => Value(Entry.ResultCode, value => value switch
    { 0 => "Not started", 1 => "In progress", 2 => "Succeeded", 3 => "Succeeded with errors", 4 => "Failed", 5 => "Aborted", _ => "Unknown" });
    public string Details => $"History record ordinal: {Record}\nTitle: {Title}\nRecorded operation time UTC: {RecordedUtc}\nOperation: {Operation}\nProvider's historical result: {RecordedResult}\n" +
        $"Recorded HRESULT: {Value(Entry.HResult, value => $"0x{unchecked((uint)value):X8}")}\nUpdate ID: {Value(Entry.UpdateId, value => value.ToString("D"))}\nRevision: {Value(Entry.Revision, value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))}\n\n" +
        "This is a provider history record. A recorded success, failure or in-progress value describes that attempt; it does not establish current installation, an active operation, complete update coverage or a MadClean-owned request. Revision is an update identity revision, not a software, driver or firmware version. Repeated Update IDs can represent distinct attempts and are retained.";
    private static string Text(UpdateHistoryText field) => field.State == UpdateHistoryFieldState.Known && field.Value is { } value
        ? value : field.State == UpdateHistoryFieldState.Failed ? "Failed to read" : "Unknown";
    private static string Value<T>(UpdateHistoryValue<T> field, Func<T, string> format) where T : struct =>
        field.State == UpdateHistoryFieldState.Known && field.Value is { } value ? format(value)
            : field.State == UpdateHistoryFieldState.Failed ? "Failed to read" : "Unknown";
}

public sealed partial class MainViewModel
{
    private readonly IWindowsUpdateHistoryInventory windowsUpdateHistoryInventory = new WindowsUpdateHistoryInventory();
    private Task<CheckResult<WindowsUpdateHistoryReport>>? windowsUpdateHistoryTask;
    private WindowsUpdateHistoryReport? windowsUpdateHistoryReport;
    private WindowsUpdateHistoryRow? selectedWindowsUpdateHistory;
    private DateTimeOffset? windowsUpdateHistoryAttemptUtc, windowsUpdateHistoryReceivedUtc;
    private long windowsUpdateHistoryGeneration;
    private string windowsUpdateHistoryMessage = "Not checked. Explicitly read bounded local Windows Update history; no update discovery or installation starts.";

    public ObservableCollection<WindowsUpdateHistoryRow> WindowsUpdateHistoryRows { get; } = [];
    public ICommand ReadWindowsUpdateHistoryCommand { get; private set; } = null!;
    public bool HasPendingWindowsUpdateHistoryRead => windowsUpdateHistoryTask is { IsCompleted: false };
    public string WindowsUpdateHistoryMessage { get => windowsUpdateHistoryMessage; private set => Set(ref windowsUpdateHistoryMessage, value); }
    public WindowsUpdateHistoryRow? SelectedWindowsUpdateHistory
    {
        get => selectedWindowsUpdateHistory is { } selected && WindowsUpdateHistoryRows.Any(row => ReferenceEquals(row, selected)) ? selected : null;
        set
        {
            if (value != null && !WindowsUpdateHistoryRows.Any(row => ReferenceEquals(row, value)))
            { PropertyChanged?.Invoke(this, new(nameof(SelectedWindowsUpdateHistory))); return; }
            if (ReferenceEquals(selectedWindowsUpdateHistory, value)) return;
            selectedWindowsUpdateHistory = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedWindowsUpdateHistory)));
            PropertyChanged?.Invoke(this, new(nameof(WindowsUpdateHistoryEntryDetails)));
        }
    }
    public string WindowsUpdateHistoryEntryDetails => SelectedWindowsUpdateHistory?.Details ??
        "Select a current history record to review its reported fields and uncertainty. No history record authorizes an install, uninstall, cleanup or restart.";
    public string WindowsUpdateHistoryDetails => windowsUpdateHistoryReport is { } report
        ? $"Source: local Windows Update Agent history (QueryHistory); provider order: newest records first.\n" +
          $"Query interval UTC: {report.QueryStartedUtc:O} to {report.CheckedUtc:O}\nResponse received UTC: {windowsUpdateHistoryReceivedUtc:O}\n" +
          $"Count observed before query: {report.TotalBefore}; requested latest records: {report.RequestedCount}; returned records: {report.Entries.Count}. Limit: {WindowsUpdateHistoryPolicy.MaximumEntries}.\n" +
          "Count and query are separate, non-atomic observations; fewer records may return. This bounded local history is always Partial coverage, including an empty history. It does not check present applicability or prove that any software is up to date."
        : windowsUpdateHistoryAttemptUtc is { } attempted
            ? $"Read attempt UTC: {attempted:O}. No completed history is accepted. " +
              (HasPendingWindowsUpdateHistoryRead ? "MadClean still owns the query client; wait or cancel its read before closing." : "Review the source status and retry explicitly.")
            : "No history read attempted. This action reads local recorded update events separately from Apps, Drivers and BIOS/Firmware discovery.";

    private void InitializeWindowsUpdateHistory() => ReadWindowsUpdateHistoryCommand = new AsyncCommand(ReadWindowsUpdateHistory,
        () => !IsBusy && !HasPendingWindowsUpdateHistoryRead);

    private Task ReadWindowsUpdateHistory()
    {
        if (IsBusy || HasPendingWindowsUpdateHistoryRead) return Task.CompletedTask;
        var generation = ++windowsUpdateHistoryGeneration;
        bool Current() => generation == windowsUpdateHistoryGeneration;
        return RunDiscoveryAsync(ActivityArea.SystemUpdates, "Reading local Windows Update history", async token =>
        {
            ResetWindowsUpdateHistory();
            windowsUpdateHistoryAttemptUtc = DateTimeOffset.UtcNow;
            WindowsUpdateHistoryMessage = "Reading at most 128 local history records. No online update search, download or installation is requested.";
            NotifyWindowsUpdateHistory();
            // Run the adapter's synchronous launch prefix away from the dispatcher.
            // Root the actual task before the first notification following scheduling.
            var readTask = Task.Run(() => windowsUpdateHistoryInventory.ReadAsync(token), CancellationToken.None);
            windowsUpdateHistoryTask = readTask;
            try
            {
                NotifyWindowsUpdateHistory();
                var result = await readTask;
                token.ThrowIfCancellationRequested();
                if (!Current()) return CheckState.Cancelled;
                var received = DateTimeOffset.UtcNow;
                if (result.State == CheckState.Partial && WindowsUpdateHistoryPolicy.IsConsistent(result, received))
                {
                    var report = result.Items[0];
                    foreach (var entry in report.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Current()) return CheckState.Cancelled;
                        WindowsUpdateHistoryRows.Add(new(entry));
                    }
                    token.ThrowIfCancellationRequested();
                    if (!Current()) return CheckState.Cancelled;
                    windowsUpdateHistoryReport = report;
                    windowsUpdateHistoryReceivedUtc = received;
                    WindowsUpdateHistoryMessage = report.Entries.Count == 0
                        ? "Partial · no records returned for this local history observation. Current updates and installation state remain unchecked."
                        : "Partial · recorded update attempts returned. Select a record; these outcomes do not establish current installed or update status.";
                }
                else if (result.Items.Count == 0 && result.State is CheckState.Unknown or CheckState.Unavailable or CheckState.Unsupported or CheckState.Failed or CheckState.Cancelled)
                {
                    windowsUpdateHistoryReceivedUtc = received;
                    WindowsUpdateHistoryMessage = result.State switch
                    {
                        CheckState.Unsupported => "Unsupported · local Windows Update history is not supported in this environment.",
                        CheckState.Unavailable => "Unavailable · Windows Update history could not be accessed. Check Windows policy/service access and retry explicitly.",
                        CheckState.Failed => "Failed · the bounded history read failed. Check Windows Update service access and permissions before retrying.",
                        CheckState.Cancelled => "Cancelled · no completed history is accepted. Query-client cancellation does not prove an OS service operation stopped.",
                        _ => "Unknown · no consistent history response was accepted. Retry the explicit read; current update status remains unchecked."
                    };
                }
                else
                {
                    WindowsUpdateHistoryMessage = "Unknown · the response did not match this bounded local-history contract; no records were accepted.";
                    return CheckState.Unknown;
                }
                NotifyWindowsUpdateHistory();
                return result.State;
            }
            finally
            {
                // Cancellation or publication/notification failure must not abandon
                // the actual child-query task beneath global busy/normal-close ownership.
                try { await readTask; } catch { /* First observation/failure handler reports the outcome. */ }
                NotifyWindowsUpdateHistory();
            }
        }, error =>
        {
            if (!Current()) return;
            ResetWindowsUpdateHistory(keepAttempt: true);
            WindowsUpdateHistoryMessage = error + " No completed history is accepted. Query-client stop is not proof that Windows services cancelled pending read-only work.";
            NotifyWindowsUpdateHistory();
        });
    }

    private void ResetWindowsUpdateHistory(bool keepAttempt = false)
    {
        windowsUpdateHistoryReport = null;
        if (!keepAttempt) windowsUpdateHistoryAttemptUtc = null;
        windowsUpdateHistoryReceivedUtc = null;
        // Clear the reference before collection notifications can observe stale details.
        selectedWindowsUpdateHistory = null;
        WindowsUpdateHistoryRows.Clear();
        PropertyChanged?.Invoke(this, new(nameof(SelectedWindowsUpdateHistory)));
        PropertyChanged?.Invoke(this, new(nameof(WindowsUpdateHistoryEntryDetails)));
    }
    private void NotifyWindowsUpdateHistory()
    {
        PropertyChanged?.Invoke(this, new(nameof(WindowsUpdateHistoryDetails)));
        PropertyChanged?.Invoke(this, new(nameof(HasPendingWindowsUpdateHistoryRead)));
        CommandManager.InvalidateRequerySuggested();
    }
    public void ExplainWindowsUpdateHistoryCloseBlock()
    {
        Status = "The read-only Windows Update history query client is still settling. Wait or cancel the read before closing; no update search or installation was requested.";
        NotifyWindowsUpdateHistory();
    }
}
