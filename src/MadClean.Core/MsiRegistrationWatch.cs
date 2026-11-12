namespace MadClean.Core;

/// <summary>Session-only remembered metadata, never an uninstall target or leftover ownership proof.</summary>
public sealed class MsiRegistrationWatchTarget
{
    internal MsiRegistrationWatchTarget(InstalledApplication app, string code, MsiArpProvenance source,
        MsiRegistrationContext context, CheckState coverage, DateTimeOffset captured)
    {
        TargetId = Guid.NewGuid(); ProductCode = code; RegistrationIdentity = app.Identity;
        Name = app.Name; Version = app.Version; Publisher = app.Publisher; OriginalSource = source;
        OriginalContext = context; HistoricalCoverage = coverage; CapturedUtc = captured;
        HistoricalNoRemove = app.RegistrationEvidence?.NoRemove;
    }
    public Guid TargetId { get; }
    public string ProductCode { get; }
    public string RegistrationIdentity { get; }
    public string Name { get; }
    public string Version { get; }
    public string Publisher { get; }
    public MsiArpProvenance OriginalSource { get; }
    public MsiRegistrationContext OriginalContext { get; }
    public CheckState HistoricalCoverage { get; }
    public DateTimeOffset CapturedUtc { get; }
    public bool? HistoricalNoRemove { get; }
    public bool CanUninstall => false;
    public bool CanDelete => false;
}

public sealed class MsiRegistrationWatchReadScope
{
    private int invalidated;
    internal MsiRegistrationWatchReadScope(MsiRegistrationWatchTarget target, DateTimeOffset requested)
    { Target = target; RequestId = Guid.NewGuid(); RequestedUtc = requested; ExpiresUtc = requested.AddMinutes(2); }
    public MsiRegistrationWatchTarget Target { get; }
    public Guid RequestId { get; }
    public DateTimeOffset RequestedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public bool IsInvalidated => Volatile.Read(ref invalidated) != 0;
    public void Invalidate() => Interlocked.Exchange(ref invalidated, 1);
}

public enum MsiRegistrationWatchPresence { Unknown, Absent, Advertised, Installed }
public sealed record MsiRegistrationWatchContext(MsiRegistrationContext Context, MsiRegistrationWatchPresence Presence, DateTimeOffset? ObservedUtc);

public interface IMsiRegistrationWatchInventory
{
    bool HasPendingNativeRead { get; }
    Task<CheckResult<MsiRegistrationWatchReport>> ReadAsync(MsiRegistrationWatchReadScope scope, CancellationToken token);
}

/// <summary>Three exact ARP and current-user/machine context observations; absence is not uninstall completion.</summary>
public sealed class MsiRegistrationWatchReport
{
    internal MsiRegistrationWatchReport(MsiRegistrationWatchReadScope scope, IReadOnlyList<MsiSelectedArpLookup> arp,
        IReadOnlyList<MsiRegistrationObservation> native, IReadOnlyList<MsiRegistrationWatchContext> contexts,
        bool complete, int issues, DateTimeOffset started, DateTimeOffset completed)
    {
        TargetId = scope.Target.TargetId; RequestId = scope.RequestId; ProductCode = scope.Target.ProductCode;
        QueryStartedUtc = started; CheckedUtc = completed; HistoricalCoverage = scope.Target.HistoricalCoverage;
        ArpLookups = Array.AsReadOnly(arp.Select(row => row with
        { Registration = row.Registration is { } app ? MsiSelectedProductPreparationPolicy.Copy(app) : null }).ToArray());
        NativeInstances = Array.AsReadOnly(native.Select(row => row with { }).ToArray());
        Contexts = Array.AsReadOnly(contexts.ToArray()); EnumerationComplete = complete; NativeIssueCount = issues;
    }
    public Guid TargetId { get; }
    public Guid RequestId { get; }
    public string ProductCode { get; }
    public DateTimeOffset QueryStartedUtc { get; }
    public DateTimeOffset CheckedUtc { get; }
    public CheckState HistoricalCoverage { get; }
    public IReadOnlyList<MsiSelectedArpLookup> ArpLookups { get; }
    public IReadOnlyList<MsiRegistrationObservation> NativeInstances { get; }
    public IReadOnlyList<MsiRegistrationWatchContext> Contexts { get; }
    public bool EnumerationComplete { get; }
    public int NativeIssueCount { get; }
    public CheckState ReportUninstallCompletion => CheckState.Unknown;
    public CheckState LeftoverOwnership => CheckState.Unknown;
    public bool CanUninstall => false;
    public bool CanDelete => false;
}

public static class MsiRegistrationWatchPolicy
{
    public static readonly TimeSpan TargetLifetime = TimeSpan.FromHours(24);
    private const string Limits = "Exact product registration observations only, not a global inventory. Sequential reads are non-atomic. " +
        "Absence does not prove an uninstall completed or establish leftover ownership. No uninstall, deletion, repair or restart is authorized.";

