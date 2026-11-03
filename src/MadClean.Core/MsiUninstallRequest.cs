namespace MadClean.Core;

public enum MsiUninstallDispatchReservation
{
    Reserved, NotConfirmed, Invalidated, AlreadyReserved, Stale, SourceChanged, Cancelled
}

public enum MsiUninstallRequestState { NotStarted, Blocked, Preparing, ClientStarted, ClientExited, MonitoringUnknown }
public enum MsiUninstallExitDisposition
{
    SuccessReported, CancelledByUser, ProductNotInstalled, InstallerBusy, RestartRequired,
    RestartInitiatedUnexpectedly, FailureReported, Unknown
}

/// <summary>Actual client observations only. Neither exit nor registration absence proves every uninstall effect.</summary>
public sealed record MsiUninstallRequestResult(Guid RequestId, MsiUninstallRequestState State,
    DateTimeOffset RequestedUtc, DateTimeOffset? StartedUtc, DateTimeOffset? ExitedUtc,
    int? ClientProcessId, int? ExitCode, string Message)
{
    public MsiUninstallExitDisposition? ExitDisposition { get; init; }
    public bool? ClientStartObserved { get; init; }
    public SystemToolIdentityDiagnostic? ToolIdentity { get; init; }
}

/// <summary>
/// One explicit registered-ProductCode request. The scope is not an immutable package/context binding,
/// publisher authentication, permission to execute arbitrary uninstall text, or a crash-recovery record.
/// </summary>
public sealed class MsiUninstallRequestIntent
{
    private readonly object dispatchLock = new();
    private bool invalidated, reserved;

    internal MsiUninstallRequestIntent(MsiActionBindingScope scope, MsiRegistrationContext context,
        long sourceGeneration, DateTimeOffset preparedUtc, DateTimeOffset expiresUtc,
        MsiSelectedProductReadScope? selectedProductScope = null)
    {
        Scope = scope; SelectedProductScope = selectedProductScope; Context = context; SourceGeneration = sourceGeneration;
        PreparedUtc = preparedUtc; ExpiresUtc = expiresUtc; RequestId = Guid.NewGuid();
        Arguments = Array.AsReadOnly(new[] { "/x", scope.ProductCode, "/norestart" });
    }

