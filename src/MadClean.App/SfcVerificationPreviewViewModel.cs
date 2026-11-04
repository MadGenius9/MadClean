using System.Globalization;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private SfcVerificationPreview? sfcVerificationPreview;
    private string sfcVerificationPreviewDetails = "No SFC verification preview. Select SFC: verify protected Windows files, verify its identity, then review this separate preview. No SFC command has run.";
    private string sfcVerificationPreviewMessage = "Not prepared. SFC execution is disabled; Windows protected-file integrity is not checked.";

    public SfcVerificationPreview? SfcVerificationLastPreview => sfcVerificationPreview;

    public ICommand PrepareSfcVerificationPreviewCommand { get; private set; } = null!;
    public string SfcVerificationPreviewDetails
    { get => sfcVerificationPreviewDetails; private set => Set(ref sfcVerificationPreviewDetails, value); }
    public string SfcVerificationPreviewMessage
    { get => sfcVerificationPreviewMessage; private set => Set(ref sfcVerificationPreviewMessage, value); }

    private bool CanPrepareSfcVerificationPreview => !IsBusy &&
        selectedSystemTool?.Operation == SystemToolOperation.SfcVerifyOnly &&
        systemToolPreview is { } identity && identity.Definition.Operation == SystemToolOperation.SfcVerifyOnly &&
        DateTimeOffset.UtcNow >= identity.CheckedUtc && DateTimeOffset.UtcNow < identity.ExpiresUtc;

    private void InitializeSfcVerificationPreview() => PrepareSfcVerificationPreviewCommand =
        new ActionCommand(PrepareSfcVerificationPreview, () => CanPrepareSfcVerificationPreview);

    private void ClearSfcVerificationPreview()
    {
        sfcVerificationPreview?.Invalidate();
        sfcVerificationPreview = null;
        SfcVerificationPreviewDetails = "No SFC verification preview. Select SFC: verify protected Windows files, verify its identity, then review this separate preview. No SFC command has run.";
        SfcVerificationPreviewMessage = "Not prepared. SFC execution is disabled; Windows protected-file integrity is not checked.";
        CommandManager.InvalidateRequerySuggested();
    }

    private void PrepareSfcVerificationPreview()
    {
        // Recheck here: ActionCommand callers and expiry races must not use the
        // enabled visual state as permission or revive an old identity snapshot.
        if (!CanPrepareSfcVerificationPreview || systemToolPreview is not { } identity)
        {
            ClearSfcVerificationPreview();
            SfcVerificationPreviewMessage = "No current accepted SFC verify-only identity. Select that operation and verify its identity again before preparing a preview. No command was requested.";
            return;
        }
        ClearSfcVerificationPreview();
        var result = SfcVerificationPolicy.CreatePreview(identity, DateTimeOffset.UtcNow);
        sfcVerificationPreview = result.Items.SingleOrDefault();
        if (sfcVerificationPreview is not { } preview)
        {
            SfcVerificationPreviewMessage = result.Message + " SFC execution remains disabled; integrity is not checked.";
            return;
        }
        SfcVerificationPreviewDetails =
            $"Read-only historical preview snapshot — not an execution request.\nFixed command: {preview.CommandPreview}\n" +
            $"Exact executable: {preview.ToolPath}\nObserved file version: {preview.ToolVersion}\n" +
            $"Identity observed UTC: {identity.CheckedUtc.ToString("O", CultureInfo.InvariantCulture)}\n" +
            $"Prepared UTC: {preview.PreparedUtc.ToString("O", CultureInfo.InvariantCulture)}\n" +
            $"Original identity expires UTC: {preview.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture)}\n" +
            "This snapshot does not extend freshness; verify identity again after expiry.\n\n" +
            $"Effect: {preview.ExactEffect}\n\nPermissions for a later qualified operation: {preview.Permissions}\n\n" +
            $"Stop limitations: {preview.Cancellation}\n\nResult limits: {preview.Limitations}\n\n" +
            $"Protected Windows log reference: {SfcVerificationPolicy.WindowsLogReference}\n" +
            "No log was opened, no administrator check was made and no SFC client was requested by this preview. SFC execution remains disabled.";
        SfcVerificationPreviewMessage = "Preview prepared from the selected identity snapshot only. No new certificate, executable or Windows integrity query ran. Integrity: not checked. SFC execution: disabled.";
    }
}
