# Independent WER store count observations

Diagnostics adds one optional read-only `WerStoreGetReportCount` query on each already-open machine queue/archive store, before the existing report-key enumeration. It runs only when the user selects **Read WER metadata**. Ordinary inventory does not call it. No report is created, submitted, uploaded, archived, purged or deleted; no report file, dump, signature payload or application name is opened or displayed.

A count is the provider's DWORD result for the entire current store, at its separately recorded UTC observation time. It is not filtered to the selected creation-time interval, is not a count of unique crashes and is not atomic with later enumeration. Stores can change or retain only some reports. Zero does not establish no incidents, no reports in a different source or a healthy system.

The optional export is discovered independently from the existing metadata-reader requirements. Missing support leaves count unknown and preserves the existing reader. Only an actual HRESULT of zero exposes the returned DWORD; nonzero results keep count null and retain the literal result separately. Native calls share the existing single worker, cooperative budget and cancellable caller wait. Interruption does not close native handles early or mean the synchronous call stopped.

`WerMetadataCountObservation` carries only nullable count, actual observation time and native result. Snapshot/status constructors remain compatible through nullable init properties. Projection rejects contradictory result/count or invalid/future/non-UTC times, withholding the count without discarding independently qualified metadata. A count never changes enumeration state or reason. In particular, `S_OK` with a null report key remains Unknown: the first/next-key documentation names `ERROR_NO_MORE_FILES` for completion and does not give a null success key empty-store semantics.

The view presents counts separately from the selected report window. Its initial state is unchecked. Query timestamps and counts stay out of the enum-only activity log. CI exports only fixed source names, count/native-result observations and provenance booleans, never keys, GUIDs, report dates, raw metadata or dump data.

## Official sources and validation

Fresh official Microsoft references checked before implementation:

- [WerStoreGetReportCount](https://learn.microsoft.com/windows/win32/api/werapi/nf-werapi-werstoregetreportcount)
- [WerStoreGetFirstReportKey](https://learn.microsoft.com/windows/win32/api/werapi/nf-werapi-werstoregetfirstreportkey) and [WerStoreGetNextReportKey](https://learn.microsoft.com/windows/win32/api/werapi/nf-werapi-werstoregetnextreportkey)
- [REPORT_STORE_TYPES](https://learn.microsoft.com/windows/win32/api/werapi/ne-werapi-report_store_types)
- [Current Microsoft Windows SDK WerApi.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/WerApi.h)

Portable tests cover count zero/positive/DWORD maximum with Unknown enumeration, native failure, contradictory count/result, invalid timestamps, selected-window independence, metadata retention and cancellation. They are not Windows API proof. The existing compile-only SDK ABI probe now also checks the exact native count function signature. A separate required `wer-store-count-contract` uses the same actual inventory result and validates both count observations independently from the existing enumeration check. No reports or crashes are manufactured to force positive results. Source-bound actual results belong in [the execution ledger](ROADMAP-STATUS.md).

Actual [run41340826576](https://github.com/MadGenius9/MadClean/actions/runs/41340826576), source733af5b, evidence87ac233c74925789bf41fd64975dd5af24641963, passed the independent count contract with native0/count0 for both machine stores on the same query. Enumeration remained Unknown/SuccessfulEnumerationReturnedNullKey/native0 in each store; no count promotion occurred. The installed-SDK assertion and initial query-free tool14 count-summary binding/capture passed, and the coordinator inspected its1404x961 image. These are narrow API/initial-render observations, not populated UI interaction, historical completeness, no-crash or health proof. Overall37 scoped passes/two inconclusive,976 core Passed/10 NotExecuted; publish skipped.