    public Guid RequestId { get; }
    public MsiActionBindingScope Scope { get; }
    public MsiSelectedProductReadScope? SelectedProductScope { get; }
    public string ProductCode => Scope.ProductCode;
    public string RegistrationIdentity => Scope.Application.Identity;
    public string Name => Scope.Application.Name;
    public string Version => Scope.Application.Version;
    public string Publisher => Scope.Application.Publisher;
    public string Source => Scope.Application.Source;
    public MsiRegistrationContext Context { get; }
    public long SourceGeneration { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public IReadOnlyList<string> Arguments { get; }
    public bool IsInvalidated { get { lock (dispatchLock) return invalidated || !reserved && SelectedProductScope?.IsInvalidated == true; } }
    public bool IsDispatchReserved { get { lock (dispatchLock) return reserved; } }

    /// <summary>Invalidation cannot revoke a request whose dispatch boundary already won.</summary>
    public void Invalidate()
    {
        lock (dispatchLock) { if (!reserved) InvalidateBeforeDispatch(); }
    }

    /// <summary>
    /// Final one-shot application dispatch boundary, not a Windows launch or MSI lock. The caller must
    /// finish fresh native binding/tool checks first and perform no await before its Process.Start call.
    /// </summary>
    public MsiUninstallDispatchReservation TryReserveDispatch(bool userConfirmed, long sourceGeneration,
        DateTimeOffset nowUtc, CancellationToken token = default)
    {
        lock (dispatchLock)
        {
            if (reserved) return MsiUninstallDispatchReservation.AlreadyReserved;
            if (invalidated || SelectedProductScope?.IsInvalidated == true)
            { InvalidateBeforeDispatch(); return MsiUninstallDispatchReservation.Invalidated; }
            if (!userConfirmed) return MsiUninstallDispatchReservation.NotConfirmed;
            if (sourceGeneration != SourceGeneration)
            { InvalidateBeforeDispatch(); return MsiUninstallDispatchReservation.SourceChanged; }
            if (!MsiUninstallRequestPolicy.ValidUtc(nowUtc) || nowUtc < PreparedUtc || nowUtc >= ExpiresUtc)
            { InvalidateBeforeDispatch(); return MsiUninstallDispatchReservation.Stale; }
            if (token.IsCancellationRequested)
            { InvalidateBeforeDispatch(); return MsiUninstallDispatchReservation.Cancelled; }
            if (SelectedProductScope is { } selected)
            {
                var refusal = MsiUninstallDispatchReservation.Invalidated;
                bool committed = selected.TryCommitDispatch(() =>
                {
                    // The shared selector lifecycle lock linearizes direct invalidation and
                    // consumes this selector once across all intents built from its facts.
                    if (token.IsCancellationRequested)
                    { refusal = MsiUninstallDispatchReservation.Cancelled; InvalidateBeforeDispatch(); return false; }
                    var actualNow = DateTimeOffset.UtcNow;
                    if (!MsiUninstallRequestPolicy.ValidUtc(actualNow) || actualNow < PreparedUtc || actualNow >= ExpiresUtc)
                    { refusal = MsiUninstallDispatchReservation.Stale; InvalidateBeforeDispatch(); return false; }
                    if (sourceGeneration != SourceGeneration || selected.SourceGeneration != SourceGeneration)
                    { refusal = MsiUninstallDispatchReservation.SourceChanged; InvalidateBeforeDispatch(); return false; }
                    if (token.IsCancellationRequested)
                    { refusal = MsiUninstallDispatchReservation.Cancelled; InvalidateBeforeDispatch(); return false; }
                    reserved = true; return true;
                });
                if (!committed) { InvalidateBeforeDispatch(); return refusal; }
                return MsiUninstallDispatchReservation.Reserved;
            }
            reserved = true;
            return MsiUninstallDispatchReservation.Reserved;
        }
    }
    private void InvalidateBeforeDispatch()
    {
        invalidated = true;
        SelectedProductScope?.Invalidate();
    }
}

public sealed class MsiUninstallRequestPreview
{
    internal MsiUninstallRequestPreview(MsiUninstallRequestIntent intent) => Intent = intent;
    public MsiUninstallRequestIntent Intent { get; }
    public string ProductCode => Intent.ProductCode;
    public string RegistrationIdentity => Intent.RegistrationIdentity;
    public string Name => Intent.Name;
    public string Version => Intent.Version;
    public string Publisher => Intent.Publisher;
    public string Source => Intent.Source;
    public MsiRegistrationContext Context => Intent.Context;
    public DateTimeOffset PreparedUtc => Intent.PreparedUtc;
    public DateTimeOffset ExpiresUtc => Intent.ExpiresUtc;
    public IReadOnlyList<string> Arguments => Intent.Arguments;
    public string ExactEffect => "Request Windows Installer's registered ProductCode uninstall operation for the selected product, requesting normal/default full Windows Installer UI (no quiet/passive flags) and /norestart. MadClean does not read or execute a registered uninstall command or force-delete application files.";
    public string Limitations => "The context is a unique current-user/machine observation, not a frozen context or immutable package. Windows Installer resolves the ProductCode and controls its package-defined effects, permissions, prompts and cancellation. Displayed publisher is registration metadata, not package authentication. Package-defined file/settings/service changes cannot all be previewed. Client exit and later registration observations remain separate; no rollback, crash recovery, global absence or completed uninstall is inferred. Use Windows Installer's Cancel control if offered after dispatch; otherwise wait for its actual result; MadClean never kills it or forces a restart.";
}

public static class MsiUninstallRequestPolicy
{
    public static CheckResult<MsiUninstallRequestPreview> CreatePreview(MsiActionBindingScope scope,
        CheckResult<MsiActionBindingReport> binding, long sourceGeneration, DateTimeOffset nowUtc,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (scope == null || sourceGeneration < 0 || !ValidUtc(nowUtc))
            return Refused("Select a fresh exact MSI registration; no request preview was prepared.");
        var reasons = ValidateBinding(scope, binding, nowUtc, scope.ValidatedUtc, null, token);
        if (reasons.Count != 0) return Refused(string.Join(" ", reasons));
        var report = binding.Items[0];
        var intent = new MsiUninstallRequestIntent(scope, report.Instances[0].Context, sourceGeneration,
            nowUtc, scope.ValidatedUtc.Add(MsiRegistrationReview.ScopeLifetime));
        return new(scope.SnapshotState == CheckState.Partial ? CheckState.Partial : CheckState.Available,
            "Exact registered-product request preview prepared; explicit confirmation and fresh native checks are still required. No installer started.",
            [new(intent)]);
    }

