namespace MadClean.Core;

public enum MsiActionBindingState { Bound, Blocked, Unknown }

// Observation diagnostics only. These values never grant action permission.
public enum MsiActionBindingReason
{
    Unknown, SourceUnavailable, SelectionChanged, RemovalRestricted, RemovalRestrictionUnknown,
    CoverageIncomplete, MultipleContexts, SourceContextMismatch, MetadataNotMatched, Bound
}

[Flags]
public enum MsiActionBindingChangedFields
{
    None = 0, Name = 1, Version = 2, Publisher = 4, Identity = 8, Source = 16,
    ProductCode = 32, ContextMask = 64, WindowsInstaller = 128, RegisteredUninstaller = 256, NoRemove = 512
}
public sealed class MsiActionBindingScope
{
    public InstalledApplication Application { get; }
    public string ProductCode { get; }
    public int ExpectedContextMask { get; }
    public CheckState SnapshotState { get; }
    public DateTimeOffset ValidatedUtc { get; }
    private MsiActionBindingScope(MsiRegistrationScope source)
    {
        Application = source.Application with { }; ProductCode = source.ProductCode;
        ExpectedContextMask = source.ExpectedContextMask; SnapshotState = source.SnapshotState; ValidatedUtc = source.ValidatedUtc;
    }
    internal static MsiActionBindingScope From(MsiRegistrationScope source) => new(source);
}
public sealed record MsiActionBindingReport(MsiActionBindingState State, string RegistrationIdentity, string ProductCode,
    CheckState SelectionSnapshotState, CheckState CurrentSnapshotState, bool? NoRemove, int ExpectedContextMask,
    IReadOnlyList<MsiRegistrationObservation> Instances, bool EnumerationComplete, DateTimeOffset CheckedUtc,
    IReadOnlyList<string> Coverage)
{
    public bool CanExecute => false;
    public MsiActionBindingReason Reason { get; init; }
    // Null means the complete source comparison was not reached (or legacy report).
    public MsiActionBindingChangedFields? ChangedFields { get; init; }
}
public interface IMsiActionBindingInventory
{
    Task<CheckResult<MsiActionBindingReport>> VerifyAsync(MsiActionBindingScope scope, CancellationToken token);
}

public static class MsiActionBindingPolicy
{
    public static CheckResult<MsiActionBindingScope> CreateScope(CheckResult<InstalledApplication> snapshot,
        InstalledApplication selected, DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (now == default || now.Offset != TimeSpan.Zero || now > DateTimeOffset.UtcNow || snapshot?.Items == null || selected == null || snapshot.Items.Count > 20_000 ||
            snapshot.Items.Count(item => ReferenceEquals(item, selected)) != 1)
            return CheckResult<MsiActionBindingScope>.Empty(CheckState.Unknown, "Select the exact observed MSI registration from the current snapshot; no query scope was established.");
        var qualified = MsiRegistrationReview.CreateScope(snapshot, selected.Identity, now, token);
        return new(qualified.State, "Read-only current-user/machine MSI binding review only; no action or global-absence permission.",
            Array.AsReadOnly(qualified.Items.Select(MsiActionBindingScope.From).ToArray()));
    }

