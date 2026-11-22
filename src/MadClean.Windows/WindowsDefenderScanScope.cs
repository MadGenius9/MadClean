using System.ComponentModel;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Point-in-time metadata observation of one selected Defender scan folder.</summary>
public static class WindowsDefenderScanScope
{
    public static Task<CheckResult<string>> ObserveAsync(DefenderCustomScanDraft draft, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Task.Run(() => Observe(draft, token), token);
    }

    private static CheckResult<string> Observe(DefenderCustomScanDraft draft, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return CheckResult<string>.Empty(CheckState.Unsupported, "Folder scope checks require Windows.");

        try
        {
            // The existing bounded no-follow handle walk checks the fixed drive,
            // directory attributes, and final path at each step, without enumeration.
            var handles = SystemToolTrustNative.LockDirectoryChain(draft.Folder);
            try
            {
                token.ThrowIfCancellationRequested();
                return CheckResult<string>.Empty(CheckState.Partial,
                    "The folder and its parents opened on a fixed local drive with matching paths and supported attributes. This check covers the selected folder path only. A future scan request must check it again.");
            }
            finally
            {
                for (int index = handles.Count - 1; index >= 0; index--)
                    handles[index].Dispose();
            }
        }
        catch (SystemToolTrustFailure failure)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<string>.Empty(failure.State,
                "A fixed local drive and ordinary folder path could not be verified. Choose an accessible folder without redirected or offline components.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            return CheckResult<string>.Empty(CheckState.Unavailable,
                "Windows could not open the folder or a parent for this check. Check permissions, drive availability, and whether the folder still exists.");
        }
    }
}
