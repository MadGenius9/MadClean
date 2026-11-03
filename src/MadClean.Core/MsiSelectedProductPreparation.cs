namespace MadClean.Core;

public enum MsiArpProvenance { CurrentUserShared, LocalMachine64, LocalMachine32 }
public enum MsiSelectedArpLookupState { Unknown, Absent, Present }
public enum MsiSelectedProductPreparationState { ObservedMatch, Blocked, Unknown }
public enum MsiSelectedProductPreparationReason
{
    InvalidScope, ArpLookupUnknown, MultipleArpRegistrations, SelectedRegistrationMissing,
    RequiredArpMetadataUnknown, SelectionChanged, RemovalRestricted, HiddenComponent,
    NativeCoverageIncomplete, MultipleNativeContexts, NativeProductNotInstalled,
    NativeMetadataMismatch, ObservedMatch
}

/// <summary>One actual exact-GUID registry lookup; command contents and installation paths are excluded.</summary>
public sealed record MsiSelectedArpLookup(MsiArpProvenance Source, MsiSelectedArpLookupState State,
    InstalledApplication? Registration, bool? SystemComponent, DateTimeOffset ObservedUtc, int? NativeError);

/// <summary>Immutable read selector with one-way invalidation; never a request or execution token.</summary>
public sealed class MsiSelectedProductReadScope
{
    private readonly object lifecycle = new();
    private bool invalidated, dispatchCommitted;
    internal MsiSelectedProductReadScope(MsiRegistrationScope scope, MsiArpProvenance source, long generation)
    {
        var app = MsiSelectedProductPreparationPolicy.Copy(scope.Application);
        NativeReadScope = new(app, scope.ProductCode, scope.ExpectedContextMask, scope.SnapshotState, scope.ValidatedUtc);
        OriginalSource = source; SourceGeneration = generation;
    }
    public MsiRegistrationScope NativeReadScope { get; }
    public InstalledApplication Application => NativeReadScope.Application;
    public string ProductCode => NativeReadScope.ProductCode;
    public MsiArpProvenance OriginalSource { get; }
    public CheckState HistoricalGlobalSnapshotState => NativeReadScope.SnapshotState;
    public long SourceGeneration { get; }
    public DateTimeOffset ValidatedUtc => NativeReadScope.ValidatedUtc;
    public DateTimeOffset ExpiresUtc => ValidatedUtc.Add(MsiRegistrationReview.ScopeLifetime);
    public bool IsInvalidated { get { lock (lifecycle) return invalidated; } }
    internal bool IsDispatchCommitted { get { lock (lifecycle) return dispatchCommitted; } }
    internal bool IsReadUsable { get { lock (lifecycle) return !invalidated && !dispatchCommitted; } }
    public void Invalidate()
    {
        lock (lifecycle) { if (!dispatchCommitted) invalidated = true; }
    }
    // A separate Core intent holds its own dispatch lock before this lock. The only
    // allowed callback rechecks its managed time/token/generation and sets reservation;
    // it performs no await, native call or externally supplied work.
    internal bool TryCommitDispatch(Func<bool> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (lifecycle)
        {
            if (invalidated || dispatchCommitted || !commit()) return false;
            dispatchCommitted = true; return true;
        }
    }
}

/// <summary>Read-only target facts. Global inventory was not refreshed and matching is never action permission.</summary>
public sealed class MsiSelectedProductPreparation
{
    internal MsiSelectedProductPreparation(MsiSelectedProductReadScope scope, MsiSelectedProductPreparationState state,
        MsiSelectedProductPreparationReason reason, CheckState coverage, IReadOnlyList<MsiSelectedArpLookup> lookups,
        IReadOnlyList<MsiRegistrationObservation> instances, bool enumerationComplete, int issueCount,
        DateTimeOffset checkedUtc)
    {
        Scope = scope; State = state; Reason = reason; TargetCoverageState = coverage;
        Lookups = Array.AsReadOnly(lookups.Select(row => row with
        { Registration = row.Registration is { } app ? MsiSelectedProductPreparationPolicy.Copy(app) : null }).ToArray());
        Instances = Array.AsReadOnly(instances.Select(row => row with { }).ToArray());
        EnumerationComplete = enumerationComplete; NativeIssueCount = issueCount; CheckedUtc = checkedUtc;
    }
    public MsiSelectedProductReadScope Scope { get; }
    public MsiSelectedProductPreparationState State { get; }
    public MsiSelectedProductPreparationReason Reason { get; }
    public CheckState TargetCoverageState { get; }
    public IReadOnlyList<MsiSelectedArpLookup> Lookups { get; }
    public IReadOnlyList<MsiRegistrationObservation> Instances { get; }
    public bool EnumerationComplete { get; }
    public int NativeIssueCount { get; }
    public DateTimeOffset CheckedUtc { get; }
    public CheckState HistoricalGlobalSnapshotState => Scope.HistoricalGlobalSnapshotState;
    public bool CurrentGlobalSnapshotWasRead => false;
    public CheckState CurrentGlobalSnapshotState => CheckState.Unknown;
    public string CurrentGlobalCoverage => "Not checked by this exact-product read.";
    public bool CanExecute => false;
}

