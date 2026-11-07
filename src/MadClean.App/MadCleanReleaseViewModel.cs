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
    private MadCleanReleaseSourceChoice releaseSourceChoice = MadCleanReleaseSourceChoice.Deployment;
    private MadCleanReleaseConfiguration EffectiveMadCleanReleaseConfiguration =>
        MadCleanReleaseSourcePolicy.Resolve(releaseConfiguration, releaseSourceChoice);
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
    public ICommand MadCleanUseOfficialSourceCommand { get; private set; } = null!;
    public ICommand MadCleanDisableSourceCommand { get; private set; } = null!;
    public ICommand MadCleanUseDeploymentSourceCommand { get; private set; } = null!;
    public string MadCleanSourceSummary => releaseSourceChoice switch
    {
        MadCleanReleaseSourceChoice.OfficialRepository => "Session-only source: " + MadCleanReleaseSource.TrustedRepository,
        MadCleanReleaseSourceChoice.Disabled => "Release checking disabled for this session.",
        _ => EffectiveMadCleanReleaseConfiguration.Error != null
            ? "Deployment source is invalid or unavailable; no release check is enabled."
            : EffectiveMadCleanReleaseConfiguration.IsConfigured
                ? "Using the deployment's trusted source: " + MadCleanReleaseSource.TrustedRepository
                : "No release source configured."
    };
    public string MadCleanSourceDetails
    {
        get
        {
            string deployment = releaseConfiguration.Error != null
                ? "Deployment setting loaded at launch: invalid or unreadable. " + releaseConfiguration.Error
                : releaseConfiguration.Repository == null
                    ? "Deployment setting loaded at launch: no source configured."
                    : "Deployment setting loaded at launch: " +
                        (MadCleanReleaseSourcePolicy.Resolve(releaseConfiguration, MadCleanReleaseSourceChoice.Deployment).IsConfigured
                            ? MadCleanReleaseSource.TrustedRepository : "unsupported source; no check is enabled by that setting.");
            return deployment + "\nThe only selectable repository is " + MadCleanReleaseSource.TrustedRepository +
                ". Session choices do not edit madclean-updates.json or persist after exit. Use deployment setting restores the configuration loaded at launch; restart to reload a changed deployment file.\n" +
                "Changing the source choice clears this session's release result, notes and reminder. It starts no check, opens no browser and sends no request. Choose Check MadClean updates separately. Anonymous access may fail for private repositories, missing releases, offline connections or rate limits; no credentials are requested or stored.";
        }
    }
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
        MadCleanCheckCommand = new AsyncCommand(CheckMadCleanUpdatesAsync, () => !IsBusy && !releaseChecking);
        MadCleanReviewCommand = new ActionCommand(OpenMadCleanRelease, () => !IsBusy && !releaseChecking &&
            EffectiveMadCleanReleaseConfiguration.IsConfigured && releaseReviewUri != null);
        MadCleanReminderCommand = new ActionCommand(() => SelectedPage = 5, () => true);
        MadCleanUseOfficialSourceCommand = new ActionCommand(() => SelectMadCleanSource(MadCleanReleaseSourceChoice.OfficialRepository),
            () => !IsBusy && !releaseChecking);
        MadCleanDisableSourceCommand = new ActionCommand(() => SelectMadCleanSource(MadCleanReleaseSourceChoice.Disabled),
            () => !IsBusy && !releaseChecking);
        MadCleanUseDeploymentSourceCommand = new ActionCommand(() => SelectMadCleanSource(MadCleanReleaseSourceChoice.Deployment),
            () => !IsBusy && !releaseChecking);
        PublishMadCleanResult(InitialMadCleanResult());
    }

    private void SelectMadCleanSource(MadCleanReleaseSourceChoice choice)
    {
        // ActionCommand.Execute can be called directly; repeat the busy check at the handler.
        if (IsBusy || releaseChecking || releaseSourceChoice == choice) return;
        releaseSourceChoice = choice;
        releaseReminder.Clear();
        releaseReviewUri = null;
        releaseAttemptedAt = null;
        releaseActionMessage = "Source choice updated for this session only. No release check has run or configuration file changed.";
        PublishMadCleanResult(InitialMadCleanResult());
        Status = "MadClean release source choice changed. No check has run for this choice; see MadClean updates.";
    }

    private MadCleanReleaseResult InitialMadCleanResult()
    {
        var configuration = EffectiveMadCleanReleaseConfiguration;
        return configuration.Error != null
            ? new(MadCleanReleaseState.Unknown, configuration.Error)
            : configuration.IsConfigured
                ? new(MadCleanReleaseState.NotChecked, "The trusted MadClean release source has not been checked for this source choice. Choose Check MadClean updates to contact GitHub anonymously.")
                : new(MadCleanReleaseState.NotConfigured, releaseSourceChoice == MadCleanReleaseSourceChoice.Disabled
                    ? "Update checking not configured. Release checking is disabled for this session; no network check has run."
                    : "Update checking not configured. No deployment source is enabled. You can choose the trusted GitHub source for this session without changing a file; no network check has run.");
    }

    private async Task CheckMadCleanUpdatesAsync()
    {
        if (IsBusy || releaseChecking) return;
        var configuration = EffectiveMadCleanReleaseConfiguration;
        if (configuration.Error != null || !configuration.IsConfigured)
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
        try
        {
            MadCleanReleaseResult result;
            try { result = await releaseChecker.CheckAsync(configuration.Repository, installedReleaseVersion, source.Token); }
            catch (OperationCanceledException) { result = new(MadCleanReleaseState.Cancelled, "MadClean release checking was cancelled; no current-version claim is made."); }
            catch (Exception) { result = new(MadCleanReleaseState.Failed, "MadClean release information could not be checked. Retry or review the source in your browser; no current-version claim is made."); }
            releaseAttemptedAt = DateTimeOffset.Now;
            releaseChecking = false;
            // Keep the source-choice busy fence through publication. A callback when IsBusy
            // becomes false must not reset the source and then receive this old result.
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
        finally { releaseChecking = false; cancellation = null; IsBusy = false; }
    }

    private void PublishMadCleanResult(MadCleanReleaseResult result)
    {
        releaseResult = result;
        releaseReminder.Apply(result, DateTimeOffset.Now);
        releaseReviewUri = releaseReminder.Release?.ReviewUri ?? result.ReviewUri ??
            (EffectiveMadCleanReleaseConfiguration.IsConfigured ? new Uri("https://github.com/MadGenius9/MadClean/releases") : null);
        foreach (var property in new[] { nameof(MadCleanUpdateStatus), nameof(MadCleanUpdateMessage), nameof(MadCleanReleaseNotes),
            nameof(HasMadCleanReleaseNotes), nameof(MadCleanActionMessage), nameof(HasMadCleanReminder), nameof(MadCleanReminderText),
            nameof(MadCleanSourceSummary), nameof(MadCleanSourceDetails) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenMadCleanRelease()
    {
        if (IsBusy || releaseChecking || !EffectiveMadCleanReleaseConfiguration.IsConfigured ||
            releaseReviewUri == null || !MadCleanReleaseSource.IsTrustedReviewUri(releaseReviewUri)) return;
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
