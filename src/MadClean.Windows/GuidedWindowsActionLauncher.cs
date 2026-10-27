using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Opens only a user-selected, already-previewed fixed Windows page or official guide. It never
/// executes the underlying requested operation and cannot cancel Windows after shell dispatch.
/// </summary>
public sealed class GuidedWindowsActionLauncher
{
    private static readonly TimeSpan CallerWaitBudget = TimeSpan.FromSeconds(15);
    private static int handoffPending;
    private const string UnknownOperation = "The requested operation was not executed or monitored by MadClean; completion is Unknown.";

    public async Task<CheckResult<string>> OpenAsync(GuidedWindowsTask task, CancellationToken token)
    {
        if (token.IsCancellationRequested) return BeforeDispatchCancelled();
        var action = GuidedWindowsActions.Find(task);
        if (action == null || !GuidedWindowsActions.IsAllowedUri(task, action.TargetUri))
            return CheckResult<string>.Empty(CheckState.Unsupported, "No allowlisted Windows navigation descriptor exists for this task. Nothing was dispatched.");
        if (!OperatingSystem.IsWindows())
            return CheckResult<string>.Empty(CheckState.Unsupported, "Guided Windows handoffs are unsupported on this platform. No page was opened and no Windows operation was simulated.");
        if (Interlocked.CompareExchange(ref handoffPending, 1, 0) != 0)
            return CheckResult<string>.Empty(CheckState.Unavailable, "A previous Windows shell handoff has not returned yet. No additional handoff was dispatched; its underlying operation remains unmonitored/Unknown.");

        var state = new HandoffState();
        var completion = new TaskCompletionSource<CheckResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CheckResult<string>> pending = completion.Task;
        try
        {
            var worker = new Thread(() =>
            {
                bool initialized = false;
                try
                {
                    int status = CoInitializeEx(IntPtr.Zero, 2); // Explicit STA for Windows shell execution.
                    if (status < 0)
                    {
                        completion.TrySetResult(CheckResult<string>.Empty(CheckState.Failed,
                            $"The Windows shell apartment could not initialize (code 0x{status:X8}). Nothing was dispatched."));
                        return;
                    }
                    initialized = true;
                    completion.TrySetResult(Dispatch(action, state, token));
                }
                catch (Exception)
                {
                    completion.TrySetResult(CheckResult<string>.Empty(CheckState.Failed,
                        "The Windows handoff worker could not complete. No underlying operation was executed or monitored by MadClean; completion is Unknown."));
                }
                finally
                {
                    try { if (initialized) CoUninitialize(); }
                    finally { Interlocked.Exchange(ref handoffPending, 0); }
                }
            }) { IsBackground = true, Name = "MadClean explicit Windows handoff" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref handoffPending, 0);
            return CheckResult<string>.Empty(CheckState.Failed, "The Windows handoff worker could not start. Nothing was dispatched.");
        }
        try { return await pending.WaitAsync(CallerWaitBudget, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            bool dispatched = state.StopIfNotDispatched();
            return dispatched
                ? new(CheckState.Unknown,
                    "Cancellation was requested after Windows shell dispatch began. MadClean cannot cancel or roll back that handoff; Windows may still open or continue the page. " + UnknownOperation,
                    [action.TargetUri.OriginalString])
                : BeforeDispatchCancelled();
        }
        catch (TimeoutException)
        {
            bool dispatched = state.StopIfNotDispatched();
            return dispatched
                ? new(CheckState.Unknown,
                    "The Windows shell handoff did not return within the 15-second caller wait. It may still open the page; no second handoff is allowed while dispatch remains pending. " + UnknownOperation,
                    [action.TargetUri.OriginalString])
                : CheckResult<string>.Empty(CheckState.Unavailable,
                    "The queued handoff reached its wait budget before shell dispatch. It was stopped before dispatch; no page or operation was requested.");
        }
    }

    private static CheckResult<string> Dispatch(GuidedWindowsAction action, HandoffState state, CancellationToken token)
    {
        try
        {
            lock (state.Gate)
            {
                if (token.IsCancellationRequested || state.StopBeforeDispatch) return BeforeDispatchCancelled();
                // Revalidate the immutable catalog descriptor at the last pre-dispatch boundary.
                if (!GuidedWindowsActions.IsAllowedUri(action.Task, action.TargetUri))
                    return CheckResult<string>.Empty(CheckState.Unsupported, "The navigation descriptor was not allowlisted at dispatch. Nothing was opened.");
                state.Dispatched = true;
            }
            using var opened = Process.Start(new ProcessStartInfo
            {
                FileName = action.TargetUri.OriginalString,
                UseShellExecute = true,
                Verb = "open",
                Arguments = "",
                ErrorDialog = false
            });
            // A null Process is normal for a broker/existing window. Neither a PID nor a successful
            // ShellExecute return verifies window appearance or completion of any Windows task.
            string message = "Handoff opened (the shell request returned; target-window appearance is unverified). " + UnknownOperation;
            if (token.IsCancellationRequested)
                message += " Cancellation arrived after dispatch and cannot undo the Windows handoff.";
            return new(CheckState.Available, message, [action.TargetUri.OriginalString]);
        }
        catch (Win32Exception ex)
        {
            return CheckResult<string>.Empty(CheckState.Failed,
                $"Windows reported a shell-handoff failure (native code {ex.NativeErrorCode}). Check the page/browser handler and organizational policy; no elevation was requested. " + UnknownOperation);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Security.SecurityException or IOException)
        {
            return CheckResult<string>.Empty(CheckState.Failed,
                $"The fixed Windows page could not be handed off (code 0x{ex.HResult:X8}). No arbitrary fallback, elevation or repair command was attempted. " + UnknownOperation);
        }
    }

    private static CheckResult<string> BeforeDispatchCancelled() => CheckResult<string>.Empty(CheckState.Cancelled,
        "Windows handoff cancelled before shell dispatch. No page or requested operation was executed.");

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CoUninitialize();

    private sealed class HandoffState
    {
        internal readonly object Gate = new();
        internal bool Dispatched;
        internal bool StopBeforeDispatch;
        internal bool StopIfNotDispatched()
        {
            lock (Gate)
            {
                if (!Dispatched) StopBeforeDispatch = true;
                return Dispatched;
            }
        }
    }
}
