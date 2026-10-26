using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    // Dedicated anonymous client: no cookies, ambient server credentials, redirects or authentication prompts.
    private static readonly HttpClient ReleaseHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null
    })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly MadCleanReleaseConfiguration releaseConfiguration = MadCleanReleaseConfiguration.Load(AppContext.BaseDirectory);
    private readonly MadCleanReleaseChecker releaseChecker = new(ReleaseHttp);
    private readonly MadCleanReleaseReminder releaseReminder = new();
    private readonly Version? installedReleaseVersion = typeof(MainViewModel).Assembly.GetName().Version;
    private MadCleanReleaseResult releaseResult = new(MadCleanReleaseState.NotConfigured, "Update checking not configured");
    private Uri? releaseReviewUri;
    private DateTimeOffset? releaseAttemptedAt;
    private string releaseActionMessage = "";
    private bool releaseChecking;

    public ICommand MadCleanCheckCommand { get; private set; } = null!;
    public ICommand MadCleanReviewCommand { get; private set; } = null!;
    public ICommand MadCleanReminderCommand { get; private set; } = null!;
    public string MadCleanInstalledVersion => installedReleaseVersion == null ? "Unknown" :
        $"{installedReleaseVersion.Major}.{installedReleaseVersion.Minor}.{Math.Max(0, installedReleaseVersion.Build)}";
    public string MadCleanUpdateStatus => releaseChecking ? "Checking…" : releaseResult.State switch
    {
        MadCleanReleaseState.NotConfigured => "Update checking not configured",
        MadCleanReleaseState.NotChecked => "Not checked",
        MadCleanReleaseState.NewerRelease => $"{releaseResult.Version} available",
        MadCleanReleaseState.Current => "No newer stable release found",
        MadCleanReleaseState.Unknown => "Unknown",
        MadCleanReleaseState.Unavailable => "Release information unavailable",
        MadCleanReleaseState.Cancelled => "Cancelled",
        _ => "Check failed"
    };
    public string MadCleanUpdateMessage => releaseChecking ? "Contacting the configured GitHub release source anonymously. You can cancel this check." : releaseResult.Message + (releaseAttemptedAt.HasValue ? $" Latest attempt: {releaseAttemptedAt.Value.LocalDateTime:g}." : "");
    public string MadCleanReleaseNotes => releaseReminder.Release?.Notes ?? releaseResult.Notes ?? "";
    public bool HasMadCleanReleaseNotes => !string.IsNullOrWhiteSpace(MadCleanReleaseNotes);
    public string MadCleanActionMessage => releaseActionMessage;
    public bool HasMadCleanReminder => releaseReminder.Release != null;
    public string MadCleanReminderText => releaseReminder.Release == null ? "" :
        $"MadClean {releaseReminder.Release.Version} is available (confirmed {releaseReminder.ConfirmedAt?.LocalDateTime:g})." +
        (releaseResult.State == MadCleanReleaseState.NewerRelease ? " Review the release when you are ready." : $" Latest check: {MadCleanUpdateStatus}.");

    private void InitializeMadCleanUpdates()
    {
        MadCleanCheckCommand = new AsyncCommand(CheckMadCleanUpdatesAsync, () => !IsBusy);
        MadCleanReviewCommand = new ActionCommand(OpenMadCleanRelease, () => releaseReviewUri != null);
        MadCleanReminderCommand = new ActionCommand(() => SelectedPage = 5, () => true);
        PublishMadCleanResult(InitialMadCleanResult());
    }

    private MadCleanReleaseResult InitialMadCleanResult() => releaseConfiguration.Error != null
        ? new(MadCleanReleaseState.Unknown, releaseConfiguration.Error)
        : releaseConfiguration.IsConfigured
            ? new(MadCleanReleaseState.NotChecked, "The configured MadClean release source has not been checked. Choose Check MadClean updates to contact GitHub anonymously.")
            : new(MadCleanReleaseState.NotConfigured, "Update checking not configured. No release source is enabled. See README for the optional trusted-source configuration and restart after changing it; no network check has run.");

    private async Task CheckMadCleanUpdatesAsync()
    {
        if (IsBusy) return;
        if (releaseConfiguration.Error != null || !releaseConfiguration.IsConfigured)
        {
            PublishMadCleanResult(InitialMadCleanResult());
            Status = "No MadClean release check was made. Review the release-source configuration in MadClean updates.";
            return;
        }
        if (installedReleaseVersion == null)
        {
            PublishMadCleanResult(new(MadCleanReleaseState.Unknown, "The installed app version could not be determined; no version comparison was made."));
            return;
        }
        using var source = new CancellationTokenSource(); cancellation = source; IsBusy = true;
        Status = "Checking the configured MadClean release source…";
        releaseActionMessage = "";
        releaseChecking = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MadCleanUpdateStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MadCleanUpdateMessage)));
        Event(ActivityArea.MadCleanUpdates, ActivityOutcome.Started);
        MadCleanReleaseResult result;
        try { result = await releaseChecker.CheckAsync(releaseConfiguration.Repository, installedReleaseVersion, source.Token); }
        catch (OperationCanceledException) { result = new(MadCleanReleaseState.Cancelled, "MadClean release checking was cancelled; no current-version claim is made."); }
        catch (Exception) { result = new(MadCleanReleaseState.Failed, "MadClean release information could not be checked. Retry or review the source in your browser; no current-version claim is made."); }
        finally { releaseChecking = false; cancellation = null; IsBusy = false; }
        releaseAttemptedAt = DateTimeOffset.Now;
        PublishMadCleanResult(result);
        Status = "MadClean release check: " + MadCleanUpdateStatus + ". See MadClean updates for details.";
        Event(ActivityArea.MadCleanUpdates, result.State switch
        {
            MadCleanReleaseState.NewerRelease or MadCleanReleaseState.Current => ActivityOutcome.Completed,
            MadCleanReleaseState.Cancelled => ActivityOutcome.Cancelled,
            MadCleanReleaseState.Unavailable or MadCleanReleaseState.Unknown => ActivityOutcome.Partial,
            _ => ActivityOutcome.Failed
        });
    }

    private void PublishMadCleanResult(MadCleanReleaseResult result)
    {
        releaseResult = result;
        releaseReminder.Apply(result, DateTimeOffset.Now);
        releaseReviewUri = releaseReminder.Release?.ReviewUri ?? result.ReviewUri ??
            (releaseConfiguration.IsConfigured ? new Uri("https://github.com/MadGenius9/MadClean/releases") : null);
        foreach (var property in new[] { nameof(MadCleanUpdateStatus), nameof(MadCleanUpdateMessage), nameof(MadCleanReleaseNotes),
            nameof(HasMadCleanReleaseNotes), nameof(MadCleanActionMessage), nameof(HasMadCleanReminder), nameof(MadCleanReminderText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenMadCleanRelease()
    {
        if (releaseReviewUri == null || !MadCleanReleaseSource.IsTrustedReviewUri(releaseReviewUri)) return;
        try
        {
            // Explicit user action opens a validated HTTPS release page. No asset is fetched or executed here.
            Process.Start(new ProcessStartInfo(releaseReviewUri.AbsoluteUri) { UseShellExecute = true });
            releaseActionMessage = "Opened the configured GitHub release page in your browser. Review notes and choose any download there; MadClean does not install it.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        { releaseActionMessage = "Windows could not open the browser. Review the configured MadGenius9/MadClean releases page manually."; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MadCleanActionMessage)));
    }
}
