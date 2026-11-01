namespace MadClean.Core;

public enum ShortcutDaclReason
{
    NotChecked, Observed, NativeQueryFailed, MetadataInvalid, DescriptorReleaseUnconfirmed, Unsupported, Cancelled
}
public enum ShortcutDescriptorReleaseState { NotAttempted, Released, Unconfirmed }

/// <summary>Aggregate descriptor facts only; never effective access, ownership, privacy or action permission.</summary>
public sealed record ShortcutDaclObservation(CheckState State, ShortcutDaclReason Reason, uint? NativeResult,
    DateTimeOffset? QueryStartedUtc, DateTimeOffset? QueryCompletedUtc, bool? DaclPresent, bool? NullDacl,
    bool? DaclProtected, bool? DaclDefaulted, int? AceCount, int? AclBytes, int? Revision,
    ShortcutDescriptorReleaseState DescriptorRelease, uint? DescriptorReleaseError);

public static class ShortcutDaclObservationPolicy
{
    // Application inspection budget, not the Windows maximum ACE count.
    public const int MaximumAceCount = 4096;
    public const int MinimumAclBytes = 8;
    public const int MaximumAclBytes = 65535;

    public static ShortcutDaclObservation? Normalize(ShortcutDaclObservation? observation,
        DateTimeOffset evidenceStartedUtc, DateTimeOffset evidenceCompletedUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (observation == null) return null;
        var now = DateTimeOffset.UtcNow;
        bool intervalValid = Utc(evidenceStartedUtc) && Utc(evidenceCompletedUtc) &&
            evidenceStartedUtc <= evidenceCompletedUtc && evidenceCompletedUtc <= now;
        bool queryValid = intervalValid && observation.QueryStartedUtc is { } start && observation.QueryCompletedUtc is { } end &&
            Utc(start) && Utc(end) && evidenceStartedUtc <= start && start <= end && end <= evidenceCompletedUtc;
        bool notQueried = observation.QueryStartedUtc == null && observation.QueryCompletedUtc == null &&
            observation.NativeResult == null && observation.DescriptorRelease == ShortcutDescriptorReleaseState.NotAttempted &&
            observation.DescriptorReleaseError == null;
        var release = Enum.IsDefined(observation.DescriptorRelease)
            ? observation.DescriptorRelease : ShortcutDescriptorReleaseState.Unconfirmed;
        ShortcutDaclObservation WithoutFacts(CheckState state, ShortcutDaclReason reason) => new(state, reason,
            observation.NativeResult, queryValid ? observation.QueryStartedUtc : null,
            queryValid ? observation.QueryCompletedUtc : null, null, null, null, null, null, null, null,
            release, observation.DescriptorReleaseError);
        if (!intervalValid || (!queryValid && !notQueried) || !Enum.IsDefined(observation.State) || !Enum.IsDefined(observation.Reason) ||
            !Enum.IsDefined(observation.DescriptorRelease))
            return WithoutFacts(CheckState.Unknown, ShortcutDaclReason.MetadataInvalid);
        if (observation.State is CheckState.Unknown or CheckState.Unavailable or CheckState.Unsupported or CheckState.Failed or CheckState.Cancelled)
            return WithoutFacts(observation.State,
                observation.Reason == ShortcutDaclReason.Observed ||
                (observation.Reason == ShortcutDaclReason.NotChecked && !notQueried)
                    ? ShortcutDaclReason.MetadataInvalid : observation.Reason);
        if (!queryValid || observation.State != CheckState.Available || observation.Reason != ShortcutDaclReason.Observed || observation.NativeResult != 0)
            return WithoutFacts(CheckState.Unknown, ShortcutDaclReason.MetadataInvalid);
        if (release != ShortcutDescriptorReleaseState.Released)
            return WithoutFacts(CheckState.Unknown, ShortcutDaclReason.DescriptorReleaseUnconfirmed);
        if (observation.DescriptorReleaseError.HasValue || !observation.DaclPresent.HasValue || !observation.DaclProtected.HasValue)
            return WithoutFacts(CheckState.Unknown, ShortcutDaclReason.MetadataInvalid);
        bool shapeValid;
        if (observation.DaclPresent == false)
            shapeValid = observation.NullDacl == null && observation.DaclDefaulted == null && NoAclFacts(observation);
        else if (observation.NullDacl == true)
            shapeValid = observation.DaclDefaulted.HasValue && NoAclFacts(observation);
        else
            shapeValid = observation.NullDacl == false && observation.DaclDefaulted.HasValue &&
                observation.AceCount is >= 0 and <= MaximumAceCount &&
                observation.AclBytes is >= MinimumAclBytes and <= MaximumAclBytes &&
                observation.Revision is 2 or 4 && observation.AclBytes >= MinimumAclBytes + 4 * observation.AceCount;
        token.ThrowIfCancellationRequested();
        return shapeValid ? observation with { } : WithoutFacts(CheckState.Unknown, ShortcutDaclReason.MetadataInvalid);
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool NoAclFacts(ShortcutDaclObservation observation) =>
        observation.AceCount == null && observation.AclBytes == null && observation.Revision == null;
}
