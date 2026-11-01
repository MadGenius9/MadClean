using System.Globalization;

namespace MadClean.Core;

public enum DiagnosticEvidenceArea { Storage, Defender }
public sealed record DiagnosticInspectionEvidenceEntry(DiagnosticEvidenceArea Area, CheckState State,
    DateTimeOffset? AttemptedUtc, DateTimeOffset? CompletedUtc, string Observation, string Source,
    string Coverage, string Uncertainty, string Impact, string NextStep);

/// <summary>Minimized in-memory evidence from explicit inspections, without provider messages or health verdicts.</summary>
public static class DiagnosticInspectionEvidence
{
    public const int MaximumDrives = 64;

    public static DiagnosticInspectionEvidenceEntry NotChecked(DiagnosticEvidenceArea area) =>
        Entry(area, CheckState.Unknown, null, null, "Not checked in this session.", "No inspection result is available.");

    public static DiagnosticInspectionEvidenceEntry Pending(DiagnosticEvidenceArea area, DateTimeOffset attemptUtc)
    {
        if (!Utc(attemptUtc) || attemptUtc > DateTimeOffset.UtcNow) return InvalidTime(area);
        return Entry(area, CheckState.Unknown, attemptUtc, null, "Inspection in progress.", "No completed inspection result is available.");
    }

    public static DiagnosticInspectionEvidenceEntry Interrupted(DiagnosticEvidenceArea area, CheckState state,
        DateTimeOffset attemptUtc, DateTimeOffset completedUtc)
    {
        if (!Times(attemptUtc, completedUtc)) return InvalidTime(area);
        state = state is CheckState.Cancelled or CheckState.Failed or CheckState.Unavailable or CheckState.Unsupported
            ? state : CheckState.Unknown;
        return Entry(area, state, attemptUtc, completedUtc, "Inspection did not establish a completed observation.",
            "Source coverage is unknown; no result values were retained.");
    }

