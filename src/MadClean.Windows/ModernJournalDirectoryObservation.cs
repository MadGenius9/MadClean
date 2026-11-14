namespace MadClean.Windows;

internal enum ModernJournalDirectoryDecision { Continue, Complete, Refused }

// Bounded observations of the sample's fixed names only. Not a filesystem snapshot,
// native resource owner, journal authorship check or recovery/admission capability.
internal sealed class ModernJournalDirectoryObservation
{
    private readonly string journalName;
    private ModernJournalDirectoryDecision state = ModernJournalDirectoryDecision.Continue;
    private int queries;
    private int records;
    private int seen;

    internal ModernJournalDirectoryObservation(Guid executionId)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("A nonempty sample execution ID is required.", nameof(executionId));
        journalName = executionId.ToString("N") + ".jsonl";
    }

    internal ModernJournalDirectoryDecision Observe(ModernJournalDirectoryQuery outcome,
        IReadOnlyList<ModernJournalDirectoryName>? entries)
    {
        if (state != ModernJournalDirectoryDecision.Continue)
            return state = ModernJournalDirectoryDecision.Refused; // Completion is observed only once.
        state = ModernJournalDirectoryDecision.Refused; // Exceptions cannot leave a reusable partial observation.
        if (++queries > ModernJournalDirectoryBatch.MaxQueries) return state;
        if (outcome == ModernJournalDirectoryQuery.End)
        {
            if (entries == null && (seen & 3) == 3) state = ModernJournalDirectoryDecision.Complete;
            return state;
        }
        if (outcome != ModernJournalDirectoryQuery.Batch || entries == null ||
            queries == ModernJournalDirectoryBatch.MaxQueries) return state;
        int count = entries.Count;
        if (count is < 1 or > ModernJournalDirectoryBatch.MaxRecords || count > 4 - records) return state;
        int nextSeen = seen;
        for (int i = 0; i < count; i++)
        {
            var entry = entries[i];
            if (entry == null || (entry.Attributes & 0x400) != 0) return state;
            int bit = entry.Name switch
            {
                "lease.bin" => 1,
                "." => 4,
                ".." => 8,
                _ => string.Equals(entry.Name, journalName, StringComparison.Ordinal) ? 2 : 0
            };
            if (bit == 0 || (nextSeen & bit) != 0 ||
                ((entry.Attributes & 0x10) != 0) != (bit is 4 or 8)) return state;
            nextSeen |= bit;
        }
        seen = nextSeen;
        records += count;
        // Four unique allowed records and nonempty batches imply valid input settles
        // within five calls, while the native reader independently retains its eight-call bound.
        return state = ModernJournalDirectoryDecision.Continue;
    }
}
