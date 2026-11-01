namespace MadClean.Core;

public enum DuplicatePairReadStage
{
    NotStarted, ScopeValidation, KnownFolderExclusions, RegistrationExclusions, SelectedFiles, Completed, Unknown
}
public enum DuplicatePairReadReason
{
    None, ScopeExpired, ScopeUnsupported, KnownFolderMissing, KnownFolderUnsupported,
    RegistrationSourceUnavailable, RegistrationEnumerationInvalid, RegistrationEntryUnavailable,
    RegistrationLocationInvalid, RegistrationLocationUnsupported, ExclusionLimit,
    SelectedRootProtected, ContentNotEstablished
}

/// <summary>Minimized observed diagnostics only; no field is file protection or action permission.</summary>
public sealed record DuplicatePairReadDiagnostic(DuplicatePairReadStage Stage, DuplicatePairReadReason Reason,
    int? RegistryNativeResult, int? KnownFoldersChecked, int? RegistrationKeysVisited,
    int? RegisteredLocationsAccepted, int? ExclusionIssues);

public static class DuplicatePairReadDiagnosticPolicy
{
    public const int MaximumKnownFolders = 32;
    public const int MaximumRegistrationKeys = 10001;
    public const int MaximumRegisteredLocations = 2048;
    public const int MaximumExclusionIssues = 10020;

    public static DuplicatePairReadDiagnostic Normalize(DuplicatePairReadDiagnostic? observation)
    {
        if (observation == null || !Enum.IsDefined(observation.Stage) || !Enum.IsDefined(observation.Reason) ||
            !observation.KnownFoldersChecked.HasValue || !observation.RegistrationKeysVisited.HasValue ||
            !observation.RegisteredLocationsAccepted.HasValue || !observation.ExclusionIssues.HasValue ||
            observation.KnownFoldersChecked is < 0 or > MaximumKnownFolders ||
            observation.RegistrationKeysVisited is < 0 or > MaximumRegistrationKeys ||
            observation.RegisteredLocationsAccepted is < 0 or > MaximumRegisteredLocations ||
            observation.ExclusionIssues is < 0 or > MaximumExclusionIssues)
        {
            // Missing/invalid observations say nothing about whether work actually occurred.
            return new(DuplicatePairReadStage.Unknown, DuplicatePairReadReason.ContentNotEstablished,
                null, null, null, null, null);
        }
        // Native result zero is retained only if supplied by the observing adapter. Null is never
        // converted into a success result, and native codes are not interpreted as permission.
        return observation with { };
    }
}
