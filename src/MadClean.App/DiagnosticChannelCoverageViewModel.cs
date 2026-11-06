using System.Collections.ObjectModel;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    public ObservableCollection<DiagnosticChannelCoverage> DiagnosticChannelRows { get; } = new(
        new[] { UncheckedDiagnosticChannel("Application", DiagnosticChannelCoverageReason.NotChecked, "Not checked. Read event evidence to observe this source."),
            UncheckedDiagnosticChannel("System", DiagnosticChannelCoverageReason.NotChecked, "Not checked. Read event evidence to observe this source.") });
    private DiagnosticChannelCoverage? selectedDiagnosticChannel;
    private bool IsCurrentDiagnosticChannel(DiagnosticChannelCoverage channel) =>
        DiagnosticChannelRows.Any(row => ReferenceEquals(row, channel));
    public DiagnosticChannelCoverage? SelectedDiagnosticChannel
    {
        get => selectedDiagnosticChannel is { } channel && IsCurrentDiagnosticChannel(channel) ? channel : null;
        set
        {
            if (value != null && !IsCurrentDiagnosticChannel(value))
            {
                PropertyChanged?.Invoke(this, new(nameof(SelectedDiagnosticChannel)));
                return;
            }
            if (ReferenceEquals(selectedDiagnosticChannel, value)) return;
            selectedDiagnosticChannel = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedDiagnosticChannel)));
            PropertyChanged?.Invoke(this, new(nameof(DiagnosticChannelDetails)));
        }
    }
    public string DiagnosticChannelDetails => SelectedDiagnosticChannel is { } row
        ? $"{row.Channel} · {row.State}\n{row.Explanation}\n" +
          $"Rejected records: {Known(row.RejectedRecords)}; duplicate/ambiguous references omitted: {Known(row.DuplicateRecords)}.\n" +
          $"Whole-log retained bounds UTC: {row.OldestRetainedUtc?.ToString("u") ?? "Unknown"} to {row.NewestRetainedUtc?.ToString("u") ?? "Unknown"}. " +
          "These are separate whole-log probes, not matching-event bounds or proof of continuous coverage. No health or unique-incident conclusion is made."
        : "Select Application or System to inspect its actual query status, bounds and limitations. Unknown counts are not zero.";
    private static string Known(int? value) => value?.ToString("N0") ?? "Unknown";
    private static DiagnosticChannelCoverage UncheckedDiagnosticChannel(string channel, DiagnosticChannelCoverageReason reason, string explanation)
        => new(channel, CheckState.Unknown, reason, null, null, null, null, null, DiagnosticRetentionState.Unknown, null, null, explanation);
    private void ResetDiagnosticChannels(string explanation)
    {
        DiagnosticChannelRows.Clear();
        DiagnosticChannelRows.Add(UncheckedDiagnosticChannel("Application", DiagnosticChannelCoverageReason.NoCompletedReport, explanation));
        DiagnosticChannelRows.Add(UncheckedDiagnosticChannel("System", DiagnosticChannelCoverageReason.NoCompletedReport, explanation));
        SelectedDiagnosticChannel = DiagnosticChannelRows[0];
    }
    private void PublishDiagnosticChannels(CheckResult<DiagnosticReport> result)
    {
        if (result.Items.Count != 1 || result.Items[0].ChannelCoverage is not { Count: 2 } rows ||
            rows.Count(row => row.Channel == "Application") != 1 || rows.Count(row => row.Channel == "System") != 1)
        {
            ResetDiagnosticChannels("This attempt returned no trusted per-channel coverage. Review the overall query status; neither channel is assumed empty or successfully checked.");
            return;
        }
        DiagnosticChannelRows.Clear();
        foreach (var row in rows) DiagnosticChannelRows.Add(row);
        SelectedDiagnosticChannel = DiagnosticChannelRows[0];
    }
}