    public static CheckResult<MsiUninstallRequestPreview> CreatePreview(MsiSelectedProductReadScope scope,
        CheckResult<MsiSelectedProductPreparation> preparation, long sourceGeneration, DateTimeOffset nowUtc,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (scope == null) return Refused("Select an actual exact-product read selector; no request preview was prepared.");
        var reasons = ValidatePreparation(scope, preparation, sourceGeneration, nowUtc, scope.ValidatedUtc, null, token);
        if (reasons.Count != 0) return Refused(string.Join(" ", reasons));
        var report = preparation.Items[0];
        var displayScope = MsiActionBindingScope.From(scope.NativeReadScope);
        var intent = new MsiUninstallRequestIntent(displayScope, report.Instances[0].Context, sourceGeneration,
            nowUtc, scope.ExpiresUtc, scope);
        return new(scope.HistoricalGlobalSnapshotState == CheckState.Partial ? CheckState.Partial : CheckState.Available,
            "Exact-product request preview prepared from fresh target facts. Historical global coverage is unchanged; current global inventory was not read. Explicit confirmation and another actual target recheck are required; no installer started.", [new(intent)]);
    }

    public static IReadOnlyList<string> ValidateFreshPreparation(MsiUninstallRequestIntent intent,
        CheckResult<MsiSelectedProductPreparation> fresh, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (intent == null || intent.SelectedProductScope is not { } selected)
            return ["No exact selected-product request intent is available."];
        var reasons = new List<string>();
        if (intent.IsInvalidated || intent.IsDispatchReserved)
            reasons.Add("The selected-product request was invalidated or already reserved; prepare and review a new request.");
        if (!ValidUtc(nowUtc) || nowUtc < intent.PreparedUtc || nowUtc >= intent.ExpiresUtc)
            reasons.Add("The selected-product request expired or its time is invalid; review it again.");
        if (intent.Scope.ProductCode != selected.ProductCode || intent.Scope.ExpectedContextMask != selected.NativeReadScope.ExpectedContextMask ||
            intent.Scope.SnapshotState != selected.HistoricalGlobalSnapshotState || intent.Scope.ValidatedUtc != selected.ValidatedUtc ||
            intent.Scope.Application != selected.Application || intent.SourceGeneration != selected.SourceGeneration)
            reasons.Add("The original selected registration or generation does not match this request.");
        reasons.AddRange(ValidatePreparation(selected, fresh, intent.SourceGeneration, nowUtc, intent.PreparedUtc, intent.Context, token));
        return reasons.AsReadOnly();
    }

