namespace MadClean.Windows;

// Development test-source fence only. It does not authenticate a runner, establish
// an owned package, or enable the production dispatcher. All existing native,
// repository, source/run and held-scope checks remain independently required.
internal static class MsiQualificationSourceProfile
{
    internal const string FeatureRef = "refs/heads/feat/phase-one-foundation";
    internal const string RecoveryRef = "refs/heads/recovery/validated-20261112";
    internal const string Job = "windows-msi-request";

    internal static bool IsCurrentSourceAllowed(bool actualProductionDispatchValidated) => IsAllowed(
        Environment.GetEnvironmentVariable("GITHUB_REF"),
        Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME"),
        Environment.GetEnvironmentVariable("GITHUB_JOB"),
        Environment.GetEnvironmentVariable("MADCLEAN_MSI_BACKEND_ONLY_VALIDATION"),
        actualProductionDispatchValidated);

    internal static bool IsAllowed(string? sourceRef, string? eventName, string? job,
        string? recoveryBackendOnlyOptIn, bool actualProductionDispatchValidated)
    {
        if (job != Job) return false;
        // Preserve the legacy feature-branch profile. Its public-cycle eligibility
        // remains independently controlled by the actual production source guard.
        if (sourceRef == FeatureRef) return eventName is "push" or "workflow_dispatch";

        // Recovery is a separate, explicit manual backend-only test route. A push,
        // similar ref, truthy flag, wrong job or later public activation refuses it.
        return sourceRef == RecoveryRef && eventName == "workflow_dispatch" &&
            recoveryBackendOnlyOptIn == "1" && !actualProductionDispatchValidated;
    }
}
