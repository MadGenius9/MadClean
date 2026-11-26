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

            if (token.IsCancellationRequested) return;
            var syntax = DefenderRequestTransport.ObserveRequestSyntaxFixtureAsync(host).GetAwaiter().GetResult();
            if (!token.IsCancellationRequested && HasMarker(syntax, "MadClean.DefenderSyntax/1:parsed"))
                report.Pass("defender-request-script-syntax", "The actual fixed request script parsed without errors. It was not evaluated.");
            else
                report.Unknown("defender-request-script-syntax", "The Windows PowerShell parser result was not established.");

            if (token.IsCancellationRequested) return;
            var diagnostic = DefenderRequestTransport.ObserveDiagnosticFixtureAsync(host).GetAwaiter().GetResult();
            if (!token.IsCancellationRequested && diagnostic)
                report.Pass("defender-request-transport-stderr", "The inert success marker was rejected when standard error was present.");
            else
                report.Unknown("defender-request-transport-stderr", "Rejection of the inert marker with standard error was not established.");
        }
        catch (Exception)
        {
            if (!report.HasCheck(check))
                report.Unknown(check, "The inert client transport could not be verified.");
            if (!report.HasCheck("defender-request-script-syntax"))
                report.Unknown("defender-request-script-syntax", "The Windows PowerShell parser result was not established.");
            if (!report.HasCheck("defender-request-transport-stderr"))
                report.Unknown("defender-request-transport-stderr", "Rejection of the inert marker with standard error was not established.");
        }
    }

    private static bool HasMarker(ProcessResult? result, string marker) =>
        result is { ExitCode: 0 } && (result.Output == marker || result.Output == marker + "\r\n" || result.Output == marker + "\n");
}
