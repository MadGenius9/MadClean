using System.Runtime.InteropServices;

namespace MadClean.Windows;

/// <summary>Best-effort styling of an existing native title bar; never changes the system theme.</summary>
public static class WindowAppearance
{
    private const uint UseImmersiveDarkMode = 20;

    /// <summary>
    /// Asks Windows 11 DWM to honor dark mode for this window's standard frame.
    /// True means DWM accepted the attribute, not that a particular caption color was verified.
    /// </summary>
    public static bool TryUseDarkTitleBar(nint handle)
    {
        if (handle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        try
        {
            // Win32 BOOL is a 32-bit integer, independently of managed bool marshalling.
            int enabled = 1;
            return DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or ExternalException or System.Security.SecurityException)
        {
            // Unsupported/unavailable native styling must never prevent the window from opening.
            return false;
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint size);
}
