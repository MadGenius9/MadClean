namespace MadClean.Core;

/// <summary>
/// One exact historical request/result and a new independent registration read selector.
/// It cannot repeat an uninstall or establish complete removal or file ownership.
/// </summary>
public sealed class MsiUninstallPostcheckSession
{
    internal MsiUninstallPostcheckSession(MsiUninstallRequestIntent intent, MsiUninstallRequestResult result,
        MsiRegistrationWatchTarget target, MsiRegistrationWatchReadScope scope)
    { Intent = intent; Result = result; Target = target; Scope = scope; }
    public MsiUninstallRequestIntent Intent { get; }
    public MsiUninstallRequestResult Result { get; }
    public MsiRegistrationWatchTarget Target { get; }
    public MsiRegistrationWatchReadScope Scope { get; }
    public CheckState UninstallCompletion => CheckState.Unknown;
    public CheckState LeftoverOwnership => CheckState.Unknown;
    public bool CanUninstall => false;
    public bool CanDelete => false;
}

/// <summary>Pure post-exit read admission and observation consistency, never native-result authentication.</summary>
public static class MsiUninstallPostcheckPolicy
{
    public const string Limits = "Explicit exact-product registration postcheck only. Sequential ARP/MSI reads are non-atomic and cover only their supported sources. " +
        "Client exit and registration absence do not establish complete uninstall, exclusive leftover ownership, rollback or safe file removal. " +
        "The original request result remains historical and unchanged; no uninstall, deletion, elevation or restart is authorized.";

    public static CheckResult<MsiUninstallPostcheckSession> Prepare(MsiUninstallRequestIntent? intent,
        MsiUninstallRequestResult? result, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Bound(intent, result, nowUtc)) return Refused();
        var target = intent!.PostcheckTarget!;
        // The original selected-product scope has already been consumed. It is
        // deliberately not refreshed, revalidated for launch, or reused here.
        var prepared = MsiRegistrationWatchPolicy.Prepare(target, nowUtc, token);
        if (prepared.Items.Count != 1 || prepared.Items[0] is not { } scope ||
            !ReferenceEquals(scope.Target, target) || scope.RequestedUtc < result!.ExitedUtc!.Value)
            return Refused();
        token.ThrowIfCancellationRequested();
        return new(prepared.State, "A fresh independent post-exit registration read was prepared. " + Limits,
            [new(intent, result, target, scope)]);
    }

    // Positive report consistency only. Empty failed/unavailable/unsupported/cancelled
    // reader outcomes remain separate source statuses; they are not absence reports.
    public static bool IsConsistent(MsiUninstallPostcheckSession? session,
        CheckResult<MsiRegistrationWatchReport>? result, DateTimeOffset receivedUtc)
    {
        if (session == null || !Bound(session.Intent, session.Result, receivedUtc) ||
            !ReferenceEquals(session.Target, session.Intent.PostcheckTarget) || !ReferenceEquals(session.Scope.Target, session.Target) ||
            session.Scope.RequestedUtc < session.Result.ExitedUtc!.Value ||
            !MsiRegistrationWatchPolicy.IsConsistent(session.Scope, result, receivedUtc)) return false;
        var report = result!.Items[0];
        return report.QueryStartedUtc >= session.Result.ExitedUtc.Value && report.QueryStartedUtc >= session.Scope.RequestedUtc &&
            report.CheckedUtc <= receivedUtc;
    }

    private static bool Bound(MsiUninstallRequestIntent? intent, MsiUninstallRequestResult? result, DateTimeOffset nowUtc)
    {
        if (intent?.PostcheckTarget is not { } target || intent.SelectedProductScope is not { } original ||
            result is not { State: MsiUninstallRequestState.ClientExited, ClientStartObserved: true, ExitedUtc: { } exit } ||
            !Utc(nowUtc) || nowUtc > DateTimeOffset.UtcNow || exit > nowUtc || !intent.IsDispatchReserved ||
            !MsiUninstallResultPolicy.IsConsistent(intent, result, nowUtc) || target.TargetId == Guid.Empty ||
            target.ProductCode != intent.ProductCode || target.RegistrationIdentity != intent.RegistrationIdentity ||
            target.Name != intent.Name || target.Version != intent.Version || target.Publisher != intent.Publisher ||
            target.OriginalContext != intent.Context || !Enum.IsDefined(target.OriginalContext) ||
            target.OriginalSource != original.OriginalSource || !Enum.IsDefined(target.OriginalSource) ||
            target.HistoricalCoverage != intent.Scope.SnapshotState || target.HistoricalCoverage is not (CheckState.Available or CheckState.Partial) ||
            target.HistoricalNoRemove != intent.Scope.Application.RegistrationEvidence?.NoRemove ||
            !Utc(target.CapturedUtc) || target.CapturedUtc != intent.PreparedUtc || target.CapturedUtc > result.RequestedUtc ||
            target.CapturedUtc < original.ValidatedUtc ||
            original.ProductCode != intent.ProductCode || original.Application.Identity != intent.RegistrationIdentity ||
            ((int)target.OriginalContext & original.NativeReadScope.ExpectedContextMask) == 0 ||
            target.ProductCode is not { Length: 38 } || !Guid.TryParseExact(target.ProductCode, "B", out var code) ||
            code == Guid.Empty || code.ToString("B").ToUpperInvariant() != target.ProductCode)
            return false;
        // Source/context are copied historical observations, not assumptions derived
        // from an ARP hive, a current selection, or a returned numeric exit code.
        return intent.Source == MsiSelectedProductPreparationPolicy.SourceLabel(target.OriginalSource) &&
            string.Equals(target.RegistrationIdentity,
                MsiSelectedProductPreparationPolicy.RegistrationIdentity(target.OriginalSource, target.ProductCode), StringComparison.OrdinalIgnoreCase);
    }
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static CheckResult<MsiUninstallPostcheckSession> Refused() => CheckResult<MsiUninstallPostcheckSession>.Empty(CheckState.Unknown,
        "An actual accepted exited selected-product request with its own historical target is required; no registration postcheck was prepared. " + Limits);
}
