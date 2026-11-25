namespace MadClean.Core;

/// <summary>Metadata from the fixed command probe. No value authorizes a scan.</summary>
public enum DefenderScanCommandObservation
{
    Unavailable,
    ModuleNotAccepted,
    CommandNotAccepted,
    CustomParametersNotAccepted,
    ProfileObserved,
    Unknown,
    CommandLookupNotAccepted,
    CommandShapeNotAccepted,
    CommandBindingNotAccepted
}

public static class DefenderScanCommandProfile
{
    public static DefenderScanCommandObservation Interpret(ProcessResult result)
    {
        if (result.ExitCode != 0 || result.Output.Length > 80)
            return DefenderScanCommandObservation.Unknown;

        var marker = result.Output.EndsWith("\r\n", StringComparison.Ordinal) ? result.Output[..^2]
            : result.Output.EndsWith('\n') ? result.Output[..^1] : result.Output;
        return marker switch
        {
            "MadClean.DefenderProfile/1:module" => DefenderScanCommandObservation.ModuleNotAccepted,
            "MadClean.DefenderProfile/1:command" => DefenderScanCommandObservation.CommandNotAccepted,
            "MadClean.DefenderProfile/1:lookup" => DefenderScanCommandObservation.CommandLookupNotAccepted,
            "MadClean.DefenderProfile/1:shape" => DefenderScanCommandObservation.CommandShapeNotAccepted,
            "MadClean.DefenderProfile/1:binding" => DefenderScanCommandObservation.CommandBindingNotAccepted,
            "MadClean.DefenderProfile/1:parameters" => DefenderScanCommandObservation.CustomParametersNotAccepted,
            "MadClean.DefenderProfile/1:observed" => DefenderScanCommandObservation.ProfileObserved,
            _ => DefenderScanCommandObservation.Unknown
        };
    }
}
