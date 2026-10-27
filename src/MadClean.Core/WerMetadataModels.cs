namespace MadClean.Core;

public enum WerMetadataStore { MachineQueue, MachineArchive }
public enum WerMetadataStoreReason
{
    Unspecified, StoreOpenFailed, SuccessfulOpenReturnedNullHandle, EnumerationFailed,
    SuccessfulEnumerationReturnedNullKey, EnumerationComplete, BudgetExpired, RecordLimitReached,
    MetadataQueryFailed, InvalidSourceResponse, MissingSourceResponse, AmbiguousSourceResponse
}
public sealed record WerMetadataStoreStatus(WerMetadataStore Store, CheckState State,
    int InspectedKeys, int MetadataRecords, int ProjectedReferences, int MetadataFailures,
    bool Truncated, int? LastNativeResult, WerMetadataStoreReason Reason);
public sealed record WerMetadataObservation(string Store, Guid ReportId, DateTimeOffset CreatedUtc, string Category, string Source);
public sealed record WerMetadataReport(DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<WerMetadataObservation> Observations, IReadOnlyList<string> Coverage)
{
    public IReadOnlyList<WerMetadataStoreStatus> StoreStatuses { get; init; } = [];
}
public interface IWerMetadataInventory
{
    Task<CheckResult<WerMetadataReport>> ReadAsync(int days, CancellationToken token);
}

// The native adapter projects only these fields. Signature parameters, raw event/app names, report
// keys, filenames, buckets, status/payload fields and dumps have no place in the portable contract.
public sealed record WerMetadataRecord(Guid ReportId, long CreationFileTimeUtc);
public sealed record WerMetadataStoreSnapshot(WerMetadataStore Store, CheckState State,
    IReadOnlyList<WerMetadataRecord> Records, int InspectedKeys, bool Truncated, int MetadataFailures, int? NativeError = null,
    WerMetadataStoreReason Reason = WerMetadataStoreReason.Unspecified, int? LastNativeResult = null);
