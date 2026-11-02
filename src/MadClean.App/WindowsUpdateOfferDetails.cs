using MadClean.Core;

namespace MadClean.App;

internal static class WindowsUpdateOfferDetails
{
    internal static string Describe(SystemUpdate offer)
    {
        var metadata = offer.OfferMetadata;
        int type = offer.Kind is "Driver" or "Firmware" ? 2 : offer.Kind == "Software" ? 1 : 0;
        if (metadata == null || !WindowsUpdateOfferMetadataParser.IsConsistent(metadata, type))
            return "Windows Update installation characteristics: Unknown; no consistent optional metadata was collected. Exact OEM prerequisites and permissions remain Unknown.";
        static string Text(WindowsUpdateTextField field) => field.State == CheckState.Available
            ? field.Value! : $"Unknown ({field.State})";
        static string Flag(WindowsUpdateBooleanField field) => field.State == CheckState.Available && field.Value.HasValue
            ? field.Value.Value ? "Yes" : "No" : $"Unknown ({field.State})";
        string driver = type == 2
            ? $"Provider-reported driver manufacturer: {Text(metadata.Manufacturer)}\nProvider-reported driver provider: {Text(metadata.Provider)}\nIntended device model name: {Text(metadata.Model)} (not a verified match to this PC/device)\n"
            : "";
        string impact = metadata.Impact.State == CheckState.Available && metadata.Impact.Value.HasValue
            ? metadata.Impact.Value.Value switch
            {
                WindowsUpdateInstallationImpact.Normal => "Normal",
                WindowsUpdateInstallationImpact.Minor => "Minor",
                WindowsUpdateInstallationImpact.RequiresExclusiveHandling => "Requires exclusive handling",
                _ => "Unknown"
            } : $"Unknown ({metadata.Impact.State})";
        string reboot = metadata.RebootBehavior.State == CheckState.Available && metadata.RebootBehavior.Value.HasValue
            ? metadata.RebootBehavior.Value.Value switch
            {
                WindowsUpdateRebootBehavior.NeverReboots => "Never reboots",
                WindowsUpdateRebootBehavior.AlwaysRequiresReboot => "Always requires reboot",
                WindowsUpdateRebootBehavior.CanRequestReboot => "Can request reboot",
                _ => "Unknown"
            } : $"Unknown ({metadata.RebootBehavior.State})";
        return $"Windows Update optional metadata coverage: {metadata.State}\n" + driver +
            $"Declared installation can request user input: {Flag(metadata.CanRequestUserInput)}\nDeclared installation requires network connectivity: {Flag(metadata.RequiresNetworkConnectivity)}\nDeclared installation impact: {impact}\nDeclared installation reboot behavior: {reboot}\n" +
            "These are provider declarations, not actual installation results or a readiness verdict. Required elevation, exact-device/OEM applicability, power/recovery-key precautions and other manufacturer prerequisites remain Unknown. No installation or restart is requested.";
    }
}
