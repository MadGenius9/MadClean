namespace MadClean.Core;

public enum SystemToolJournalHistoryState { UnknownEvidence, UnresolvedPresent, RecordedTerminalOnly, NoRecordsObserved }
public sealed record SystemToolJournalHistory(SystemToolJournalHistoryState State,
    IReadOnlyList<SystemToolJournalReview> Journals, bool EnumerationComplete, string Message);

/// <summary>One cooperating per-user history, never recorded authorship or dispatch authority.</summary>
public static class SystemToolJournalReconciliation
{
    public const int MaximumJournals = 128;
    public static SystemToolJournalHistory Review(IReadOnlyList<SystemToolJournalReview>? journals,
        bool enumerationComplete, DateTimeOffset nowUtc)
    {
        var unknown = new SystemToolJournalHistory(SystemToolJournalHistoryState.UnknownEvidence,
            Array.AsReadOnly(Array.Empty<SystemToolJournalReview>()), false,
            "Journal coverage is unknown. New cooperating intents are blocked; no absence, process state, recorded authorship or dispatch permission is established.");
        if (!enumerationComplete || journals == null || journals.Count > MaximumJournals || nowUtc == default || nowUtc.Offset != TimeSpan.Zero)
            return unknown;
        try
        {
            var copy = new List<SystemToolJournalReview>(); var identities = new HashSet<Guid>();
            int count = journals.Count;
            for (int i = 0; i < count; i++)
            {
                var source = journals[i];
                if (source == null) return unknown;
                var reviewed = SystemToolJournal.Review(source.Entries, nowUtc);
                if (reviewed.State == SystemToolJournalState.Unknown || source.State != reviewed.State ||
                    !identities.Add(reviewed.Entries[0].ExecutionId)) return unknown;
                copy.Add(reviewed);
            }
            if (journals.Count != count) return unknown;
            var state = copy.Any(row => row.State == SystemToolJournalState.Unresolved) ? SystemToolJournalHistoryState.UnresolvedPresent
                : copy.Count == 0 ? SystemToolJournalHistoryState.NoRecordsObserved : SystemToolJournalHistoryState.RecordedTerminalOnly;
            return new(state, Array.AsReadOnly(copy.ToArray()), true, state switch
            {
                SystemToolJournalHistoryState.UnresolvedPresent => "Unresolved records block new cooperating intents. They do not prove a process is running, stopped or safe to replace; no PID is reopened or operation resumed.",
                SystemToolJournalHistoryState.NoRecordsObserved => "No records were observed in this bounded cooperating per-user store. This is not global absence, authorship proof, permission or evidence that an operation never ran.",
                _ => "Only recorded terminal observations were found. This removes one historical block only; records do not authenticate their author, prove operation success or grant dispatch permission."
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        { return unknown; }
    }
}
