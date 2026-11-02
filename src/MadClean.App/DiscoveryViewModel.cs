using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed record CapabilityRow(string Capability, string Discovery, string NextStep, string RequiredReview);

public sealed partial class MainViewModel
{
    public ICommand CapabilityQueueCommand { get; private set; } = null!;
    public IReadOnlyList<CapabilityRow> Capabilities { get; } =
    [
        new("Cleanup", "Read-only exact-path preview, persistent user exclusions and an explicit eight-file metadata worksheet", "Queued: selected cleanup execution", "Fresh exact file/byte preview, revalidate identity and protected paths, user selection and confirmation; deletion is not recoverable through restore points."),
        new("Startup management", "Read-only Run/RunOnce and Startup-folder inventory", "Queued: reversible selected enable/disable", "Show exact entry/source, current and proposed state, restore plan and permissions. Never infer enabled state or launch a discovered command."),
        new("Installed apps / uninstall", "Separate desktop and current-user Windows-package inventories, local Name/Publisher search and selected read-only MSI evidence", "Explicit desktop Windows review route; direct supported uninstall and package actions remain queued", "Source rows can overlap and are not unique apps. Missing fields stay unknown; signing/removal flags and MSI matches grant no action permission. Identify the exact chosen app and supported mechanism before any later action; never force-delete folders or run bulk/silent uninstall commands."),
        new("Disk space map", "Selected folder logical-byte map plus explicit bounded observed hard-link identity accounting", "Physical allocation and broader analysis remain separate work", "Incomplete bytes stay unknown; size/color never means junk. Any future action requires an exact selected-object preview."),
        new("Leftover review", "Complete in-session before/after registrations and surviving prior install paths", "Queued: richer ownership evidence and selected review actions", "Candidates are not proven junk. Protect shared dependencies, user data, saves and settings. Registration absence is not proof uninstall completed; no automatic removal or AppData sweep."),
        new("Duplicate-file review", "Selected-folder byte comparison, hard-link observations, explicit pair worksheet and separate current two-file recheck", "F2b read-only current pair observations; removal remains absent and gated", "Names alone are not duplicates. Pair choices start empty; same-file aliases are not independent backups. Current equality grants no removal permission; all read handles close before publication and no keeper remains protected. Later actions require fresh identity/content evidence and a verified independent retained copy protected through each change; block if consistency is uncertain."),
        new("Broken-shortcut review", "Selected-folder observations, exact-file metadata recheck and separate bounded file-identity/content evidence", "Read-only evidence only; S2 moving/removal remains absent", "Offline/network/temporary/inaccessible targets are unknown, not confirmed broken. Current read consistency does not prove historical identity, inactivity, permissions, ownership or removal safety. Protected and Startup folders are excluded from content evidence. Never launch targets, activate installers, search/track/save links or auto-delete. Later actions affect selected shortcuts only, never their targets."),
        new("Driver discovery", "Separate Windows Update offers and explicit present-device INF driver version/date/provider observations", "Queued: exact-device/source qualification and individually selected supported installation", "Names, provider labels, versions and dates do not bind a device to an update offer or prove loaded binary versions, signatures or that drivers are up to date. Missing or denied fields stay unknown. No automatic driver replacement, elevation or restart."),
        new("BIOS / Firmware", "Reported BIOS/PC/board identity, Windows Update firmware offers and explicitly checked limited official release history", "Limited Surface Laptop 6 for Business 15-inch history; broader exact OEM/model/revision applicability remains queued", "Available BIOS version, model match, release notes and prerequisites stay Unknown until verified. OEM PCs use OEM firmware, never a similarly named retail board. No downloads, flashing, silent installs or forced restarts."),
        new("Hardware Upgrades", "Explicit PC/board/CPU/RAM/GPU/storage facts and limited supported official family service observations", "Limited live official source checks available; upgrade limits, exact-part compatibility and H3 advice remain gated", "Separate detected firmware facts, manufacturer-verified limits and manual checks. Never infer PSU/connectors, clearance, free physical slots or soldered-memory upgradeability. Verify compatibility; no purchases or BIOS updates authorized."),
        new("Windows Diagnostics", "Local event evidence with observed recurrence; separate minimized WER store metadata and fixed Microsoft tool identity previews", "Tested WER store enumeration remains Unknown; populated metadata is unverified. D2/D3 execution stays disabled", "Explain source, UTC time range, coverage, uncertainty and impact per finding. Event counts are not unique crashes; missing evidence is not health. Later repairs require exact selection, supported Microsoft semantics, elevation consent, safe cancellation/status and actual result logs. No health score, arbitrary DLL downloads or speed promises."),
        new("Guided Windows workflows", "Explicit exact-destination preview for Windows Settings and official Microsoft instructions", "Handoff available; underlying operation completion stays Unknown", "Windows controls any later manual actions. MadClean does not pass an uninstall/update target or execute the underlying operation; a shell return does not confirm the target window appeared."),
        new("Automatic Maintenance", "Planned only; no background schedule or automatic action is implemented", "Requires validated selected actions before separate explicit opt-in policies", "Keep inspections, program updates and driver updates separately controlled. Missing consent or prerequisites means defer; no silent elevation, forced restart, automatic cleanup or automatic BIOS/firmware updates. Notifications do not authorize changes."),
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
