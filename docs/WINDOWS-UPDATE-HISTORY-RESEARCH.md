# Windows Update history integration research

Research attempted on 2026-11-11; no integration implemented. Local work remains off GitHub. No client-machine query, update check, download, installation or history alteration ran.

A possible later milestone is a bounded explicit read of supported WUA history to distinguish provider-reported outcomes from current applicability and complete system state. Before implementation, inspect the current official contracts for query bounds, ordering, timestamp timezone, result/operation codes, HRESULT meaning, identity/revision and cleanup. Do not implement these from memory or substitute history for actual MadClean-owned operation results.

These exact public Microsoft references were requested:

- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-queryhistory
- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-gettotalhistorycount
- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iupdatehistoryentry
- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatehistoryentry-get_date

The sandbox prevented the initial network operation. A permitted public-document retry reached the configured proxy but all four HTTPS CONNECT attempts returned 403 Forbidden; no document bytes or current semantics were obtained. This was an environment proxy failure, not an automatic approval-review rejection or proof that WUA history is unavailable. No GitHub, mirror, third-party summary, source fallback or repeated retry was used.

The smallest next research step is access to those official documents, particularly the exact Date timezone and QueryHistory bounds/ordering/error semantics. Additional references may then be required for operation/result enum values, identity/revision and returned-object lifetime. No history/backend gate is enabled by a URL or successful compilation.

## Coordinated official-source retrieval — 2026-11-12

The coordinator retrieved the current official Microsoft references and supplied the following facts. This resolves the source-contract gap above; this workspace has not directly retrieved those document bytes, queried Windows, or qualified the adapter at runtime.

- [QueryHistory](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-queryhistory) is synchronous and returns records in descending chronological order. Its LONG startIndex/count parameters reject a negative start or nonpositive count. The [corresponding Open Specification](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-uamg/4f64f49e-e6dc-4dbb-bbfb-7fb92654ba46) establishes zero-based indexing, rejection when startIndex is outside the current collection and the possibility of fewer returned records than requested. Do not query with count zero.
- [GetTotalHistoryCount](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-gettotalhistorycount) returns the computer's update-event count. Neither reference promises an atomic count-plus-query snapshot. Successful HRESULT means that call succeeded, not that an update succeeded or is currently installed.
- [IUpdateHistoryEntry](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iupdatehistoryentry) describes historical metadata. [UpdateIdentity](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatehistoryentry-get_updateidentity) is a returned interface; its UpdateID and revision are identity metadata, not installed software/driver versions. Repeated attempts may legitimately share that identity.
- The Win32 Date page does not specify timezone, but the [operation-time Open Specification](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-uamg/81f33109-ff5f-4c51-8a1c-412f49c47bab) explicitly defines UTC. [DateTime.FromOADate](https://learn.microsoft.com/en-us/dotnet/api/system.datetime.fromoadate?view=net-10.0) documents the numeric representation, range and invalid-value exception. Representation alone supplies no timezone metadata: preserve the documented UTC wall time without a second local-to-UTC conversion.
- [UpdateOperation](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/ne-wuapi-updateoperation) defines installation=1 and uninstallation=2. Unknown values must remain unknown. OperationResultCode values describe the recorded operation; they do not establish current applicability, current installation or MadClean ownership.
- The [ordered read-only collection](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iupdatehistoryentrycollection) supplies Count and indexed Item. [COM reference-count rules](https://learn.microsoft.com/en-us/windows/win32/com/rules-for-managing-reference-counts) do not justify force-releasing possibly shared managed wrappers or abandoning a synchronous query. The adapter uses an owned short-lived read-only query host, not forced release of shared WPF COM objects.

## Implemented read-only profile and remaining evidence

The additive Update Center history view requests at most 128 recent local WUA records on explicit user command only. It reads bounded title, operation time, operation/result codes, HRESULT and update identity/revision. It requests no description, URLs, client identity or uninstall commands. It does not invoke Search, download, installation, removal, history alteration, elevation or restart, and it is separate from ordinary inspection and offer discovery. No fallback to online update discovery is allowed.

A total-count observation and later collection read are non-atomic. Fewer records are valid; positive reports, including an empty returned history, remain Partial and cannot establish complete historical coverage, a clean system, current installation, current update availability or a MadClean-owned action. Per-field unknown/failure stays visible. The 128-entry and transport/text limits are MadClean policy, not Microsoft native limits.

Cancellation/timeout may terminate the owned read-only query client under the existing bounded process policy. That is not proof a Windows service cancelled its already-dispatched work. No child command output or title/identity is written to the activity log. Actual Windows COM projection, permissions/policy/offline behavior and WPF interactions still require separately coordinated current-source Windows qualification. The old frozen cd1718b UI pass does not qualify this feature.

Local validation for this source slice: independent review found no blocking issues after preserving failed/cancelled attempt times and requiring explicit UTC root timestamps. The Release solution build passed with zero warnings/errors. Core: 2,284 passed, one Windows-only skipped, zero failed; all 49 new history cases passed. A separate process-local `TZ=Pacific/Honolulu` history run also passed all 49 cases without modifying system timezone. Python: 297 passed, one skipped, zero failed. Parser and source review evidence do not execute the PowerShell script, query COM or observe the actual WPF view; those Windows-only checks are not run.
