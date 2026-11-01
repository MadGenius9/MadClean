using MadClean.Core;

namespace MadClean.Windows;

/// <summary>A separate explicit exact-pair read. It never enumerates siblings or retains a keeper lease.</summary>
public sealed partial class WindowsDuplicateInventory : IDuplicatePairRecheck
{
    public Task<CheckResult<DuplicatePairRecheckReport>> ReadAsync(DuplicatePairRecheckScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scope);
        if (!PairScopeFresh(scope))
            return Task.FromResult(CheckResult<DuplicatePairRecheckReport>.Empty(CheckState.Unknown,
                "The selected pair scope expired or has an invalid UTC time. Select the pair again; no protection metadata or contents were queried."));
        return RunOwnedFileReadAsync(stop =>
        {
            stop.ThrowIfCancellationRequested();
            if (!PairScopeFresh(scope))
                return CheckResult<DuplicatePairRecheckReport>.Empty(CheckState.Unknown,
                    "The selected pair scope expired before the worker began. Select the pair again; no protection metadata or contents were queried.");
            var access = new WindowsAccess();
            try
            {
                // Refresh protection metadata before either selected file can be opened.
                // Incomplete exclusions block this new content read rather than promoting
                // a historical snapshot's coverage into current permission.
                access.PrepareScope(scope.RootPath, stop, requireCompleteExclusions: true);
            }
            catch (DuplicateReadException exception)
            {
                stop.ThrowIfCancellationRequested();
                return CheckResult<DuplicatePairRecheckReport>.Empty(CheckState.Unknown, exception.Message);
            }
            return new DuplicatePairRecheckEngine().Read(scope, access, stop);
        }, token);
    }
    private static bool PairScopeFresh(DuplicatePairRecheckScope scope)
    {
        var now = DateTimeOffset.UtcNow;
        return scope.ValidatedUtc != default && scope.ValidatedUtc.Offset == TimeSpan.Zero &&
            scope.ValidatedUtc <= now && now - scope.ValidatedUtc <= DuplicatePairRecheckEngine.ScopeLifetime;
    }

}
