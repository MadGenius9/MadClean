namespace MadClean.Core;

public enum JournalParentAceKind { Unsupported = 0, OrdinaryAllow = 1 }

public sealed record JournalParentAce(JournalParentAceKind Kind, string? Sid, int AccessMask, int Flags);

/// <summary>
/// Excludes unsupported owner/DACL descriptors for a journal creation namespace parent.
/// This is not an effective-access check, authorization, or proof of a private child store.
/// Inherited ACEs and unprotected DACLs are permitted here; child-store policy is separate.
/// </summary>
public static class JournalCreationParentSecurity
{
    public const int MaximumAces = 32;
    private const int FileAllAccess = 0x001f01ff;
    private const int SupportedFlags = 0x1f;

    public static bool IsSupported(string? ownerSid, string? currentUserSid, bool daclPresent,
        IReadOnlyList<JournalParentAce>? aces)
    {
        if (!IsCanonicalSid(currentUserSid) || !string.Equals(ownerSid, currentUserSid, StringComparison.Ordinal)
            || !daclPresent || aces is null) return false;
        try
        {
            // Never enumerate caller-controlled collections or use a changing Count as a loop bound.
            var count = aces.Count;
            if (count is < 1 or > MaximumAces) return false;
            for (var index = 0; index < count; index++)
            {
                var ace = aces[index];
                if (ace is null || ace.Kind != JournalParentAceKind.OrdinaryAllow
                    || !IsCanonicalSid(ace.Sid)
                    || !(string.Equals(ace.Sid, currentUserSid, StringComparison.Ordinal)
                        || ace.Sid == "S-1-5-18" || ace.Sid == "S-1-5-32-544")
                    || ace.AccessMask == 0 || (ace.AccessMask & ~FileAllAccess) != 0
                    || (ace.Flags & ~SupportedFlags) != 0) return false;
            }
            return aces.Count == count;
        }
        catch (Exception)
        {
            // A malformed/throwing projection is unsupported, never an empty or partial success.
            return false;
        }
    }

    private static bool IsCanonicalSid(string? sid)
    {
        if (sid is null || sid.Length is < 5 or > 184 || !sid.StartsWith("S-1-", StringComparison.Ordinal))
            return false;
        var component = 0;
        var start = 4;
        for (var index = start; index <= sid.Length; index++)
        {
            if (index != sid.Length && sid[index] != '-') continue;
            if (index == start || (index - start > 1 && sid[start] == '0')) return false;
            var maximum = component == 0 ? 281474976710655UL : uint.MaxValue;
            ulong value = 0;
            for (var digit = start; digit < index; digit++)
            {
                if (sid[digit] is < '0' or > '9') return false;
                var next = (uint)(sid[digit] - '0');
                if (value > (maximum - next) / 10) return false;
                value = value * 10 + next;
            }
            if (++component > 16) return false;
            start = index + 1;
        }
        return component > 0;
    }
}
