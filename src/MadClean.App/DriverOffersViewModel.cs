using System.Collections.ObjectModel;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private SystemUpdate? selectedDriverOffer;
    private DateTimeOffset? driverOfferAttemptUtc, driverOfferResultUtc, driverOfferInterruptedUtc;
    private CheckState driverOfferSourceState = CheckState.Unknown;
    private bool driverOfferQueryPending;
    public ObservableCollection<SystemUpdate> DriverOffers { get; } = [];
    public SystemUpdate? SelectedDriverOffer
    {
        get => selectedDriverOffer;
        set
        {
            if (value != null && !DriverOffers.Any(offer => ReferenceEquals(offer, value))) return;
            Set(ref selectedDriverOffer, value);
            PropertyChanged?.Invoke(this, new(nameof(DriverOfferDetails)));
        }
    }
    public string DriverOfferQueryDetails => driverOfferAttemptUtc == null
        ? "Source: configured Windows Update service. Not checked in this session. No driver update, exact-device match or up-to-date status has been established."
        : $"Source: configured Windows Update service.\nCheck updates attempt started UTC: {driverOfferAttemptUtc.Value:u}\n" +
            (driverOfferQueryPending ? "Windows Update result: pending; no completed result is claimed."
                : driverOfferResultUtc is { } completed
                    ? $"Windows Update result received UTC: {completed:u}\nSource state: {driverOfferSourceState}. This is the result of this configured-service query, not manufacturer-wide coverage."
                    : (driverOfferInterruptedUtc is { } interrupted ? $"Attempt interruption recorded UTC: {interrupted:u}\n" : "") +
                        "No completed Windows Update result is established for this attempt; source coverage remains Unknown.");
    public string DriverOfferDetails => selectedDriverOffer is not { } offer
        ? "Select an actual driver offer to inspect and copy its source-qualified metadata. No returned offers does not establish that drivers are up to date. Installed/available driver versions, exact hardware matching, official release notes and installation prerequisites remain Unknown."
        : $"{offer.Title}\nUpdate ID: {offer.Identity}\nSource: configured Windows Update service; source state: {driverOfferSourceState}\n" +
            $"Provider driver class: {offer.DriverClass}\nProvider driver package date: {offer.PackageDate} (not a verified release date or installed-driver date)\n" +
            $"Provider RebootRequired flag: {offer.RebootRequired} (an observed flag, not permission or a restart request)\n" +
            "Installed driver version: Unknown. Offered driver/package version: Unknown.\nExact device/OEM model and revision match: Unverified.\nOfficial release notes: Unknown; the description below is not verified manufacturer release notes.\nInstallation prerequisites: Unknown; verify the exact OEM/device instructions before considering a future installation.\n\n" +
            $"Windows Update provider description (untrusted plain text):\n{offer.Description}\n\n" +
            "This read-only offer is not a compatibility, signature or installation-readiness verdict. No install, driver replacement, firmware asset download or restart is requested. Manufacturer driver catalog coverage is Unsupported in this increment.";

    private void BeginDriverOffersQuery(DateTimeOffset attemptedUtc)
    {
        DriverOffers.Clear(); SelectedDriverOffer = null;
        driverOfferAttemptUtc = attemptedUtc; driverOfferResultUtc = null; driverOfferInterruptedUtc = null;
        driverOfferSourceState = CheckState.Unknown; driverOfferQueryPending = true;
        NotifyDriverOfferQuery();
    }
    private void PublishDriverOffersQuery(CheckResult<SystemUpdate> result, DateTimeOffset completedUtc)
    {
        DriverOffers.Clear(); SelectedDriverOffer = null;
        driverOfferResultUtc = completedUtc; driverOfferInterruptedUtc = null; driverOfferQueryPending = false;
        driverOfferSourceState = Enum.IsDefined(result.State) ? result.State : CheckState.Unknown;
        if ((result.State is CheckState.Available or CheckState.Partial) && result.Items != null)
            foreach (var offer in result.Items)
                if (offer != null && offer.Kind == "Driver") DriverOffers.Add(offer);
        NotifyDriverOfferQuery();
    }
    private void InterruptDriverOffersQuery(DateTimeOffset completedUtc)
    {
        DriverOffers.Clear(); SelectedDriverOffer = null;
        // The existing interruption callback carries fixed display text, not a typed provider
        // completion. Do not invent a successful response or derive a state by parsing text.
        driverOfferResultUtc = null; driverOfferInterruptedUtc = completedUtc; driverOfferQueryPending = false; driverOfferSourceState = CheckState.Unknown;
        NotifyDriverOfferQuery();
    }
    private void NotifyDriverOfferQuery()
    {
        PropertyChanged?.Invoke(this, new(nameof(DriverOfferQueryDetails)));
        PropertyChanged?.Invoke(this, new(nameof(DriverOfferDetails)));
    }
}
