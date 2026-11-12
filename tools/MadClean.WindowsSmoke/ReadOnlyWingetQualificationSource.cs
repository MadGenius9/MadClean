namespace MadClean.WindowsSmoke;

/// <summary>
/// Exact development source-ref fence for the existing read-only WinGet qualifications.
/// Matching grants no native action permission and does not authenticate the runner or source.
/// </summary>
internal static class ReadOnlyWingetQualificationSource
{
    internal static bool IsAllowedRef(string? sourceRef) => sourceRef is
        "refs/heads/feat/phase-one-foundation" or "refs/heads/recovery/validated-20261112";
}
