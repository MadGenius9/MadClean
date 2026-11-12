using System.Windows.Input;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private readonly PackagedAppSettingsLauncher packagedSettingsLauncher = new();
    private CheckResult<PackagedApplication>? packagedSettingsSource;
    private DateTimeOffset? packagedSettingsSourceUtc;
    private PackagedAppSettingsPreview? packagedSettingsPreview;
    private CancellationTokenSource? packagedSettingsSelectionStop;
    private Task<CheckResult<PackagedApplication>>? packagedSettingsRefreshTask;
    private Task<CheckResult<string>>? packagedSettingsHandoffTask;
    private long packagedSettingsGeneration;
    private string packagedSettingsMessage = "No package page requested. Select a loaded package, preview its family-level destination, then explicitly open it.";
    public ICommand PreviewPackagedSettingsCommand { get; private set; } = null!;
    public ICommand OpenPackagedSettingsCommand { get; private set; } = null!;
    public bool HasPendingPackagedSettingsWork => packagedSettingsRefreshTask is { IsCompleted: false } ||
        packagedSettingsHandoffTask is { IsCompleted: false } || packagedSettingsLauncher.HasPendingHandoff;
    public string PackagedSettingsMessage { get => packagedSettingsMessage; private set => Set(ref packagedSettingsMessage, value); }
    private PackagedAppSettingsPreview? CurrentPackagedSettingsPreview => packagedSettingsPreview is { } preview &&
        ReferenceEquals(CurrentPackagedApplication, preview.Selected) ? preview : null;
    public string PackagedSettingsPreview => CurrentPackagedSettingsPreview is { } preview
        ? $"Selected registration: {preview.Selected.FullName}\nFamily destination: {preview.Selected.FamilyName}\nExact URI: {preview.TargetUri.OriginalString}\nOriginal source received UTC: {preview.SourceCompletedUtc:u}; coverage: {preview.SourceCoverage}\nPreview created UTC: {preview.CreatedUtc:u}; expires after five minutes\nReported non-removable: {Flag(preview.Selected.NonRemovable)}\n\n{PackagedAppSettingsPreview.Effect}\n\nBefore handoff, MadClean rereads current-user Main/Bundle metadata and requires the exact selected registration metadata still to match. Source changes can occur after that observation; this is not a locked package snapshot.\n\n{PackagedAppSettingsPreview.Limits}\n\nNo elevation is requested by MadClean. Windows may require separate consent for any later action.\nOfficial reference: {PackagedAppSettingsPreview.OfficialReference}"
        : "No current navigation preview. A preview must come from a supported exact loaded registration observed within five minutes. Windows' documented destination is family-level; it does not select an exact version or guarantee an uninstall control. No package operation is requested.";
    private void InitializePackagedAppSettings()
    {
        PreviewPackagedSettingsCommand = new ActionCommand(PreviewPackagedSettings,
            () => !IsBusy && !HasPendingPackagedSettingsWork && CurrentPackagedApplication != null && packagedSettingsSource != null);
        OpenPackagedSettingsCommand = new AsyncCommand(OpenPackagedSettings,
            () => !IsBusy && !HasPendingPackagedSettingsWork && CurrentPackagedSettingsPreview is { } preview &&
                PackagedAppSettingsPolicy.IsAllowed(preview, DateTimeOffset.UtcNow));
    }
    private void InvalidatePackagedSettings(bool clearSource)
    {
        packagedSettingsGeneration++;
        packagedSettingsSelectionStop?.Cancel();
        packagedSettingsPreview = null;
        if (clearSource) { packagedSettingsSource = null; packagedSettingsSourceUtc = null; }
        PackagedSettingsMessage = "Selection, search or source changed. No current page preview; a queued handoff is cancelled before dispatch when possible. A page already dispatched cannot be undone.";
        NotifyPackagedSettings();
    }
    private void PublishPackagedSettingsSource(CheckResult<PackagedApplication> source, DateTimeOffset completedUtc)
    {
        packagedSettingsSource = source; packagedSettingsSourceUtc = completedUtc;
        NotifyPackagedSettings();
    }
    private void PreviewPackagedSettings()
    {
        if (IsBusy || HasPendingPackagedSettingsWork || CurrentPackagedApplication is not { } selected ||
            packagedSettingsSource is not { } source || packagedSettingsSourceUtc is not { } completed) return;
        packagedSettingsPreview = PackagedAppSettingsPolicy.Create(source, selected, completed, DateTimeOffset.UtcNow);
        PackagedSettingsMessage = packagedSettingsPreview == null
            ? "No supported current source-bound family page can be previewed. Read packages again and select one actual supported Main/Bundle registration; unknown, stale or conflicting identity stays blocked."
            : "Family-level destination previewed below. Nothing has been opened; Open reviewed package page performs a fresh metadata check first.";
        NotifyPackagedSettings();
    }
    private async Task OpenPackagedSettings()
    {
        if (IsBusy || HasPendingPackagedSettingsWork || CurrentPackagedSettingsPreview is not { } preview ||
            !PackagedAppSettingsPolicy.IsAllowed(preview, DateTimeOffset.UtcNow)) return;
        long generation = packagedSettingsGeneration;
        bool Current() => generation == packagedSettingsGeneration && ReferenceEquals(packagedSettingsPreview, preview) &&
            ReferenceEquals(CurrentPackagedApplication, preview.Selected);
        using var sourceStop = new CancellationTokenSource();
        using var selectedStop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sourceStop.Token, selectedStop.Token);
        packagedSettingsSelectionStop = selectedStop;
        cancellation = sourceStop; IsBusy = true;
        bool handoffRequested = false;
        Status = "Rechecking the reviewed package before Windows navigation…";
        Event(ActivityArea.GuidedWindowsHandoff, ActivityOutcome.Started);
        try
        {
            var state = await Work();
            // Shell cancellation after dispatch is Unknown, not a cancelled inventory operation.
            // The typed launcher owns that distinction; do not replace its result with a token check.
            Status = $"Package family page request: {state}. Page appearance and any later Windows operation remain unverified.";
            Event(ActivityArea.GuidedWindowsHandoff, Outcome(state));
        }
        catch (OperationCanceledException)
        {
            var state = handoffRequested ? CheckState.Unknown : CheckState.Cancelled;
            if (Current()) PackagedSettingsMessage = handoffRequested
                ? "Unknown · cancellation followed a shell request; the page may still open. No package operation completion is claimed. Pending shell ownership blocks another handoff."
                : "Cancelled before shell dispatch. No page or package operation was requested.";
            Status = $"Package family page request: {state}. No package operation completion is claimed.";
            Event(ActivityArea.GuidedWindowsHandoff, Outcome(state));
        }
        catch (Exception)
        {
            var state = handoffRequested ? CheckState.Unknown : CheckState.Failed;
            if (Current()) PackagedSettingsMessage = handoffRequested
                ? "Unknown · the shell request could not be monitored. A dispatched page cannot be undone; no package operation completion is claimed."
                : "Failed · current-user package membership could not be rechecked. No page was requested; read packages again and retry.";
            Status = $"Package family page request: {state}. No package operation completion is claimed.";
            Event(ActivityArea.GuidedWindowsHandoff, Outcome(state));
        }
        finally
        {
            if (ReferenceEquals(packagedSettingsSelectionStop, selectedStop)) packagedSettingsSelectionStop = null;
            if (ReferenceEquals(cancellation, sourceStop)) cancellation = null;
            IsBusy = false; NotifyPackagedSettings();
        }
        async Task<CheckState> Work()
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            PackagedSettingsMessage = "Rereading current-user package metadata before any shell dispatch. Cancel stops the wait; selection/search/source changes invalidate this preview.";
            packagedSettingsRefreshTask = packagedApplicationInventory.ReadAsync(linked.Token);
            var fresh = await packagedSettingsRefreshTask;
            linked.Token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            var received = DateTimeOffset.UtcNow;
            var handoff = PackagedAppSettingsPolicy.PrepareHandoff(preview, fresh, received, received);
            if (handoff == null)
            {
                InvalidatePackagedSettings(clearSource: false);
                PackagedSettingsMessage = "The fresh current-user registration did not match this preview, or coverage/identity is unsupported. No page was requested. Read packages again and review a new selection.";
                return CheckState.Unknown;
            }
            linked.Token.ThrowIfCancellationRequested();
            if (!Current()) return CheckState.Cancelled;
            PackagedSettingsMessage = $"Fresh source received UTC: {received:u}; coverage: {fresh.State}. Requesting only the reviewed family-level Windows page; later Windows operations remain Unknown.";
            handoffRequested = true;
            packagedSettingsHandoffTask = packagedSettingsLauncher.OpenAsync(handoff, linked.Token);
            var opened = await packagedSettingsHandoffTask;
            if (Current()) PackagedSettingsMessage = Stamp(opened.State, opened.Message) +
                $" Fresh source received UTC: {received:u}; coverage: {fresh.State}. No package operation was requested by MadClean. " +
                (HasPendingPackagedSettingsWork ? "Native shell ownership remains pending; another handoff/recheck is blocked." : "");
            return opened.State;
        }
    }
    private void NotifyPackagedSettings()
    {
        PropertyChanged?.Invoke(this, new(nameof(PackagedSettingsPreview)));
        PropertyChanged?.Invoke(this, new(nameof(HasPendingPackagedSettingsWork)));
        CommandManager.InvalidateRequerySuggested();
    }
}
