using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private HardwareCompatibilityUpgradeArea? upgradeReviewArea;
    private string upgradeReviewBudgetText = "", upgradeReviewCurrency = "", upgradeReviewRegion = "", upgradeReviewUseCase = "";
    private HardwareUpgradeReviewReport? upgradeReviewReport;
    public IReadOnlyList<HardwareCompatibilityUpgradeArea> UpgradeReviewAreas { get; } =
        Array.AsReadOnly(Enum.GetValues<HardwareCompatibilityUpgradeArea>());
    public HardwareCompatibilityUpgradeArea? UpgradeReviewArea
    {
        get => upgradeReviewArea;
        set
        {
            var accepted = value is { } area && Enum.IsDefined(area) ? value : null;
            if (upgradeReviewArea == accepted) return;
            upgradeReviewArea = accepted;
            InvalidateUpgradeReview();
            PropertyChanged?.Invoke(this, new(nameof(UpgradeReviewArea)));
        }
    }
    public string UpgradeReviewBudgetText
    { get => upgradeReviewBudgetText; set => SetUpgradeReviewText(ref upgradeReviewBudgetText, value, 32, nameof(UpgradeReviewBudgetText)); }
    public string UpgradeReviewCurrency
    { get => upgradeReviewCurrency; set => SetUpgradeReviewText(ref upgradeReviewCurrency, value, 3, nameof(UpgradeReviewCurrency)); }
    public string UpgradeReviewRegion
    { get => upgradeReviewRegion; set => SetUpgradeReviewText(ref upgradeReviewRegion, value, 2, nameof(UpgradeReviewRegion)); }
    public string UpgradeReviewUseCase
    { get => upgradeReviewUseCase; set => SetUpgradeReviewText(ref upgradeReviewUseCase, value, 1024, nameof(UpgradeReviewUseCase)); }
    public string UpgradeReviewSummary => upgradeReviewReport?.Summary ??
        "Not reviewed. Choose an upgrade area and optionally enter your planning preferences, then review the missing evidence. Compatibility remains Unknown.";
    public IReadOnlyList<string> UpgradeReviewReasons => upgradeReviewReport?.Reasons ?? Array.Empty<string>();
    public IReadOnlyList<HardwareUpgradeReviewRequirement> UpgradeReviewRequirements =>
        upgradeReviewReport?.Requirements ?? Array.Empty<HardwareUpgradeReviewRequirement>();
    public ICommand ReviewUpgradePrerequisitesCommand { get; private set; } = null!;
    public ICommand ResetUpgradeReviewCommand { get; private set; } = null!;

    private void InitializeHardwareUpgradeReview()
    {
        InitializeHardwareUpgradeContext();
        ReviewUpgradePrerequisitesCommand = new ActionCommand(ReviewUpgradePrerequisites, () => true);
        ResetUpgradeReviewCommand = new ActionCommand(ResetUpgradeReview, () => true);
    }
    private void SetUpgradeReviewText(ref string field, string? value, int limit, string property)
    {
        // Oversized programmatic input is discarded, never truncated into a plausible budget/code.
        bool rejected = value != null && value.Length > limit;
        string accepted = rejected ? "" : value ?? "";
        if (field == accepted && !rejected) return;
        field = accepted;
        InvalidateUpgradeReview();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    private void InvalidateUpgradeReview()
    {
        upgradeReviewReport = null;
        InvalidateHardwareUpgradeContext();
        NotifyUpgradeReview();
    }
    private void ReviewUpgradePrerequisites()
    {
        // This generic worksheet has no detected-PC binding, provider query or verified checks.
        upgradeReviewReport = HardwareUpgradeReview.Build(new(UpgradeReviewArea, UpgradeReviewBudgetText,
            UpgradeReviewCurrency, UpgradeReviewRegion, UpgradeReviewUseCase));
        NotifyUpgradeReview();
    }
    private void ResetUpgradeReview()
    {
        UpgradeReviewArea = null;
        UpgradeReviewBudgetText = "";
        UpgradeReviewCurrency = "";
        UpgradeReviewRegion = "";
        UpgradeReviewUseCase = "";
        InvalidateUpgradeReview();
    }
    private void NotifyUpgradeReview()
    {
        PropertyChanged?.Invoke(this, new(nameof(UpgradeReviewSummary)));
        PropertyChanged?.Invoke(this, new(nameof(UpgradeReviewReasons)));
        PropertyChanged?.Invoke(this, new(nameof(UpgradeReviewRequirements)));
    }
}
