using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly ISystemToolActions systemTools = new SystemToolActionsProvider();
    private SystemToolActionDefinition? selectedSystemTool;
    private SystemToolActionPreview? systemToolPreview;
    private bool systemToolIdentityCallActive;
    private string systemToolMessage = "No tool checked or run. Choose an operation to review its exact command and impact.";
    private string systemToolIdentitySummary = "Tool identity: not checked. Windows integrity: not checked. No command has run.";
    public IReadOnlyList<SystemToolActionDefinition> SystemToolDefinitions => SystemToolActionsCatalog.All;
    public ICommand VerifySystemToolCommand { get; private set; } = null!;
    public bool HasPendingSystemToolIdentity => systemToolIdentityCallActive || systemTools.HasPendingIdentityWork;
    public SystemToolActionPreview? SystemToolIdentityLastObservation => systemToolPreview;
    public string SystemToolMessage { get => systemToolMessage; private set => Set(ref systemToolMessage, value); }
    public string SystemToolIdentitySummary { get => systemToolIdentitySummary; private set => Set(ref systemToolIdentitySummary, value); }
    public SystemToolActionDefinition? SelectedSystemTool
    {
        get => selectedSystemTool;
        set
        {
            if (IsBusy || HasPendingSystemToolIdentity || value != null && !SystemToolDefinitions.Contains(value)) return;
            selectedSystemTool = value;
            systemToolPreview = null;
            // Invalidate the repair draft before publishing the new selection.
            RepairSelectionChanged(value?.Operation);
            ClearSfcVerificationPreview();
            PropertyChanged?.Invoke(this, new(nameof(SelectedSystemTool)));
            SystemToolMessage = "Selection changed. No new identity check or Windows command has been requested.";
            SystemToolIdentitySummary = "Tool identity: not checked for this selection. Identity evidence is separate from any requested integrity-tool result.";
            NotifySystemToolIdentityState();
        }
    }
    public string SystemToolDetails
    {
        get
        {
            if (selectedSystemTool is not { } action) return "Select a fixed Microsoft operation. Ordinary inspection never runs these tools. Identity verification starts no command. The separately acknowledged SFC verify-only request below has its own scope and qualification; repairs and the legacy executor remain disabled.";
            string permissions = action.Kind == SystemToolActionKind.Repair
                ? "The disabled repair candidate requires already-effective, nonimpersonated administrator access. MadClean requests no UAC, relaunch or alternate credentials. Review availability and ownership separately below."
                : action.Operation == SystemToolOperation.SfcVerifyOnly
                ? "The separate verify-only request requires already-effective administrator access and never requests elevation. Identity verification alone requires no administrator relaunch."
                : action.Elevation;
            string description = $"{action.Title}\nCommand descriptor: {action.FileName} {string.Join(" ", action.Arguments)}\nAction type: {action.Kind}\n\nEffect: {action.Effect}\nPermissions: {permissions}\nNetwork/source behavior: {action.Network}\nCancellation: {action.Cancellation}\nRestart: {action.Reboot}\nProtected Windows result log: {action.WindowsLogReference}";
            if (systemToolPreview is { } preview)
                description += $"\n\nIdentity observation UTC: {preview.CheckedUtc:u}; valid before {preview.ExpiresUtc:u}\nExact tool: {preview.ToolPath}\nReported file version: {preview.ToolVersion}\nSHA-256: {preview.Sha256}\nSignature observation: {preview.TrustState}\n{preview.TrustDetail}\n\nRequest scope: {(action.Operation == SystemToolOperation.SfcVerifyOnly ? "This identity does not run SFC. Review and acknowledge the separate verify-only request below; no repair is selected." : preview.Blocker)}";
            return description;
        }
    }
    private void InitializeSystemTools()
    {
        VerifySystemToolCommand = new AsyncCommand(VerifySystemTool, () => !IsBusy && !HasPendingSystemToolIdentity && selectedSystemTool != null);
        InitializeSfcVerificationPreview();
        InitializeRepairReview();
    }
    private Task VerifySystemTool() => RunDiscoveryAsync(ActivityArea.SystemToolIdentity, "Verifying selected Microsoft tool identity", async token =>
    {
        if (selectedSystemTool is not { } action) return CheckState.Unknown;
        systemToolPreview = null;
        ClearSfcVerificationPreview();
        SystemToolIdentitySummary = "Tool identity: checking. No completed observation is claimed. Windows integrity: not checked.";
        NotifySystemToolIdentityState();
        SystemToolMessage = "Checking only the selected executable and signature. Windows certificate verification may retrieve certificate metadata; no SFC/DISM command is run.";
        var result = await ReadSystemToolIdentityOwned(action.Operation, token);
        token.ThrowIfCancellationRequested();
        var observation = SystemToolIdentityObservationPolicy.Evaluate(action.Operation, result,
            Environment.GetFolderPath(Environment.SpecialFolder.System), DateTimeOffset.UtcNow, token);
        systemToolPreview = observation.IdentityObserved == true ? result.Items.Single() : null;
        SystemToolIdentitySummary = $"Tool identity observation: {observation.State}. " +
            (observation.IdentityObserved == true ? "The selected executable's Microsoft signature and fixed tool identity were observed. " +
                (observation.State == CheckState.Partial ? "Some identity metadata remains unknown; review the details. " : "") :
                "No accepted identity observation is available from this attempt; review the provider result. ") +
            (observation.IdentityObserved == true ? $" Checked UTC: {observation.CheckedUtc:u}; valid before {observation.ExpiresUtc:u}. Verify again for new evidence." : "") +
            " This identity query starts no SFC/DISM command and supplies no Windows integrity or repair verdict. Review separately requested checks in their own result panels.";
        NotifySystemToolIdentityState();
        string message = observation.State == result.State ? result.Message :
            "The returned identity metadata could not be accepted. Verify again and review the selected tool and certificate access; identity remains Unknown.";
        SystemToolMessage = Stamp(observation.State, message) + " This identity observation is not an integrity or repair result and starts no Windows tool command.";
        return observation.State;
    }, error =>
    {
        systemToolPreview = null;
        ClearSfcVerificationPreview();
        SystemToolIdentitySummary = "Tool identity: no completed observation from this attempt. Windows integrity: not checked by this identity attempt. No command was requested by this attempt.";
        NotifySystemToolIdentityState();
        SystemToolMessage = error + " No integrity or repair command was requested.";
    });

    private async Task<CheckResult<SystemToolActionPreview>> ReadSystemToolIdentityOwned(
        SystemToolOperation operation, CancellationToken token)
    {
        systemToolIdentityCallActive = true;
        NotifySystemToolIdentityState();
        try
        {
            // Preserve this caller's actual result. A later native completion must
            // never replace a timeout/cancellation observation with success.
            return await systemTools.PrepareAsync(operation, token);
        }
        finally
        {
            if (systemTools.HasPendingIdentityWork)
            {
                SystemToolMessage = "The tool-identity wait ended while its shared native owner is still finishing. No accepted identity or integrity result is claimed. Cancel does not abort Windows signature/revocation work; wait for ownership to settle before retrying or closing.";
                Status = "Waiting for the owned Microsoft tool identity check to finish; no process or native thread will be killed.";
                NotifySystemToolIdentityState();
                while (systemTools.HasPendingIdentityWork) await Task.Delay(100);
            }
            systemToolIdentityCallActive = false;
            NotifySystemToolIdentityState();
        }
    }

    private void NotifySystemToolIdentityState()
    {
        PropertyChanged?.Invoke(this, new(nameof(HasPendingSystemToolIdentity)));
        PropertyChanged?.Invoke(this, new(nameof(SystemToolIdentityLastObservation)));
        PropertyChanged?.Invoke(this, new(nameof(SystemToolDetails)));
        CommandManager.InvalidateRequerySuggested();
    }

    public void ExplainSystemToolIdentityCloseBlock()
    {
        Status = "The Microsoft tool-identity owner is still finishing. Cancel stops the caller's wait, not native signature/revocation work. Wait for the actual identity worker to return before closing.";
        NotifySystemToolIdentityState();
    }

}
