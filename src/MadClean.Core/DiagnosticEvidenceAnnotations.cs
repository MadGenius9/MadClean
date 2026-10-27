namespace MadClean.Core;

internal static class DiagnosticEvidenceAnnotations
{
    internal sealed record Annotation(string Observation, string Uncertainty, string Impact, string NextStep);
    public static Annotation For(string channel, string provider, int id)
    {
        if (channel == "Application" && provider == "Application Error" && id == 1000)
            return new("Windows Application Error recorded application-crash event metadata.",
                "The application, module and exception payloads are intentionally omitted. The cause and number of distinct incidents are unknown; a faulting Windows module is not proof it is defective.",
                "An application failure may have interrupted work; actual user impact is not established by metadata alone.",
                "Review the referenced records in Event Viewer and correlate their times with observed symptoms before seeking application/vendor guidance. No repair is run.");
        if (channel == "Application" && provider == "Windows Error Reporting" && id == 1001)
            return new("A Windows Error Reporting event record was observed.",
                "A reporting record may describe an incident also recorded elsewhere or a queued report. Its payload and application were not collected; this is not another proven unique crash.",
                "Additional user impact cannot be established from this reporting record alone.",
                "Correlate the referenced Event Viewer records with nearby application-error evidence and the user's observed symptoms; no WER reports or dumps are opened.");
        if (channel == "System" && provider == "Microsoft-Windows-Kernel-Power" && id == 41)
            return new("Windows recorded a restart without a clean shutdown.",
                "Event 41 alone does not identify a power supply, driver, memory defect or other root cause. Event payloads and bugcheck data were not collected.",
                "The session may have been interrupted; exact data loss or hardware impact is unknown.",
                "Review the referenced Event Viewer record and shutdown timeline with the user's observed context. No stress test, setting change or repair is started.");
        if (channel == "System" && provider == "EventLog" && id == 6008)
            return new("The Event Log provider recorded an unexpected/dirty-shutdown observation.",
                "The record timestamp is not necessarily the actual shutdown instant. Cause and correlation with other records remain unknown.",
                "Possible interruption of work; exact consequences are not established by this metadata.",
                "Compare the referenced record with nearby startup/shutdown evidence and observed symptoms before selecting any later diagnostic.");
        return new("Windows reported event metadata for this provider/event/version/channel within the requested interval.",
            "This event class has no specific interpretation in MadClean. Recurrence is not proof of corruption, a unique incident or a repair benefit.",
            "Unknown from metadata; Windows' reported level is not a diagnosis or health score.",
            "Inspect the exact records locally in Event Viewer and correlate their times with observed symptoms before selecting any later diagnostic.");
    }
}
