using System.Globalization;
using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows.WinGet;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly IStructuredWingetInventory appUpdateDiscovery = new StructuredWingetInventory();
    private bool appUpdateCallActive;
    private StructuredWingetObservation? appUpdateLastObservation;
    private string appUpdateQueryState = "Not checked";
    private string appUpdateAvailableCount = "Unknown", appUpdateUnknownCount = "Unknown", appUpdateCatalogCount = "Unknown";
    private string appUpdateCheckedUtc = "Not checked";

    public StructuredWingetObservation? AppUpdateLastObservation
    { get => appUpdateLastObservation; private set => Set(ref appUpdateLastObservation, value); }
    public bool HasPendingAppUpdateDiscovery => appUpdateCallActive || appUpdateDiscovery.HasPendingNativeRead;
    public string AppUpdateQueryState { get => appUpdateQueryState; private set => Set(ref appUpdateQueryState, value); }
    public string AppUpdateAvailableCount { get => appUpdateAvailableCount; private set => Set(ref appUpdateAvailableCount, value); }
    public string AppUpdateUnknownCount { get => appUpdateUnknownCount; private set => Set(ref appUpdateUnknownCount, value); }
    public string AppUpdateCatalogCount { get => appUpdateCatalogCount; private set => Set(ref appUpdateCatalogCount, value); }
    public string AppUpdateCheckedUtc { get => appUpdateCheckedUtc; private set => Set(ref appUpdateCheckedUtc, value); }
    public string AppUpdateOwnershipStatus => appUpdateCallActive ? "Checking app updates…"
        : appUpdateDiscovery.HasPendingNativeRead ? "WinGet is still finishing its query. Cancel does not stop the shared provider."
        : "No app-update query is pending.";

    private void BeginStructuredAppUpdateCheck()
    {
        AppUpdateLastObservation = null;
        AppUpdateQueryState = "Checking";
        AppUpdateAvailableCount = AppUpdateUnknownCount = AppUpdateCatalogCount = "Unknown";
        AppUpdateCheckedUtc = "Not checked";
        NotifyAppUpdateOwnership();
    }

    private async Task<CheckResult<AppUpdate>> ReadStructuredAppUpdatesAsync(CancellationToken token)
    {
        var attempted = DateTimeOffset.UtcNow;
        appUpdateCallActive = true; NotifyAppUpdateOwnership();
        try
        {
            var observation = await appUpdateDiscovery.ReadObservedAsync(token);
            token.ThrowIfCancellationRequested();
            var received = DateTimeOffset.UtcNow;
            if (!StructuredWingetObservationPolicy.IsConsistent(observation, attempted, received,
                appUpdateDiscovery.HasPendingNativeRead))
                throw new InvalidOperationException("The app-update provider returned inconsistent observations.");
            AppUpdateLastObservation = observation;
            AppUpdateQueryState = observation.Result.State.ToString();
            AppUpdateCheckedUtc = received.ToString("O", CultureInfo.InvariantCulture);
            if (observation.Result.State is CheckState.Available or CheckState.Partial)
            {
                AppUpdateCatalogCount = observation.Result.Items.Count.ToString(CultureInfo.InvariantCulture);
                AppUpdateAvailableCount = observation.Result.Items.Count(row => row.Status == CheckState.Available).ToString(CultureInfo.InvariantCulture);
                AppUpdateUnknownCount = observation.Result.Items.Count(row => row.Status == CheckState.Unknown).ToString(CultureInfo.InvariantCulture);
            }
            return observation.Result;
        }
        finally { appUpdateCallActive = false; NotifyAppUpdateOwnership(); }
    }

    private void InterruptStructuredAppUpdateCheck(string reason)
    {
        // Counts belong only to a current accepted result, never a cancelled/failed attempt.
        AppUpdateLastObservation = null;
        AppUpdateQueryState = reason.StartsWith("Cancelled", StringComparison.Ordinal) ? "Cancelled" : "Failed";
        AppUpdateAvailableCount = AppUpdateUnknownCount = AppUpdateCatalogCount = "Unknown";
        AppUpdateCheckedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        NotifyAppUpdateOwnership();
    }

    private async Task DrainAppUpdateDiscoveryAsync()
    {
        if (HasPendingAppUpdateDiscovery)
        {
            Status = "Waiting for the owned WinGet query to finish. No process or service will be killed.";
            NotifyAppUpdateOwnership();
            while (HasPendingAppUpdateDiscovery) await Task.Delay(100);
            // Preserve the original timeout/cancellation result after ownership settles.
            Status = "App-update query ownership settled. Review the recorded result; no earlier timeout was promoted to success.";
        }
        NotifyAppUpdateOwnership();
    }

    public void ExplainAppUpdateCloseBlock()
    {
        Status = "The app-update query is still owned by MadClean. Use Cancel to stop waiting, then wait for the actual WinGet call to return before closing.";
        NotifyAppUpdateOwnership();
    }

    private void NotifyAppUpdateOwnership()
    {
        PropertyChanged?.Invoke(this, new(nameof(HasPendingAppUpdateDiscovery)));
        PropertyChanged?.Invoke(this, new(nameof(AppUpdateOwnershipStatus)));
        CommandManager.InvalidateRequerySuggested();
    }
}
