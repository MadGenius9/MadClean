namespace MadClean.Core;

public static class LeftoverContentReview
{
    /// <summary>Validates only supplied complete ARP registrations; performs no path probes or contents reads.</summary>
    public static CheckResult<LeftoverContentScope> ValidateScope(LeftoverCandidate candidate,
        CheckResult<InstalledApplication> before, CheckResult<InstalledApplication> after, DateTimeOffset now,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (candidate == null || before == null || after == null || before.State != CheckState.Available || after.State != CheckState.Available ||
            before.Items == null || after.Items == null || before.Items.Count > LeftoverContentPolicy.MaximumInventoryEntries ||
            after.Items.Count > LeftoverContentPolicy.MaximumInventoryEntries || now == default || now.Offset != TimeSpan.Zero ||
            !LeftoverContentPolicy.SafeText(candidate.ApplicationIdentity, 1_024) || !LeftoverContentPolicy.SafeText(candidate.Evidence, 8_192) ||
            !LeftoverContentPolicy.SafeText(candidate.Caution, 8_192))
            return Block("Complete bounded before/after ARP snapshots, a valid selected candidate and UTC validation time are required.");
        if (!UniqueIdentities(before.Items, token) || !UniqueIdentities(after.Items, token))
            return Block("An inventory has missing, malformed or duplicate registration identities; no disappearance or path scope was inferred.");
        InstalledApplication? selected = null;
        foreach (var app in before.Items)
        {
            token.ThrowIfCancellationRequested();
            if (app.Identity.Equals(candidate.ApplicationIdentity, StringComparison.OrdinalIgnoreCase)) selected = app;
        }
        if (selected == null || after.Items.Any(app => app.Identity.Equals(candidate.ApplicationIdentity, StringComparison.OrdinalIgnoreCase)))
            return Block("The uniquely selected prior registration is not absent from the refreshed complete inventory; no leftover scope was created.");
        string? prior = InstalledApplicationPaths.NormalizeLocalDirectory(selected.InstallLocation);
        string? path = InstalledApplicationPaths.NormalizeLocalDirectory(candidate.Path);
        if (prior == null || path == null || !prior.Equals(path, StringComparison.OrdinalIgnoreCase))
            return Block("Candidate path must exactly match the selected prior normalized local install directory; no replacement path or broader root is accepted.");
        if (LeftoverContentPolicy.IsProtectedRoot(path))
            return Block("The selected path is a broad, shared, user/profile/save/settings/credential or system location; metadata preview is blocked.", CheckState.Unsupported);
        int unknownPaths = 0;
        foreach (var app in before.Items.Concat(after.Items))
        {
            token.ThrowIfCancellationRequested();
            if (app.Identity.Equals(selected.Identity, StringComparison.OrdinalIgnoreCase)) continue;
            string? other = InstalledApplicationPaths.NormalizeLocalDirectory(app.InstallLocation);
            if (other == null) { unknownPaths++; continue; }
            if (LeftoverContentPolicy.Overlaps(path, other))
                return Block("The selected directory overlaps another prior or current application's recorded install root. Shared ownership is unresolved; preview is blocked.", CheckState.Unsupported);
        }
        token.ThrowIfCancellationRequested();
        string coverage = "Only recorded install roots in the supplied complete ARP snapshots were compared. " + unknownPaths +
            " other snapshot entries have no supported recorded local install location; unregistered/portable/packaged software and unknown ownership remain outside coverage. No known overlap is not proof of exclusive ownership. " +
            "Validation time is not snapshot age or filesystem probe time. Registration disappearance is not proof uninstall completed; prior path existence must be rechecked by the Windows metadata adapter. Review only, not proven junk or a cleanup target. Active use remains Unknown; no content read or removal is authorized.";
        return new(CheckState.Available, "Selected prior path qualified only for a bounded guarded metadata preview; filesystem safety/existence and ownership remain unverified.",
            [new(path, selected.Identity, now, candidate.Evidence, candidate.Caution, coverage)]);
    }
    private static bool UniqueIdentities(IReadOnlyList<InstalledApplication> items, CancellationToken token)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in items)
        {
            token.ThrowIfCancellationRequested();
            if (app == null || !LeftoverContentPolicy.SafeText(app.Identity, 1_024) || !identities.Add(app.Identity)) return false;
        }
        return true;
    }
    private static CheckResult<LeftoverContentScope> Block(string reason, CheckState state = CheckState.Unknown) =>
        CheckResult<LeftoverContentScope>.Empty(state, reason + " No filesystem paths were probed, no contents read and no deletion performed.");
}