public interface IMsiSelectedProductPreparationInventory
{
    bool HasPendingNativeRead { get; }
    Task<CheckResult<MsiSelectedProductPreparation>> ReadAsync(MsiSelectedProductReadScope scope,
        long sourceGeneration, CancellationToken token);
}

public static class MsiSelectedProductPreparationPolicy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    public static string SourceLabel(MsiArpProvenance source) => source switch
    {
        MsiArpProvenance.CurrentUserShared => "HKCU / shared / Add or Remove Programs",
        MsiArpProvenance.LocalMachine64 => "HKLM / 64-bit / Add or Remove Programs",
        MsiArpProvenance.LocalMachine32 => "HKLM / 32-bit / Add or Remove Programs",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
    public static string RegistrationIdentity(MsiArpProvenance source, string productCode)
    {
        if (!CanonicalCode(productCode)) throw new ArgumentException("A canonical exact ProductCode is required.", nameof(productCode));
        string prefix = source switch
        {
            MsiArpProvenance.CurrentUserShared => "HKCU/shared/",
            MsiArpProvenance.LocalMachine64 => "HKLM/64/",
            MsiArpProvenance.LocalMachine32 => "HKLM/32/",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        return prefix + KeyPath + productCode;
    }

    public static CheckResult<MsiSelectedProductReadScope> CreateScope(CheckResult<InstalledApplication> snapshot,
        InstalledApplication selected, long sourceGeneration, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (sourceGeneration < 0 || !Utc(nowUtc) || nowUtc > DateTimeOffset.UtcNow || snapshot?.Items == null || selected == null ||
            snapshot.Items.Count > 20_000 || snapshot.Items.Any(row => row == null) || snapshot.Items.Count(row => ReferenceEquals(row, selected)) != 1)
            return CheckResult<MsiSelectedProductReadScope>.Empty(CheckState.Unknown,
                "Select the actual exact registration in the bounded current snapshot. No selected-product read scope was created.");
        var source = MsiRegistrationReview.CreateScope(snapshot, selected.Identity, nowUtc, token).Items.SingleOrDefault();
        if (source == null) return CheckResult<MsiSelectedProductReadScope>.Empty(CheckState.Unknown,
            "The selected Windows Installer registration is stale, ambiguous or unqualified. No selected-product query ran.");
        var provenance = Enum.GetValues<MsiArpProvenance>().Where(value => selected.Source == SourceLabel(value) &&
            string.Equals(selected.Identity, RegistrationIdentity(value, source.ProductCode), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (provenance.Length != 1) return CheckResult<MsiSelectedProductReadScope>.Empty(CheckState.Unknown,
            "The original exact registry provenance is unsupported. No selected-product query ran.");
        return new(snapshot.State, "Read-only exact-product selector created. Source hive is provenance, not proof of the native MSI context or action permission.",
            [new(source, provenance[0], sourceGeneration)]);
    }

    public static bool IsFresh(MsiSelectedProductReadScope? scope, long sourceGeneration, DateTimeOffset nowUtc) =>
        scope != null && scope.IsReadUsable && sourceGeneration == scope.SourceGeneration && sourceGeneration >= 0 &&
        Enum.IsDefined(scope.OriginalSource) && (scope.HistoricalGlobalSnapshotState is CheckState.Available or CheckState.Partial) &&
        Utc(nowUtc) && Utc(scope.ValidatedUtc) && scope.ValidatedUtc <= nowUtc && nowUtc < scope.ExpiresUtc &&
        CanonicalCode(scope.ProductCode) && scope.NativeReadScope.ExpectedContextMask is 3 or 4;

    public static CheckResult<MsiSelectedProductPreparation> Assess(MsiSelectedProductReadScope scope,
        IReadOnlyList<MsiSelectedArpLookup> lookups, IReadOnlyList<MsiRegistrationObservation> instances,
        bool nativeEnumerationComplete, int nativeIssueCount, long sourceGeneration, DateTimeOffset checkedUtc,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var nowUtc = DateTimeOffset.UtcNow;
        if (!IsFresh(scope, sourceGeneration, nowUtc) || !IsFresh(scope, sourceGeneration, checkedUtc) || checkedUtc > nowUtc || lookups == null ||
            lookups.Count != 3 || instances == null || instances.Count > MsiRegistrationReview.MaxInstances || nativeIssueCount is < 0 or > 32)
            return CheckResult<MsiSelectedProductPreparation>.Empty(CheckState.Unknown,
                "Selected-product observations are invalid, expired, interrupted or outside their bounds. No complete target or action permission is inferred.");
        var sources = new HashSet<MsiArpProvenance>();
        bool unknownArp = false, malformedArp = false;
        foreach (var lookup in lookups)
        {
            token.ThrowIfCancellationRequested();
            if (lookup == null || !Enum.IsDefined(lookup.Source) || !sources.Add(lookup.Source) || !Enum.IsDefined(lookup.State) ||
                !Utc(lookup.ObservedUtc) || lookup.ObservedUtc < scope.ValidatedUtc || lookup.ObservedUtc > checkedUtc)
            { malformedArp = true; break; }
            if (lookup.State == MsiSelectedArpLookupState.Unknown)
            {
                if (lookup.Registration != null || lookup.SystemComponent != null) malformedArp = true;
                unknownArp = true; continue;
            }
            if (lookup.State == MsiSelectedArpLookupState.Absent)
            {
                if (lookup.Registration != null || lookup.SystemComponent != null || lookup.NativeError is not (2 or 3)) malformedArp = true;
                continue;
            }
            if (lookup.Registration is not { } row || lookup.NativeError is not (null or 0) || row.InstallLocation != null ||
                !Text(row.UninstallSupport, 512) || row.Source != SourceLabel(lookup.Source) || !string.Equals(row.Identity, RegistrationIdentity(lookup.Source, scope.ProductCode), StringComparison.OrdinalIgnoreCase) ||
                row.RegistrationEvidence is not { } evidence || evidence.Identity != row.Identity || evidence.Source != row.Source ||
                evidence.ProductCode != scope.ProductCode || !Utc(evidence.ObservedUtc) || evidence.ObservedUtc != lookup.ObservedUtc)
                malformedArp = true;
        }
        if (sources.Count != 3 || malformedArp)
            return CheckResult<MsiSelectedProductPreparation>.Empty(CheckState.Unknown,
                "Exactly three unique, coherent actual registry-source observations are required. No target match or action permission was accepted.");
        var contexts = new HashSet<MsiRegistrationContext>();
        bool malformedNative = false;
        foreach (var instance in instances)
        {
            token.ThrowIfCancellationRequested();
            if (instance == null || instance.ProductCode != scope.ProductCode || !Enum.IsDefined(instance.Context) || !contexts.Add(instance.Context) ||
                !Enum.IsDefined(instance.ProductState) || !Utc(instance.ObservedUtc) || instance.ObservedUtc < scope.ValidatedUtc || instance.ObservedUtc > checkedUtc ||
                !Text(instance.Name, MsiRegistrationReview.NameCharacters) || !Text(instance.Version, MsiRegistrationReview.VersionCharacters) ||
                !Text(instance.Publisher, MsiRegistrationReview.PublisherCharacters)) { malformedNative = true; break; }
        }
        if (malformedNative) return CheckResult<MsiSelectedProductPreparation>.Empty(CheckState.Unknown,
            "The exact-product native context observations were malformed or duplicated. No unique context match was accepted.");
        var coverage = unknownArp || !nativeEnumerationComplete || nativeIssueCount != 0 ? CheckState.Partial : CheckState.Available;
        var present = lookups.Where(row => row.State == MsiSelectedArpLookupState.Present).ToArray();
        var state = MsiSelectedProductPreparationState.Unknown;
        var reason = MsiSelectedProductPreparationReason.ArpLookupUnknown;
        if (unknownArp) { }
        else if (present.Length > 1)
        { state = MsiSelectedProductPreparationState.Blocked; reason = MsiSelectedProductPreparationReason.MultipleArpRegistrations; }
        else if (present.Length != 1 || present[0].Source != scope.OriginalSource)
        { state = MsiSelectedProductPreparationState.Blocked; reason = MsiSelectedProductPreparationReason.SelectedRegistrationMissing; }
        else
        {
            var observed = present[0]; var app = observed.Registration!; var evidence = app.RegistrationEvidence!;
            if (observed.SystemComponent == true)
            { state = MsiSelectedProductPreparationState.Blocked; reason = MsiSelectedProductPreparationReason.HiddenComponent; }
            else if (evidence.NoRemove == true)
            { state = MsiSelectedProductPreparationState.Blocked; reason = MsiSelectedProductPreparationReason.RemovalRestricted; }
            else if (evidence.WindowsInstaller == false)
            { state = MsiSelectedProductPreparationState.Blocked; reason = MsiSelectedProductPreparationReason.SelectionChanged; }
            else if (observed.SystemComponent != false || evidence.WindowsInstaller != true || evidence.NoRemove != false ||
                !evidence.HasRegisteredUninstaller.HasValue || !Known(app.Name, RegistrationMetadataDecoder.NameCharacters) ||
                !Known(app.Version, RegistrationMetadataDecoder.VersionCharacters) || !Known(app.Publisher, RegistrationMetadataDecoder.PublisherCharacters))
            { coverage = CheckState.Partial; reason = MsiSelectedProductPreparationReason.RequiredArpMetadataUnknown; }
            else if (!SameSelection(scope.Application, app))
            { state = MsiSelectedProductPreparationState.Blocked; reason = MsiSelectedProductPreparationReason.SelectionChanged; }
            else if (!nativeEnumerationComplete || nativeIssueCount != 0)
            { reason = MsiSelectedProductPreparationReason.NativeCoverageIncomplete; }
            else if (instances.Count > 1)
            { state = MsiSelectedProductPreparationState.Blocked; reason = MsiSelectedProductPreparationReason.MultipleNativeContexts; }
            else if (instances.Count == 1 && instances[0].ProductState == MsiObservedProductState.Unknown)
            { reason = MsiSelectedProductPreparationReason.NativeMetadataMismatch; coverage = CheckState.Partial; }
            else if (instances.Count != 1 || instances[0].ProductState != MsiObservedProductState.Installed)
            { state = MsiSelectedProductPreparationState.Blocked; reason = MsiSelectedProductPreparationReason.NativeProductNotInstalled; }
            else if (instances[0].Name != app.Name || instances[0].Version != app.Version || instances[0].Publisher != app.Publisher)
            { reason = MsiSelectedProductPreparationReason.NativeMetadataMismatch; coverage = CheckState.Partial; }
            else { state = MsiSelectedProductPreparationState.ObservedMatch; reason = MsiSelectedProductPreparationReason.ObservedMatch; }
        }
        token.ThrowIfCancellationRequested();
        if (!IsFresh(scope, sourceGeneration, DateTimeOffset.UtcNow)) return CheckResult<MsiSelectedProductPreparation>.Empty(CheckState.Unknown,
            "Selection invalidation won before publication. No current selected-product match is shown.");
        var report = new MsiSelectedProductPreparation(scope, state, reason, coverage, lookups, instances,
            nativeEnumerationComplete, nativeIssueCount, checkedUtc);
        var resultState = state == MsiSelectedProductPreparationState.ObservedMatch
            ? scope.HistoricalGlobalSnapshotState == CheckState.Partial ? CheckState.Partial : CheckState.Available
            : state == MsiSelectedProductPreparationState.Blocked ? CheckState.Unavailable : CheckState.Unknown;
        return new(resultState,
            "Exact-product read-only observation: " + reason + ". Historical inventory coverage is preserved; current global inventory was not read. Matching target facts do not grant dispatch, uninstall, baseline, leftover or global-absence permission.", [report]);
    }

    internal static InstalledApplication Copy(InstalledApplication application) => application with
    { RegistrationEvidence = application.RegistrationEvidence is { } evidence ? evidence with { } : null };
    private static bool SameSelection(InstalledApplication original, InstalledApplication current) =>
        string.Equals(original.Identity, current.Identity, StringComparison.OrdinalIgnoreCase) && original.Source == current.Source &&
        original.Name == current.Name && original.Version == current.Version && original.Publisher == current.Publisher &&
        original.RegistrationEvidence is { } before && current.RegistrationEvidence is { } after &&
        before.ProductCode == after.ProductCode && before.WindowsInstaller == after.WindowsInstaller &&
        before.NoRemove == after.NoRemove && before.HasRegisteredUninstaller == after.HasRegisteredUninstaller;
    private static bool CanonicalCode(string? value) => value is { Length: 38 } && Guid.TryParseExact(value, "B", out var code) &&
        code != Guid.Empty && code.ToString("B").ToUpperInvariant() == value;
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Text(string? value, int bound) => value != null && value.Length <= bound && ShortcutPathPolicy.HasSafeUnicode(value);
    private static bool Known(string? value, int bound) => !string.IsNullOrWhiteSpace(value) && Text(value, bound) &&
        !value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);
}
