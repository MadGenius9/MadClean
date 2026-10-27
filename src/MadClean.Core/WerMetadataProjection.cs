namespace MadClean.Core;

/// <summary>Validates minimized existing-store observations without interpreting them as crashes/health.</summary>
public static class WerMetadataProjection
{
    public const int MaximumRecordsPerStore = 128;
    private static readonly WerMetadataStore[] Stores = [WerMetadataStore.MachineQueue, WerMetadataStore.MachineArchive];

    public static CheckResult<WerMetadataReport> Create(DateTimeOffset startUtc, DateTimeOffset endUtc,
        IReadOnlyList<WerMetadataStoreSnapshot> snapshots, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (startUtc.Offset != TimeSpan.Zero || endUtc.Offset != TimeSpan.Zero ||
            !new[] { TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30) }.Contains(endUtc - startUtc) ||
            snapshots == null || snapshots.Count > 4)
            return CheckResult<WerMetadataReport>.Empty(CheckState.Unknown, "WER metadata boundaries/sources are invalid; no store observations were trusted.");
        List<WerMetadataObservation> observations = [];
        List<WerMetadataStoreStatus> statuses = [];
        List<string> coverage =
        [
            $"Explicit local machine queue/archive metadata, fixed UTC creation-time interval [{startUtc:O}, {endUtc:O}). At most {MaximumRecordsPerStore} report keys inspected per store; enumeration order is not a documented chronological order.",
            "Report creation time is not necessarily incident time. Observations are stored report references, not distinct crashes, confirmed diagnoses, upload-success proof or a health verdict. Stores may change, move, purge or retain only part of their history; no records does not establish no incidents.",
            "Only report GUID and creation time are projected. Category is the generic Stored WER report label, not an app-authored diagnosis. No report keys, signatures/event names/parameters, app names/paths, filenames, buckets, raw Report.wer contents or dumps are displayed/exported/logged. No WER submission, archive/delete/purge, process launch or filesystem sweep occurs."
        ];
        bool partial = snapshots.Any(snapshot => snapshot == null || !Stores.Contains(snapshot.Store));
        int completed = 0, unavailable = 0, unsupported = 0, failed = 0, cancelled = 0;
        if (partial) coverage.Add("An unrecognized store response was omitted; only the two machine stores are in scope.");
        foreach (var store in Stores)
        {
            token.ThrowIfCancellationRequested();
            string name = Label(store);
            var matches = snapshots.Where(snapshot => snapshot != null && snapshot.Store == store).ToArray();
            if (matches.Length != 1)
            {
                partial = true; coverage.Add($"{name}: {(matches.Length == 0 ? "missing" : "duplicate/ambiguous")} source response; coverage unknown.");
                statuses.Add(new(store, CheckState.Unknown, 0, 0, 0, 0, false, null,
                    matches.Length == 0 ? WerMetadataStoreReason.MissingSourceResponse : WerMetadataStoreReason.AmbiguousSourceResponse));
                continue;
            }
            var source = matches[0];
            if (!Enum.IsDefined(source.State) || !Enum.IsDefined(source.Reason) ||
                source.Records == null || source.InspectedKeys < 0 || source.InspectedKeys > MaximumRecordsPerStore ||
                source.MetadataFailures < 0 || source.MetadataFailures > source.InspectedKeys ||
                source.Records.Count > source.InspectedKeys || source.Records.Count > MaximumRecordsPerStore ||
                (source.Reason is WerMetadataStoreReason.SuccessfulOpenReturnedNullHandle or WerMetadataStoreReason.SuccessfulEnumerationReturnedNullKey &&
                    (source.State != CheckState.Unknown || source.LastNativeResult != 0)))
            {
                partial = true; coverage.Add($"{name}: inconsistent/bounded metadata counts or diagnostic state; source observations were not trusted.");
                statuses.Add(new(store, CheckState.Unknown, 0, 0, 0, 0, false, source.LastNativeResult ?? source.NativeError,
                    WerMetadataStoreReason.InvalidSourceResponse));
                continue;
            }
            WerMetadataStoreStatus Status(int projected = 0) => new(store, source.State, source.InspectedKeys, source.Records.Count,
                projected, source.MetadataFailures, source.Truncated, source.LastNativeResult ?? source.NativeError, source.Reason);
            string code = source.NativeError.HasValue ? $" Native code 0x{unchecked((uint)source.NativeError.Value):X8}." : "";
            if (source.State is not (CheckState.Available or CheckState.Partial))
            {
                partial = true;
                switch (source.State)
                {
                    case CheckState.Unavailable: unavailable++; break;
                    case CheckState.Unsupported: unsupported++; break;
                    case CheckState.Failed: failed++; break;
                    case CheckState.Cancelled: cancelled++; break;
                }
                coverage.Add($"{name}: {source.State}. " + (source.State switch
                {
                    CheckState.Unavailable => "Store is inaccessible, absent or access-denied; check existing WER read permissions. No elevation was requested.",
                    CheckState.Unsupported => "Required local store API/platform support is unavailable; no filesystem fallback was used.",
                    CheckState.Failed => "Store call failed; existing metadata coverage is unknown. Retry after checking local provider access.",
                    CheckState.Cancelled => "Collection did not complete; no empty-store conclusion is valid.",
                    _ => "Source was not completed or returned an unrecognized state; no records trusted."
                }) + code);
                statuses.Add(Status());
                continue;
            }
            completed++;
            if (source.State == CheckState.Partial || source.Truncated || source.MetadataFailures > 0 || source.NativeError.HasValue) partial = true;
            var accepted = new Dictionary<Guid, DateTimeOffset>();
            var ambiguous = new HashSet<Guid>();
            int invalid = 0, outside = 0, duplicates = 0;
            foreach (var row in source.Records)
            {
                token.ThrowIfCancellationRequested();
                if (row == null || row.ReportId == Guid.Empty || !Created(row.CreationFileTimeUtc, out var created))
                { partial = true; invalid++; continue; }
                if (created < startUtc || created >= endUtc) outside++;
                if (ambiguous.Contains(row.ReportId)) { duplicates++; continue; }
                if (accepted.TryGetValue(row.ReportId, out var prior))
                {
                    duplicates++; partial = true;
                    if (prior != created) { accepted.Remove(row.ReportId); ambiguous.Add(row.ReportId); }
                    continue;
                }
                accepted.Add(row.ReportId, created);
            }
            var withinWindow = accepted.Where(row => row.Value >= startUtc && row.Value < endUtc).ToArray();
            statuses.Add(Status(withinWindow.Length));
            observations.AddRange(withinWindow.Select(row => new WerMetadataObservation(name, row.Key, row.Value,
                "Stored WER report", $"WerStoreQueryReportMetadataV1 / {name}")));
            coverage.Add($"{name}: {source.State}; {source.InspectedKeys} keys inspected, {withinWindow.Length} usable references in the selected creation-time window, {outside} outside-window records, {source.MetadataFailures} metadata query failures, {invalid} invalid records, {duplicates} duplicate/conflicting references omitted." +
                (source.Truncated ? " TRUNCATED/limited: additional or unattempted records may match; this is not complete historical coverage." : " Enumeration remains a non-atomic current-store observation, not complete incident history.") +
                (source.MetadataFailures > 0 ? " Some report metadata was unreadable or changed; check existing WER read permissions. No elevation was requested." : "") + code);
        }
        if (observations.GroupBy(row => row.ReportId).Any(group => group.Select(row => row.Store).Distinct().Count() > 1))
        { partial = true; coverage.Add("A report GUID appeared in both stores; entries are source-specific observations and must not be added into a unique incident/crash count. Stores can change during collection."); }
        token.ThrowIfCancellationRequested();
        var state = completed > 0 ? partial ? CheckState.Partial : CheckState.Available
            : failed > 0 ? CheckState.Failed
            : cancelled > 0 ? CheckState.Cancelled
            : unsupported == 2 ? CheckState.Unsupported
            : unavailable > 0 ? CheckState.Unavailable : CheckState.Unknown;
        var report = new WerMetadataReport(startUtc, endUtc,
            observations.OrderByDescending(row => row.CreatedUtc).ThenBy(row => row.Store, StringComparer.Ordinal).ToArray(), coverage)
            { StoreStatuses = statuses.ToArray() };
        return new(state, "Existing WER store metadata only. Review source coverage and report creation times; no unique-crash, cause, health, repair or upload-success conclusion is made.", [report]);
    }

    private static bool Created(long fileTime, out DateTimeOffset created)
    {
        created = default;
        if (fileTime <= 0) return false;
        try { created = new(DateTime.FromFileTimeUtc(fileTime)); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
    public static string Label(WerMetadataStore store) => store switch
    {
        WerMetadataStore.MachineQueue => "E_STORE_MACHINE_QUEUE",
        WerMetadataStore.MachineArchive => "E_STORE_MACHINE_ARCHIVE",
        _ => "Unknown store"
    };
}
