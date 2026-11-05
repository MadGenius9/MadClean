using System.Globalization;

namespace MadClean.Core;

/// <summary>Pure, minimized projection of already observed explicit checks; never dispatches work.</summary>
public static class DiagnosticIntegrityEvidence
{
    public static DiagnosticInspectionEvidenceEntry ComponentStore(ComponentStoreStatusIntent? intent,
        ComponentStoreStatusResult? result, DateTimeOffset observedUtc)
    {
        var area = intent?.Kind == ComponentStoreCheckKind.ScanHealth
            ? DiagnosticEvidenceArea.ComponentStoreScan : DiagnosticEvidenceArea.ComponentStoreRecordedFlags;
        if (intent == null || result == null || !Enum.IsDefined(intent.Kind) || !Enum.IsDefined(result.Kind) ||
            !Enum.IsDefined(result.State) || !Enum.IsDefined(result.Flag) || !Enum.IsDefined(result.ScanFinding) ||
            intent.RequestId == Guid.Empty || result.RequestId != intent.RequestId || result.Kind != intent.Kind ||
            !Times(intent.PreparedUtc, intent.ExpiresUtc, result.AttemptedUtc, result.CompletedUtc, observedUtc) ||
            result.Transport == null || !ComponentStoreStatusPolicy.IsTransportConsistent(result.Transport) ||
            result.Evidence != null && !ComponentStoreStatusParser.IsConsistent(result.Evidence, intent.RequestId, intent.Kind))
            return Unknown(area);

        var assessed = ComponentStoreStatusPolicy.Assess(intent, result.Evidence, result.Transport,
            result.AttemptedUtc, result.CompletedUtc, result.State == CheckState.Cancelled);
        if (assessed.State != result.State || assessed.Flag != result.Flag || assessed.ScanFinding != result.ScanFinding)
            return Unknown(area);
        if (result.State != CheckState.Available)
            return NoCurrent(area, result.State, result.AttemptedUtc, result.CompletedUtc);

        string finding = intent.Kind == ComponentStoreCheckKind.RecordedFlags ? result.Flag switch
        {
            ComponentStoreRecordedFlag.NoRecordedCorruptionFlag => "No recorded component-store corruption flag reported.",
            ComponentStoreRecordedFlag.RecordedRepairable => "Recorded component-store repairable-corruption flag reported.",
            ComponentStoreRecordedFlag.RecordedNonRepairable => "Recorded component-store non-repairable-corruption flag reported.",
            _ => ""
        } : result.ScanFinding switch
        {
            ComponentStoreScanFinding.NoCorruptionReported => "The selected fresh component-store scan reported no corruption.",
            ComponentStoreScanFinding.RepairableCorruptionReported => "The selected fresh component-store scan reported repairable corruption.",
            ComponentStoreScanFinding.NonRepairableCorruptionReported => "The selected fresh component-store scan reported non-repairable corruption.",
            _ => ""
        };
        if (finding.Length == 0) return Unknown(area);
        return DiagnosticInspectionEvidence.NotChecked(area) with
        {
            State = CheckState.Available, AttemptedUtc = result.AttemptedUtc, CompletedUtc = result.CompletedUtc,
            Observation = finding,
            Coverage = intent.Kind == ComponentStoreCheckKind.RecordedFlags
                ? "Qualified recorded flags from this explicit CheckHealth request only; no fresh scan occurred. The prior flag or scan date is unknown."
                : "Qualified finding from this explicit ScanHealth request only; no system-file verification, per-file result, repair or restart is inferred."
        };
    }

    public static DiagnosticInspectionEvidenceEntry Sfc(SfcVerificationIntent? intent,
        SfcVerificationResult? result, DateTimeOffset observedUtc)
    {
        const DiagnosticEvidenceArea area = DiagnosticEvidenceArea.SystemFiles;
        if (intent == null || result == null || intent.RequestId == Guid.Empty || result.RequestId != intent.RequestId ||
            !Enum.IsDefined(result.State) ||
            !Times(intent.PreparedUtc, intent.ExpiresUtc, result.AttemptedUtc, result.CompletedUtc, observedUtc) ||
            result.Transport == null || !SfcVerificationPolicy.IsTransportConsistent(result.Transport) ||
            result.Transport.ClientStartObserved == true && !result.Transport.ClientStartedUtc.HasValue) return Unknown(area);
        var assessed = SfcVerificationPolicy.Assess(intent, result.Transport, result.AttemptedUtc, result.CompletedUtc);
        if (assessed.State != result.State) return Unknown(area);
        if (result.State != CheckState.Available)
            return NoCurrent(area, result.State, result.AttemptedUtc, result.CompletedUtc);
        // Available qualifies the client observation only. Every numeric exit leaves integrity Unknown.
        return DiagnosticInspectionEvidence.NotChecked(area) with
        {
            State = CheckState.Available, AttemptedUtc = result.AttemptedUtc, CompletedUtc = result.CompletedUtc,
            Observation = "SFC client exit observed; protected-file integrity remains Unknown.",
            Coverage = $"The selected /verifyonly client exited with numeric code {result.Transport.ClientExitCode!.Value.ToString(CultureInfo.InvariantCulture)}; stdout and stderr were drained and ownership released. These are client observations, not proof of successful verification. No client output is retained."
        };
    }

    private static DiagnosticInspectionEvidenceEntry Unknown(DiagnosticEvidenceArea area) =>
        DiagnosticInspectionEvidence.NotChecked(area) with
        {
            Observation = "No qualified current assessment is available; request, time or source evidence was absent or inconsistent.",
            Coverage = "No result values or provider messages were retained."
        };

    private static DiagnosticInspectionEvidenceEntry NoCurrent(DiagnosticEvidenceArea area, CheckState state,
        DateTimeOffset attempted, DateTimeOffset? completed) => DiagnosticInspectionEvidence.NotChecked(area) with
        {
            State = state, AttemptedUtc = attempted, CompletedUtc = completed,
            Observation = "This request did not establish a current assessment. Integrity remains Unknown.",
            Coverage = "Any output or exit facts are historical only; no current finding or completion is inferred."
        };

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Times(DateTimeOffset prepared, DateTimeOffset expires, DateTimeOffset attempted,
        DateTimeOffset? completed, DateTimeOffset observed) =>
        Utc(prepared) && Utc(expires) && expires > prepared && Utc(attempted) && attempted >= prepared &&
        completed is { } end && Utc(end) && end >= attempted && Utc(observed) && end <= observed && observed <= DateTimeOffset.UtcNow;
}
