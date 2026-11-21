using MadClean.Windows;

namespace MadClean.WindowsSmoke;

// Fixed synthetic-fixture evidence only. No journal text, path, SID, identity or native error is serialized.
internal sealed record ExistingReaderCaseEvidence(string Label, string State, bool SetupConfirmed,
    ModernJournalSampleObservation? Observation, bool? SampleVerified, bool? ResourcesReleased,
    bool? HasPendingWork, bool? RequiresRetention, bool? BoundaryMatched, bool? IndependentComparison,
    bool? TeardownConfirmed);

internal sealed record ExistingReaderQualificationEvidence(int FormatVersion, string Context,
    string SourceCommit, string SourceRef, string RunId, int RunAttempt, string Job,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, string State, bool TimeoutObserved,
    bool OwnershipSettled, int? WindowsBuild, bool ProcessX64, IReadOnlyList<ExistingReaderCaseEvidence> Cases,
    string SourceAuthenticity = "unverified", string ReparseCoverage = "unverified");

internal static class ExistingReaderQualificationCases
{
    internal static readonly string[] Labels = ["valid", "missing-lease", "missing-journal", "extra-file",
        "extra-directory", "case-mismatch", "missing-lf"];

    internal static bool Matches(string label, ModernOwnedJournalSampleAttempt attempt)
    {
        var o = attempt.Observation;
        if (!attempt.ResourcesReleased || attempt.HasPendingWork || attempt.RequiresRetention || o.StopObserved ||
            o.Prepare != ModernJournalCallbackOutcome.Confirmed || o.Directory != ModernJournalCallbackOutcome.Confirmed ||
            o.Release != ModernJournalCallbackOutcome.Confirmed) return false;
        var c = ModernJournalCallbackOutcome.Confirmed;
        var r = ModernJournalCallbackOutcome.Refused;
        var n = ModernJournalCallbackOutcome.NotEntered;
        if (label == "missing-lease")
            return !attempt.SampleVerified && o.Lease == r && o.Journal == n && o.Exercise == n &&
                o.Names == ModernJournalNamesObservation.NotObserved;
        if (o.Lease != c) return false;
        if (label == "missing-journal")
            return !attempt.SampleVerified && o.Journal == r && o.Exercise == n &&
                o.Names == ModernJournalNamesObservation.NotObserved;
        if (o.Journal != c) return false;
        if (label == "valid") return attempt.SampleVerified && o.Exercise == c && o.Names == ModernJournalNamesObservation.Complete;
        if (label == "missing-lf") return !attempt.SampleVerified && o.Exercise == r && o.Names == ModernJournalNamesObservation.Complete;
        return label is "extra-file" or "extra-directory" or "case-mismatch" && !attempt.SampleVerified &&
            o.Exercise == r && o.Names == ModernJournalNamesObservation.Refused;
    }
}
