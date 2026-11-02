# Versioned operation-record prototype

This increment is an owned development prototype, not a production operation journal. It adds no UI action, LocalAppData access, servicing command, elevation, restart or runtime execution-gate override. The five existing operation identifiers are schema data in these tests; no corresponding Windows tool is dispatched.

`SystemToolJournal` accepts one operation with at most three immutable entries and 4,096 UTF-8 bytes. Version1 requires every field, canonical enum/GUID/UTC representations, one execution/operation, sequences1–3, monotonic nonfuture timestamps and consistent PID/nullable start metadata. Unknown/duplicate/extra/missing fields, malformed UTF-8/JSON, unsupported versions and conflicting terminal facts remain Unknown with no decoded entries or raw errors.

DispatchIntent, ClientObserved and ObservationLost are Unresolved. They do not establish that a process is running or stopped. ClientExited requires the recorded PID/start to match and retains a signed exit code; RecordedClientExit is neither health nor action permission. LaunchDenied requires the recorded native cancellation code1223 and no client facts. Valid bytes do not prove authorship.

Append-only files contain up to three LF-terminated snapshots. Each snapshot adds exactly one entry and retains the entire preceding immutable prefix. Every byte and line is validated. A malformed/truncated tail makes the whole log Unknown; the decoder never salvages an earlier successful prefix or treats empty decoded entries as no operation.

The Windows store uses a separate fixed Lifecycle leaf, isolated from its Primary fixed-data tests. The first intent can be written only to the newly created empty file, once. Appending requires a valid complete existing log and unchanged history. It appends canonical JSON plus LF at the observed EOF, enforces the whole-file cap, flushes and verifies exact same-handle bytes plus decoded history. It never truncates, overwrites, repairs or resets a journal. Once writing starts, owned flush/readback finishes before honoring late cancellation; cancellation is not rollback. An uncertain write/readback prevents further positive use of that lease.

## Owned Windows validation contract

The fixed `journal-intent-exit` harness child creates only inert schema data, verifies its flushed/read-back intent, and deliberately self-exits37 while its store lease is held, skipping managed finally cleanup. The parent must observe the actual exit and exact child files, then reopen the existing record as Unresolved. This does not launch a real servicing client or prove crash/power-loss durability.

The parent also rejects a changed historical prefix with actual bytes unchanged, appends an inert ObservationLost payload, then appends exactly one owned malformed tail byte without truncation. Subsequent whole-log reads must be Unknown and refused appends must preserve all bytes. Independent bounded audits borrow the synchronous file handle, preserve/verify its native cursor and clear buffers. Cursor or resource uncertainty stops further I/O and retains the exact owned store/tree and participants; prior uncertainty cannot be reset.

Independent review found no material blocker in the portable model, store seam or fixture ownership. Local build passed with zero warnings/errors; all1,104 portable tests passed. Python129 total:128 passed/one Windows-only skip. Actual Windows lifecycle evidence is recorded below. Existing baseline/cross-process store results are documented separately in [store evidence](SYSTEM-TOOL-JOURNAL-STORE.md).

Production fixed per-user scope, durable unresolved-operation reconciliation, bounded UAC dispatch, actual servicing/result interpretation and user-reviewed runtime validation remain unfinished. A persisted PID is never reopened as identity proof. This prototype does not enable another operation from a terminal-looking record.


## Actual owned Windows lifecycle

[Run 41369687422](https://github.com/MadGenius9/MadClean/actions/runs/41369687422), source `38ec883`, evidence `aa401f3a4922bea1ed1af4ae684f55e833918c96`, passed the actual owned lifecycle fixture: writer self-exit37 after intent flush/readback, existing Unresolved intent, rejected changed prefix with unchanged bytes, inert ObservationLost append, malformed-tail whole-log Unknown/empty and further append refusal with unchanged bytes. Cursor/native/participant cleanup and final parent teardown passed. This is observed deliberate self-exit behavior, not crash/power-loss durability or production recovery.

Core1,094 Passed/10 NotExecuted reconciled1,104. There were55 scoped passes, zero failures and seven inconclusive checks, including unavailable DISM module metadata. Build/SDK/control/tooling passed; packaging was skipped. No servicing client, UAC, production journal or action gate was enabled.
