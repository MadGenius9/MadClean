using System.Collections.ObjectModel;

namespace MadClean.Core;

public enum GuidedWindowsTask
{
    StartupSettings, InstalledApps, DefenderProtection, WindowsUpdates, OptionalDriverUpdates, SystemFileRepairGuide
}

public sealed record GuidedWindowsAction(GuidedWindowsTask Task, string Title, Uri TargetUri,
    string ExactEffect, string Permissions, string Limitations, string Source);

/// <summary>
/// Fixed navigation descriptors, not execution descriptors. No selected program, update, command,
/// credential or discovered path can be added to these targets.
/// </summary>
public static class GuidedWindowsActions
{
    private const string SettingsSource = "Microsoft Settings URI reference: https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings";
    private const string SettingsPermissions = "MadClean requests no elevation. Windows may require separate permission or administrator consent for an action you later select inside Windows; organizational policy may restrict the page.";
    private const string UnknownCompletion = "MadClean does not execute or monitor the requested operation. Operation completion remains Unknown; opening this page is not proof that any action occurred.";

    private static readonly ReadOnlyCollection<GuidedWindowsAction> Catalog = Array.AsReadOnly(new[]
    {
        new GuidedWindowsAction(GuidedWindowsTask.StartupSettings, "Open Startup apps settings",
            new Uri("ms-settings:startupapps"),
            "Request the Windows Startup apps Settings page only. No startup entry is selected, enabled, disabled, added or removed by this handoff.",
            SettingsPermissions,
            "Choose any change yourself inside Windows. The page has Windows-defined coverage and may not list every startup mechanism. " + UnknownCompletion,
            SettingsSource),
        new GuidedWindowsAction(GuidedWindowsTask.InstalledApps, "Open Installed apps settings",
            new Uri("ms-settings:appsfeatures"),
            "Request the Windows Apps & Features / Installed apps Settings page only. No application identifier or uninstall command is passed, and no uninstall is executed.",
            SettingsPermissions,
            "Find and review an application inside Windows before choosing any supported action. This does not complete a queued MadClean uninstall or prove an application was removed. " + UnknownCompletion,
            SettingsSource),
        new GuidedWindowsAction(GuidedWindowsTask.DefenderProtection, "Open Windows Security settings",
            new Uri("ms-settings:windowsdefender"),
            "Request the Windows Security section of Settings only. No Defender scan, quarantine, remediation, protection-setting change or scan target is requested.",
            SettingsPermissions,
            "This is the documented Settings page, not a verified deep link into a particular Windows Security scan screen. Continue explicitly inside Windows if needed. Windows retains control of its existing security policy/background protection. " + UnknownCompletion,
            SettingsSource),
        new GuidedWindowsAction(GuidedWindowsTask.WindowsUpdates, "Open Windows Update settings",
            new Uri("ms-settings:windowsupdate"),
            "Request the Windows Update Settings page only. No package/update identity, installation, download or restart command is passed by MadClean.",
            SettingsPermissions,
            "Windows may query configured update sources or perform already-configured background servicing under Windows policy. MadClean does not select an update or force a restart. " + UnknownCompletion,
            SettingsSource),
        new GuidedWindowsAction(GuidedWindowsTask.OptionalDriverUpdates, "Open Optional updates settings",
            new Uri("ms-settings:windowsupdate-optionalupdates"),
            "Request the Windows Update Optional updates page only. No driver is selected, downloaded, installed or replaced by MadClean.",
            SettingsPermissions,
            "Availability depends on Windows version, hardware, update service and policy. The page can include updates other than drivers; users must review individual offers. Windows may query configured sources. " + UnknownCompletion,
            SettingsSource),
        new GuidedWindowsAction(GuidedWindowsTask.SystemFileRepairGuide, "Read Microsoft's system-file guide",
            new Uri("https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc"),
            "Request the exact official Microsoft SFC instruction page in the registered browser. No shell, SFC, DISM, diagnostic scan or repair command is executed.",
            "MadClean requests no elevation to open documentation. Microsoft's guide states administrator requirements for actually running SFC; any later manual command requires its own informed selection and permissions.",
            "The browser may contact Microsoft using its own browser settings. Reading documentation neither diagnoses nor repairs Windows and does not finish the integrity-check or repair roadmap. " + UnknownCompletion,
            "Microsoft SFC command reference: https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc")
    });

    public static IReadOnlyList<GuidedWindowsAction> All => Catalog;

    public static GuidedWindowsAction? Find(GuidedWindowsTask task) => Catalog.FirstOrDefault(action => action.Task == task);

    public static bool IsAllowedUri(GuidedWindowsTask task, Uri uri)
    {
        var action = Find(task);
        if (action == null || uri is null || !uri.IsAbsoluteUri || !uri.IsWellFormedOriginalString() ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        // Compare the exact authored URI, not Uri.Equals normalization. Harmless-looking case,
        // escape, dot-segment, authority and default-port aliases are not alternate action inputs.
        return uri.OriginalString.Equals(action.TargetUri.OriginalString, StringComparison.Ordinal);
    }
}
