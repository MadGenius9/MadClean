using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IWerMetadataInventory werMetadata = new WindowsWerMetadataInventory();
    private WerMetadataObservation? selectedWerObservation;
    private string werMessage = "Not checked. WER store metadata is a separate explicit read; no report or dump contents are opened.";
    private string werWindow = "No WER store window queried.";
    public ObservableCollection<WerMetadataObservation> WerRows { get; } = [];
    public ObservableCollection<string> WerCoverage { get; } = [];
    public ICommand ReadWerMetadataCommand { get; private set; } = null!;
    public string WerMessage { get => werMessage; private set => Set(ref werMessage, value); }
    public string WerWindow { get => werWindow; private set => Set(ref werWindow, value); }
    public WerMetadataObservation? SelectedWerObservation
    {
        get => selectedWerObservation;
        set { Set(ref selectedWerObservation, value); PropertyChanged?.Invoke(this, new(nameof(WerDetails))); }
    }
    public string WerDetails => selectedWerObservation is { } item
        ? $"Category: {item.Category}\nStore: {item.Store}\nReport reference: {item.ReportId}\nRecorded creation UTC: {item.CreatedUtc:u}\nSource: {item.Source}\n{WerWindow}\n\nThis is stored report metadata, not a confirmed unique crash, current problem, cause, severity or integrity verdict. Queue/archive entries can overlap, expire or be absent under retention/policy. App names, raw signatures/parameters, file names and dump contents are not read into this view."
        : "Select a stored-report observation to inspect its source and recorded time. Retention, access, OS/API support and bounded coverage limit this corroborating evidence; no records does not mean healthy.";
    private void InitializeWerMetadata() => ReadWerMetadataCommand = new AsyncCommand(ReadWerMetadata, () => !IsBusy);
    private void InvalidateWerMetadata()
    {
        WerRows.Clear(); WerCoverage.Clear(); SelectedWerObservation = null;
        WerMessage = "Time range changed. Choose Read WER metadata for the selected range.";
        WerWindow = "Not checked for the new time range.";
    }
    private Task ReadWerMetadata() => RunDiscoveryAsync(ActivityArea.WerMetadata, "Reading supported WER store metadata", async token =>
    {
        WerRows.Clear(); WerCoverage.Clear(); SelectedWerObservation = null;
        WerMessage = "Reading bounded machine queue/archive references and creation times only…";
        WerWindow = "In progress; no completed WER metadata window is claimed.";
        var result = await werMetadata.ReadAsync(SelectedDiagnosticRange.Days, token); token.ThrowIfCancellationRequested();
        WerWindow = "No completed WER store window was returned; review the source status and coverage.";
        foreach (var report in result.Items)
        {
            foreach (var item in report.Observations) WerRows.Add(item);
            foreach (var item in report.Coverage) WerCoverage.Add(item);
            WerWindow = $"UTC window [{report.StartUtc:u}, {report.EndUtc:u}); local {report.StartUtc.ToLocalTime():g} to {report.EndUtc.ToLocalTime():g} ({TimeZoneInfo.Local.DisplayName}).";
        }
        WerMessage = Stamp(result.State, result.Message);
        return result.State;
    }, error =>
    {
        WerRows.Clear(); WerCoverage.Clear(); SelectedWerObservation = null;
        WerMessage = error + " Existing synchronous Windows calls may still finish; no forced cancellation or healthy verdict is claimed.";
        WerWindow = "No completed WER report from this attempt.";
    });
}