    public static CheckResult<MsiActionBindingReport> Assess(MsiActionBindingScope scope,
        CheckResult<InstalledApplication> freshSnapshot, IReadOnlyList<MsiRegistrationObservation> instances,
        bool enumerationComplete, IReadOnlyList<string> issues, DateTimeOffset checkedUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (scope == null || instances == null || instances.Count > 3 || issues == null || issues.Count > 32 ||
            checkedUtc == default || checkedUtc.Offset != TimeSpan.Zero || checkedUtc > DateTimeOffset.UtcNow ||
            scope.ValidatedUtc > checkedUtc || checkedUtc - scope.ValidatedUtc > MsiRegistrationReview.ScopeLifetime)
            return CheckResult<MsiActionBindingReport>.Empty(CheckState.Unknown, "Current bounded selection/context evidence is invalid or stale; no binding was accepted.");
        var rows = new List<MsiRegistrationObservation>();
        var seen = new HashSet<MsiRegistrationContext>();
        bool malformed = false;
        foreach (var row in instances)
        {
            token.ThrowIfCancellationRequested();
            if (row == null || !Guid.TryParseExact(row.ProductCode, "B", out var rowCode) || rowCode.ToString("B").ToUpperInvariant() != scope.ProductCode || !Enum.IsDefined(row.Context) || !seen.Add(row.Context) ||
                !Enum.IsDefined(row.ProductState) || row.ObservedUtc.Offset != TimeSpan.Zero || row.ObservedUtc < scope.ValidatedUtc || row.ObservedUtc > checkedUtc ||
                !Text(row.Name, 1024) || !Text(row.Version, 256) || !Text(row.Publisher, 512)) { malformed = true; break; }
            rows.Add(row with { ProductCode = scope.ProductCode });
        }
        foreach (var issue in issues) { token.ThrowIfCancellationRequested(); if (!Text(issue, 1024)) malformed = true; }
        bool freshBounded = freshSnapshot?.Items != null && freshSnapshot.Items.Count <= 20_000 && (freshSnapshot.State is CheckState.Available or CheckState.Partial);
        var matches = freshBounded ? freshSnapshot!.Items.Where(row => row != null && string.Equals(row.Identity, scope.Application.Identity, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray() : null;
        InstalledApplication? fresh = matches is { Length: 1 } ? matches[0] : null;
        var current = fresh != null && freshSnapshot != null
            ? MsiRegistrationReview.CreateScope(freshSnapshot, fresh.Identity, checkedUtc, token).Items.SingleOrDefault() : null;
        var state = MsiActionBindingState.Unknown;
        var reasonCode = MsiActionBindingReason.MetadataNotMatched;
        MsiActionBindingChangedFields? changedFields = null;
        string reason = "A complete fresh exact-selection/current-user/machine binding was not established.";
        if (!freshBounded || current == null || fresh == null ||
            fresh.RegistrationEvidence is not { } evidence || evidence.ObservedUtc < scope.ValidatedUtc)
        {
            reasonCode = MsiActionBindingReason.SourceUnavailable;
            reason = "The fresh ARP registration is absent, ambiguous, unqualified or not observed after this selection; no absence or completed-uninstall inference is made.";
        }
        else
        {
            // Evaluate these managed comparisons independently for diagnostics. The original
            // acceptance conditions below remain unchanged and do not consume diagnostic fields.
            changedFields = CompareSelectionFields(scope, fresh, current, evidence);
            if (fresh.Name != scope.Application.Name || fresh.Version != scope.Application.Version || fresh.Publisher != scope.Application.Publisher ||
                fresh.Identity != scope.Application.Identity || fresh.Source != scope.Application.Source || current.ProductCode != scope.ProductCode ||
                current.ExpectedContextMask != scope.ExpectedContextMask || evidence.WindowsInstaller != scope.Application.RegistrationEvidence?.WindowsInstaller ||
                evidence.HasRegisteredUninstaller != scope.Application.RegistrationEvidence?.HasRegisteredUninstaller || evidence.NoRemove != scope.Application.RegistrationEvidence?.NoRemove)
            { state = MsiActionBindingState.Blocked; reasonCode = MsiActionBindingReason.SelectionChanged; reason = "Selected registration identity, source, display or mechanism/restriction evidence changed; select and review the current registration again."; }
            else if (evidence.NoRemove == true)
            { state = MsiActionBindingState.Blocked; reasonCode = MsiActionBindingReason.RemovalRestricted; reason = "Fresh registration declares NoRemove; read-only MSI observations cannot override that restriction."; }
            else if (evidence.NoRemove == null)
            { reasonCode = MsiActionBindingReason.RemovalRestrictionUnknown; reason = "Fresh removal restriction is unknown; no selection/context binding was accepted for future action review."; }
            else if (malformed || !enumerationComplete || issues.Count != 0)
            { reasonCode = MsiActionBindingReason.CoverageIncomplete; reason = "Full current-user/machine context enumeration or property coverage is incomplete/malformed; no unique binding was inferred."; }
            else if (rows.Count > 1)
            { state = MsiActionBindingState.Blocked; reasonCode = MsiActionBindingReason.MultipleContexts; reason = "The selected product code was observed in multiple current-user/machine contexts; no single selected-context binding was accepted."; }
            else if (rows.Count == 1 && ((int)rows[0].Context & scope.ExpectedContextMask) == 0)
            { state = MsiActionBindingState.Blocked; reasonCode = MsiActionBindingReason.SourceContextMismatch; reason = "The observed MSI context does not match the selected ARP source."; }
            else if (rows.Count == 1 && rows[0].ProductState == MsiObservedProductState.Installed &&
                Known(fresh.Name) && Known(fresh.Version) && Known(fresh.Publisher) &&
                rows[0].Name == fresh.Name && rows[0].Version == fresh.Version && rows[0].Publisher == fresh.Publisher)
            { state = MsiActionBindingState.Bound; reasonCode = MsiActionBindingReason.Bound; reason = "Selection/context binding observed within current-user and machine scope only; no dispatch permission or removal eligibility is established."; }
        }
        var coverage = Array.AsReadOnly(new[]
        {
            reason,
            "Source: fresh bounded ARP registration inventory, then exact-product MsiEnumProductsExW contexts 1|2|4 and allowlisted MsiGetProductInfoExW metadata. Input and output SID pointers are NULL; other-user registrations are not enumerated. No global absence is established.",
            "ARP enumeration, MSI contexts and properties are sequential non-atomic observations. No lock, uninterrupted source continuity, package authenticity, effective permissions, removability or installer readiness is established.",
            "Partial historical/current ARP coverage stays Partial. NoRemove remains independent; absent registration or zero matching contexts never proves uninstall completion. No raw uninstall commands, package paths, activation, installation, uninstall, elevation or restart is requested.",
            "CanExecute is always false. This report is evidence for later separately reviewed actions, not an action token."
        });
        var resultState = state == MsiActionBindingState.Bound
            ? scope.SnapshotState == CheckState.Partial || freshSnapshot!.State == CheckState.Partial ? CheckState.Partial : CheckState.Available
            : state == MsiActionBindingState.Blocked ? CheckState.Unavailable : CheckState.Unknown;
        return new(resultState, reason, [new(state, scope.Application.Identity, scope.ProductCode, scope.SnapshotState,
            freshSnapshot != null && Enum.IsDefined(freshSnapshot.State) ? freshSnapshot.State : CheckState.Unknown,
            fresh?.RegistrationEvidence?.NoRemove, scope.ExpectedContextMask,
            Array.AsReadOnly(malformed ? Array.Empty<MsiRegistrationObservation>() : rows.ToArray()), enumerationComplete && !malformed, checkedUtc, coverage) { Reason = reasonCode, ChangedFields = changedFields }]);
    }
    private static MsiActionBindingChangedFields CompareSelectionFields(MsiActionBindingScope scope,
        InstalledApplication fresh, MsiRegistrationScope current, InstalledAppRegistrationEvidence evidence)
    {
        var fields = MsiActionBindingChangedFields.None;
        if (fresh.Name != scope.Application.Name) fields |= MsiActionBindingChangedFields.Name;
        if (fresh.Version != scope.Application.Version) fields |= MsiActionBindingChangedFields.Version;
        if (fresh.Publisher != scope.Application.Publisher) fields |= MsiActionBindingChangedFields.Publisher;
        if (fresh.Identity != scope.Application.Identity) fields |= MsiActionBindingChangedFields.Identity;
        if (fresh.Source != scope.Application.Source) fields |= MsiActionBindingChangedFields.Source;
        if (current.ProductCode != scope.ProductCode) fields |= MsiActionBindingChangedFields.ProductCode;
        if (current.ExpectedContextMask != scope.ExpectedContextMask) fields |= MsiActionBindingChangedFields.ContextMask;
        if (evidence.WindowsInstaller != scope.Application.RegistrationEvidence?.WindowsInstaller) fields |= MsiActionBindingChangedFields.WindowsInstaller;
        if (evidence.HasRegisteredUninstaller != scope.Application.RegistrationEvidence?.HasRegisteredUninstaller) fields |= MsiActionBindingChangedFields.RegisteredUninstaller;
        if (evidence.NoRemove != scope.Application.RegistrationEvidence?.NoRemove) fields |= MsiActionBindingChangedFields.NoRemove;
        return fields;
    }
    private static bool Known(string value) => Text(value, 1024) && !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && ShortcutPathPolicy.HasSafeUnicode(value);
}
