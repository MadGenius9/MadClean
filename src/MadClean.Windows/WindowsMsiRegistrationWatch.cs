using System.Diagnostics;
using System.Security.Principal;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit exact-product observation for a remembered target; never an installer request or absence-of-files proof.</summary>
public sealed class WindowsMsiRegistrationWatch : IMsiRegistrationWatchInventory
{
    public bool HasPendingNativeRead => ScheduledMsiReadOwnership.HasPending ||
        WindowsMsiRegistrationInventory.PendingRead || InstalledApplicationInventory.PendingRead;

    public async Task<CheckResult<MsiRegistrationWatchReport>> ReadAsync(MsiRegistrationWatchReadScope scope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
            return Empty(CheckState.Unsupported, "The exact-product registration watch requires 64-bit Windows. No source absence or native result was invented.");
        if (!MsiRegistrationWatchPolicy.IsFresh(scope, DateTimeOffset.UtcNow))
            return Empty(CheckState.Unknown, "The remembered-product read selector is invalidated, stale or changed. No query started.");
        var ownership = new ScheduledMsiReadOwnership();
        var worker = new Task<CheckResult<MsiRegistrationWatchReport>>(() =>
        {
            bool msiOwned = false, arpOwned = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!MsiRegistrationWatchPolicy.IsFresh(scope, DateTimeOffset.UtcNow))
                    return Empty(CheckState.Unknown, "The remembered-product selector changed before the worker started. No query ran.");
                if (!(msiOwned = WindowsMsiRegistrationInventory.TryBeginRead()))
                    return Empty(CheckState.Unknown, "An existing MSI worker is pending. No registration watch query started.");
                if (!(arpOwned = InstalledApplicationInventory.TryBeginRead()))
                    return Empty(CheckState.Unknown, "An existing registry worker is pending. No registration watch query started.");
                return new Reader(scope, token).Read();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return Empty(CheckState.Cancelled, "The registration-watch worker observed cancellation. No completed registration observation is claimed."); }
            catch (ReadScopeChanged)
            { return Empty(CheckState.Unknown, "The remembered target was invalidated during its read. No current observation is published."); }
            catch (ReadBudgetExceeded)
            { return Empty(CheckState.Unknown, "The cooperative registration-watch read budget elapsed. Source coverage remains incomplete."); }
            catch (WindowsMsiRegistrationBudgetException)
            { return Empty(CheckState.Unknown, "The native MSI read budget elapsed. No complete context absence or presence was established."); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { return Empty(CheckState.Unsupported, "Required Windows metadata entry points are unavailable. No completed registration observation was substituted."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or System.Security.SecurityException or System.ComponentModel.Win32Exception or NotSupportedException)
            { return Empty(CheckState.Failed, "The registration-watch read could not complete. Check Windows registry/Installer access and retry; no provider text or global absence is inferred."); }
            finally
            {
                if (arpOwned) InstalledApplicationInventory.EndRead();
                if (msiOwned) WindowsMsiRegistrationInventory.EndRead();
                ownership.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
        // Scheduled-but-unstarted work remains visible to both exact-product adapters.
        _ = worker.ContinueWith(value => { _ = value.Exception; ownership.Release(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        ownership.Register();
        try { worker.Start(TaskScheduler.Default); }
        catch (Exception)
        {
            if (worker.Status == TaskStatus.Created || worker.IsCompleted) ownership.Release();
            return Empty(CheckState.Unknown, "The registration-watch worker did not start conclusively. Any queued ownership remains visible; no completed query is claimed.");
        }
        try
        {
            var result = await worker.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Expiry admits dispatch; it does not manufacture a timeout for an owned read.
            if (scope.IsInvalidated)
                return Empty(CheckState.Unknown, "Target invalidation won before publication. No current registration observation is shown.");
            return result;
        }
        catch (TimeoutException)
        { return Empty(CheckState.Unknown, "The caller stopped waiting for the registration watch. Synchronous native work may continue under worker ownership and blocks new ARP/MSI reads; no force-close occurred."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Empty(CheckState.Cancelled, "The caller requested cancellation. This does not prove native calls stopped; their worker retains resources and shared gates until return."); }
    }

    private static CheckResult<MsiRegistrationWatchReport> Empty(CheckState state, string message) =>
        CheckResult<MsiRegistrationWatchReport>.Empty(state, message +
            " Current global inventory, complete uninstall and leftover-file ownership remain unchecked. No installer, file action or maintenance operation was requested.");
    private sealed class ReadScopeChanged : Exception { }
    private sealed class ReadBudgetExceeded : Exception { }
    private sealed class Reader(MsiRegistrationWatchReadScope scope, CancellationToken token)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly DateTimeOffset queryStarted = DateTimeOffset.UtcNow;
        internal CheckResult<MsiRegistrationWatchReport> Read()
        {
            Check();
            using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true))
                if (impersonated != null) throw new NotSupportedException();
            var target = scope.Target;
            var arp = new WindowsMsiExactProductArpReader(target.ProductCode, target.RegistrationIdentity,
                target.OriginalSource, Check, token).Read();
            Check();
            var native = new WindowsMsiRegistrationInventoryNative(target.ProductCode, token, Check).ReadObservations(7);
            Check();
            return MsiRegistrationWatchPolicy.Assess(scope, arp, native.Instances, native.EnumerationComplete,
                native.Issues.Count, queryStarted, DateTimeOffset.UtcNow, token);
        }
        private void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= TimeSpan.FromSeconds(20)) throw new ReadBudgetExceeded();
            if (scope.IsInvalidated) throw new ReadScopeChanged();
        }
    }
}
