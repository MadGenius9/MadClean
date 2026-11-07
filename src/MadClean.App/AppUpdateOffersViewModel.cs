using System.Collections.ObjectModel;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private AppUpdate? selectedAppUpdateOffer;
    private DateTimeOffset? appOfferAttemptUtc, appOfferResultUtc, appOfferInterruptedUtc;
    private CheckState appOfferSourceState = CheckState.Unknown;
    private bool appOfferQueryPending;
    public ObservableCollection<AppUpdate> AppUpdateOffers { get; } = [];
    public AppUpdate? SelectedAppUpdateOffer
    {
        get => selectedAppUpdateOffer is { } offer && AppUpdateOffers.Any(row => ReferenceEquals(row, offer)) ? offer : null;
        set
        {
            if (value != null && !AppUpdateOffers.Any(offer => ReferenceEquals(offer, value)))
            { PropertyChanged?.Invoke(this, new(nameof(SelectedAppUpdateOffer))); return; }
            if (ReferenceEquals(selectedAppUpdateOffer, value)) return;
            selectedAppUpdateOffer = value;
            InvalidateAppPackageMetadata();
            PropertyChanged?.Invoke(this, new(nameof(SelectedAppUpdateOffer)));
            PropertyChanged?.Invoke(this, new(nameof(AppUpdateOfferDetails)));
        }
    }
    public string AppUpdateOfferQueryDetails => appOfferAttemptUtc == null
        ? "Source: WinGet matched installed-package listing using the configured winget source. Not checked in this session; no package update or up-to-date determination is established."
        : $"Source: WinGet matched installed-package listing using the configured winget source.\nCheck updates attempt started UTC: {appOfferAttemptUtc.Value:u}\n" +
            (appOfferQueryPending ? "WinGet result: pending; no completed response is claimed."
                : appOfferResultUtc is { } completed
                    ? $"WinGet result received UTC: {completed:u}\nOverall source state: {appOfferSourceState}. This is matched-package coverage, not every installed application."
                    : (appOfferInterruptedUtc is { } interrupted ? $"Attempt interruption recorded UTC: {interrupted:u}\n" : "") +
                        "No completed WinGet result is established for this attempt; package coverage remains Unknown.");
    public string AppUpdateOfferDetails => SelectedAppUpdateOffer is not { } offer
        ? "Select an actual matched package to inspect and copy its reported metadata. Empty or unchecked results do not mean applications are up to date. Source success is separate from each package's update determination."
        : $"Reported package name: {offer.Name}\nMatched package ID: {offer.Id}\nReported package source label: {Reported(offer.Source)}\n" +
            $"Reported installed version: {Reported(offer.InstalledVersion)}\nReported available version: {Reported(offer.AvailableVersion)}\n" +
            $"Per-package status: {offer.Status}\nOverall source state: {appOfferSourceState}\n\n" +
            (offer.Status == CheckState.Available
                ? "WinGet reported usable installed and offered version fields for this matched package. MadClean has not independently compared vendor version ordering or verified package authenticity, compatibility or installation readiness.\n"
                : "No verified update determination is established for this package. Missing, unknown or truncated versions and matching/source limitations remain Unknown; this is not an up-to-date result.\n") +
            "\nOfficial release notes: Unknown; this listing does not retrieve them.\nInstallation prerequisites and exact vendor applicability: Unknown.\nThe reported source label is not publisher/signature verification or a trust verdict. Unmatched applications, source agreements, pinning and unsupported/localized output can limit coverage.\nNo package page is opened, package downloaded, agreement accepted, application installed or restart requested.";

    private static string Reported(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown (not reported)" : value;
    private void BeginAppUpdateOffersQuery(DateTimeOffset attemptedUtc)
    {
        InvalidateAppPackageMetadata();
        AppUpdateOffers.Clear(); SelectedAppUpdateOffer = null;
        appOfferAttemptUtc = attemptedUtc; appOfferResultUtc = null; appOfferInterruptedUtc = null;
        appOfferSourceState = CheckState.Unknown; appOfferQueryPending = true;
        NotifyAppUpdateOffersQuery();
    }
    private void PublishAppUpdateOffersQuery(CheckResult<AppUpdate> result, DateTimeOffset receivedUtc)
    {
        InvalidateAppPackageMetadata();
        AppUpdateOffers.Clear(); SelectedAppUpdateOffer = null;
        appOfferResultUtc = receivedUtc; appOfferInterruptedUtc = null; appOfferQueryPending = false;
        appOfferSourceState = Enum.IsDefined(result.State) ? result.State : CheckState.Unknown;
        if ((result.State is CheckState.Available or CheckState.Partial) && result.Items != null)
            foreach (var offer in result.Items) if (offer != null) AppUpdateOffers.Add(offer);
        NotifyAppUpdateOffersQuery();
    }
    private void InterruptAppUpdateOffersQuery(DateTimeOffset observedUtc)
    {
        InvalidateAppPackageMetadata();
        AppUpdateOffers.Clear(); SelectedAppUpdateOffer = null;
        appOfferResultUtc = null; appOfferInterruptedUtc = observedUtc; appOfferQueryPending = false;
        appOfferSourceState = CheckState.Unknown;
        NotifyAppUpdateOffersQuery();
    }
    private void NotifyAppUpdateOffersQuery()
    {
        PropertyChanged?.Invoke(this, new(nameof(AppUpdateOfferQueryDetails)));
        PropertyChanged?.Invoke(this, new(nameof(AppUpdateOfferDetails)));
    }
}