    private static IReadOnlyList<string> ValidatePreparation(MsiSelectedProductReadScope? scope,
        CheckResult<MsiSelectedProductPreparation>? preparation, long generation, DateTimeOffset nowUtc,
        DateTimeOffset minimumObservation, MsiRegistrationContext? expectedContext, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidUtc(nowUtc) || nowUtc > DateTimeOffset.UtcNow || !ValidUtc(minimumObservation) || minimumObservation > nowUtc ||
            !MsiSelectedProductPreparationPolicy.IsFresh(scope, generation, nowUtc))
            return ["The exact selected-product read selector is stale, invalidated, malformed or changed."];
        if (preparation?.Items == null || preparation.Items.Count != 1 || preparation.State is not (CheckState.Available or CheckState.Partial))
            return ["One fresh actual selected-product preparation report is required."];
        var report = preparation.Items[0];
        if (report == null || !ReferenceEquals(report.Scope, scope) || report.State != MsiSelectedProductPreparationState.ObservedMatch ||
            report.TargetCoverageState != CheckState.Available || !report.EnumerationComplete || report.NativeIssueCount != 0 ||
            !ValidUtc(report.CheckedUtc) || report.CheckedUtc < minimumObservation || report.CheckedUtc > nowUtc ||
            report.Lookups == null || report.Lookups.Count != 3 || report.Instances == null || report.Instances.Count != 1)
            return ["The exact selector, complete target coverage and one observed installed context must match this request."];
        foreach (var lookup in report.Lookups)
        {
            token.ThrowIfCancellationRequested();
            if (lookup == null || !ValidUtc(lookup.ObservedUtc) || lookup.ObservedUtc < minimumObservation || lookup.ObservedUtc > report.CheckedUtc)
                return ["Every actual registry-source lookup must be fresh for this request."];
            if (lookup.Registration?.RegistrationEvidence is { } evidence &&
                (!ValidUtc(evidence.ObservedUtc) || evidence.ObservedUtc < minimumObservation || evidence.ObservedUtc != lookup.ObservedUtc))
                return ["The actual selected registration evidence must be fresh for this request."];
        }
        var row = report.Instances[0];
        if (row == null || !ValidUtc(row.ObservedUtc) || row.ObservedUtc < minimumObservation || row.ObservedUtc > report.CheckedUtc ||
            expectedContext is { } context && row.Context != context)
            return ["The unique native installed context must be unchanged and freshly observed for this request."];
        // Recompute from frozen primitive observations. A caller-supplied success label or Reason
        // is not accepted as evidence and historical Partial is never upgraded to global completeness.
        var reassessed = MsiSelectedProductPreparationPolicy.Assess(scope!, report.Lookups, report.Instances,
            report.EnumerationComplete, report.NativeIssueCount, generation, report.CheckedUtc, token);
        if (reassessed.Items.Count != 1 || reassessed.State != preparation.State || reassessed.Items[0].State != MsiSelectedProductPreparationState.ObservedMatch ||
            reassessed.Items[0].TargetCoverageState != CheckState.Available || reassessed.Items[0].Instances.Count != 1 ||
            reassessed.Items[0].Instances[0].Context != row.Context)
            return ["Actual selected-product facts did not re-establish an unrestricted unique installed match. No request was accepted."];
        token.ThrowIfCancellationRequested();
        return Array.Empty<string>();
    }

    public static IReadOnlyList<string> ValidateFreshBinding(MsiUninstallRequestIntent intent,
        CheckResult<MsiActionBindingReport> fresh, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (intent == null) return ["No exact selected request intent is available."];
        var reasons = new List<string>();
        if (intent.IsInvalidated || intent.IsDispatchReserved)
            reasons.Add("The request was invalidated or already reserved; prepare and review a new request.");
        if (!ValidUtc(nowUtc) || nowUtc < intent.PreparedUtc || nowUtc >= intent.ExpiresUtc)
            reasons.Add("The request preview expired or its observation time is invalid; review it again.");
        reasons.AddRange(ValidateBinding(intent.Scope, fresh, nowUtc, intent.PreparedUtc, intent.Context, token));
        return reasons.AsReadOnly();
    }

