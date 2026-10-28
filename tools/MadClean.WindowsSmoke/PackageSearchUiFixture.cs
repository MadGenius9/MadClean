using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Controls;
using MadClean.App;
using MadClean.Core;

namespace MadClean.WindowsSmoke;

internal static partial class UiCapture
{
    // Invoked only after every capture and the separate actual desktop-search workflow.
    // No Capture call is permitted here: real package metadata must remain in memory only.
    private static async Task CheckPackagedAppSearch(MainWindow window, MainViewModel model, SmokeReport report)
    {
        const string check = "ui-packaged-app-search";
        if (model.IsBusy)
        {
            report.Unknown(check, "An earlier UI metadata workflow remains pending; no package command or search interaction started. No worker completion or software absence is inferred.");
            return;
        }
        model.SelectedPage = 6;
        await Idle(window);
        var sources = Descendants<TabControl>(window).Single(control => AutomationProperties.GetName(control) == "Installed application sources");
        sources.SelectedIndex = 1;
        await Idle(window);
        var search = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Search loaded Windows packages by name or publisher");
        var clear = Descendants<Button>(window).Single(button => Equals(button.Content, "Clear package search"));
        var read = Descendants<Button>(window).Single(button => Equals(button.Content, "Read Windows packages"));
        var rows = Descendants<DataGrid>(window).Single(grid => AutomationProperties.GetName(grid) == "Loaded current-user Windows package registrations");
        var details = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "Selected current-user package identity and coverage");
        if (!ReferenceEquals(read.Command, model.ReadPackagedApplicationsCommand) || !read.IsEnabled ||
            !ReferenceEquals(clear.Command, model.ClearInstalledAppSearchCommand) || clear.IsEnabled ||
            !ReferenceEquals(rows.ItemsSource, model.PackagedApplicationsView) ||
            !ReferenceEquals(model.PackagedApplicationsView.SourceCollection, model.PackagedApplicationRows) ||
            search.MaxLength != InstalledApplicationSearch.MaximumQueryCharacters || search.Text.Length != 0 ||
            model.PackagedApplicationRows.Count != 0 || !model.PackagedApplicationsView.IsEmpty ||
            model.SelectedPackagedApplication != null || !details.IsReadOnly || details.Text != model.PackagedApplicationDetails)
            throw new SmokeAssertionException("ui-package-search-initial-bindings");

        var desktopRows = model.InstalledApplications.ToArray();
        var desktopSource = model.InstalledApplicationsView.SourceCollection;
        var desktopSelection = model.SelectedInstalledApplication;
        string desktopMessage = model.InstalledMessage, baselineMessage = model.ApplicationBaselineMessage;
        string leftoverMessage = model.LeftoverMessage, desktopDetails = model.SelectedApplicationDetails;
        string planDetails = model.InstalledAppPlanDetails, msiMessage = model.MsiRegistrationMessage, msiDetails = model.MsiRegistrationDetails;
        var msiRows = model.MsiRegistrationRows.ToArray();
        var msiCoverage = model.MsiRegistrationCoverage.ToArray();
        string emptyDetails = model.PackagedApplicationDetails;
        int activityBeforeQuery = model.Activity.Count;
        bool DesktopSourceUnchanged() => model.InstalledApplications.SequenceEqual(desktopRows) &&
            ReferenceEquals(model.InstalledApplicationsView.SourceCollection, desktopSource) &&
            ReferenceEquals(model.SelectedInstalledApplication, desktopSelection) && model.InstalledMessage == desktopMessage &&
            model.ApplicationBaselineMessage == baselineMessage && model.LeftoverMessage == leftoverMessage &&
            model.SelectedApplicationDetails == desktopDetails && model.InstalledAppPlanDetails == planDetails &&
            model.MsiRegistrationMessage == msiMessage && model.MsiRegistrationDetails == msiDetails &&
            model.MsiRegistrationRows.SequenceEqual(msiRows) && model.MsiRegistrationCoverage.SequenceEqual(msiCoverage);

