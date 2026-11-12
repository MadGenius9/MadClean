using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private long appInstallerReviewGeneration;
    private AppUpdate? appInstallerReviewSelection;
    private StructuredWingetObservation? appInstallerReviewSnapshot;
    private AppInstallerReviewReport? appInstallerReviewReport;
    private DateTimeOffset? appInstallerReviewAttemptUtc, appInstallerReviewResponseUtc;
    private CheckState appInstallerReviewState = CheckState.Unknown;
    private bool appInstallerReviewReading, appInstallerReviewAdapterInvoked;
    private string appInstallerReviewMessage = "Not checked. Select a current Available package and explicitly read its offered-version installer requirements. This may contact WinGet sources and cache manifest metadata; no package binary is downloaded or installed.";

    public ICommand ReviewAppInstallerCommand { get; private set; } = null!;
    public string AppInstallerReviewMessage => appInstallerReviewMessage;
    public AppInstallerReviewReport? AppInstallerReviewLastReport => appInstallerReviewReport != null &&
        ReferenceEquals(appInstallerReviewSelection, SelectedAppUpdateOffer) &&
        ReferenceEquals(appInstallerReviewSnapshot, AppUpdateLastObservation) ? appInstallerReviewReport : null;
    public string AppInstallerReviewQueryDetails => appInstallerReviewAttemptUtc is not { } attempt
        ? "No installer requirements request for the current selection. Selecting a package does not start a query."
        : $"App request attempt UTC: {attempt:u}\n" +
            (appInstallerReviewSelection is { } selection
                ? $"Selected package ID: {selection.Id}\nExpected installed version: {selection.InstalledVersion}; offered version: {selection.AvailableVersion}\n"
                : "No current selected package scope.\n") +
            (appInstallerReviewResponseUtc is { } response
                ? $"Adapter response received UTC: {response:u}; state: {appInstallerReviewState}. Receipt is not proof that manifest retrieval succeeded or that native work stopped."
                : appInstallerReviewReading ? "Waiting for the selected installer review; no completed response is displayed."
                    : appInstallerReviewAdapterInvoked ? "No completed adapter response was accepted."
                        : "No installer review adapter call was made.") +
            "\nSource: the fixed supported WinGet catalog, with fresh package ID, installed/offered version and source checks. Profile: User scope / Interactive mode. Agreements, hash override, force and unknown-version upgrades remain disabled; installer arguments and paths are not supplied. These are request/response observations, not installation permission.";
    public string AppInstallerReviewDetails => AppInstallerReviewLastReport is { } report
        ? $"Matched package ID: {report.PackageId}\nRechecked installed version: {report.InstalledVersion}\nRechecked offered version: {report.OfferedVersion}\nInstaller metadata observed UTC: {report.CheckedUtc:u}\nProfile: User scope / Interactive mode\n\n" +
            (report.ApplicableInstaller
                ? "The SDK reported a preferred installer for this offered version and profile. This is manifest selection, not proof that it can upgrade this installed application.\n\n"
                : "The SDK reported no applicable installer for this offered version and profile. Other profiles were not checked. This does not mean the application is up to date.\n\n") +
            DescribeAppInstallerReviewField("Reported installer type", report.InstallerType) + "\n\n" +
            DescribeAppInstallerReviewField("Reported nested installer type", report.NestedInstallerType) + "\n\n" +
            DescribeAppInstallerReviewField("Reported installer architecture", report.Architecture) + "\n\n" +
            DescribeAppInstallerReviewField("Reported installer scope", report.Scope) + "\n\n" +
            DescribeAppInstallerReviewField("Reported installer locale", report.Locale) + "\n\n" +
            DescribeAppInstallerReviewField("Declared elevation requirement — no elevation requested", report.Elevation) + "\n\n" +
            DescribeAppInstallerReviewField("Reported installer authentication type — no credential flow requested", report.Authentication) + "\n\n" +
            "Installed scope, upgrade applicability, dependency effects, restart behavior, required consent and installation readiness remain Unknown. Declared elevation/authentication fields are metadata, not a permission or a verified prompt policy. No binary was downloaded; no agreement, installation, upgrade, elevation, browser or restart request was made. No installation action is available."
        : "No accepted installer review for the current selected offer. Select a current Available package and explicitly review its installer requirements. Missing or unknown fields do not establish installation readiness, upgrade applicability or up-to-date status. No installation action is available.";

    private static string DescribeAppInstallerReviewField(string title, AppPackageMetadataField field) =>
        title + "\nState: " + field.State + "\n" +
        (field.State == CheckState.Available && field.Value != null ? field.Value : "Unknown / no qualified value returned.");

    private void InitializeAppInstallerReview() => ReviewAppInstallerCommand = new AsyncCommand(ReviewAppInstaller,
        () => !IsBusy && !HasPendingAppUpdateDiscovery && SelectedAppUpdateOffer is { Status: CheckState.Available });

    private void InvalidateAppInstallerReview()
    {
        appInstallerReviewGeneration++;
        appInstallerReviewSelection = null; appInstallerReviewSnapshot = null; appInstallerReviewReport = null;
        appInstallerReviewAttemptUtc = appInstallerReviewResponseUtc = null;
        appInstallerReviewState = CheckState.Unknown; appInstallerReviewReading = appInstallerReviewAdapterInvoked = false;
        appInstallerReviewMessage = "Not checked for this selection. Review selected installer requirements is a separate online action; it may fetch/cache manifest metadata, but never downloads or installs the package.";
        NotifyAppInstallerReview();
    }

    private void NotifyAppInstallerReview()
    {
        foreach (string property in new[] { nameof(AppInstallerReviewMessage), nameof(AppInstallerReviewLastReport),
            nameof(AppInstallerReviewQueryDetails), nameof(AppInstallerReviewDetails) })
            PropertyChanged?.Invoke(this, new(property));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ReviewAppInstaller()
    {
        if (IsBusy || HasPendingAppUpdateDiscovery || SelectedAppUpdateOffer is not { Status: CheckState.Available } selection) return;
        var snapshot = AppUpdateLastObservation;
        InvalidateAppInstallerReview();
        long generation = appInstallerReviewGeneration;
        bool Current() => generation == appInstallerReviewGeneration && ReferenceEquals(selection, SelectedAppUpdateOffer) &&
            ReferenceEquals(snapshot, AppUpdateLastObservation);
        appInstallerReviewSelection = selection; appInstallerReviewSnapshot = snapshot;
        using var stop = new CancellationTokenSource();
        cancellation = stop;
        // Close protection also covers preparation before the shared native owner starts.
        appUpdateCallActive = true;
        appInstallerReviewAttemptUtc = DateTimeOffset.UtcNow; appInstallerReviewReading = true;
        appInstallerReviewMessage = "Revalidating the selected offer before reading installer requirements. This may contact the fixed WinGet source and cache its manifest; no package asset, agreement acceptance or installation is requested.";
        try
        {
            IsBusy = true; NotifyAppUpdateOwnership();
            Status = "Reading selected WinGet installer requirements…";
            NotifyAppInstallerReview();
            Event(ActivityArea.AppInstallerReview, ActivityOutcome.Started);
            var prepared = AppInstallerReviewPolicy.Prepare(snapshot?.Result, selection, DateTimeOffset.UtcNow, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            if (prepared.State != CheckState.Available || prepared.Items.Count != 1)
            {
                appInstallerReviewReading = false; appInstallerReviewState = prepared.State;
                appInstallerReviewMessage = Stamp(prepared.State, prepared.Message);
                NotifyAppInstallerReview(); Event(ActivityArea.AppInstallerReview, Outcome(prepared.State)); return;
            }
            if (appUpdateDiscovery is not IAppInstallerReviewInventory reader)
            {
                appInstallerReviewReading = false; appInstallerReviewState = CheckState.Unsupported;
                appInstallerReviewMessage = "Unsupported · the selected read-only provider has no installer requirements contract. No replacement provider or package action was used.";
                NotifyAppInstallerReview(); Event(ActivityArea.AppInstallerReview, ActivityOutcome.Partial); return;
            }
            var request = prepared.Items[0];
            appInstallerReviewAdapterInvoked = true;
            NotifyAppInstallerReview();
            stop.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            var result = await reader.ReviewInstallerAsync(request, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            var received = DateTimeOffset.UtcNow;
            if (received < appInstallerReviewAttemptUtc.Value) throw new InvalidOperationException("Metadata response time is inconsistent.");
            appInstallerReviewResponseUtc = received; appInstallerReviewReading = false;
            if (!AppInstallerReviewPolicy.IsResultConsistent(request, result, received))
            {
                bool emptyFailure = Enum.IsDefined(result.State) && result.Items.Count == 0 &&
                    (result.State is CheckState.Unknown or CheckState.Unavailable or CheckState.Unsupported or CheckState.Failed or CheckState.Cancelled);
                appInstallerReviewState = emptyFailure ? result.State : CheckState.Unknown;
                string reason = emptyFailure && result.Message is { Length: > 0 and <= 4096 } message &&
                    !message.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                        ? message : "No consistent selected-version installer review was accepted. The source/server may be unavailable, the exact offer may have changed, or metadata may be missing.";
                appInstallerReviewMessage = Stamp(appInstallerReviewState,
                    reason + " Refresh updates, review the source and versions, then retry; no installation readiness is established.");
            }
            else
            {
                appInstallerReviewReport = result.Items[0]; appInstallerReviewState = result.State;
                appInstallerReviewMessage = Stamp(result.State, appInstallerReviewReport.ApplicableInstaller
                    ? "The SDK reported an installer for the exact offered version under User/Interactive constraints. Upgrade applicability, installed scope, restart/dependency effects and installation readiness remain Unknown."
                    : "The SDK reported no applicable installer under the User/Interactive constraints. No other profile was tried; this is not an up-to-date result.");
            }
            NotifyAppInstallerReview();
            Status = "Selected installer requirements: " + appInstallerReviewState + ". Review the source, fields and limitations.";
            Event(ActivityArea.AppInstallerReview, Outcome(appInstallerReviewState));
        }
        catch (OperationCanceledException)
        {
            if (Current())
            {
                appInstallerReviewReport = null; appInstallerReviewReading = false; appInstallerReviewState = CheckState.Cancelled;
                appInstallerReviewMessage = "Cancelled · no current installer review was accepted. Stop waiting does not cancel a synchronous manifest getter or the WinGet server; the actual owner remains retained until return.";
                NotifyAppInstallerReview();
            }
            Event(ActivityArea.AppInstallerReview, ActivityOutcome.Cancelled);
        }
        catch (Exception)
        {
            if (Current())
            {
                appInstallerReviewReport = null; appInstallerReviewReading = false; appInstallerReviewState = CheckState.Failed;
                appInstallerReviewMessage = "Failed · selected installer requirements could not be accepted. Check App Installer/source availability, policy and network access, refresh updates and retry. No package or installation status was promoted.";
                NotifyAppInstallerReview();
            }
            Event(ActivityArea.AppInstallerReview, ActivityOutcome.Failed);
        }
        finally
        {
            // A bounded caller result does not release the shared MTA owner. Clear the caller
            // marker before draining, then keep Busy/Cancel/close protection until actual return.
            appUpdateCallActive = false;
            try
            {
                try
                {
                    NotifyAppUpdateOwnership();
                    await DrainAppUpdateDiscoveryAsync();
                }
                finally
                {
                    // A display-notification failure must not release Busy beneath a worker.
                    while (appUpdateDiscovery.HasPendingNativeRead) await Task.Delay(100);
                }
                Status = Current()
                    ? "Installer requirements request: " + appInstallerReviewState + ". Review its recorded outcome; owner settlement does not promote an earlier timeout/cancellation."
                    : "The previous installer requirements request ended. The changed selection remains unchecked until explicitly requested.";
            }
            finally { cancellation = null; IsBusy = false; NotifyAppInstallerReview(); }
        }
    }
}