    private static IReadOnlyList<string> ValidateBinding(MsiActionBindingScope scope,
        CheckResult<MsiActionBindingReport>? binding, DateTimeOffset nowUtc, DateTimeOffset minimumObservation,
        MsiRegistrationContext? expectedContext, CancellationToken token)
    {
        var reasons = new List<string>();
        if (!ValidUtc(nowUtc) || scope.ValidatedUtc.Offset != TimeSpan.Zero || scope.ValidatedUtc == default ||
            scope.ValidatedUtc > nowUtc || nowUtc - scope.ValidatedUtc >= MsiRegistrationReview.ScopeLifetime ||
            scope.SnapshotState is not (CheckState.Available or CheckState.Partial) ||
            !Guid.TryParseExact(scope.ProductCode, "B", out var code) || code == Guid.Empty ||
            code.ToString("B").ToUpperInvariant() != scope.ProductCode || scope.ExpectedContextMask is not (3 or 4))
            return ["The selected registration scope is stale, malformed or unsupported."];
        token.ThrowIfCancellationRequested();
        var source = MsiRegistrationReview.CreateScope(new(scope.SnapshotState, "Exact selected source", [scope.Application]),
            scope.Application.Identity, nowUtc, token).Items.SingleOrDefault();
        if (source == null || source.ProductCode != scope.ProductCode || source.ExpectedContextMask != scope.ExpectedContextMask ||
            scope.Application.RegistrationEvidence?.NoRemove != false)
            reasons.Add("The selected MSI source or known removal restriction no longer qualifies.");
        if (binding?.Items == null || binding.Items.Count != 1 || binding.State is not (CheckState.Available or CheckState.Partial))
        { reasons.Add("One complete fresh selection/context report is required."); return reasons.AsReadOnly(); }
        var report = binding.Items[0];
        if (report == null || report.State != MsiActionBindingState.Bound || report.NoRemove != false ||
            !report.EnumerationComplete || report.CurrentSnapshotState != CheckState.Available ||
            report.SelectionSnapshotState != scope.SnapshotState || report.ExpectedContextMask != scope.ExpectedContextMask ||
            report.RegistrationIdentity != scope.Application.Identity || report.ProductCode != scope.ProductCode ||
            !ValidUtc(report.CheckedUtc) || report.CheckedUtc < minimumObservation || report.CheckedUtc > nowUtc ||
            report.Instances == null || report.Instances.Count != 1)
        { reasons.Add("Fresh complete ARP coverage, unrestricted source and exactly one installed current-user/machine context are required."); return reasons.AsReadOnly(); }
        var row = report.Instances[0];
        if (row == null || row.ProductCode != scope.ProductCode || !Enum.IsDefined(row.Context) ||
            ((int)row.Context & scope.ExpectedContextMask) == 0 ||
            expectedContext is { } context && row.Context != context || row.ProductState != MsiObservedProductState.Installed ||
            !ValidUtc(row.ObservedUtc) || row.ObservedUtc < minimumObservation || row.ObservedUtc > report.CheckedUtc ||
            !Known(row.Name, MsiRegistrationReview.NameCharacters) || !Known(row.Version, MsiRegistrationReview.VersionCharacters) ||
            !Known(row.Publisher, MsiRegistrationReview.PublisherCharacters) || row.Name != scope.Application.Name ||
            row.Version != scope.Application.Version || row.Publisher != scope.Application.Publisher)
            reasons.Add("The current installed context or selected name/version/publisher changed or is unknown; select and review it again.");
        token.ThrowIfCancellationRequested();
        return reasons.AsReadOnly();
    }

    public static MsiUninstallExitDisposition ClassifyExit(int exitCode) => exitCode switch
    {
        0 => MsiUninstallExitDisposition.SuccessReported,
        1602 => MsiUninstallExitDisposition.CancelledByUser,
        1605 or 1614 => MsiUninstallExitDisposition.ProductNotInstalled,
        1618 => MsiUninstallExitDisposition.InstallerBusy,
        3010 => MsiUninstallExitDisposition.RestartRequired,
        1641 => MsiUninstallExitDisposition.RestartInitiatedUnexpectedly,
        > 0 => MsiUninstallExitDisposition.FailureReported,
        _ => MsiUninstallExitDisposition.Unknown
    };

    internal static bool ValidUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Known(string? value, int bound) => !string.IsNullOrWhiteSpace(value) && value.Length <= bound &&
        !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && ShortcutPathPolicy.HasSafeUnicode(value);
    private static CheckResult<MsiUninstallRequestPreview> Refused(string message) =>
        CheckResult<MsiUninstallRequestPreview>.Empty(CheckState.Unknown, message);
}
