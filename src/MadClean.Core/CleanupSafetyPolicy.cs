namespace MadClean.Core;

// Shared lexical inventory exclusions. These predicates alone never authorize a native read or action.
public static class CleanupSafetyPolicy
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Downloads", "Documents", "Desktop", "Pictures", "Videos", "Music", "Saved Games", "Saves",
        "Credentials", "Vault", "Protect", "Profiles", "User Data", "Default", "Login Data", ".ssh", ".gnupg",
        "Chrome", "Chromium", "Edge", "Firefox", "Brave-Browser", "Opera", "Cookies", "Sessions", "Session Storage", "Local Storage"
    };
    private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".db", ".sqlite", ".sqlite3", ".kdbx", ".pem", ".key", ".pfx", ".p12", ".sav" };
    private static readonly string[] SensitiveNameParts = ["credential", "password", "token", "secret", "login", "cookie", "session", "wallet"];

    public static bool IsProtectedName(string name) => ProtectedNames.Contains(name);
    public static bool IsProtectedExtension(string extension) => ProtectedExtensions.Contains(extension);
    public static bool IsSensitiveName(string name) => SensitiveNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    public static bool IsProtectedRelativePath(string relativePath)
    {
        var parts = relativePath.Split('\\', '/');
        return parts.Any(part => IsProtectedName(part) || IsSensitiveName(part)) ||
            IsProtectedExtension(Path.GetExtension(parts[^1]));
    }
}
