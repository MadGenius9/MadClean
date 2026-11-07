using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private long appPackageMetadataGeneration;
    private AppUpdate? appPackageMetadataSelection;
    private StructuredWingetObservation? appPackageMetadataSnapshot;
    private AppPackageMetadataReport? appPackageMetadataReport;
    private DateTimeOffset? appPackageMetadataAttemptUtc, appPackageMetadataResponseUtc;
    private CheckState appPackageMetadataState = CheckState.Unknown;
    private bool appPackageMetadataReading, appPackageMetadataAdapterInvoked;
    private string appPackageMetadataMessage = "Not checked. Select a current Available package and explicitly read its offered-version catalog metadata. This may contact WinGet sources and cache manifest metadata; no package binary is downloaded or installed.";

    public ICommand ReadAppPackageMetadataCommand { get; private set; } = null!;
    public string AppPackageMetadataMessage => appPackageMetadataMessage;
    public AppPackageMetadataReport? AppPackageMetadataLastReport => appPackageMetadataReport != null &&
        ReferenceEquals(appPackageMetadataSelection, SelectedAppUpdateOffer) &&
        ReferenceEquals(appPackageMetadataSnapshot, AppUpdateLastObservation) ? appPackageMetadataReport : null;
    public string AppPackageMetadataQueryDetails => appPackageMetadataAttemptUtc is not { } attempt
        ? "No catalog metadata request for the current selection. Selecting a package does not start a query."
        : $"App request attempt UTC: {attempt:u}\n" +
            (appPackageMetadataSelection is { } selection
                ? $"Selected package ID: {selection.Id}\nExpected installed version: {selection.InstalledVersion}; offered version: {selection.AvailableVersion}\n"
                : "No current selected package scope.\n") +
            (appPackageMetadataResponseUtc is { } response
                ? $"Adapter response received UTC: {response:u}; state: {appPackageMetadataState}. Receipt is not proof that manifest retrieval succeeded or that native work stopped."
                : appPackageMetadataReading ? "Waiting for the selected metadata request; no completed response is displayed."
                    : appPackageMetadataAdapterInvoked ? "No completed adapter response was accepted."
                        : "No metadata adapter call was made.") +
            "\nSource: the fixed supported WinGet catalog, with fresh package ID, installed/offered version and source checks. These are request/response observations, not a manufacturer release date or installation permission.";
    public string AppPackageMetadataDetails => AppPackageMetadataLastReport is { } report
        ? $"Matched package ID: {report.PackageId}\nRechecked installed version: {report.InstalledVersion}\nRechecked offered version: {report.OfferedVersion}\nMetadata observed UTC: {report.CheckedUtc:u}\n\n" +
            DescribeAppPackageMetadataField("Catalog-reported publisher — identity/signature not verified", report.Publisher) + "\n\n" +
            DescribeAppPackageMetadataField("Catalog description — untrusted plain text", report.Description) + "\n\n" +
            DescribeAppPackageMetadataField("Catalog-reported license — not a permission or legal determination", report.License) + "\n\n" +
            DescribeAppPackageMetadataField("Catalog release notes — authenticity and applicability not verified", report.ReleaseNotes) + "\n\n" +
            "These are localized catalog-manifest claims, not verified publisher identity, official manufacturer release notes, package authenticity, device compatibility or installation readiness. Prerequisites remain Unknown. Text is not rendered as HTML/Markdown; no returned link is opened. No package asset, agreement acceptance, installation, elevation or restart is requested."
        : "No accepted catalog metadata for the current selected offer. Read metadata explicitly after reviewing the package ID and versions. Unknown/missing fields must not become publisher verification, official release-note claims, compatibility or installation readiness. No installation action is available.";

    private static string DescribeAppPackageMetadataField(string title, AppPackageMetadataField field) =>
        title + "\nState: " + field.State + "\n" +
        (field.State == CheckState.Available && field.Value != null ? field.Value : "Unknown / no qualified value returned.");

    private void InitializeAppPackageMetadataReview() => ReadAppPackageMetadataCommand = new AsyncCommand(ReadAppPackageMetadata,
        () => !IsBusy && !HasPendingAppUpdateDiscovery && SelectedAppUpdateOffer is { Status: CheckState.Available });

    private void InvalidateAppPackageMetadata()
    {
        appPackageMetadataGeneration++;
        appPackageMetadataSelection = null; appPackageMetadataSnapshot = null; appPackageMetadataReport = null;
        appPackageMetadataAttemptUtc = appPackageMetadataResponseUtc = null;
        appPackageMetadataState = CheckState.Unknown; appPackageMetadataReading = appPackageMetadataAdapterInvoked = false;
        appPackageMetadataMessage = "Not checked for this selection. Read selected catalog metadata is a separate online action; it may fetch/cache manifest metadata, but never downloads or installs the package.";
        NotifyAppPackageMetadata();
    }

    private void NotifyAppPackageMetadata()
    {
        foreach (string property in new[] { nameof(AppPackageMetadataMessage), nameof(AppPackageMetadataLastReport),
            nameof(AppPackageMetadataQueryDetails), nameof(AppPackageMetadataDetails) })
            PropertyChanged?.Invoke(this, new(property));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ReadAppPackageMetadata()
    {
        if (IsBusy || HasPendingAppUpdateDiscovery || SelectedAppUpdateOffer is not { Status: CheckState.Available } selection) return;
        var snapshot = AppUpdateLastObservation;
        InvalidateAppPackageMetadata();
        long generation = appPackageMetadataGeneration;
        bool Current() => generation == appPackageMetadataGeneration && ReferenceEquals(selection, SelectedAppUpdateOffer) &&
            ReferenceEquals(snapshot, AppUpdateLastObservation);
        appPackageMetadataSelection = selection; appPackageMetadataSnapshot = snapshot;
        using var stop = new CancellationTokenSource();
        cancellation = stop;
        // Close protection also covers preparation before the shared native owner starts.
        appUpdateCallActive = true; IsBusy = true; NotifyAppUpdateOwnership();
        appPackageMetadataAttemptUtc = DateTimeOffset.UtcNow; appPackageMetadataReading = true;
        appPackageMetadataMessage = "Revalidating the selected offer before reading catalog metadata. This may contact the fixed WinGet source and cache its manifest; no package asset, agreement acceptance or installation is requested.";
        Status = "Reading selected WinGet catalog metadata…";
        try
        {
            NotifyAppPackageMetadata();
            Event(ActivityArea.AppPackageMetadata, ActivityOutcome.Started);
            var prepared = AppPackageMetadataPolicy.Prepare(snapshot?.Result, selection, DateTimeOffset.UtcNow, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            if (prepared.State != CheckState.Available || prepared.Items.Count != 1)
            {
                appPackageMetadataReading = false; appPackageMetadataState = prepared.State;
                appPackageMetadataMessage = Stamp(prepared.State, prepared.Message);
                NotifyAppPackageMetadata(); Event(ActivityArea.AppPackageMetadata, Outcome(prepared.State)); return;
            }
            if (appUpdateDiscovery is not IAppPackageMetadataInventory reader)
            {
                appPackageMetadataReading = false; appPackageMetadataState = CheckState.Unsupported;
                appPackageMetadataMessage = "Unsupported · the selected read-only provider has no catalog metadata contract. No replacement provider or package action was used.";
                NotifyAppPackageMetadata(); Event(ActivityArea.AppPackageMetadata, ActivityOutcome.Partial); return;
            }
            var request = prepared.Items[0];
            appPackageMetadataAdapterInvoked = true;
            NotifyAppPackageMetadata();
            stop.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            var result = await reader.ReadPackageMetadataAsync(request, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            var received = DateTimeOffset.UtcNow;
            if (received < appPackageMetadataAttemptUtc.Value) throw new InvalidOperationException("Metadata response time is inconsistent.");
            appPackageMetadataResponseUtc = received; appPackageMetadataReading = false;
            if (!AppPackageMetadataPolicy.IsResultConsistent(request, result, received))
            {
                bool emptyFailure = Enum.IsDefined(result.State) && result.Items.Count == 0 &&
                    (result.State is CheckState.Unknown or CheckState.Unavailable or CheckState.Unsupported or CheckState.Failed or CheckState.Cancelled);
                appPackageMetadataState = emptyFailure ? result.State : CheckState.Unknown;
                string reason = emptyFailure && result.Message is { Length: > 0 and <= 4096 } message &&
                    !message.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                        ? message : "No consistent selected-version catalog metadata was accepted. The source/server may be unavailable, the exact offer may have changed, or metadata may be missing.";
                appPackageMetadataMessage = Stamp(appPackageMetadataState,
                    reason + " Refresh updates, review the source and versions, then retry; no installation readiness is established.");
            }
            else
            {
                appPackageMetadataReport = result.Items[0]; appPackageMetadataState = result.State;
                appPackageMetadataMessage = Stamp(result.State, "Selected-version catalog claims returned. Publisher identity, notes authenticity, device applicability and installation prerequisites remain unverified.");
            }
            NotifyAppPackageMetadata();
            Status = "Selected catalog metadata: " + appPackageMetadataState + ". Review the source, fields and limitations.";
            Event(ActivityArea.AppPackageMetadata, Outcome(appPackageMetadataState));
        }
        catch (OperationCanceledException)
        {
            if (Current())
            {
                appPackageMetadataReport = null; appPackageMetadataReading = false; appPackageMetadataState = CheckState.Cancelled;
                appPackageMetadataMessage = "Cancelled · no current metadata report was accepted. Stop waiting does not cancel a synchronous manifest getter or the WinGet server; the actual owner remains retained until return.";
                NotifyAppPackageMetadata();
            }
            Event(ActivityArea.AppPackageMetadata, ActivityOutcome.Cancelled);
        }
        catch (Exception)
        {
            if (Current())
            {
                appPackageMetadataReport = null; appPackageMetadataReading = false; appPackageMetadataState = CheckState.Failed;
                appPackageMetadataMessage = "Failed · selected catalog metadata could not be accepted. Check App Installer/source availability, policy and network access, refresh updates and retry. No package or installation status was promoted.";
                NotifyAppPackageMetadata();
            }
            Event(ActivityArea.AppPackageMetadata, ActivityOutcome.Failed);
        }
        finally
        {
            // A bounded caller result does not release the shared MTA owner. Clear the caller
            // marker before draining, then keep Busy/Cancel/close protection until actual return.
            appUpdateCallActive = false; NotifyAppUpdateOwnership();
            try
            {
                await DrainAppUpdateDiscoveryAsync();
                Status = Current()
                    ? "Catalog metadata request: " + appPackageMetadataState + ". Review its recorded outcome; owner settlement does not promote an earlier timeout/cancellation."
                    : "The previous catalog metadata request ended. The changed selection remains unchecked until explicitly requested.";
            }
            finally { cancellation = null; IsBusy = false; NotifyAppPackageMetadata(); }
        }
    }
}