    public static DiagnosticInspectionEvidenceEntry Storage(CheckResult<DiskInfo>? result,
        DateTimeOffset attemptUtc, DateTimeOffset completedUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Times(attemptUtc, completedUtc)) return InvalidTime(DiagnosticEvidenceArea.Storage);
        if (result == null || !Positive(result.State))
            return Interrupted(DiagnosticEvidenceArea.Storage, result?.State ?? CheckState.Unknown, attemptUtc, completedUtc);
        if (result.Items == null || result.Items.Count > MaximumDrives)
            return Entry(DiagnosticEvidenceArea.Storage, CheckState.Unknown, attemptUtc, completedUtc,
                "Storage metadata did not meet the bounded source contract.", "No drive values were trusted.");
        var drives = new Dictionary<char, DiskInfo>();
        var ambiguous = new HashSet<char>();
        bool partial = result.State == CheckState.Partial;
        foreach (var disk in result.Items)
        {
            token.ThrowIfCancellationRequested();
            if (disk == null || disk.Name == null || disk.Name.Length != 3 || !char.IsAsciiLetter(disk.Name[0]) ||
                disk.Name[1] != ':' || disk.Name[2] != '\\' || disk.TotalBytes <= 0 || disk.FreeBytes < 0 || disk.FreeBytes > disk.TotalBytes)
            { partial = true; continue; }
            char letter = char.ToUpperInvariant(disk.Name[0]);
            if (ambiguous.Contains(letter)) { partial = true; continue; }
            if (!drives.TryAdd(letter, disk)) { drives.Remove(letter); ambiguous.Add(letter); partial = true; }
        }
        token.ThrowIfCancellationRequested();
        string facts = string.Join(" ", drives.OrderBy(pair => pair.Key).Select(pair =>
            $"{pair.Key}:\\ free {pair.Value.FreeBytes.ToString(CultureInfo.InvariantCulture)} bytes of {pair.Value.TotalBytes.ToString(CultureInfo.InvariantCulture)} capacity bytes."));
        return Entry(DiagnosticEvidenceArea.Storage, drives.Count == 0 ? CheckState.Unknown : partial ? CheckState.Partial : CheckState.Available,
            attemptUtc, completedUtc, drives.Count == 0 ? "No valid drive capacity observation was established." : $"Capacity metadata observed for {drives.Count} drive(s).",
            $"Fixed letter-drive roots only; at most {MaximumDrives} source rows. {facts} " +
            (partial ? "Invalid or ambiguous rows were omitted; coverage is partial." : "Only the returned drive roots were observed."));
    }

    public static DiagnosticInspectionEvidenceEntry Defender(CheckResult<DefenderInfo>? result,
        DateTimeOffset attemptUtc, DateTimeOffset completedUtc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Times(attemptUtc, completedUtc)) return InvalidTime(DiagnosticEvidenceArea.Defender);
        if (result == null || !Positive(result.State))
            return Interrupted(DiagnosticEvidenceArea.Defender, result?.State ?? CheckState.Unknown, attemptUtc, completedUtc);
        if (result.Items == null || result.Items.Count != 1 || result.Items[0] == null)
            return Entry(DiagnosticEvidenceArea.Defender, CheckState.Unknown, attemptUtc, completedUtc,
                "A unique Defender status observation was not established.", "No Defender values were trusted.");
        var info = result.Items[0];
        string mode = info.Mode switch
        {
            "Normal" => "Normal", "Passive" => "Passive", "EDRBlock" or "EDR Block Mode" => "EDRBlock",
            "SxS passive" or "SxS Passive Mode" => "SxS passive", _ => "Unknown"
        };
        int known = (info.AntivirusEnabled.HasValue ? 1 : 0) + (info.RealTimeEnabled.HasValue ? 1 : 0) +
            (info.ServiceEnabled.HasValue ? 1 : 0) + (mode != "Unknown" ? 1 : 0);
        var state = known == 0 ? CheckState.Unknown : known < 4 || result.State == CheckState.Partial ? CheckState.Partial : CheckState.Available;
        token.ThrowIfCancellationRequested();
        return Entry(DiagnosticEvidenceArea.Defender, state, attemptUtc, completedUtc,
            known == 0 ? "No recognized Defender status fields were established." : "Defender status fields observed; protection and scan outcome are not established.",
            $"Antivirus enabled: {Flag(info.AntivirusEnabled)}; real-time enabled: {Flag(info.RealTimeEnabled)}; service enabled: {Flag(info.ServiceEnabled)}; mode: {mode}. " +
            "Only these flags and a recognized mode are retained; threat identifiers, resources, versions, signature dates and detection/scan history are excluded.");
    }

    private static bool Positive(CheckState state) => state is CheckState.Available or CheckState.Partial;
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Times(DateTimeOffset attempt, DateTimeOffset completed)
    {
        var now = DateTimeOffset.UtcNow;
        return Utc(attempt) && Utc(completed) && attempt <= completed && completed <= now;
    }
    private static string Flag(bool? flag) => flag.HasValue ? flag.Value ? "Yes" : "No" : "Unknown";
    private static DiagnosticInspectionEvidenceEntry InvalidTime(DiagnosticEvidenceArea area) =>
        Entry(area, CheckState.Unknown, null, null, "Inspection timestamps were invalid; observation unavailable.", "No dates or result values were inferred.");
    private static DiagnosticInspectionEvidenceEntry Entry(DiagnosticEvidenceArea area, CheckState state,
        DateTimeOffset? attempted, DateTimeOffset? completed, string observation, string coverage) => new(area, state,
        attempted, completed, observation,
        area == DiagnosticEvidenceArea.Storage ? "System.IO.DriveInfo / TotalSize and AvailableFreeSpace" : "Microsoft Defender / Get-MpComputerStatus",
        coverage,
        area == DiagnosticEvidenceArea.Storage
            ? "AvailableFreeSpace is caller-visible and may reflect quotas. Drive metadata is not disk health, physical allocation, reclaimable space or complete storage ownership evidence. Drives may overlap; no global free-space sum is inferred."
            : "Status flags do not establish protection, absence of threats, a completed scan or a healthy system.",
        area == DiagnosticEvidenceArea.Storage
            ? "Lack of caller-visible free space can prevent new writes. These capacity values do not establish cause, drive failure or performance; no cleanup or repair was performed."
            : "Disabled or passive Defender settings can reflect another security provider or policy. Review the source context before deciding whether any action is needed; no scan or security change was performed.",
        area == DiagnosticEvidenceArea.Storage ? "Review drive details in Overview." : "Review status and source limitations in Security.");
}
