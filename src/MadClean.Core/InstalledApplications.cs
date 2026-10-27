namespace MadClean.Core;

public sealed record InstalledApplication(string Identity, string Name, string Version, string Publisher,
    string Source, string? InstallLocation, string UninstallSupport, InstalledAppRegistrationEvidence? RegistrationEvidence = null);

public sealed record LeftoverCandidate(string ApplicationIdentity, string Path, string Evidence, string Caution);

public interface IInstalledApplicationInventory
{
    Task<CheckResult<InstalledApplication>> ScanAsync(CancellationToken token);
}

/// <summary>Registration hints only; no command text or executable is accepted by this policy.</summary>
public static class InstalledApplicationSupport
{
    public static string Describe(bool windowsInstaller, bool hasRegisteredUninstaller, bool removalRestricted)
    {
        if (removalRestricted) return "Removal restricted by registration; action unavailable";
        if (windowsInstaller) return "Windows Installer; action queued";
        return hasRegisteredUninstaller ? "Registered uninstaller; action queued" : "Unknown";
    }
}

/// <summary>Validates local Windows directory syntax without probing a filesystem or expanding metadata.</summary>
public static class InstalledApplicationPaths
{
    public static string? NormalizeLocalDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8_192 || value != value.Trim()) return null;
        string path = value.Replace('/', '\\');
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) ||
            path[2..].IndexOfAny([':', '*', '?', '"', '<', '>', '|', '%']) >= 0)
            return null;

        var segments = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        // A drive root is too broad to be an application's recorded directory. UNC/device paths
        // cannot reach this branch because they do not start with a local drive designator.
        if (segments.Length == 0) return null;
        foreach (string segment in segments)
        {
            if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') || IsDeviceName(segment))
                return null;
        }
        return char.ToUpperInvariant(path[0]) + ":\\" + string.Join('\\', segments);
    }

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}

/// <summary>
/// Compares complete, explicitly captured in-session inventories. It never scans a tree or authorizes
/// deletion: only prior validated install directories can be passed to the supplied existence probe.
/// </summary>
public static class LeftoverReview
{
    private const int MaximumInventoryEntries = 20_000;
    private const int MaximumProbes = 1_000;
    private const int MaximumCandidates = 1_000;
    private const string Evidence = "registration no longer present; prior install path exists";
    private const string BaseCaution = "Review only; not proven junk. Registration disappearance does not prove an uninstall completed. " +
        "This directory may contain shared files, user data, saves or settings. No deletion or cleanup is authorized.";
    private static readonly HashSet<string> ProtectedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Users", "Documents", "Downloads", "Desktop", "Pictures", "Music", "Videos", "AppData", "Profiles",
        "Saved Games", "Saves", "SaveGames", "Settings", "Config", "Credentials", "Vault", "Windows"
    };
    private static readonly HashSet<string> BroadRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "ProgramData", "Applications", "Apps", "Games"
    };

    public static CheckResult<LeftoverCandidate> Compare(IReadOnlyList<InstalledApplication> before,
        IReadOnlyList<InstalledApplication> after, Func<string, bool?> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (before.Count > MaximumInventoryEntries || after.Count > MaximumInventoryEntries)
            return CheckResult<LeftoverCandidate>.Empty(CheckState.Partial,
                "The inventory comparison exceeded its safety limit. No paths were probed and no absence conclusion was made.");
        if (before.Any(app => string.IsNullOrWhiteSpace(app.Identity)) || after.Any(app => string.IsNullOrWhiteSpace(app.Identity)))
            return CheckResult<LeftoverCandidate>.Empty(CheckState.Partial,
                "An inventory contained an invalid registration identity. No paths were probed and no absence conclusion was made.");

        var registered = after.Select(app => app.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownLocations = before.Concat(after)
            .Select(app => (app.Identity, Path: InstalledApplicationPaths.NormalizeLocalDirectory(app.InstallLocation)))
            .Where(item => item.Path != null).ToArray();
        var probes = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        List<LeftoverCandidate> candidates = [];
        int missing = 0, unknown = 0, unsupported = 0;
        bool limited = false;
        foreach (var group in before.GroupBy(app => app.Identity, StringComparer.OrdinalIgnoreCase))
        {
            if (registered.Contains(group.Key)) continue;
            missing++;
            var priorPaths = group.Select(app => InstalledApplicationPaths.NormalizeLocalDirectory(app.InstallLocation))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (priorPaths.Length != 1 || priorPaths[0] is not string path)
            {
                unsupported++;
                continue;
            }
            if (candidates.Count == MaximumCandidates)
            {
                limited = true;
                break;
            }
            if (!probes.TryGetValue(path, out bool? exists))
            {
                if (probes.Count == MaximumProbes)
                {
                    limited = true;
                    break;
                }
                try { exists = directoryExists(path); }
                catch (OperationCanceledException)
                {
                    return new(CheckState.Cancelled,
                        "Leftover review cancelled. Completed observations remain review-only; no cleanup is authorized.", candidates);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
                { exists = null; }
                probes.Add(path, exists);
            }
            if (exists is null) { unknown++; continue; }
            if (!exists.Value) continue;

            string caution = BaseCaution;
            if (knownLocations.Any(other => !other.Identity.Equals(group.Key, StringComparison.OrdinalIgnoreCase) && Overlaps(path, other.Path!)))
                caution += " Protected/unverified: this path overlaps another application's recorded install location.";
            var segments = path[3..].Split('\\');
            if (segments.Any(ProtectedSegments.Contains))
                caution += " Protected: this is a user, save, settings, profile or system path; it must not be treated as a cleaning target.";
            if (segments.Length == 1 && BroadRootNames.Contains(segments[0]))
                caution += " Protected: this is a broad shared installation root, not an isolated application directory.";
            candidates.Add(new(group.Key, path, Evidence, caution));
        }
        bool partial = unknown > 0 || unsupported > 0 || limited;
        return new(partial ? CheckState.Partial : CheckState.Available,
            $"Compared complete in-session registrations: {missing} no longer present; {candidates.Count} surviving prior install paths for review. " +
            $"{unknown} existence checks unknown; {unsupported} registrations had no unambiguous supported local install path. " +
            (limited ? "Safety limit reached; review is incomplete. " : "") +
            "Only recorded install directories were probed. Registration disappearance is not proof of completed uninstallation; this is not a filesystem sweep or a junk verdict.", candidates);
    }

    private static bool Overlaps(string first, string second) => first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith(second + "\\", StringComparison.OrdinalIgnoreCase) || second.StartsWith(first + "\\", StringComparison.OrdinalIgnoreCase);
}
