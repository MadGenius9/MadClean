namespace MadClean.Core;

/// <summary>
/// Checks an already-returned read-only preview against the exact selected target and request times.
/// Consistency does not authenticate a creator, establish current registry identity or authorize a change.
/// </summary>
public static class StartupPreviewEvidence
{
    private const int MaximumDescriptionCharacters = 4096;

    public static bool IsConsistent(CheckResult<StartupActionPreview>? result,
        StartupRegistrationReference? selectedReference, DateTimeOffset attemptedUtc, DateTimeOffset receivedUtc)
    {
        if (result == null || result.State != CheckState.Available || result.Items is not { Count: 1 } ||
            !StartupActionPolicy.IsValidReference(selectedReference) ||
            !Utc(attemptedUtc) || !Utc(receivedUtc) || receivedUtc < attemptedUtc || receivedUtc > DateTimeOffset.UtcNow)
            return false;
        var preview = result.Items[0];
        return preview != null && StartupActionPolicy.IsValidReference(preview.Reference) &&
            preview.Reference.Source == selectedReference!.Source &&
            string.Equals(preview.Reference.ValueName, selectedReference.ValueName, StringComparison.Ordinal) &&
            StartupActionPolicy.IsOpaqueId(preview.Token) && Guid.ParseExact(preview.Token, "N") != Guid.Empty &&
            Utc(preview.ObservedUtc) && preview.ObservedUtc >= attemptedUtc && preview.ObservedUtc <= receivedUtc &&
            preview.ValueKind is "REG_SZ" or "REG_EXPAND_SZ" &&
            preview.ByteCount >= 2 && preview.ByteCount <= StartupActionPolicy.MaximumValueBytes && preview.ByteCount % 2 == 0 &&
            Description(preview.ExactEffect) && Description(preview.Permissions) && Description(preview.RecoveryLimits);
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Description(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumDescriptionCharacters &&
        !value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format);
}
