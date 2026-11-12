using System.Diagnostics;
using System.Security.Principal;
using MadClean.Core;

namespace MadClean.Windows;

/// <summary>
/// Separate explicit exact-product metadata read. It shares the existing ARP/MSI owners;
/// it never changes a request policy, global inventory or Windows Installer state.
/// </summary>
public sealed class WindowsMsiSelectedProductPreparation : IMsiSelectedProductPreparationInventory
{
    public bool HasPendingNativeRead => ScheduledMsiReadOwnership.HasPending ||
        WindowsMsiRegistrationInventory.PendingRead || InstalledApplicationInventory.PendingRead;
    public async Task<CheckResult<MsiSelectedProductPreparation>> ReadAsync(MsiSelectedProductReadScope scope,
        long sourceGeneration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
            return Empty(CheckState.Unsupported, "The three-source selected-product read requires 64-bit Windows. No HKLM64 absence or native result was invented.");
        if (!MsiSelectedProductPreparationPolicy.IsFresh(scope, sourceGeneration, DateTimeOffset.UtcNow))
            return Empty(CheckState.Unknown, "The selected-product read selector is invalidated, stale or changed. No query started.");
        var ownership = new ScheduledMsiReadOwnership();
        var worker = new Task<CheckResult<MsiSelectedProductPreparation>>(() =>
        {
            bool msiOwned = false, arpOwned = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!MsiSelectedProductPreparationPolicy.IsFresh(scope, sourceGeneration, DateTimeOffset.UtcNow))
                    return Empty(CheckState.Unknown, "The selected-product read selector changed before the worker started. No query ran.");
                if (!(msiOwned = WindowsMsiRegistrationInventory.TryBeginRead()))
                    return Empty(CheckState.Unknown, "An existing MSI worker is pending. No selected-product read started.");
                if (!(arpOwned = InstalledApplicationInventory.TryBeginRead()))
                    return Empty(CheckState.Unknown, "An existing registry worker is pending. No selected-product read started.");
                return new Reader(scope, sourceGeneration, token).Read();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return Empty(CheckState.Cancelled, "The selected-product worker observed cancellation. No completed preparation or action permission is claimed."); }
            catch (ReadScopeChanged)
            { return Empty(CheckState.Unknown, "The selection changed or expired during its read. No current target match is published."); }
            catch (ReadBudgetExceeded)
            { return Empty(CheckState.Unknown, "The cooperative selected-product read budget elapsed. Coverage remains incomplete; no action was attempted."); }
            catch (WindowsMsiRegistrationBudgetException)
            { return Empty(CheckState.Unknown, "The native MSI read budget elapsed. No complete context match or action permission was established."); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { return Empty(CheckState.Unsupported, "Required Windows metadata entry points are unavailable. No completed target read or action was substituted."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or System.Security.SecurityException or System.ComponentModel.Win32Exception or NotSupportedException)
            { return Empty(CheckState.Failed, "The selected-product metadata read could not complete. No provider text, global absence or action permission is inferred."); }
            finally
            {
                if (arpOwned) InstalledApplicationInventory.EndRead();
                if (msiOwned) WindowsMsiRegistrationInventory.EndRead();
                ownership.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
        // Allocate the task and its terminal observer before registering ownership. A
        // scheduled-but-unstarted task remains visible through timeout/cancellation.
        _ = worker.ContinueWith(value => { _ = value.Exception; ownership.Release(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        ownership.Register();
        try { worker.Start(TaskScheduler.Default); }
        catch (Exception)
        {
            // Created was never queued; a terminal task cannot begin later. Any other
            // state stays owned until its delegate/terminal continuation releases it.
            if (worker.Status == TaskStatus.Created || worker.IsCompleted) ownership.Release();
            return Empty(CheckState.Unknown, "The selected-product worker did not start conclusively. Any queued ownership remains visible; no completed query or action is claimed.");
        }
        try
        {
            var result = await worker.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!MsiSelectedProductPreparationPolicy.IsFresh(scope, sourceGeneration, DateTimeOffset.UtcNow))
                return Empty(CheckState.Unknown, "Selection invalidation or expiry won before publication. No current target match is shown.");
            return result;
        }
        catch (TimeoutException)
        { return Empty(CheckState.Unknown, "The caller stopped waiting for the selected-product read. Synchronous native work may continue under worker ownership and blocks new ARP/MSI reads; no force-close or action occurred."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Empty(CheckState.Cancelled, "The caller requested cancellation. This does not prove native calls stopped; their worker retains resources and shared gates until return."); }
    }
    private static CheckResult<MsiSelectedProductPreparation> Empty(CheckState state, string message) =>
        CheckResult<MsiSelectedProductPreparation>.Empty(state, message);
    private sealed class ReadScopeChanged : Exception { }
    private sealed class ReadBudgetExceeded : Exception { }

    private sealed class Reader(MsiSelectedProductReadScope scope, long generation, CancellationToken token)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        internal CheckResult<MsiSelectedProductPreparation> Read()
        {
            Check();
            using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true))
                if (impersonated != null) throw new NotSupportedException();
            var observations = new WindowsMsiExactProductArpReader(scope.ProductCode, scope.Application.Identity,
                scope.OriginalSource, Check, token).Read();
            Check();
            // The existing raw reader explicitly supports mask7. The original read scope
            // retains its original3/4 provenance hint; no old report or policy is changed.
            var native = new WindowsMsiRegistrationInventoryNative(scope.NativeReadScope, token).ReadObservations(7);
            Check();
            return MsiSelectedProductPreparationPolicy.Assess(scope, observations, native.Instances,
                native.EnumerationComplete, native.Issues.Count, generation, DateTimeOffset.UtcNow, token);
        }
        private void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= TimeSpan.FromSeconds(20)) throw new ReadBudgetExceeded();
            if (!MsiSelectedProductPreparationPolicy.IsFresh(scope, generation, DateTimeOffset.UtcNow)) throw new ReadScopeChanged();
        }
    }
}
