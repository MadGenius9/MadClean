using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void ObserveDefenderRequestTransport(SmokeReport report, CancellationToken token)
    {
        const string check = "defender-inert-request-transport";
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_JOB") != "windows-verification")
        {
            report.Unknown(check, "The inert process fixture requires the hosted Windows validation job.");
            return;
        }

        try
        {
            using var host = SystemToolTrustedLease.OpenWindowsPowerShell(token);
            token.ThrowIfCancellationRequested();
            var result = DefenderRequestTransport.ObserveInertFixtureAsync(host).GetAwaiter().GetResult();
            if (token.IsCancellationRequested || result == null ||
                DefenderScanHostProtocol.Interpret(result) != DefenderScanSubmission.RequestReturnedWithoutCommandError)
                report.Unknown(check, "The fixed inert input, output and settled client were not verified.");
            else
                report.Pass(check, "The inert child received the fixed input and closed its streams and process. No Defender module was loaded by the child.");
        }
        catch (Exception)
        {
            report.Unknown(check, "The inert client transport could not be verified.");
        }
    }
}
