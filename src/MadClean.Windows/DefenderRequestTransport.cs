using System.Diagnostics;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

// Used only with fixed application or fixture source. The caller holds its leases until return.
internal static class DefenderRequestTransport
{
    internal static async Task<ProcessResult?> SubmitAsync(SystemToolTrustedLease host, DefenderCustomScanDraft draft) =>
        (await RunAsync(host, DefenderScanRequestHost.EncodedSource, DefenderScanHostProtocol.EncodeFolderInput(draft))
            .ConfigureAwait(false))?.AcceptedResult;

    internal static async Task<ProcessResult?> ObserveInertFixtureAsync(SystemToolTrustedLease host)
    {
        const string script = """
            $value = [Console]::In.ReadToEnd()
            if ($value -cne 'MadCleanInertFixture') { exit 1 }
            [Console]::WriteLine('MadClean.DefenderRequest/1:returned')
            """;
        return (await RunAsync(host, Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), "MadCleanInertFixture")
            .ConfigureAwait(false))?.AcceptedResult;
    }

    internal static async Task<ProcessResult?> ObserveRequestSyntaxFixtureAsync(SystemToolTrustedLease host)
    {
        const string script = """
            try {
              $encoded = [Console]::In.ReadToEnd()
              $source = [Text.UnicodeEncoding]::new($false, $false, $true).GetString([Convert]::FromBase64String($encoded))
              $tokens = $null
              $errors = $null
              [void][Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
              if ($errors.Count -ne 0) { exit 1 }
              [Console]::WriteLine('MadClean.DefenderSyntax/1:parsed')
            } catch { exit 1 }
            """;
        // The child treats candidate source as parser input, never as an executable script block.
        return (await RunAsync(host, Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), DefenderScanRequestHost.EncodedSource)
            .ConfigureAwait(false))?.AcceptedResult;
    }

    internal static async Task<bool> ObserveDiagnosticFixtureAsync(SystemToolTrustedLease host)
    {
        const string script = """
            $value = [Console]::In.ReadToEnd()
            if ($value -cne 'MadCleanInertFixture') { exit 1 }
            [Console]::Error.WriteLine('Inert diagnostic')
            [Console]::WriteLine('MadClean.DefenderRequest/1:returned')
            """;
        var observation = await RunAsync(host, Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), "MadCleanInertFixture")
            .ConfigureAwait(false);
        return observation is { HasStandardError: true, AcceptedResult: null } &&
            DefenderScanHostProtocol.Interpret(observation.Result) ==
            DefenderScanSubmission.RequestReturnedWithoutCommandError;
    }

    private sealed record SettledClient(ProcessResult Result, bool HasStandardError)
    {
        internal ProcessResult? AcceptedResult => HasStandardError ? null : Result;
    }

    private static async Task<SettledClient?> RunAsync(SystemToolTrustedLease host, string encodedSource, string input)
    {
        var process = new Process { StartInfo = CreateStartInfo(host, encodedSource) };
        bool settled = false;
        Task<string?>? stdout = null, stderr = null;
        Task<bool>? sent = null;
        try
        {
            if (!process.Start())
            {
                settled = true;
                return null;
            }

            stdout = DrainAsync(process, standardOutput: true);
            stderr = DrainAsync(process, standardOutput: false);
            sent = SendAsync(process, input);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr, sent).ConfigureAwait(false);
            settled = true;
            if (!sent.Result || stdout.Result is not { } marker || stderr.Result == null)
                return null;
            return new(new(process.ExitCode, marker), stderr.Result.Length != 0);
        }
        catch (Exception) { return null; }
        finally
        {
            if (settled)
                process.Dispose();
            else
            {
                // Keep managed owners reachable across the pending await. No kill or caller
                // cancellation can substitute for knowing the redirected client has settled.
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
                GC.KeepAlive(process);
                GC.KeepAlive(stdout);
                GC.KeepAlive(stderr);
                GC.KeepAlive(sent);
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(SystemToolTrustedLease lease, string encodedSource)
    {
        var info = new ProcessStartInfo(lease.ToolPath)
        {
            WorkingDirectory = lease.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedSource })
            info.ArgumentList.Add(argument);
        info.Environment.Clear();
        string windows = Path.GetDirectoryName(lease.SystemDirectory)!;
        info.Environment["SystemRoot"] = windows;
        info.Environment["windir"] = windows;
        info.Environment["PATH"] = lease.SystemDirectory;
        return info;
    }

    private static async Task<bool> SendAsync(Process child, string input)
    {
        try
        {
            using var stream = child.StandardInput;
            await stream.WriteAsync(input).ConfigureAwait(false);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static async Task<string?> DrainAsync(Process child, bool standardOutput)
    {
        char[] buffer = new char[256];
        try
        {
            var stream = standardOutput ? child.StandardOutput : child.StandardError;
            var text = new StringBuilder();
            bool exceeded = false, diagnosticSeen = false;
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                if (!standardOutput) { diagnosticSeen = true; continue; }
                if (exceeded) continue;
                if (text.Length + count <= 64) text.Append(buffer, 0, count);
                else { exceeded = true; text.Clear(); }
            }
            return exceeded ? null : diagnosticSeen ? "present" : text.ToString();
        }
        catch (Exception) { return null; }
        finally { Array.Clear(buffer); }
    }
}
