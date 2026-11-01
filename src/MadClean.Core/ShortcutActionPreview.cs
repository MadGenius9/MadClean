namespace MadClean.Core;

public enum ShortcutActionReviewState { Preview, Blocked, Unknown }
public enum ShortcutProtectionState { Unknown, Protected, OutsideKnownProtectedFolders }
public enum ShortcutReadConsistency { Unknown, ObservedConsistent }
public sealed record ShortcutFileEvidence(string ShortcutPath, ulong VolumeSerialNumber, Guid FileId,
    long Bytes, uint LinkCount, string Sha256, DateTimeOffset ReadStartedUtc, DateTimeOffset ReadCompletedUtc,
    ShortcutReadConsistency Consistency)
{
    public ShortcutDaclObservation? Dacl { get; init; }
}
public sealed record ShortcutActionPreview(ShortcutObservation Prior, ShortcutObservation? Current,
    ShortcutFileEvidence? Evidence, ShortcutProtectionState Protection, string ProtectionReason,
    ShortcutActionReviewState ReviewState, CheckState SourceCoverageState, string Coverage)
{
    public bool CanMove => false;
    public string DaclAssessment => Evidence?.Dacl is { } dacl
        ? $"Recorded DACL facts: {dacl.State}. Effective access, ownership and privacy remain unverified."
        : "NotChecked; no DACL observation. Effective access and ownership remain unverified.";
}
public interface IShortcutActionPreview
{
    Task<CheckResult<ShortcutActionPreview>> InspectAsync(ShortcutRecheckScope scope, CancellationToken token);
}

/// <summary>Documentary evidence only: no move permission, continuity claim, durable lease or action token.</summary>
public static class ShortcutActionPreviewPolicy
{
    public const long MaximumBytes = 1_048_576;
    public static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(2);

