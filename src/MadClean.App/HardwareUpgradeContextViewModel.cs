using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private HardwareUpgradeContextReport? hardwareUpgradeContext;
    private string hardwareUpgradeContextMessage = "No detected inventory attached. Choose an upgrade area and explicitly attach a completed local inventory snapshot.";
    public ICommand AttachHardwareUpgradeContextCommand { get; private set; } = null!;
    public ICommand ClearHardwareUpgradeContextCommand { get; private set; } = null!;
    public string HardwareUpgradeContextMessage { get => hardwareUpgradeContextMessage; private set => Set(ref hardwareUpgradeContextMessage, value); }
    public IReadOnlyList<HardwareFact> HardwareUpgradeContextFacts => hardwareUpgradeContext?.Facts ?? Array.Empty<HardwareFact>();
    public string HardwareUpgradeContextDetails => hardwareUpgradeContext is { } report
        ? $"Area: {report.Area}; inventory coverage: {report.SourceState}\n" +
          $"Inventory attempt UTC: {report.AttemptedUtc:O}; response received UTC: {report.ReceivedUtc:O}; attached UTC: {report.ReviewedUtc:O}\n" +
          "These times describe the shared request and local receipt, not per-class observations or an atomic hardware snapshot. Attached values are historical reported facts, not verified manufacturer specifications.\n" + HardwareUpgradeContextPolicy.Limits
        : "No captured context for this selection. Attaching uses only the existing inventory; it starts no query, retrieves no specifications and approves no prerequisite.";

    private void InitializeHardwareUpgradeContext()
    {
        AttachHardwareUpgradeContextCommand = new ActionCommand(AttachHardwareUpgradeContext,
            () => !IsBusy && !HasPendingHardwareQuery && UpgradeReviewArea.HasValue && HardwareLastObservation is { State: CheckState.Available or CheckState.Partial });
        ClearHardwareUpgradeContextCommand = new ActionCommand(InvalidateHardwareUpgradeContext, () => hardwareUpgradeContext != null);
    }
    private void AttachHardwareUpgradeContext()
    {
        if (IsBusy || HasPendingHardwareQuery) return;
        InvalidateHardwareUpgradeContext();
        var source = HardwareLastObservation;
        var area = UpgradeReviewArea;
        var result = HardwareUpgradeContextPolicy.Capture(source, HardwareFacts, area,
            HardwareAttemptedUtc ?? default, HardwareCompletedUtc ?? default, DateTimeOffset.UtcNow);
        if (!ReferenceEquals(source, HardwareLastObservation) || area != UpgradeReviewArea) return;
        if (result.State == CheckState.Partial && result.Items.Count == 1) hardwareUpgradeContext = result.Items[0];
        HardwareUpgradeContextMessage = Stamp(result.State, result.Message);
        NotifyHardwareUpgradeContext();
    }
    private void InvalidateHardwareUpgradeContext()
    {
        hardwareUpgradeContext = null;
        HardwareUpgradeContextMessage = "No detected inventory attached for the current planning inputs. Attach explicitly after choosing an area and reading hardware inventory.";
        NotifyHardwareUpgradeContext();
    }
    private void NotifyHardwareUpgradeContext()
    {
        PropertyChanged?.Invoke(this, new(nameof(HardwareUpgradeContextFacts)));
        PropertyChanged?.Invoke(this, new(nameof(HardwareUpgradeContextDetails)));
        CommandManager.InvalidateRequerySuggested();
    }
}