        // A real, explicitly selected production getter. No synthetic rows or deployment actions.
        read.Command.Execute(null);
        var watch = Stopwatch.StartNew();
        while (model.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(70))
        { await Task.Delay(50); await Idle(window); }
        if (model.IsBusy)
        {
            model.Cancel();
            var cancellationWait = Stopwatch.StartNew();
            while (model.IsBusy && cancellationWait.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(50); await Idle(window); }
            report.Unknown(check, "The explicit current-user package read exceeded its bounded UI wait. Cancellation was requested through the production command owner; completion is unverified, no forced worker/resource closure or later screenshot was attempted.");
            return;
        }
        if (!DesktopSourceUnchanged()) throw new SmokeAssertionException("ui-package-query-changed-desktop-source");
        var state = model.PackagedApplicationsMessage.StartsWith("Available · ", StringComparison.Ordinal) ? CheckState.Available :
            model.PackagedApplicationsMessage.StartsWith("Partial · ", StringComparison.Ordinal) ? CheckState.Partial : CheckState.Unknown;
        var unfiltered = model.PackagedApplicationRows.ToArray();
        var candidate = unfiltered.FirstOrDefault(package =>
        {
            var query = InstalledApplicationSearch.Create(package.Name);
            return query.IsValid && !query.IsEmpty;
        });
        if (state == CheckState.Unknown || unfiltered.Length is < 1 or > PackagedApplicationParser.MaximumRecords || candidate == null)
        {
            report.Unknown(check, "The real current-user package read supplied no completed usable nonempty bounded name projection. Empty/unavailable/unknown or unusable Partial coverage is not a positive UI proof or software-absence verdict. No sample rows, provider text or later screenshots substituted.");
            return;
        }
        string expectedOutcome = state == CheckState.Available ? "Completed" : "Partial";
        if (model.Activity.Count != activityBeforeQuery + 2 ||
            !model.Activity[0].EndsWith("PackagedApplications · " + expectedOutcome, StringComparison.Ordinal) ||
            !model.Activity[1].EndsWith("PackagedApplications · Started", StringComparison.Ordinal))
            throw new SmokeAssertionException("ui-package-query-activity-scope");
        var activity = model.Activity.ToArray();
        string packageMessage = model.PackagedApplicationsMessage, packageAttempt = model.PackagedApplicationsAttempt, status = model.Status;
        bool FilterOnly() => !model.IsBusy && model.Activity.SequenceEqual(activity) && DesktopSourceUnchanged() &&
            model.PackagedApplicationRows.SequenceEqual(unfiltered) && model.PackagedApplicationsMessage == packageMessage &&
            model.PackagedApplicationsAttempt == packageAttempt && model.Status == status;

        search.Text = candidate.Name;
        await Idle(window);
        var positive = InstalledApplicationSearch.Create(candidate.Name);
        int matches = unfiltered.Count(package => positive.MatchesText(package.Name, package.Publisher));
        if (model.InstalledAppSearchText != candidate.Name || !clear.IsEnabled || matches < 1 ||
            !model.PackagedApplicationsView.Contains(candidate) || model.PackagedApplicationsView.Cast<object>().Count() != matches || !FilterOnly())
            throw new SmokeAssertionException("ui-package-search-positive-binding");
        rows.SelectedItem = candidate;
        await Idle(window);
        if (!ReferenceEquals(model.SelectedPackagedApplication, candidate) || !ReferenceEquals(rows.SelectedItem, candidate) ||
            details.Text != model.PackagedApplicationDetails || !details.Text.Contains(candidate.FullName, StringComparison.Ordinal) || !FilterOnly())
            throw new SmokeAssertionException("ui-package-search-selection-details");
        string absentQuery = "MadClean-package-search-fixture-" + Guid.NewGuid().ToString("N");
        var negative = InstalledApplicationSearch.Create(absentQuery);
        if (unfiltered.Any(package => negative.MatchesText(package.Name, package.Publisher)))
            throw new SmokeAssertionException("ui-package-search-negative-not-distinct");
        search.Text = absentQuery;
        await Idle(window);
        if (model.InstalledAppSearchText != absentQuery || !model.PackagedApplicationsView.IsEmpty || rows.Items.Count != 0 ||
            model.SelectedPackagedApplication != null || rows.SelectedItem != null || model.PackagedApplicationDetails != emptyDetails ||
            details.Text != emptyDetails || !FilterOnly())
            throw new SmokeAssertionException("ui-package-search-hidden-selection-details");
        clear.Command.Execute(null);
        await Idle(window);
        if (search.Text.Length != 0 || model.InstalledAppSearchText.Length != 0 || clear.IsEnabled ||
            model.PackagedApplicationsView.Cast<object>().Count() != unfiltered.Length || rows.Items.Count != unfiltered.Length ||
            model.SelectedPackagedApplication != null || details.Text != emptyDetails || !FilterOnly())
            throw new SmokeAssertionException("ui-package-search-clear-or-source");
        report.Pass(check, $"Package sourceState={state}; actualLoadedRowCount={unfiltered.Length}; positiveMatchCount={matches}; textboxBindingVerified=true; exactSelectionAndDetailsVerified=true; hiddenSelectionAndDetailsCleared=true; clearRestoresSameRows=true; desktopSourceBaselineAndStatusUnchanged=true; noExtraFilterActivity=true. " +
            "One explicit production current-user package getter supplied real rows after all captures and desktop search. No package values, query text, details, provider messages or later screenshots exported. Partial coverage remains incomplete; no-match is not absence proof, and no deployment or other package action occurred.");
    }
}
