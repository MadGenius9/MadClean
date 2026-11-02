using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Fresh ARP plus exact-product current-user/machine MSI observations; never dispatches an installer.</summary>
public sealed class WindowsMsiActionBindingInventory : IMsiActionBindingInventory
{
    private readonly InstalledApplicationInventory registrations = new();
    // ARP's bounded caller can return before its synchronous worker; its own static gate
    // still owns that worker independently of the MSI gate. Never call that state settled.
    public bool HasPendingNativeRead => WindowsMsiRegistrationInventory.PendingRead || registrations.HasPendingNativeRead;
    public async Task<CheckResult<MsiActionBindingReport>> VerifyAsync(MsiActionBindingScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Empty(CheckState.Unsupported, "This selected binding check requires Windows; no query ran.");
        if (!Fresh(scope)) return Empty(CheckState.Unknown, "The selected binding scope is invalid or expired; refresh and select it again.");
        if (!WindowsMsiRegistrationInventory.TryBeginRead())
            return Empty(CheckState.Unavailable, "An existing native MSI lookup is still pending. No second lookup started; wait before retrying.");
        var worker = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!Fresh(scope)) return Empty(CheckState.Unknown, "The selection expired before the worker started; no binding was inferred.");
                var fresh = await registrations.ScanAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var current = MsiRegistrationReview.CreateScope(fresh, scope.Application.Identity, DateTimeOffset.UtcNow, token).Items.SingleOrDefault();
                if (current == null || current.ProductCode != scope.ProductCode)
                    return MsiActionBindingPolicy.Assess(scope, fresh, [], false, [], DateTimeOffset.UtcNow, token);
                // The outer shared lease already owns the MSI worker. Do not call VerifyAsync
                // again or acquire a second lease; this exact-product reader makes no actions.
                var raw = new WindowsMsiRegistrationInventoryNative(current, token).ReadObservations(7);
                token.ThrowIfCancellationRequested();
                return MsiActionBindingPolicy.Assess(scope, fresh, raw.Instances, raw.EnumerationComplete,
                    raw.Issues, raw.CheckedUtc, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return Empty(CheckState.Cancelled, "Cancellation was observed; no completed binding is claimed. No installer action occurred."); }
            catch (WindowsMsiRegistrationBudgetException)
            { return Empty(CheckState.Unknown, "The MSI cooperative query budget elapsed; context coverage is incomplete. Retry after the provider is responsive."); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { return Empty(CheckState.Unsupported, "Required Windows Installer metadata APIs are unavailable; no binding was established."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
            { return Empty(CheckState.Failed, "Registration/context metadata could not be read; no binding or action permission was established."); }
            finally { WindowsMsiRegistrationInventory.EndRead(); }
        }, CancellationToken.None);
        _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var result = await worker.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return result;
        }
        catch (TimeoutException)
        { return Empty(CheckState.Unknown, "The caller wait expired. A synchronous query may still return; its worker retains resources and blocks another MSI lookup. No force-abort or installer action occurred."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Empty(CheckState.Cancelled, "The caller stopped waiting. This does not prove synchronous queries stopped; the worker owns its resources until it returns. No installer action occurred."); }
    }
    private static bool Fresh(MsiActionBindingScope? scope)
    {
        var now = DateTimeOffset.UtcNow;
        return scope != null && scope.ValidatedUtc != default && scope.ValidatedUtc.Offset == TimeSpan.Zero &&
            scope.ValidatedUtc <= now && now - scope.ValidatedUtc <= MsiRegistrationReview.ScopeLifetime;
    }
    private static CheckResult<MsiActionBindingReport> Empty(CheckState state, string message) => CheckResult<MsiActionBindingReport>.Empty(state, message);
}
