using System.Diagnostics;
using System.Text;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

/// <summary>Fixed Windows request client candidate. The production gate precedes all access.</summary>
internal sealed class WindowsDefenderCustomScanClient : IDefenderCustomScanClient
{
    private static bool ExecutionValidated => false;
    private static Session? active;

    public Task<DefenderScanSubmission> SubmitAsync(DefenderCustomScanDraft draft)
    {
        if (!ExecutionValidated || !OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return Task.FromResult(DefenderScanSubmission.Unavailable);

        ArgumentNullException.ThrowIfNull(draft);
        var session = new Session(draft);
        if (Interlocked.CompareExchange(ref active, session, null) != null)
            return Task.FromResult(DefenderScanSubmission.NotSubmitted);
        return Task.Run(session.RunAsync);
    }

    private sealed class Session(DefenderCustomScanDraft draft)
    {
        private SystemToolTrustedLease? host;
        private List<SafeFileHandle>? targetDirectories, moduleDirectories;
        private SafeFileHandle? manifest;
        private Process? process;
        private Task<string?>? stdout, stderr;
        private Task<bool>? sent;

        internal async Task<DefenderScanSubmission> RunAsync()
        {
            bool startAttempted = false, settled = false;
            try
            {
                host = SystemToolTrustedLease.OpenWindowsPowerShell(CancellationToken.None);
                var moduleFolder = Path.Combine(Path.GetDirectoryName(host.ToolPath)!, "Modules", "Defender");
                var manifestPath = Path.Combine(moduleFolder, "Defender.psd1");
                moduleDirectories = SystemToolTrustNative.LockDirectoryChain(moduleFolder);
                manifest = SystemToolTrustNative.OpenFile(manifestPath);
                SystemToolTrustNative.ValidateAttributes(manifest, directory: false);
                SystemToolTrustNative.ValidateFinalPath(manifest, manifestPath);

                var info = CreateStartInfo(host);
                string input = DefenderScanHostProtocol.EncodeFolderInput(draft);
                targetDirectories = SystemToolTrustNative.LockDirectoryChain(draft.Folder);

                process = new Process { StartInfo = info };
                startAttempted = true;
                if (!process.Start())
                {
                    settled = true;
                    return DefenderScanSubmission.Rejected;
                }

                // Start both drains before sending input. Keep observing the actual client
                // without a kill deadline or a caller cancellation token.
                stdout = DrainAsync(process, standardOutput: true);
                stderr = DrainAsync(process, standardOutput: false);
                sent = SendAsync(process, input);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr, sent).ConfigureAwait(false);
                settled = true;
                return sent.Result && stdout.Result != null && stderr.Result != null
                    ? DefenderScanHostProtocol.Interpret(new(process.ExitCode, stdout.Result))
                    : DefenderScanSubmission.Unknown;
            }
            catch (Exception)
            {
                return startAttempted ? DefenderScanSubmission.Unknown : DefenderScanSubmission.Rejected;
            }
            finally
            {
                // A thrown Start or failed exit observation leaves ownership ambiguous.
                // A static root alone cannot keep an App owner awaiting this operation.
                if ((!startAttempted || settled) && TryRelease())
                    Interlocked.CompareExchange(ref active, null, this);
                else
                    await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private bool TryRelease()
        {
            if (SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty)
                return false;
            try
            {
                process?.Dispose();
                Release(targetDirectories);
                manifest?.Dispose();
                Release(moduleDirectories);
                host?.Dispose();
                return true;
            }
            catch (Exception) { return false; }
        }

        private static ProcessStartInfo CreateStartInfo(SystemToolTrustedLease lease)
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
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", DefenderScanRequestHost.EncodedSource })
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

        private static void Release(List<SafeFileHandle>? handles)
        {
            if (handles == null) return;
            for (int index = handles.Count - 1; index >= 0; index--)
                handles[index].Dispose();
        }
    }
}
