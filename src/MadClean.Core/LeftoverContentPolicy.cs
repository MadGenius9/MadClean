using System.Globalization;

namespace MadClean.Core;

public static class LeftoverContentPolicy
{
    public const int MaximumFiles = 128;
    public const int MaximumDirectories = 128;
    public const int MaximumDepth = 8;
    public const int MaximumIssues = 256;
    public const int MaximumInventoryEntries = 20_000;
    public static readonly TimeSpan ScopeLifetime = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> ProtectedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Users", "Documents", "My Documents", "Downloads", "Desktop", "Pictures", "Music", "Videos", "AppData",
        "Profiles", "Profile", "User Profiles", "UserProfiles", "User Data", "UserData", "Data", "Saved Games",
        "Saves", "Save", "SaveGames", "Saved", "Settings", "Config", "Configuration", "Preferences", "Credentials",
        "Vault", "Secrets", "Keys", "Certificates", "Passwords", "Browser", "Browser Profiles", "Windows",
        "System32", "SysWOW64", "WinSxS", "DriverStore", "Installer", "Recovery", "System Volume Information",
        "$Recycle.Bin", "ProgramData", "Common Files", "Microsoft Shared", "WindowsApps", "Package Cache",
        "Windows Kits", "Microsoft SDKs", "Microsoft.NET", "MSBuild", "Reference Assemblies",
        "Shared", "Common", "Dependencies", "Frameworks", "Runtimes", ".git", ".ssh", ".aws", ".azure", ".config", ".local", "Default"
    };
    private static readonly HashSet<string> BroadRoots = new(StringComparer.OrdinalIgnoreCase)
    { "Program Files", "Program Files (x86)", "Applications", "Apps", "Games", "Temp", "Tmp" };
    private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".key", ".pem", ".pfx", ".p12", ".cer", ".crt",
        ".kdbx", ".sav", ".save", ".config", ".ini", ".json", ".yaml", ".yml", ".toml", ".xml",
        ".log", ".bak", ".backup", ".dat", ".pdb", ".csv", ".txt", ".doc", ".docx", ".xls", ".xlsx",
        ".pdf", ".jpg", ".jpeg", ".png", ".mp3", ".mp4", ".env"
    };

    /// <summary>Conservative name-based exclusion, not a content classifier or proof of safe ownership.</summary>
    public static bool IsProtectedDescendant(string? name, bool isDirectory)
    {
        if (!ValidComponent(name)) return true;
        if (ProtectedDirectories.Contains(name!)) return true;
        if (isDirectory) return name!.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
        int dot = name!.LastIndexOf('.');
        return dot >= 0 && ProtectedExtensions.Contains(name[dot..]);
    }
    internal static bool IsProtectedRoot(string normalized)
    {
        string[] segments = normalized[3..].Split('\\');
        return segments.Any(segment => IsProtectedDescendant(segment, true)) ||
            segments.Length == 1 && BroadRoots.Contains(segments[0]);
    }
    internal static bool Overlaps(string first, string second) => first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith(second + "\\", StringComparison.OrdinalIgnoreCase) || second.StartsWith(first + "\\", StringComparison.OrdinalIgnoreCase);
    internal static bool SafeText(string? text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum &&
        !text.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format);
    private static bool ValidComponent(string? name) => SafeText(name, 255) && name == name!.Trim() &&
        name is not ("." or "..") && !name.EndsWith('.') && !DeviceName(name) && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|', '%']) < 0;
    private static bool DeviceName(string name)
    {
        string stem = name.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) || stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}

