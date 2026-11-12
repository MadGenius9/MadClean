using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private DriverUpdateReviewReport? driverUpdateReview;
    private string driverUpdateReviewMessage = "Not reviewed. Select one actual driver offer, then review its prerequisites. Installation is unavailable.";
    public ICommand ReviewDriverPrerequisitesCommand { get; private set; } = null!;
    public string DriverUpdateReviewMessage { get => driverUpdateReviewMessage; private set => Set(ref driverUpdateReviewMessage, value); }
    public IReadOnlyList<DriverUpdateReviewRequirement> DriverUpdateRequirements =>
        driverUpdateReview?.Requirements ?? Array.Empty<DriverUpdateReviewRequirement>();
    public string DriverUpdateReviewDetails => driverUpdateReview is { } report
        ? $"Reviewed UTC: {report.ReviewedUtc:O}\nHistorical query attempt UTC: {report.AttemptedUtc:O}; response received UTC: {report.ReceivedUtc:O}\n" +
          $"Source coverage: {report.SourceState}. Update ID: {report.SelectedOffer.Identity}\n" +
          "This worksheet describes the selected query snapshot at review time. It performs no fresh provider check. Readiness remains Unknown; no installation is available. Refreshing offers or changing the selection clears it.\n" + DriverUpdateReviewPolicy.Limits
        : "No selected-offer worksheet is displayed. Provider declarations cannot verify device compatibility or authorize installation. No prerequisite is automatically approved.";
    private void InitializeDriverUpdateReview() => ReviewDriverPrerequisitesCommand =
        new ActionCommand(ReviewDriverPrerequisites, () => !IsBusy && selectedDriverOffer != null && driverOfferSnapshot != null);
    private void ReviewDriverPrerequisites()
    {
        if (IsBusy) return;
        InvalidateDriverUpdateReview();
        var result = DriverUpdateReviewPolicy.Review(driverOfferSnapshot, DriverOffers, selectedDriverOffer,
            driverOfferAttemptUtc ?? default, driverOfferResultUtc ?? default, DateTimeOffset.UtcNow);
        if (result.State == CheckState.Partial && result.Items.Count == 1) driverUpdateReview = result.Items[0];
        DriverUpdateReviewMessage = Stamp(result.State, result.Message);
        NotifyDriverUpdateReview();
    }
    private void InvalidateDriverUpdateReview()
    {
        driverUpdateReview = null;
        DriverUpdateReviewMessage = "Not reviewed for this selection. Review one current driver offer; no installation is available.";
        NotifyDriverUpdateReview();
    }
    private void NotifyDriverUpdateReview()
    {
        PropertyChanged?.Invoke(this, new(nameof(DriverUpdateRequirements)));
        PropertyChanged?.Invoke(this, new(nameof(DriverUpdateReviewDetails)));
        CommandManager.InvalidateRequerySuggested();
    }
}
