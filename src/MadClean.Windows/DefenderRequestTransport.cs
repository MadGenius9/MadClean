using System.Diagnostics;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

// Used only with fixed application or fixture source. The caller holds its leases until return.
internal static class DefenderRequestTransport
{
    internal static Task<ProcessResult?> SubmitAsync(SystemToolTrustedLease host, DefenderCustomScanDraft draft) =>
        RunAsync(host, DefenderScanRequestHost.EncodedSource, DefenderScanHostProtocol.EncodeFolderInput(draft));

    internal static Task<ProcessResult?> ObserveInertFixtureAsync(SystemToolTrustedLease host)
    {
        const string script = """
            $value = [Console]::In.ReadToEnd()
            if ($value -cne 'MadCleanInertFixture') { exit 1 }
            [Console]::WriteLine('MadClean.DefenderRequest/1:returned')
            """;
        return RunAsync(host, Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), "MadCleanInertFixture");
    }

    private static async Task<ProcessResult?> RunAsync(SystemToolTrustedLease host, string encodedSource, string input)
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
            return sent.Result && stdout.Result is { } marker && stderr.Result != null
                ? new(process.ExitCode, marker) : null;
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
            bool exceeded = false;
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                if (!standardOutput) { exceeded = true; continue; }
                if (exceeded) continue;
                if (text.Length + count <= 64) text.Append(buffer, 0, count);
                else { exceeded = true; text.Clear(); }
            }
            return exceeded ? null : text.ToString();
        }
        catch (Exception) { return null; }
        finally { Array.Clear(buffer); }
    }

}