    public static CheckResult<ShortcutActionPreview> Create(ShortcutRecheckScope scope, CheckResult<ShortcutObservation>? currentResult,
        ShortcutFileEvidence? evidence, ShortcutProtectionState protection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        token.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        const string coverage = "Prior file identity continuity is unknown; the historical observation does not identify the current file object. " +
            "Read consistency is observed for one bounded inspection, not immutability or later path stability. " +
            "Optional DACL facts describe one security-descriptor query only; effective access, ownership and privacy remain unverified. " +
            "No lease survives this result, and no move, deletion, backup or action permission is granted. " +
            "A missing target is not proof of obsolete software or disposable shortcut data.";
        CheckResult<ShortcutActionPreview> Result(ShortcutActionReviewState review, string reason,
            ShortcutObservation? acceptedCurrent = null, ShortcutFileEvidence? acceptedEvidence = null,
            CheckState? queryFailure = null)
        {
            token.ThrowIfCancellationRequested();
            var state = review == ShortcutActionReviewState.Preview
                ? scope.SourceCoverageState == CheckState.Partial || currentResult?.State == CheckState.Partial ? CheckState.Partial : CheckState.Available
                : review == ShortcutActionReviewState.Blocked ? CheckState.Unavailable : queryFailure ?? CheckState.Unknown;
            return new(state, reason, [new(scope.PriorObservation with { }, acceptedCurrent, acceptedEvidence,
                Enum.IsDefined(protection) ? protection : ShortcutProtectionState.Unknown,
                protection switch
                {
                    ShortcutProtectionState.Protected => "Selected path overlaps a known protected folder; content inspection is blocked.",
                    ShortcutProtectionState.OutsideKnownProtectedFolders => "No overlap with the checked known protected folders was observed; ownership and removal safety remain unknown.",
                    _ => "Known-folder protection could not be established; content inspection is blocked."
                }, review, scope.SourceCoverageState, coverage)]);
        }
        if (!Utc(scope.ValidatedUtc) || scope.ValidatedUtc > now || now - scope.ValidatedUtc > EvidenceLifetime ||
            scope.SourceCoverageState is not (CheckState.Available or CheckState.Partial))
            return Result(ShortcutActionReviewState.Unknown, "Historical selection is expired or has invalid provenance; refresh and select it again.");
        if (protection == ShortcutProtectionState.Protected)
            return Result(ShortcutActionReviewState.Blocked, "Known-folder protection blocks this read-only content preview.");
        if (protection != ShortcutProtectionState.OutsideKnownProtectedFolders)
            return Result(ShortcutActionReviewState.Unknown, "Known-folder protection is unknown; no file evidence was accepted.");
        if (currentResult == null || currentResult.State is not (CheckState.Available or CheckState.Partial) ||
            currentResult.Items == null || currentResult.Items.Count != 1)
            return Result(ShortcutActionReviewState.Unknown, "A unique completed fresh metadata query was not established; no prior target state was reused.",
                queryFailure: currentResult?.State is CheckState.Failed or CheckState.Cancelled or CheckState.Unavailable or CheckState.Unsupported
                    ? currentResult.State : null);
        var current = currentResult.Items[0];
        if (current == null || !SamePath(current.ShortcutPath, scope.ShortcutPath) ||
            current.Source != ShortcutTargetClassifier.Source || !Enum.IsDefined(current.State) ||
            !Utc(current.CheckedAt) || current.CheckedAt < scope.ValidatedUtc || current.CheckedAt > now ||
            now - current.CheckedAt > EvidenceLifetime || !SafeText(current.Reason, 4096, false) ||
            !SafeText(current.TargetPath, 32768, true))
            return Result(ShortcutActionReviewState.Unknown, "Fresh shortcut metadata did not establish the exact selected path/source/time.");
        if (current.State is ShortcutTargetState.TargetExists or ShortcutTargetState.Unsupported)
            return Result(ShortcutActionReviewState.Blocked, "The current target exists or is unsupported; no missing-target review preview was established.", current with { });
        if (current.State != ShortcutTargetState.LocalTargetMissing)
            return Result(ShortcutActionReviewState.Unknown, "Current target status is unresolved; prior missing-target observations were not reused.", current with { });
        if (!SamePath(current.TargetPath, current.TargetPath))
            return Result(ShortcutActionReviewState.Unknown, "Missing-target metadata has unsupported target syntax; no file evidence was accepted.");
        if (evidence == null || !SamePath(evidence.ShortcutPath, scope.ShortcutPath) ||
            evidence.VolumeSerialNumber == 0 || evidence.FileId == Guid.Empty || evidence.Bytes is <= 0 or > MaximumBytes ||
            evidence.LinkCount != 1 || evidence.Sha256 == null || evidence.Sha256.Length != 64 ||
            !evidence.Sha256.All(char.IsAsciiHexDigit) || evidence.Consistency != ShortcutReadConsistency.ObservedConsistent ||
            !Utc(evidence.ReadStartedUtc) || !Utc(evidence.ReadCompletedUtc) ||
            evidence.ReadStartedUtc < scope.ValidatedUtc || evidence.ReadStartedUtc > current.CheckedAt ||
            current.CheckedAt > evidence.ReadCompletedUtc || evidence.ReadCompletedUtc > now ||
            now - evidence.ReadStartedUtc > EvidenceLifetime)
            return Result(ShortcutActionReviewState.Unknown, "Bounded current file identity/hash/time/link evidence was not established; no action permission exists.", current with { });
        return Result(ShortcutActionReviewState.Preview,
            "Read-only documentary preview established for one current missing-target shortcut. Moving remains disabled.",
            current with { }, evidence with { Dacl = ShortcutDaclObservationPolicy.Normalize(evidence.Dacl,
                evidence.ReadStartedUtc, evidence.ReadCompletedUtc, token) });
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool SamePath(string? path, string expected) => path != null &&
        string.Equals(path, ShortcutPathPolicy.NormalizeLocalPath(path, false), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(path, expected, StringComparison.OrdinalIgnoreCase);
    private static bool SafeText(string? value, int maximum, bool empty) => value != null && value.Length <= maximum &&
        (empty || !string.IsNullOrWhiteSpace(value)) && ShortcutPathPolicy.HasSafeUnicode(value);
}