    public static CheckResult<MsiRegistrationWatchTarget> Capture(CheckResult<InstalledApplication>? snapshot,
        InstalledApplication? selected, CheckResult<MsiRegistrationReport>? verified, DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (snapshot?.Items == null || selected == null || snapshot.Items.Count > 20_000 ||
            snapshot.Items.Count(row => ReferenceEquals(row, selected)) != 1 || !Time(now) || now > DateTimeOffset.UtcNow ||
            verified?.State is not (CheckState.Available or CheckState.Partial) || verified.Items is not { Count: 1 } ||
            verified.Items[0] is not { } report) return RejectTarget();
        var scope = MsiRegistrationReview.CreateScope(snapshot, selected.Identity, now, token).Items.SingleOrDefault();
        if (scope == null || report.RegistrationIdentity != selected.Identity || report.ProductCode != scope.ProductCode ||
            report.SnapshotState != snapshot.State || report.NoRemove != selected.RegistrationEvidence!.NoRemove ||
            !Time(report.CheckedUtc) || report.CheckedUtc > now || now - report.CheckedUtc > TimeSpan.FromMinutes(2) ||
            report.CheckedUtc < selected.RegistrationEvidence.ObservedUtc || report.Instances is not { Count: 1 } ||
            report.Instances[0] is not { } instance || instance.ProductCode != scope.ProductCode ||
            !Enum.IsDefined(instance.Context) || ((int)instance.Context & scope.ExpectedContextMask) == 0 ||
            instance.ProductState != MsiObservedProductState.Installed || !Time(instance.ObservedUtc) ||
            instance.ObservedUtc < selected.RegistrationEvidence.ObservedUtc || instance.ObservedUtc > report.CheckedUtc ||
            !Known(selected.Name, 1024) || !Known(selected.Version, 256) || !Known(selected.Publisher, 512) ||
            instance.Name != selected.Name || instance.Version != selected.Version || instance.Publisher != selected.Publisher)
            return RejectTarget();
        var sources = Enum.GetValues<MsiArpProvenance>().Where(source => selected.Source == MsiSelectedProductPreparationPolicy.SourceLabel(source) &&
            string.Equals(selected.Identity, MsiSelectedProductPreparationPolicy.RegistrationIdentity(source, scope.ProductCode), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sources.Length != 1) return RejectTarget();
        token.ThrowIfCancellationRequested();
        var coverage = snapshot.State == CheckState.Partial || verified.State == CheckState.Partial ? CheckState.Partial : CheckState.Available;
        return new(coverage, "Verified registration remembered in memory for at most 24 hours. " + Limits,
            [new(selected, scope.ProductCode, sources[0], instance.Context, coverage, now)]);
    }

    public static CheckResult<MsiRegistrationWatchReadScope> Prepare(MsiRegistrationWatchTarget? target, DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (target == null || !Time(now) || now > DateTimeOffset.UtcNow || now < target.CapturedUtc || now - target.CapturedUtc >= TargetLifetime ||
            now > DateTimeOffset.MaxValue.AddMinutes(-2)) return CheckResult<MsiRegistrationWatchReadScope>.Empty(CheckState.Unknown, "Remember a current verified registration first. " + Limits);
        return new(target.HistoricalCoverage, "Fresh explicit registration read prepared; historical facts grant no action. " + Limits, [new(target, now)]);
    }

    public static bool IsFresh(MsiRegistrationWatchReadScope? scope, DateTimeOffset now) => scope != null && !scope.IsInvalidated &&
        Time(now) && now <= DateTimeOffset.UtcNow && now >= scope.RequestedUtc && now < scope.ExpiresUtc &&
        scope.RequestedUtc >= scope.Target.CapturedUtc && scope.RequestedUtc - scope.Target.CapturedUtc < TargetLifetime;

    public static CheckResult<MsiRegistrationWatchReport> Assess(MsiRegistrationWatchReadScope? scope,
        IReadOnlyList<MsiSelectedArpLookup>? arp, IReadOnlyList<MsiRegistrationObservation>? native,
        bool enumerationComplete, int nativeIssueCount, DateTimeOffset queryStarted, DateTimeOffset checkedUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (scope == null || !IsFresh(scope, queryStarted) || !Time(checkedUtc) || checkedUtc < queryStarted || checkedUtc > DateTimeOffset.UtcNow ||
            arp is not { Count: 3 } || native == null || native.Count > 3 || nativeIssueCount is < 0 or > 32) return RejectReport();
        var sources = new HashSet<MsiArpProvenance>();
        foreach (var lookup in arp)
        {
            token.ThrowIfCancellationRequested();
            if (lookup == null || !Enum.IsDefined(lookup.Source) || !sources.Add(lookup.Source) || !Enum.IsDefined(lookup.State) ||
                !Observed(lookup.ObservedUtc, queryStarted, checkedUtc)) return RejectReport();
            if (lookup.State == MsiSelectedArpLookupState.Absent)
            {
                if (lookup.NativeError is not (2 or 3) || lookup.Registration != null || lookup.SystemComponent != null) return RejectReport();
            }
            else if (lookup.State == MsiSelectedArpLookupState.Unknown)
            {
                if (lookup.Registration != null || lookup.SystemComponent != null) return RejectReport();
            }
            else if (lookup.Registration is not { } app || lookup.NativeError is not (null or 0) || app.InstallLocation != null ||
                app.Source != MsiSelectedProductPreparationPolicy.SourceLabel(lookup.Source) ||
                !string.Equals(app.Identity, MsiSelectedProductPreparationPolicy.RegistrationIdentity(lookup.Source, scope!.Target.ProductCode), StringComparison.OrdinalIgnoreCase) ||
                !Text(app.Name, 1024) || !Text(app.Version, 256) || !Text(app.Publisher, 512) || !Text(app.UninstallSupport, 512) ||
                app.RegistrationEvidence is not { } evidence || evidence.Identity != app.Identity || evidence.Source != app.Source ||
                evidence.ProductCode != scope.Target.ProductCode || evidence.ObservedUtc != lookup.ObservedUtc) return RejectReport();
        }
        var contexts = new Dictionary<MsiRegistrationContext, MsiRegistrationObservation>();
        foreach (var instance in native)
        {
            token.ThrowIfCancellationRequested();
            if (instance == null || instance.ProductCode != scope!.Target.ProductCode || !Enum.IsDefined(instance.Context) ||
                !Enum.IsDefined(instance.ProductState) || !contexts.TryAdd(instance.Context, instance) ||
                !Observed(instance.ObservedUtc, queryStarted, checkedUtc) || !Text(instance.Name, 1024) ||
                !Text(instance.Version, 256) || !Text(instance.Publisher, 512)) return RejectReport();
        }
        var rows = Enum.GetValues<MsiRegistrationContext>().Select(context => contexts.TryGetValue(context, out var instance)
            ? new MsiRegistrationWatchContext(context, instance.ProductState switch
            { MsiObservedProductState.Installed => MsiRegistrationWatchPresence.Installed, MsiObservedProductState.Advertised => MsiRegistrationWatchPresence.Advertised, _ => MsiRegistrationWatchPresence.Unknown }, instance.ObservedUtc)
            : new MsiRegistrationWatchContext(context, enumerationComplete && nativeIssueCount == 0 ? MsiRegistrationWatchPresence.Absent : MsiRegistrationWatchPresence.Unknown,
                enumerationComplete && nativeIssueCount == 0 ? checkedUtc : null)).ToArray();
        token.ThrowIfCancellationRequested();
        if (scope!.IsInvalidated) return RejectReport();
        var state = scope.Target.HistoricalCoverage == CheckState.Partial || arp.Any(row => row.State == MsiSelectedArpLookupState.Unknown) ||
            !enumerationComplete || nativeIssueCount != 0 || rows.Any(row => row.Presence == MsiRegistrationWatchPresence.Unknown) ? CheckState.Partial : CheckState.Available;
        return new(state, "Read-only registration watch observations recorded. " + Limits,
            [new(scope, arp, native, rows, enumerationComplete, nativeIssueCount, queryStarted, checkedUtc)]);
    }

    public static bool IsConsistent(MsiRegistrationWatchReadScope? scope, CheckResult<MsiRegistrationWatchReport>? result, DateTimeOffset received)
    {
        if (scope == null || result?.Items is not { Count: 1 } || result.Items[0] is not { } report || !Time(received) ||
            received > DateTimeOffset.UtcNow || report.CheckedUtc > received || report.TargetId != scope.Target.TargetId ||
            report.RequestId != scope.RequestId || report.ProductCode != scope.Target.ProductCode || report.HistoricalCoverage != scope.Target.HistoricalCoverage) return false;
        var assessed = Assess(scope, report.ArpLookups, report.NativeInstances, report.EnumerationComplete, report.NativeIssueCount, report.QueryStartedUtc, report.CheckedUtc);
        return assessed.State == result.State && assessed.Items.Count == 1 && assessed.Items[0].Contexts.SequenceEqual(report.Contexts);
    }

    private static bool Time(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Observed(DateTimeOffset value, DateTimeOffset start, DateTimeOffset end) => Time(value) && value >= start && value <= end;
    private static bool Text(string? value, int maximum) => value != null && value.Length <= maximum && ShortcutPathPolicy.HasSafeUnicode(value);
    private static bool Known(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && Text(value, maximum) && !value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    private static CheckResult<MsiRegistrationWatchTarget> RejectTarget() => CheckResult<MsiRegistrationWatchTarget>.Empty(CheckState.Unknown, "A fresh exact selected verified MSI registration is required. " + Limits);
    private static CheckResult<MsiRegistrationWatchReport> RejectReport() => CheckResult<MsiRegistrationWatchReport>.Empty(CheckState.Unknown, "Watch observations were invalid, incomplete in shape or invalidated; no registration absence was accepted. " + Limits);
}
