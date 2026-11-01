using System.ComponentModel;
using System.IO;
using MadClean.Core;
using MadClean.Windows;

namespace MadClean.WindowsSmoke;

internal static partial class NativeFixtures
{
    private static void StorageInspectionEvidence(SmokeReport report, CancellationToken token)
    {
        const string check = "storage-inspection-evidence-contract";
        try
        {
            var before = DateTimeOffset.UtcNow;
            var source = Query(ct => new WindowsInventory().DisksAsync(ct), token);
            var after = DateTimeOffset.UtcNow;
            var projected = DiagnosticInspectionEvidence.Storage(source, before, after, token);
            bool positiveSource = source.State is CheckState.Available or CheckState.Partial;
            bool actualRows = positiveSource && source.Items.Count is > 0 and <= DiagnosticInspectionEvidence.MaximumDrives &&
                source.Items.Any(disk => disk != null && disk.Name?.Length == 3 && char.IsAsciiLetter(disk.Name[0]) &&
                    disk.Name[1] == ':' && disk.Name[2] == '\\' && disk.TotalBytes > 0 && disk.FreeBytes >= 0 && disk.FreeBytes <= disk.TotalBytes &&
                    source.Items.Count(other => string.Equals(other?.Name, disk.Name, StringComparison.OrdinalIgnoreCase)) == 1);
            CheckInspectionEvidence(check, DiagnosticEvidenceArea.Storage, source.State, projected, before, after, actualRows, report);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            report.Unknown(check, "The actual bounded storage read/projection did not complete; no drive values or provider messages exported, no health proof.");
        }
    }

    private static void DefenderInspectionEvidence(CheckResult<DefenderInfo> source, DateTimeOffset before,
        DateTimeOffset after, SmokeReport report, CancellationToken token)
    {
        var projected = DiagnosticInspectionEvidence.Defender(source, before, after, token);
        bool known = (source.State is CheckState.Available or CheckState.Partial) && source.Items.Count == 1 && source.Items[0] is { } data &&
            (data.AntivirusEnabled.HasValue || data.RealTimeEnabled.HasValue || data.ServiceEnabled.HasValue ||
                data.Mode is "Normal" or "Passive" or "EDRBlock" or "EDR Block Mode" or "SxS passive" or "SxS Passive Mode");
        CheckInspectionEvidence("defender-inspection-evidence-contract", DiagnosticEvidenceArea.Defender, source.State,
            projected, before, after, known, report);
    }

    private static void CheckInspectionEvidence(string check, DiagnosticEvidenceArea area, CheckState sourceState,
        DiagnosticInspectionEvidenceEntry projected, DateTimeOffset before, DateTimeOffset after, bool actualRows,
        SmokeReport report)
    {
        string expectedSource = area == DiagnosticEvidenceArea.Storage ? "System.IO.DriveInfo / TotalSize and AvailableFreeSpace" : "Microsoft Defender / Get-MpComputerStatus";
        bool schema = Enum.IsDefined(sourceState) && projected.Area == area && Enum.IsDefined(projected.State) && projected.Source == expectedSource &&
            projected.AttemptedUtc == before && projected.CompletedUtc == after && before.Offset == TimeSpan.Zero && after.Offset == TimeSpan.Zero &&
            before <= after && new[] { projected.Observation, projected.Coverage, projected.Uncertainty, projected.Impact, projected.NextStep }
                .All(text => !string.IsNullOrWhiteSpace(text) && text.Length <= 32768);
        if (!schema || sourceState == CheckState.Partial && projected.State == CheckState.Available ||
            sourceState is not (CheckState.Available or CheckState.Partial) && (projected.State is CheckState.Available or CheckState.Partial))
        {
            report.Fail(check, "Inspection evidence contradicted its typed area/source/time/coverage schema. No provider values exported or health inferred.");
            return;
        }
        string detail = $"sourceState={sourceState}; projectedState={projected.State}; schemaValid=true; collectionTimesPreserved=true; actualNonemptySource={actualRows}. " +
            "Actual read result projected in memory only; no source values, drive capacities, security settings, messages or times exported. Collection time is not provider freshness, health, protection, complete storage ownership or action permission.";
        if (actualRows && projected.State is CheckState.Available or CheckState.Partial) report.Pass(check, detail);
        else report.Unknown(check, detail);
    }
}
