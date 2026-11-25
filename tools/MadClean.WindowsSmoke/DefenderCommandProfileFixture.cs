using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void DefenderCommandProfile(SmokeReport report, CancellationToken token)
    {
        const string check = "defender-custom-command-profile";
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_JOB") != "windows-verification")
        {
            report.Unknown(check, "profile=NotRun");
            return;
        }

        try
        {
            // Await the actual worker. The harness watchdog can record a deadline,
            // but must not abandon the host or its held module/manifest handles.
            var result = WindowsDefenderCommandProbe.ObserveAsync(token).GetAwaiter().GetResult();
            if (token.IsCancellationRequested)
                report.Unknown(check, "profile=Cancelled");
            else if (result == DefenderScanCommandObservation.ProfileObserved)
                report.Pass(check, "profile=ProfileObserved");
            else
                report.Unknown(check, "profile=" + (result switch
                {
                    DefenderScanCommandObservation.Unavailable => "Unavailable",
                    DefenderScanCommandObservation.ModuleNotAccepted => "ModuleNotAccepted",
                    DefenderScanCommandObservation.CommandNotAccepted => "CommandNotAccepted",
                    DefenderScanCommandObservation.CommandLookupNotAccepted => "CommandLookupNotAccepted",
                    DefenderScanCommandObservation.CommandShapeNotAccepted => "CommandShapeNotAccepted",
                    DefenderScanCommandObservation.CommandBindingNotAccepted => "CommandBindingNotAccepted",
                    DefenderScanCommandObservation.CustomParametersNotAccepted => "CustomParametersNotAccepted",
                    _ => "Unknown"
                }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            report.Unknown(check, "profile=Cancelled");
        }
    }
}
