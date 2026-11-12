using System.Globalization;
using MadClean.Core;

namespace MadClean.App;

internal static class WindowsUpdateOfferDetails
{
    internal static string Describe(SystemUpdate offer)
    {
        var metadata = offer.OfferMetadata;
        int type = offer.Kind is "Driver" or "Firmware" ? 2 : offer.Kind == "Software" ? 1 : 0;
        string identity = DescribeIdentity(offer.IdentityMetadata, type);
        if (metadata == null || !WindowsUpdateOfferMetadataParser.IsConsistent(metadata, type))
            return identity + "\nWindows Update installation characteristics: Unknown; no consistent optional metadata was collected. Exact OEM prerequisites and permissions remain Unknown.";
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
        return identity + $"\nWindows Update optional installation metadata coverage: {metadata.State}\n" + driver +
            $"Declared installation can request user input: {Flag(metadata.CanRequestUserInput)}\nDeclared installation requires network connectivity: {Flag(metadata.RequiresNetworkConnectivity)}\nDeclared installation impact: {impact}\nDeclared installation reboot behavior: {reboot}\n" +
            "These are provider declarations, not actual installation results or a readiness verdict. Required elevation, exact-device/OEM applicability, power/recovery-key precautions and other manufacturer prerequisites remain Unknown. No installation or restart is requested.";
    }

    private static string DescribeIdentity(WindowsUpdateIdentityMetadata? metadata, int type)
    {
        if (metadata == null || !WindowsUpdateIdentityMetadataParser.IsConsistent(metadata, type))
            return "Windows Update identity metadata coverage: Unknown.\nProvider update revision: Unknown (metadata revision, not an installed or offered driver version).\n" +
                "Declared hardware or compatible ID: Unknown. No machine-instance identity, current-device match or OEM compatibility is established.";
        string revision = metadata.RevisionNumber.State == CheckState.Available && metadata.RevisionNumber.Value is { } number
            ? number.ToString(CultureInfo.InvariantCulture) : $"Unknown ({metadata.RevisionNumber.State})";
        string hardware = metadata.DriverHardwareId.State == CheckState.Available
            ? metadata.DriverHardwareId.Value!
            : metadata.DriverHardwareId.State == CheckState.Unsupported ? "Unsupported for this offer type" : $"Unknown ({metadata.DriverHardwareId.State})";
        return $"Windows Update identity metadata coverage: {metadata.State}\nProvider update revision: {revision}\n" +
            "The revision describes Windows Update metadata. It is independent of the update ID and is not a driver version, installed version or BIOS version; it does not establish their ordering.\n" +
            $"Declared hardware or compatible ID: {hardware}\n" +
            "This is the provider's required-match declaration, not a machine-instance identifier or evidence of a match to a currently installed device. It does not establish complete OEM applicability, compatibility or installation readiness.";
    }
}
