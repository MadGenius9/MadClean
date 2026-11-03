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
        long sourceGeneration, DateTimeOffset preparedUtc, DateTimeOffset expiresUtc)
    {
        Scope = scope; Context = context; SourceGeneration = sourceGeneration;
        PreparedUtc = preparedUtc; ExpiresUtc = expiresUtc; RequestId = Guid.NewGuid();
        Arguments = Array.AsReadOnly(new[] { "/x", scope.ProductCode, "/norestart" });
    }

    public Guid RequestId { get; }
    public MsiActionBindingScope Scope { get; }
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
    public bool IsInvalidated { get { lock (dispatchLock) return invalidated; } }
    public bool IsDispatchReserved { get { lock (dispatchLock) return reserved; } }

    /// <summary>Invalidation cannot revoke a request whose dispatch boundary already won.</summary>
    public void Invalidate()
    {
        lock (dispatchLock) { if (!reserved) invalidated = true; }
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
            if (invalidated) return MsiUninstallDispatchReservation.Invalidated;
            if (!userConfirmed) return MsiUninstallDispatchReservation.NotConfirmed;
            if (sourceGeneration != SourceGeneration)
            { invalidated = true; return MsiUninstallDispatchReservation.SourceChanged; }
            if (!MsiUninstallRequestPolicy.ValidUtc(nowUtc) || nowUtc < PreparedUtc || nowUtc >= ExpiresUtc)
            { invalidated = true; return MsiUninstallDispatchReservation.Stale; }
            if (token.IsCancellationRequested)
            { invalidated = true; return MsiUninstallDispatchReservation.Cancelled; }
            reserved = true;
            return MsiUninstallDispatchReservation.Reserved;
        }
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
    public string ExactEffect => "Request Windows Installer's registered ProductCode uninstall operation for the selected product, using visible installer UI and /norestart. MadClean does not read or execute a registered uninstall command or force-delete application files.";
    public string Limitations => "The context is a unique current-user/machine observation, not a frozen context or immutable package. Windows Installer resolves the ProductCode and controls its package-defined effects, permissions, prompts and cancellation. Displayed publisher is registration metadata, not package authentication. Package-defined file/settings/service changes cannot all be previewed. Client exit and later registration observations remain separate; no rollback, crash recovery, global absence or completed uninstall is inferred. Use Windows Installer's Cancel control after dispatch; MadClean never kills it or forces a restart.";
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
