using MadClean.Core;

namespace MadClean.Windows;

/// <summary>Explicit family-level Settings navigation only. No package operation is requested.</summary>
public sealed class PackagedAppSettingsLauncher
{
    private readonly GuidedWindowsActionLauncher owner = new();
    public bool HasPendingHandoff => GuidedWindowsActionLauncher.HasPendingHandoff;
    public Task<CheckResult<string>> OpenAsync(PackagedAppSettingsHandoff handoff, CancellationToken token)
    {
        if (!PackagedAppSettingsPolicy.IsAllowed(handoff, DateTimeOffset.UtcNow))
            return Task.FromResult(CheckResult<string>.Empty(CheckState.Unsupported, "The current-user package navigation preview is stale or inconsistent. Nothing was dispatched; review again."));
        return owner.OpenPackagedSettingsAsync(handoff, token);
    }
}
