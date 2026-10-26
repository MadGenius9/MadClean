using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed record CapabilityRow(string Capability, string Discovery, string NextStep, string RequiredReview);

public sealed partial class MainViewModel
{
    public ICommand CapabilityQueueCommand { get; private set; } = null!;
    public IReadOnlyList<CapabilityRow> Capabilities { get; } =
    [
        new("Cleanup", "Read-only exact-path preview and persistent user exclusions", "Queued: selected cleanup execution", "Fresh exact file/byte preview, revalidate identity and protected paths, user selection and confirmation; deletion is not recoverable through restore points."),
        new("Startup management", "Read-only Run/RunOnce and Startup-folder inventory", "Queued: reversible selected enable/disable", "Show exact entry/source, current and proposed state, restore plan and permissions. Never infer enabled state or launch a discovered command."),
        new("Installed apps / uninstall", "Read-only Add/Remove Programs inventory and mechanism hints", "Queued: selected supported uninstall workflow", "Identify the chosen app, supported registered/Windows Installer mechanism, consequences and prompts. Never force-delete its folder or run bulk/silent uninstall commands."),
        new("Disk space map", "Selected local folder, known logical bytes, linked list and drill-down", "Queued: broader allocation/hardlink-aware analysis", "Incomplete bytes stay unknown; size/color never means junk. Any future action requires an exact selected-object preview."),
        new("Leftover review", "Complete in-session before/after registrations and surviving prior install paths", "Queued: richer ownership evidence and selected review actions", "Candidates are not proven junk. Protect shared dependencies, user data, saves and settings. Registration absence is not proof uninstall completed; no automatic removal or AppData sweep."),
        new("Windows Diagnostics", "Module queued; no Windows integrity/repair command runs in phase one", "D1: read-only health/crash evidence; D2: explicit non-repair corruption checks; D3: guided selected repairs", "Explain source, UTC time range, coverage, uncertainty and impact per finding. Use supported Microsoft tools/documented semantics. Later repairs require exact selection, elevation consent, safe cancellation/status and actual result logs. No health score, arbitrary DLL downloads or speed promises."),
        new("Security / updates", "Defender status, update offers and explicit MadClean release checks", "Queued: user-initiated scans and individually selected installs", "Show the specific operation/source and permissions; retain unknown/unavailable states. No silent elevation or forced restart.")
    ];
    private void InitializeDiscovery()
    {
        CapabilityQueueCommand = new ActionCommand(() => SelectedPage = 9, () => true);
        InitializeCleanupPreview();
        InitializeApplicationInventory();
        InitializeDiskMap();
    }
    private async Task RunDiscoveryAsync(ActivityArea area, string title, Func<CancellationToken, Task<CheckState>> work, Action<string> failure)
    {
        if (IsBusy) return;
        using var source = new CancellationTokenSource(); cancellation = source; IsBusy = true;
        Status = title + "…"; Event(area, ActivityOutcome.Started);
        try
        {
            var state = await work(source.Token);
            source.Token.ThrowIfCancellationRequested();
            Status = title + ": " + state + ". Review the page for scope and errors.";
            Event(area, Outcome(state));
        }
        catch (OperationCanceledException) { failure("Cancelled · no completed discovery is claimed."); Status = title + ": cancelled."; Event(area, ActivityOutcome.Cancelled); }
        catch (Exception) { failure("Failed · discovery could not complete. Check the selected source/path and permissions, then retry."); Status = title + ": failed."; Event(area, ActivityOutcome.Failed); }
        finally { cancellation = null; IsBusy = false; }
    }
}
