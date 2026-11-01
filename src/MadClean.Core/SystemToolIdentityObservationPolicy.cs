using System.Buffers;
using System.Globalization;
using System.Text;

namespace MadClean.Core;

public sealed record SystemToolIdentityObservation(CheckState State, bool? IdentityObserved,
    DateTimeOffset? CheckedUtc, DateTimeOffset? ExpiresUtc, string Explanation);

/// <summary>Validates a provider projection only; performs no native trust check or launch authorization.</summary>
public static class SystemToolIdentityObservationPolicy
{
    private const string Limits = "This accepts provider-reported identity evidence only; it does not perform Authenticode verification or authenticate the supplied system-directory argument. Windows integrity is not checked. No tool execution, elevation, repair or restart is authorized.";

    public static SystemToolIdentityObservation Evaluate(SystemToolOperation operation,
        CheckResult<SystemToolActionPreview>? result, string independentOSsystemDirectory, DateTimeOffset nowUTC,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        SystemToolIdentityObservation Empty(CheckState state, string reason)
        {
            token.ThrowIfCancellationRequested();
            return new(state, null, null, null, reason + " " + Limits);
        }
        SystemToolIdentityObservation Invalid() => Empty(CheckState.Unknown,
            "The identity projection is missing, inconsistent, stale or outside the fixed read-only contract; identity remains unknown.");
        if (result == null) return Invalid();
        // A non-Windows source must remain Unsupported even when no OS system path exists.
        if (result.State is not (CheckState.Available or CheckState.Partial))
            return Empty(Enum.IsDefined(result.State) ? result.State : CheckState.Unknown, result.State switch
            {
                CheckState.Unsupported => "Native tool identity verification is unsupported on this platform.",
                CheckState.Unavailable => "Native tool identity evidence is unavailable; no successful verification is claimed.",
                CheckState.Failed => "Native tool identity verification failed; no successful verification is claimed.",
                CheckState.Cancelled => "Native tool identity verification was cancelled; no current identity observation is established.",
                _ => "Native tool identity verification has not established a usable observation."
            });
        var expected = SystemToolActionsCatalog.Find(operation);
        if (expected == null || result.Items == null || result.Items.Count != 1 || result.Items[0] is not { } preview ||
            !ValidDirectory(independentOSsystemDirectory) ||
            !string.Equals(preview.SystemDirectory, independentOSsystemDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(preview.ToolPath, independentOSsystemDirectory + "\\" + expected.FileName, StringComparison.OrdinalIgnoreCase) ||
            preview.PreviewId == Guid.Empty || preview.CanLaunch || preview.TrustState != SystemToolTrustState.MicrosoftSignatureVerified ||
            !SystemToolActionsPolicy.ValidHash(preview.Sha256) || !SafeText(preview.TrustDetail, 4096) ||
            !SafeText(preview.Blocker, 4096) || !SafeText(preview.ToolVersion, 128) ||
            nowUTC == default || nowUTC.Offset != TimeSpan.Zero || preview.CheckedUtc == default ||
            preview.CheckedUtc.Offset != TimeSpan.Zero || preview.ExpiresUtc.Offset != TimeSpan.Zero ||
            preview.CheckedUtc > nowUTC || preview.ExpiresUtc <= nowUTC || preview.ExpiresUtc <= preview.CheckedUtc ||
            preview.ExpiresUtc - preview.CheckedUtc > TimeSpan.FromMinutes(2)) return Invalid();
        var actual = preview.Definition;
        if (actual == null || actual.Operation != expected.Operation || actual.FileName != expected.FileName ||
            actual.Title != expected.Title || actual.Kind != expected.Kind || actual.Effect != expected.Effect ||
            actual.Elevation != expected.Elevation || actual.Network != expected.Network || actual.Cancellation != expected.Cancellation ||
            actual.Reboot != expected.Reboot || actual.WindowsLogReference != expected.WindowsLogReference ||
            actual.Arguments == null || actual.Arguments.Count != expected.Arguments.Count) return Invalid();
        for (int index = 0; index < expected.Arguments.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (actual.Arguments[index] != expected.Arguments[index]) return Invalid();
        }
        bool unknownVersion = string.Equals(preview.ToolVersion, "Unknown", StringComparison.OrdinalIgnoreCase);
        if (unknownVersion && result.State == CheckState.Available) return Invalid();
        token.ThrowIfCancellationRequested();
        return new(result.State, true, preview.CheckedUtc, preview.ExpiresUtc,
            (unknownVersion ? "Provider-reported Microsoft signature identity observed; executable version remains unknown. " :
                "Provider-reported Microsoft signature, fixed tool descriptor and executable identity projection accepted. ") +
            (result.State == CheckState.Partial ? "The source remains Partial. " : "") + Limits);
    }

    private static bool ValidDirectory(string? value)
    {
        if (!SafeText(value, 1024) || value!.Length < 4 || !char.IsAsciiLetter(value[0]) || value[1] != ':' ||
            value[2] != '\\' || value.Contains('/') || value.EndsWith('\\') || value[2..].IndexOfAny([':', '*', '?', '"', '<', '>', '|', '%']) >= 0)
            return false;
        return value[3..].Split('\\').All(part => part.Length > 0 && part is not ("." or "..") &&
            !part.EndsWith('.') && !part.EndsWith(' '));
    }

    private static bool SafeText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim()) return false;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
