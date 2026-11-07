namespace MadClean.Core;

public enum MadCleanReleaseSourceChoice { Deployment, OfficialRepository, Disabled }

/// <summary>Session-only selection of the existing pinned source; performs no IO or release check.</summary>
public static class MadCleanReleaseSourcePolicy
{
    public static MadCleanReleaseConfiguration Resolve(MadCleanReleaseConfiguration deployment,
        MadCleanReleaseSourceChoice choice)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return choice switch
        {
            MadCleanReleaseSourceChoice.OfficialRepository => new(MadCleanReleaseSource.TrustedRepository, null),
            MadCleanReleaseSourceChoice.Disabled => new(null, null),
            MadCleanReleaseSourceChoice.Deployment => ResolveDeployment(deployment),
            _ => new(null, "The release-source selection is unsupported. No release check was made.")
        };
    }

    private static MadCleanReleaseConfiguration ResolveDeployment(MadCleanReleaseConfiguration deployment)
    {
        if (deployment.Error != null)
            return InvalidDeployment();
        if (deployment.Repository == null)
            return new(null, null);
        return string.Equals(deployment.Repository, MadCleanReleaseSource.TrustedRepository, StringComparison.OrdinalIgnoreCase)
            ? new(MadCleanReleaseSource.TrustedRepository, null) : InvalidDeployment();
    }

    private static MadCleanReleaseConfiguration InvalidDeployment() => new(null,
        "The deployment release configuration is invalid or unavailable. Review madclean-updates.json, or explicitly choose the pinned source for this session. No release check was made.");
}
