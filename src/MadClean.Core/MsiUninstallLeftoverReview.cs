namespace MadClean.Core;

/// <summary>
/// Connects an accepted historical MSI observation to the existing complete-inventory
/// candidate review. Supplied snapshots/times are consistency evidence, not native authentication.
/// </summary>
public static class MsiUninstallLeftoverReview
{
    public const string Limits = "Historical-request candidate review only. Client exit, registration absence and prior-path existence " +
        "do not establish complete uninstall, exclusive ownership or proven junk. Shared dependencies, user data, saves and settings " +
        "remain protected. No file content read, cleanup, uninstall, elevation or restart is authorized.";

    public static bool CanReview(MsiUninstallPostcheckSession? session, CheckResult<MsiRegistrationWatchReport>? postcheck,
        DateTimeOffset postcheckReceivedUtc, CheckResult<InstalledApplication>? baseline, DateTimeOffset baselineReceivedUtc,
        DateTimeOffset nowUtc)
    {
        if (session == null || !Utc(nowUtc) || nowUtc > DateTimeOffset.UtcNow || !Utc(postcheckReceivedUtc) ||
            postcheckReceivedUtc > nowUtc || !Utc(baselineReceivedUtc) ||
            baselineReceivedUtc > session.Intent.PreparedUtc || session.Intent.SelectedProductScope is not { } selectedScope ||
            baselineReceivedUtc > selectedScope.ValidatedUtc ||
            !MsiUninstallPostcheckPolicy.IsConsistent(session, postcheck, postcheckReceivedUtc) || !Complete(baseline)) return false;
        InstalledApplication? selected = null;
        foreach (var app in baseline!.Items)
            if (app.Identity.Equals(session.Intent.RegistrationIdentity, StringComparison.OrdinalIgnoreCase)) selected = app;
        return selected != null && SameHistoricalApplication(selected, session.Intent.Scope.Application, baselineReceivedUtc);
    }

    public static CheckResult<LeftoverCandidate> Compare(MsiUninstallPostcheckSession? session,
        CheckResult<MsiRegistrationWatchReport>? postcheck, DateTimeOffset postcheckReceivedUtc,
        CheckResult<InstalledApplication>? baseline, DateTimeOffset baselineReceivedUtc,
        CheckResult<InstalledApplication>? after, DateTimeOffset globalReadStartedUtc, DateTimeOffset globalReadReceivedUtc,
        Func<string, bool?> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        // Capture immutable row copies, then recheck those copies before any supplied
        // filesystem probe. A CanReview UI observation is never a consumed capability.
        if (!TryCopy(baseline, out var beforeCopy) || !TryCopy(after, out var afterCopy) ||
            !Utc(globalReadStartedUtc) || !Utc(globalReadReceivedUtc) || globalReadStartedUtc < postcheckReceivedUtc ||
            globalReadStartedUtc > globalReadReceivedUtc ||
            !CanReview(session, postcheck, postcheckReceivedUtc, beforeCopy, baselineReceivedUtc, globalReadReceivedUtc))
            return Refused();
        var review = LeftoverReview.CompareSelected(beforeCopy!.Items, afterCopy!.Items,
            session!.Intent.RegistrationIdentity, probe);
        return new(review.State, review.Message + " " + Limits, Array.AsReadOnly(review.Items.ToArray()));
    }

    private static bool SameHistoricalApplication(InstalledApplication baseline, InstalledApplication historical,
        DateTimeOffset baselineReceivedUtc)
    {
        string? beforePath = InstalledApplicationPaths.NormalizeLocalDirectory(baseline.InstallLocation);
        string? originalPath = InstalledApplicationPaths.NormalizeLocalDirectory(historical.InstallLocation);
        return beforePath != null && originalPath != null && beforePath.Equals(originalPath, StringComparison.OrdinalIgnoreCase) &&
            baseline.Identity.Equals(historical.Identity, StringComparison.OrdinalIgnoreCase) && baseline.Source == historical.Source &&
            baseline.Name == historical.Name && baseline.Version == historical.Version && baseline.Publisher == historical.Publisher &&
            baseline.UninstallSupport == historical.UninstallSupport && baseline.RegistrationEvidence is { } before &&
            historical.RegistrationEvidence is { } original && before.Identity == original.Identity && before.Source == original.Source &&
            before.NoRemove == original.NoRemove && before.WindowsInstaller == original.WindowsInstaller &&
            before.HasRegisteredUninstaller == original.HasRegisteredUninstaller && before.ProductCode == original.ProductCode &&
            Utc(before.ObservedUtc) && before.ObservedUtc <= baselineReceivedUtc;
    }

    private static bool Complete(CheckResult<InstalledApplication>? snapshot)
    {
        if (snapshot is not { State: CheckState.Available, Items: { } items } || items.Count > LeftoverContentPolicy.MaximumInventoryEntries)
            return false;
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in items)
            if (app == null || !LeftoverContentPolicy.SafeText(app.Identity, 1_024) || !identities.Add(app.Identity)) return false;
        return true;
    }

    private static bool TryCopy(CheckResult<InstalledApplication>? source, out CheckResult<InstalledApplication>? copy)
    {
        copy = null;
        if (!Complete(source)) return false;
        var rows = source!.Items.Select(MsiSelectedProductPreparationPolicy.Copy).ToArray();
        copy = new(CheckState.Available, "Copied complete registration snapshot for historical review.", Array.AsReadOnly(rows));
        return Complete(copy);
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static CheckResult<LeftoverCandidate> Refused() => CheckResult<LeftoverCandidate>.Empty(CheckState.Unknown,
        "A matching complete baseline captured before preparation, an accepted same-request registration postcheck, " +
        "and a complete ordered post-exit global inventory are required. No path was probed. " + Limits);
}
