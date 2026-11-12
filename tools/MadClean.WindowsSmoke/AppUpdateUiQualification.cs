using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MadClean.App;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

/// <summary>Actual read-only application workflow in a separate disposable-CI mode, never passive smoke.</summary>
internal static class AppUpdateUiQualification
{
    private const string Check = "app-update-public-ui-contract";
    private const string MetadataCheck = "app-package-metadata-public-ui-contract";
    private const string DirectoryName = "app-update-ui-validation";
    private const int MaximumBytes = 16384;
    private static readonly object Gate = new();
    private static Owner? rootedOwner;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    private sealed record Context(string SourceCommit, string RunId, int RunAttempt);
    private sealed record Observation(int FormatVersion, string SourceCommit, string RunId, int RunAttempt,
        DateTimeOffset StartedUtc, DateTimeOffset ObservedUtc, string State,
        bool? ControlObserved, bool? CommandInvoked, bool? ResultReturned, bool? FreshResultObserved,
        bool? RenderedCountsMatched, bool? Settled, bool? WindowClosed, int? ExceptionHResult,
        bool ResultSchemaAccepted, string? ResultState, int? ItemCount, int? AvailableRows, int? UnknownRows,
        bool? PendingNativeRead, StructuredWingetProbe.Diagnostic? Diagnostic, MetadataObservation? Metadata = null);
    private sealed record MetadataObservation(string State, DateTimeOffset StartedUtc, DateTimeOffset ObservedUtc,
        bool? SelectionObserved, bool? ControlObserved, bool? CommandInvoked, bool? ReportReturned,
        bool? FreshReportObserved, bool? ReportSchemaAccepted, bool? RenderedFieldsMatched,
        bool? BaseObservationUnchanged, bool? SelectionClearInvalidated, bool? SelectionRestoreStayedUnchecked,
        bool? Settled, int? ExceptionHResult);

