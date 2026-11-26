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

                targetDirectories = SystemToolTrustNative.LockDirectoryChain(draft.Folder);
                startAttempted = true;
                var result = await DefenderRequestTransport.SubmitAsync(host, draft).ConfigureAwait(false);
                settled = true;
                return result == null ? DefenderScanSubmission.Unknown : DefenderScanHostProtocol.Interpret(result);
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
                Release(targetDirectories);
                manifest?.Dispose();
                Release(moduleDirectories);
                host?.Dispose();
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void Release(List<SafeFileHandle>? handles)
        {
            if (handles == null) return;
            for (int index = handles.Count - 1; index >= 0; index--)
                handles[index].Dispose();
        }
    }
}
