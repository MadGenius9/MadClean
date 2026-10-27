using System.Globalization;

namespace MadClean.Core;

public sealed record ShortcutStoredMetadata(string? RawTarget, bool HasItemIdList = false,
    bool IsAdvertised = false, bool HasArguments = false, bool HasEnvironmentMetadata = false,
    bool HasUnsupportedActivation = false, string? MetadataProblem = null);

public enum ShortcutProbeState { Exists, FileNotFound, PathNotFound, Unknown }

/// <summary>Missing evidence is valid only after the producer verifies both local volume and parent ancestry.</summary>
public sealed record ShortcutTargetProbe(ShortcutProbeState State, string Reason,
    bool FixedLocalVolumeAvailable = false, bool ParentChainValidated = false);

public static class ShortcutTargetClassifier
{
    public const string Source = "Read-only Windows Shell link raw metadata; guarded local native attributes (no resolution or launch)";
    public const int MaximumRawTargetCharacters = 258;

    public static ShortcutObservation Classify(string shortcutPath, ShortcutStoredMetadata metadata,
        Func<string, ShortcutTargetProbe> probe, DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(probe);
        string target = metadata.RawTarget ?? "";
        ShortcutObservation Result(ShortcutTargetState state, string reason) =>
            new(shortcutPath, target, state, reason, Source, checkedAt);

        if (metadata.MetadataProblem != null)
            return Result(ShortcutTargetState.UnresolvedMetadata, SafeReason(metadata.MetadataProblem));
        if (metadata.IsAdvertised)
            return Result(ShortcutTargetState.Unsupported, "Advertised installer metadata is present. No installer activation, repair, resolution or target probe was attempted.");
        if (metadata.HasUnsupportedActivation)
            return Result(ShortcutTargetState.Unsupported, "Extended target or activation metadata requires a separate supported contract; no activation or target probe was attempted.");
        if (string.IsNullOrWhiteSpace(target))
            return Result(metadata.HasItemIdList ? ShortcutTargetState.Unsupported : ShortcutTargetState.UnresolvedMetadata,
                metadata.HasItemIdList ? "A Shell item/namespace identifier exists without a supported stored filesystem path; it was not bound or resolved." :
                "No stored target path was returned. Empty or malformed metadata is not proof of an obsolete shortcut.");
        if (target.Length > MaximumRawTargetCharacters)
            return Result(ShortcutTargetState.UnresolvedMetadata, "Stored target may exceed or meet the Shell path-buffer limit; truncation cannot be excluded, so no target probe was attempted.");
        if (target.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) || target.Contains('"'))
            return Result(ShortcutTargetState.UnresolvedMetadata, "Stored target contains ambiguous control/format or quoting metadata; no target probe was attempted.");
        if (target.StartsWith(@"\\?\", StringComparison.Ordinal) || target.StartsWith(@"\\.\", StringComparison.Ordinal))
            return Result(ShortcutTargetState.Unsupported, "Device/extended namespace targets are not supported by this bounded Shell path contract.");
        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal))
            return Result(ShortcutTargetState.Unknown, "Network/UNC target was not contacted or authenticated. Network availability is unknown, not broken.");
        if (metadata.HasEnvironmentMetadata || target.Contains('%'))
            return Result(ShortcutTargetState.Unknown, "Environment-dependent stored target was not expanded or resolved; its current target context is unknown.");
        if (metadata.HasArguments)
            return Result(ShortcutTargetState.Unknown, "The link contains argument/activation context. Argument values and working directory were not collected; target activation semantics remain unverified and no target probe was attempted.");
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("::", StringComparison.Ordinal) ||
            target.StartsWith("ms-app", StringComparison.OrdinalIgnoreCase) || target.Contains("://", StringComparison.Ordinal))
            return Result(ShortcutTargetState.Unsupported, "Shell namespace, packaged-app or URI targets are not filesystem absence checks; no activation was attempted.");
        string? local = ShortcutPathPolicy.NormalizeLocalPath(target);
        if (local == null)
            return Result(ShortcutTargetState.Unknown, "Stored path is relative, ambiguous or unsupported. No contextual resolution or target probe was attempted.");

        ShortcutTargetProbe observation;
        try { observation = probe(local); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return Result(ShortcutTargetState.Unknown, $"Local metadata could not be verified (code 0x{ex.HResult:X8}); no missing-target conclusion was made.");
        }
        if (observation == null)
            return Result(ShortcutTargetState.Unknown, "Local metadata returned no observation; no absence conclusion was made.");
        if (!observation.FixedLocalVolumeAvailable || !observation.ParentChainValidated)
            return Result(ShortcutTargetState.Unknown, SafeReason(observation.Reason) + " Fixed local volume and accessible non-reparse ancestry were not both established.");
        return observation.State switch
        {
            ShortcutProbeState.Exists => Result(ShortcutTargetState.TargetExists,
                "The stored fixed-local target exists at the check time. No shortcut resolution, argument/working-directory validation or launch was performed."),
            ShortcutProbeState.FileNotFound or ShortcutProbeState.PathNotFound => Result(ShortcutTargetState.LocalTargetMissing,
                SafeReason(observation.Reason) + " This concerns the stored local path only; the target may have moved. It is not proof the shortcut is obsolete and authorizes no removal."),
            _ => Result(ShortcutTargetState.Unknown, SafeReason(observation.Reason))
        };
    }

    private static string SafeReason(string reason) => new(reason.Take(700)
        .Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c).ToArray());
}

/// <summary>Windows local path syntax validation only; it never expands variables or accesses any filesystem.</summary>
public static class ShortcutPathPolicy
{
    public static string? NormalizeLocalPath(string? value, bool allowDriveRoot = true)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > ShortcutTargetClassifier.MaximumRawTargetCharacters || value != value.Trim())
            return null;
        string path = value.Replace('/', '\\');
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) ||
            path[2..].IndexOfAny([':', '*', '?', '"', '<', '>', '|', '%']) >= 0) return null;
        var parts = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (!allowDriveRoot && parts.Length == 0) return null;
        foreach (string part in parts)
        {
            if (part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || IsDevice(part)) return null;
        }
        return char.ToUpperInvariant(path[0]) + ":\\" + string.Join('\\', parts);
    }

    private static bool IsDevice(string part)
    {
        string stem = part.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