    internal static async Task<int> RunAsync(string repository)
    {
        try
        {
            var context = Current(repository);
            string output = FixedOutput(repository);
            CheckAncestry(output, true); Directory.CreateDirectory(output); CheckAncestry(output, false);
            string final = Path.Combine(output, "report.json"), temporary = final + ".new";
            if (Exists(final) || Exists(temporary)) return 1;
            Owner owner;
            lock (Gate)
            {
                if (rootedOwner is not null) return 2;
                owner = new Owner(context); rootedOwner = owner;
                try
                {
                    owner.Thread = new Thread(owner.Run) { IsBackground = false, Name = "MadClean actual app-update UI qualification" };
                    owner.Thread.SetApartmentState(ApartmentState.STA); owner.Thread.Start();
                }
                catch (Exception error) { owner.BeforeStartFailed(error.HResult); }
            }
            // The foreground UI owner keeps its real model/window alive through actual
            // native settlement. No reporter deadline can abandon that owner or kill it.
            var value = await owner.Completion.Task.ConfigureAwait(false);
            if (!Valid(value)) return 1;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > MaximumBytes) return 1;
            CheckAncestry(output, false);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, final, false);
            return value.State == "failed" || value.Metadata?.State == "failed" ? 1
                : value.State == "passed" && value.Metadata?.State == "passed" ? 0 : 2;
        }
        catch (Exception) { return 1; }
    }

    internal static void ReadIntoReport(string repository, string expectedSourceCommit, SmokeReport report)
    {
        try
        {
            var context = Current(repository);
            if (context.SourceCommit != expectedSourceCommit) throw new InvalidDataException();
            string output = FixedOutput(repository); CheckAncestry(output, false);
            var guards = SystemToolTrustNative.LockDirectoryChain(output);
            try
            {
                string path = Path.Combine(output, "report.json");
                using var handle = SystemToolTrustNative.OpenFile(path);
                SystemToolTrustNative.ValidateAttributes(handle, false); SystemToolTrustNative.ValidateFinalPath(handle, path);
                using var stream = new FileStream(handle, FileAccess.Read, 4096, false);
                if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException();
                byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1) throw new InvalidDataException();
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                var root = document.RootElement;
                int version = root.GetProperty("formatVersion").GetInt32();
                string[] outerFields = ["formatVersion", "sourceCommit", "runId", "runAttempt", "startedUtc", "observedUtc", "state",
                    "controlObserved", "commandInvoked", "resultReturned", "freshResultObserved", "renderedCountsMatched", "settled",
                    "windowClosed", "exceptionHResult", "resultSchemaAccepted", "resultState", "itemCount", "availableRows", "unknownRows",
                    "pendingNativeRead", "diagnostic"];
                if (!Fields(root, version == 2 ? [.. outerFields, "metadata"] : outerFields)) throw new InvalidDataException();
                if (version == 2 && root.GetProperty("metadata") is { ValueKind: not JsonValueKind.Null } metadata &&
                    !Fields(metadata, ["state", "startedUtc", "observedUtc", "selectionObserved", "controlObserved", "commandInvoked",
                        "reportReturned", "freshReportObserved", "reportSchemaAccepted", "renderedFieldsMatched", "baseObservationUnchanged",
                        "selectionClearInvalidated", "selectionRestoreStayedUnchecked", "settled", "exceptionHResult"])) throw new InvalidDataException();
                var diagnostic = root.GetProperty("diagnostic");
                if (diagnostic.ValueKind != JsonValueKind.Null && !Fields(diagnostic,
                    ["stage", "outcome", "hResult", "attempted", "startedUtc", "completedUtc", "projectedCount", "propertyFailureCount",
                     "sourceMatched", "agreementsDisabled", "authenticationNone", "connectSucceeded", "findSucceeded",
                     "nativeLimitExceeded", "projectionLimitExceeded"])) throw new InvalidDataException();
                var value = root.Deserialize<Observation>(Json) ?? throw new InvalidDataException();
                if (!Valid(value) || value.SourceCommit != context.SourceCommit || value.RunId != context.RunId || value.RunAttempt != context.RunAttempt ||
                    value.ObservedUtc > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - value.ObservedUtc > TimeSpan.FromMinutes(15)) throw new InvalidDataException();
                string detail = $"control={value.ControlObserved}; invoked={value.CommandInvoked}; result={value.ResultReturned}/{value.ResultState ?? "unobserved"}; fresh={value.FreshResultObserved}; renderedCounts={value.RenderedCountsMatched}; settled={value.Settled}; windowClosed={value.WindowClosed}; schema={value.ResultSchemaAccepted}; rows={Number(value.ItemCount)}/available={Number(value.AvailableRows)}/unknown={Number(value.UnknownRows)}; pending={value.PendingNativeRead}; stage={value.Diagnostic?.Stage ?? "unobserved"}; outcome={value.Diagnostic?.Outcome ?? "unobserved"}. Actual app controls and returned WinGet observations only; no package identities, provider text or screenshots exported. Partial remains Partial; zero rows never means all software is up to date. No installation, source agreements or restart requested.";
                if (detail.Length > 1000) throw new InvalidDataException();
                if (value.State == "passed") report.Pass(Check, detail);
                else if (value.State == "failed") report.Fail(Check, detail);
                else report.Unknown(Check, detail);
                ReportMetadata(value, report);
            }
            finally { foreach (var guard in guards.AsEnumerable().Reverse()) guard.Dispose(); }
        }
        catch (Exception)
        {
            report.Unknown(Check, "No accepted source/run/attempt-bound actual app-update UI observation. No query was started by this passive reader; compilation, SDK-only checks or neutral startup do not substitute for this workflow.");
            report.Unknown(MetadataCheck, "No accepted source/run-bound selected catalog metadata UI observation. Passive reading starts no query; discovery-only evidence does not qualify metadata.");
        }
    }

    private sealed class Owner(Context context)
    {
        internal readonly TaskCompletionSource<Observation> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Thread? Thread;
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private MadClean.App.App? application;
        private MainWindow? window;
        private MainViewModel? model;
        private DateTimeOffset? invocationUtc;
        private bool invoked, closed;
        private bool? control, command, returned, fresh, rendered, settled;
        private int? exception;
        private StructuredWingetObservation? actual;
        private MetadataObservation? metadata;

        internal void BeforeStartFailed(int hresult) { exception = hresult; Finish(); }
        internal void Run()
        {
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                application = new MadClean.App.App(); application.InitializeComponent();
                application.StartupUri = new Uri("/MadClean;component/MainWindow.xaml", UriKind.Relative);
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.DispatcherUnhandledException += (_, args) => { exception ??= args.Exception.HResult; args.Handled = true; };
                application.Startup += async (_, _) => await ObserveAsync();
                application.Run();
            }
            catch (Exception error) { exception ??= error.HResult; }
            // A dispatcher unexpectedly exiting does not prove a pending query ended.
            // Do not dispose/release the real model under a native worker or kill a server.
            while (Pending()) System.Threading.Thread.Sleep(100);
            Finish();
        }

        private async Task ObserveAsync()
        {
            try
            {
                await Idle();
                window = application!.MainWindow as MainWindow ?? throw new SmokeAssertionException("app-update-ui-window");
                window.Closed += (_, _) => closed = true;
                window.Width = 1200; window.Height = 900; await Idle();
                model = window.DataContext as MainViewModel ?? throw new SmokeAssertionException("app-update-ui-model");
                Require(!Pending() && model.AppUpdateLastObservation == null && model.Activity.Count == 0 &&
                    model.AppRows.Count == 0 && model.AppUpdateOffers.Count == 0 && model.SystemRows.Count == 0 &&
                    model.Disks.Count == 0 && model.CleanupRows.Count == 0 && model.StartupRows.Count == 0 && model.SecurityRows.Count == 0,
                    "app-update-ui-neutral-model");
                var navigation = Descendants<ListBox>(window).Single(item => AutomationProperties.GetName(item) == "Main navigation");
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => Equals(item.Content, "Updates"));
                await Idle();
                var button = Descendants<Button>(window).Single(item => AutomationProperties.GetAutomationId(item) == "AppCheckUpdates");
                control = ReferenceEquals(button.Command, model.UpdatesCommand) && button.IsVisible && button.IsEnabled &&
                    model.UpdatesCommand.CanExecute(null) && model.AppUpdateLastObservation == null;
                Require(control == true, "app-update-ui-control-binding");
                button.BringIntoView(); await Idle();
                var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
                if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new SmokeAssertionException("app-update-ui-invoke-pattern");
                invocationUtc = DateTimeOffset.UtcNow; invoked = true;
                invoke.Invoke(); command = true;
                await Idle(); await WaitForSettlement();
                actual = model.AppUpdateLastObservation; returned = actual != null;
                fresh = Fresh();
                if (actual != null)
                {
                    var grid = Descendants<DataGrid>(window).Single(item => AutomationProperties.GetName(item) == "Actual WinGet matched package observations");
                    rendered = ReferenceEquals(grid.ItemsSource, model.AppUpdateOffers) && grid.Items.Count == actual.Result.Items.Count &&
                        model.AppUpdateOffers.Count == actual.Result.Items.Count &&
                        model.AppUpdateOffers.Count(row => row.Status == CheckState.Available) == actual.Result.Items.Count(row => row.Status == CheckState.Available) &&
                        model.AppUpdateOffers.Count(row => row.Status == CheckState.Unknown) == actual.Result.Items.Count(row => row.Status == CheckState.Unknown) &&
                        Text("AppUpdateQueryState") == model.AppUpdateQueryState && Text("AppUpdateAvailableCount") == model.AppUpdateAvailableCount &&
                        Text("AppUpdateUnknownCount") == model.AppUpdateUnknownCount && Text("AppUpdateCatalogCount") == model.AppUpdateCatalogCount &&
                        Text("AppUpdateCheckedUtc") == model.AppUpdateCheckedUtc && Text("AppUpdateOwnership") == model.AppUpdateOwnershipStatus;
                    Require(rendered == true, "app-update-ui-real-rendered-bindings");
                    if (Fresh() && StructuredWingetObservationPolicy.IsConsistent(actual, invocationUtc!.Value, DateTimeOffset.UtcNow, false) &&
                        actual.Result.State is CheckState.Available or CheckState.Partial)
                        await ObserveMetadataAsync(grid);
                }
            }
            catch (Exception error) { exception ??= error.HResult; }
            finally
            {
                await WaitForSettlement();
                if (invoked && model != null)
                { actual ??= model.AppUpdateLastObservation; returned ??= actual != null; fresh ??= Fresh(); }
                if (window != null && !closed) window.Close();
                await WaitForSettlement();
                settled = !Pending() && closed;
                application!.Shutdown(exception == null ? 0 : 1);
            }
        }

        private async Task ObserveMetadataAsync(DataGrid grid)
        {
            var began = DateTimeOffset.UtcNow;
            bool? selected = null, observedControl = null, didInvoke = null, reportReturned = null, freshReport = null,
                accepted = null, fieldsRendered = null, baseUnchanged = null, cleared = null, restored = null, ownerSettled = null;
            int? errorCode = null;
            try
            {
                var before = model!.AppUpdateLastObservation;
                var offers = model.AppUpdateOffers.ToArray();
                string[] counts = [model.AppUpdateQueryState, model.AppUpdateAvailableCount, model.AppUpdateUnknownCount,
                    model.AppUpdateCatalogCount, model.AppUpdateCheckedUtc, model.AppUpdateOfferQueryDetails];
                var offer = offers.FirstOrDefault(row => row.Status == CheckState.Available);
                selected = offer != null;
                if (offer == null) return;
                grid.SelectedItem = offer; grid.ScrollIntoView(offer); await Idle();
                selected = ReferenceEquals(grid.SelectedItem, offer) && ReferenceEquals(model.SelectedAppUpdateOffer, offer) &&
                    model.AppPackageMetadataLastReport == null && !Pending();
                Require(selected == true, "app-metadata-ui-current-selection");
                var button = Descendants<Button>(window!).Single(item => AutomationProperties.GetAutomationId(item) == "ReadAppPackageMetadata");
                button.BringIntoView(); await Idle();
                observedControl = ReferenceEquals(button.Command, model.ReadAppPackageMetadataCommand) && button.IsVisible &&
                    button.IsEnabled && model.ReadAppPackageMetadataCommand.CanExecute(null);
                Require(observedControl == true, "app-metadata-ui-control-binding");
                var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
                if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                    throw new SmokeAssertionException("app-metadata-ui-invoke-pattern");
                var boundary = DateTimeOffset.UtcNow;
                invoke.Invoke(); didInvoke = true;
                await Idle(); await WaitForSettlement();
                var received = DateTimeOffset.UtcNow;
                var report = model.AppPackageMetadataLastReport;
                reportReturned = report != null;
                if (report != null)
                {
                    freshReport = report.CheckedUtc >= boundary && report.CheckedUtc <= received;
                    accepted = ConsistentMetadataReport(report, offer, boundary, received);
                    var source = Descendants<TextBox>(window!).Single(item => AutomationProperties.GetAutomationId(item) == "AppPackageMetadataQueryDetails");
                    var details = Descendants<TextBox>(window!).Single(item => AutomationProperties.GetAutomationId(item) == "AppPackageMetadataDetails");
                    details.BringIntoView(); await Idle();
                    fieldsRendered = source.IsVisible && details.IsVisible && source.IsReadOnly && details.IsReadOnly &&
                        source.Text == model.AppPackageMetadataQueryDetails && details.Text == model.AppPackageMetadataDetails &&
                        details.Text.Contains(report.PackageId, StringComparison.Ordinal) &&
                        details.Text.Contains(report.InstalledVersion, StringComparison.Ordinal) &&
                        details.Text.Contains(report.OfferedVersion, StringComparison.Ordinal) &&
                        new[] { report.Publisher, report.Description, report.License, report.ReleaseNotes }
                            .Where(field => field.State == CheckState.Available).All(field => details.Text.Contains(field.Value!, StringComparison.Ordinal)) &&
                        source.GetBindingExpression(TextBox.TextProperty) != null && details.GetBindingExpression(TextBox.TextProperty) != null;
                    Require(freshReport == true && accepted == true && fieldsRendered == true, "app-metadata-ui-fresh-rendered-report");
                }
                baseUnchanged = ReferenceEquals(before, model.AppUpdateLastObservation) && ReferenceEquals(before, actual) &&
                    offers.Length == model.AppUpdateOffers.Count && offers.Select((row, index) => ReferenceEquals(row, model.AppUpdateOffers[index])).All(value => value) &&
                    counts.SequenceEqual(new[] { model.AppUpdateQueryState, model.AppUpdateAvailableCount, model.AppUpdateUnknownCount,
                        model.AppUpdateCatalogCount, model.AppUpdateCheckedUtc, model.AppUpdateOfferQueryDetails });
                Require(baseUnchanged == true, "app-metadata-ui-base-observation-unchanged");
                // With no accepted fields there is no report whose invalidation can be
                // observed. Preserve that negative result as inconclusive, not a pass.
                if (report == null) return;
                int activityCount = model.Activity.Count;
                grid.SelectedItem = null; await Idle();
                cleared = model.SelectedAppUpdateOffer == null && model.AppPackageMetadataLastReport == null &&
                    !model.ReadAppPackageMetadataCommand.CanExecute(null) && !Pending() && model.Activity.Count == activityCount;
                Require(cleared == true, "app-metadata-ui-selection-clear");
                grid.SelectedItem = offer; await Idle();
                restored = ReferenceEquals(model.SelectedAppUpdateOffer, offer) && model.AppPackageMetadataLastReport == null &&
                    !Pending() && model.Activity.Count == activityCount && model.ReadAppPackageMetadataCommand.CanExecute(null);
                Require(restored == true, "app-metadata-ui-selection-restore-unchecked");
            }
            catch (Exception error) { errorCode = error.HResult; }
            finally
            {
                try { await WaitForSettlement(); ownerSettled = !Pending(); }
                catch (Exception error) { errorCode ??= error.HResult; }
                var observation = new MetadataObservation("inconclusive", began, DateTimeOffset.UtcNow,
                    selected, observedControl, didInvoke, reportReturned, freshReport, accepted, fieldsRendered,
                    baseUnchanged, cleared, restored, ownerSettled, errorCode);
                metadata = observation with { State = MetadataState(observation) };
            }
        }

        // The real UI report is checked against its exact current selected ID/versions and
        // a policy validation envelope bounded by the actual UI invocation. This neither
        // injects results nor claims access to the provider's private original request token.
        private static bool ConsistentMetadataReport(AppPackageMetadataReport report, AppUpdate selected,
            DateTimeOffset invokedUtc, DateTimeOffset receivedUtc)
        {
            if (selected.Source != StructuredWingetPolicy.SourceName || report.PackageId != selected.Id ||
                report.InstalledVersion != selected.InstalledVersion || report.OfferedVersion != selected.AvailableVersion || report.CanInstall) return false;
            var request = new AppPackageMetadataRequest(report.RequestId, selected.Id, selected.InstalledVersion,
                selected.AvailableVersion, invokedUtc, invokedUtc + AppPackageMetadataPolicy.RequestLifetime);
            int known = new[] { report.Publisher, report.Description, report.License, report.ReleaseNotes }.Count(field => field.State == CheckState.Available);
            return AppPackageMetadataPolicy.IsResultConsistent(request,
                new CheckResult<AppPackageMetadataReport>(known == 4 ? CheckState.Available : CheckState.Partial, "", [report]), receivedUtc);
        }

        private bool Fresh() => actual is { Diagnostic.Attempted: true, Diagnostic.StartedUtc: { } attempt } &&
            invocationUtc is { } boundary && attempt >= boundary && attempt <= DateTimeOffset.UtcNow;
        private bool Pending() => model != null && (model.IsBusy || model.HasPendingAppUpdateDiscovery ||
            model.HasPendingComponentStoreStatus || model.HasPendingMsiUninstallRequest);
        private async Task WaitForSettlement() { while (Pending()) { await Task.Delay(50); await Idle(); } }
        private async Task Idle() => await application!.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        private string Text(string id) => Descendants<TextBlock>(window!).Single(item => AutomationProperties.GetAutomationId(item) == id).Text;

        private void Finish()
        {
            if (Pending()) return;
            bool schema = actual != null && invocationUtc is { } boundary &&
                StructuredWingetObservationPolicy.IsConsistent(actual, boundary, DateTimeOffset.UtcNow, false);
            StructuredWingetProbe.Diagnostic? diagnostic = null;
            int? count = null, available = null, unknown = null;
            string? resultState = null;
            if (schema)
            {
                var data = actual!.Diagnostic;
                diagnostic = new(data.Stage.ToString(), data.Outcome.ToString(), data.HResult, data.Attempted, data.StartedUtc,
                    data.CompletedUtc, data.ProjectedCount, data.PropertyFailureCount, data.SourceMatched, data.AgreementsDisabled,
                    data.AuthenticationNone, data.ConnectSucceeded, data.FindSucceeded, data.NativeLimitExceeded, data.ProjectionLimitExceeded);
                count = actual.Result.Items.Count; available = actual.Result.Items.Count(row => row.Status == CheckState.Available);
                unknown = actual.Result.Items.Count(row => row.Status == CheckState.Unknown); resultState = actual.Result.State.ToString();
            }
            var value = new Observation(2, context.SourceCommit, context.RunId, context.RunAttempt, started, DateTimeOffset.UtcNow,
                "inconclusive", control, command, returned, schema ? fresh : null, rendered, settled, window is null ? null : closed, exception,
                schema, resultState, count, available, unknown, model == null ? null : model.HasPendingAppUpdateDiscovery, diagnostic, metadata);
            bool passed = Qualified(value);
            string state = exception != null || actual != null && !schema ? "failed" : passed ? "passed" : "inconclusive";
            lock (Gate) if (ReferenceEquals(rootedOwner, this)) rootedOwner = null;
            Completion.TrySetResult(value with { State = state });
        }
    }

    private static bool Valid(Observation value)
    {
        if (value.FormatVersion is not (1 or 2) || value.FormatVersion == 1 && value.Metadata != null || value.StartedUtc == default || value.StartedUtc.Offset != TimeSpan.Zero ||
            value.ObservedUtc.Offset != TimeSpan.Zero || value.StartedUtc > value.ObservedUtc ||
            value.ObservedUtc - value.StartedUtc > TimeSpan.FromMinutes(6) || value.State is not ("passed" or "failed" or "inconclusive")) return false;
        if (value.CommandInvoked == true && value.ControlObserved != true || value.ResultReturned == true && value.CommandInvoked != true ||
            value.FreshResultObserved == true && value.ResultReturned != true || value.RenderedCountsMatched == true && value.ResultReturned != true ||
            value.Settled == true && (value.WindowClosed != true || value.PendingNativeRead == true)) return false;
        if (!value.ResultSchemaAccepted)
        {
            if (value.ResultState is not null || value.ItemCount is not null || value.AvailableRows is not null || value.UnknownRows is not null || value.Diagnostic is not null) return false;
        }
        else
        {
            if (value.ResultReturned != true || value.Diagnostic is not { } data ||
                !Enum.TryParse<CheckState>(value.ResultState, out var state) || !Enum.IsDefined(state) || state.ToString() != value.ResultState ||
                !Enum.TryParse<StructuredWingetStage>(data.Stage, out var stage) || !Enum.IsDefined(stage) || stage.ToString() != data.Stage ||
                !Enum.TryParse<StructuredWingetOutcome>(data.Outcome, out var outcome) || !Enum.IsDefined(outcome) || outcome.ToString() != data.Outcome ||
                !StateMatches(outcome, state) || value.ItemCount is null or < 0 or > 256 || value.AvailableRows is null or < 0 || value.UnknownRows is null or < 0 ||
                value.AvailableRows + value.UnknownRows != value.ItemCount || data.ProjectedCount is < 0 or > 256 || data.PropertyFailureCount is < 0 or > 4096 ||
                (data.Attempted ? data.StartedUtc is null : data.StartedUtc is not null) ||
                (data.StartedUtc is { } start && (start.Offset != TimeSpan.Zero || start < value.StartedUtc || start > value.ObservedUtc)) ||
                (data.CompletedUtc is { } end && (end.Offset != TimeSpan.Zero || end < value.StartedUtc || end > value.ObservedUtc || data.StartedUtc is { } begin && end < begin)) ||
                (outcome is StructuredWingetOutcome.Completed or StructuredWingetOutcome.Partial ? value.ItemCount != data.ProjectedCount : value.ItemCount != 0) ||
                (outcome is StructuredWingetOutcome.Completed or StructuredWingetOutcome.Partial &&
                    (stage != StructuredWingetStage.Completed || !data.Attempted || data.HResult is not null || data.StartedUtc is null || data.CompletedUtc is null ||
                     data.SourceMatched != true || data.AgreementsDisabled != true || data.AuthenticationNone != true ||
                     data.ConnectSucceeded != true || data.FindSucceeded != true)) ||
                (outcome == StructuredWingetOutcome.Completed && (data.NativeLimitExceeded != false || data.ProjectionLimitExceeded != false || data.PropertyFailureCount != 0))) return false;
        }
        if (value.FreshResultObserved == true && value.Diagnostic is not { Attempted: true, StartedUtc: not null }) return false;
        string expected = value.ExceptionHResult != null || value.ResultReturned == true && !value.ResultSchemaAccepted
            ? "failed" : Qualified(value) ? "passed" : "inconclusive";
        return value.State == expected && ValidMetadata(value);
    }

    private static bool MetadataQualified(MetadataObservation value) => value.SelectionObserved == true && value.ControlObserved == true &&
        value.CommandInvoked == true && value.ReportReturned == true && value.FreshReportObserved == true && value.ReportSchemaAccepted == true &&
        value.RenderedFieldsMatched == true && value.BaseObservationUnchanged == true && value.SelectionClearInvalidated == true &&
        value.SelectionRestoreStayedUnchecked == true && value.Settled == true && value.ExceptionHResult == null;
    private static string MetadataState(MetadataObservation value) => value.ExceptionHResult != null ||
        value.ReportReturned == true && value.ReportSchemaAccepted == false ? "failed" : MetadataQualified(value) ? "passed" : "inconclusive";
    private static bool ValidMetadata(Observation outer)
    {
        if (outer.Metadata is not { } value) return true;
        if (outer.FormatVersion != 2 || value.StartedUtc == default || value.StartedUtc.Offset != TimeSpan.Zero ||
            value.ObservedUtc.Offset != TimeSpan.Zero || value.StartedUtc < outer.StartedUtc || value.StartedUtc > value.ObservedUtc ||
            value.ObservedUtc > outer.ObservedUtc || value.ObservedUtc - value.StartedUtc > TimeSpan.FromMinutes(6) ||
            !outer.ResultSchemaAccepted || outer.FreshResultObserved != true || outer.RenderedCountsMatched != true ||
            outer.ResultState is not ("Available" or "Partial") ||
            outer.Diagnostic?.CompletedUtc is not { } discoveryCompleted || value.StartedUtc < discoveryCompleted ||
            value.SelectionObserved == true && outer.AvailableRows is not > 0 ||
            value.SelectionObserved == false && outer.AvailableRows != 0 && value.ExceptionHResult == null ||
            value.State != MetadataState(value) ||
            value.ControlObserved == true && value.SelectionObserved != true || value.CommandInvoked == true && value.ControlObserved != true ||
            value.ReportReturned == true && value.CommandInvoked != true || value.FreshReportObserved == true && value.ReportReturned != true ||
            value.ReportSchemaAccepted == true && (value.ReportReturned != true || value.FreshReportObserved != true) ||
            value.RenderedFieldsMatched == true && value.ReportReturned != true || value.BaseObservationUnchanged == true && value.CommandInvoked != true ||
            value.SelectionClearInvalidated == true && value.BaseObservationUnchanged != true ||
            value.SelectionRestoreStayedUnchecked == true && value.SelectionClearInvalidated != true ||
            value.Settled == true && (outer.Settled != true || outer.PendingNativeRead != false || outer.WindowClosed != true) ||
            value.State == "passed" && !Qualified(outer)) return false;
        return true;
    }
    private static void ReportMetadata(Observation outer, SmokeReport report)
    {
        if (outer.Metadata is not { } value)
        {
            report.Unknown(MetadataCheck, "No separate selected catalog metadata UI observation. Discovery-only/v1 evidence cannot qualify this online workflow; passive reading starts no query.");
            return;
        }
        string detail = $"selection={value.SelectionObserved}; control={value.ControlObserved}; invoked={value.CommandInvoked}; report={value.ReportReturned}; fresh={value.FreshReportObserved}; schema={value.ReportSchemaAccepted}; rendered={value.RenderedFieldsMatched}; baseUnchanged={value.BaseObservationUnchanged}; clear={value.SelectionClearInvalidated}; restoreUnchecked={value.SelectionRestoreStayedUnchecked}; settled={value.Settled}. Actual selected metadata button and copyable read-only fields only; no IDs, versions, provider text or screenshots exported. No available row/accepted fields is inconclusive. No installation, agreement acceptance, cancellation-race or restart proof.";
        if (value.State == "passed") report.Pass(MetadataCheck, detail);
        else if (value.State == "failed") report.Fail(MetadataCheck, detail);
        else report.Unknown(MetadataCheck, detail);
    }

    private static bool Qualified(Observation value) => value.ControlObserved == true && value.CommandInvoked == true && value.ResultReturned == true &&
        value.FreshResultObserved == true && value.RenderedCountsMatched == true && value.Settled == true && value.WindowClosed == true &&
        value.ExceptionHResult == null && value.ResultSchemaAccepted && value.PendingNativeRead == false && value.ResultState is "Available" or "Partial" &&
        value.Diagnostic is { Attempted: true, Stage: "Completed", Outcome: "Completed" or "Partial", HResult: null, StartedUtc: not null, CompletedUtc: not null,
            SourceMatched: true, AgreementsDisabled: true, AuthenticationNone: true, ConnectSucceeded: true, FindSucceeded: true };

    private static bool StateMatches(StructuredWingetOutcome outcome, CheckState state) => outcome switch
    {
        StructuredWingetOutcome.Completed => state == CheckState.Available, StructuredWingetOutcome.Partial => state == CheckState.Partial,
        StructuredWingetOutcome.Cancelled => state == CheckState.Cancelled, StructuredWingetOutcome.Unsupported => state == CheckState.Unsupported,
        StructuredWingetOutcome.Unavailable => state == CheckState.Unavailable, StructuredWingetOutcome.Failed => state == CheckState.Failed,
        StructuredWingetOutcome.NotStarted or StructuredWingetOutcome.TimedOut or StructuredWingetOutcome.Busy => state == CheckState.Unknown, _ => false
    };
    private static Context Current(string repository)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !Environment.Is64BitProcess ||
            repository != Path.GetFullPath(repository) || !string.Equals(repository, Path.GetFullPath(Environment.CurrentDirectory), StringComparison.OrdinalIgnoreCase) ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "MadGenius9/MadClean" || !ReadOnlyWingetQualificationSource.IsAllowedRef(Environment.GetEnvironmentVariable("GITHUB_REF")) ||
            Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is not ("push" or "workflow_dispatch") ||
            Environment.GetEnvironmentVariable("GITHUB_JOB") != "windows-verification" || Environment.GetEnvironmentVariable("MADCLEAN_WINDOWS_VALIDATION") != "1") throw new InvalidDataException();
        string source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "", run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "", attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "";
        if (source.Length != 40 || source.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) || run.Length is < 1 or > 32 || run[0] == '0' ||
            run.Any(c => c is < '0' or > '9') || attempt.Length is < 1 or > 9 || !int.TryParse(attempt, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0 || number.ToString(CultureInfo.InvariantCulture) != attempt) throw new InvalidDataException();
        return new(source, run, number);
    }
    private static string FixedOutput(string repository)
    {
        if (repository.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(repository)!).DriveType != DriveType.Fixed) throw new InvalidDataException();
        CheckAncestry(repository, false);
        if (!Exists(Path.Combine(repository, "MadClean.sln")) || !Exists(Path.Combine(repository, "src", "MadClean.App", "MadClean.App.csproj"))) throw new InvalidDataException();
        return Path.Combine(repository, "artifacts", DirectoryName);
    }
    private static bool Exists(string path) { try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static void CheckAncestry(string path, bool allowMissing)
    {
        var chain = new Stack<string>();
        for (string? current = path; current != null; current = Path.GetDirectoryName(current)) chain.Push(current);
        foreach (string current in chain)
        {
            try { var attributes = File.GetAttributes(current); if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(); }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }
    private static bool Fields(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!found.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) return false;
        return found.Count == names.Length;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value) yield return value;
            foreach (var below in Descendants<T>(child)) yield return below;
        }
    }
    private static void Require(bool value, string code) { if (!value) throw new SmokeAssertionException(code); }
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unobserved";
}
