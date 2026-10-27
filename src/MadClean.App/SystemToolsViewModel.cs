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
    private string systemToolMessage = "No tool checked or run. Choose an operation to review its exact command and impact.";
    public IReadOnlyList<SystemToolActionDefinition> SystemToolDefinitions => SystemToolActionsCatalog.All;
    public ICommand VerifySystemToolCommand { get; private set; } = null!;
    public string SystemToolMessage { get => systemToolMessage; private set => Set(ref systemToolMessage, value); }
    public SystemToolActionDefinition? SelectedSystemTool
    {
        get => selectedSystemTool;
        set
        {
            if (IsBusy || value != null && !SystemToolDefinitions.Contains(value)) return;
            Set(ref selectedSystemTool, value); systemToolPreview = null;
            SystemToolMessage = "Selection changed. No new identity check or Windows command has been requested.";
            PropertyChanged?.Invoke(this, new(nameof(SystemToolDetails)));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SystemToolDetails
    {
        get
        {
            if (selectedSystemTool is not { } action) return "Select a fixed Microsoft operation. Ordinary inspection never runs these tools. Execution is blocked pending separate Windows validation; this page can verify a selected executable's identity only.";
            string description = $"{action.Title}\nCommand descriptor: {action.FileName} {string.Join(" ", action.Arguments)}\nAction type: {action.Kind}\n\nEffect: {action.Effect}\nElevation: {action.Elevation}\nNetwork/source behavior: {action.Network}\nCancellation: {action.Cancellation}\nRestart: {action.Reboot}\nProtected Windows result log: {action.WindowsLogReference}";
            if (systemToolPreview is { } preview)
                description += $"\n\nIdentity observation UTC: {preview.CheckedUtc:u}; valid before {preview.ExpiresUtc:u}\nExact tool: {preview.ToolPath}\nReported file version: {preview.ToolVersion}\nSHA-256: {preview.Sha256}\nSignature observation: {preview.TrustState}\n{preview.TrustDetail}\n\nExecution gate: {preview.Blocker}";
            return description;
        }
    }
    private void InitializeSystemTools()
    {
        VerifySystemToolCommand = new AsyncCommand(VerifySystemTool, () => !IsBusy && selectedSystemTool != null);
    }
    private Task VerifySystemTool() => RunDiscoveryAsync(ActivityArea.SystemToolIdentity, "Verifying selected Microsoft tool identity", async token =>
    {
        if (selectedSystemTool is not { } action) return CheckState.Unknown;
        systemToolPreview = null;
        PropertyChanged?.Invoke(this, new(nameof(SystemToolDetails)));
        SystemToolMessage = "Checking only the selected executable and signature. Windows certificate verification may retrieve certificate metadata; no SFC/DISM command is run.";
        var result = await systemTools.PrepareAsync(action.Operation, token);
        token.ThrowIfCancellationRequested();
        systemToolPreview = result.Items.FirstOrDefault();
        PropertyChanged?.Invoke(this, new(nameof(SystemToolDetails)));
        SystemToolMessage = Stamp(result.State, result.Message) + " Execution remains disabled; this observation is not an integrity or repair result.";
        return result.State;
    }, error =>
    {
        systemToolPreview = null;
        PropertyChanged?.Invoke(this, new(nameof(SystemToolDetails)));
        SystemToolMessage = error + " No integrity or repair command was requested.";
    });
}
