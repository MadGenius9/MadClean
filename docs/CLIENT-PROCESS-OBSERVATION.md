# Client process observation infrastructure

This increment observes an already-started process; it does not enable system-tool execution. The production execution gate remains get-only false. Ordinary inspection and the Diagnostics identity page run no DISM/SFC command, elevation, repair, installation or restart.

The internal Windows observer borrows a caller-owned `Process`. It never starts, kills, closes or disposes that process, reopens it by PID, or captures its output. The caller must retain ownership until observation returns. The observer reads available PID/start metadata, checks actual exit/code before waiting, then checks again after wait completion, cancellation or observation failure. An actually observed exit takes precedence over a simultaneous request to stop monitoring. An unknown exit remains unknown; stopping observation does not establish that the child stopped, cancelled its work or rolled back.

The portable projection keeps a literal exit code, including zero and negative values, separate from health or repair interpretation. Mandatory observation times must be valid UTC, ordered and not future-dated. Optional client-start metadata is independent: invalid or unavailable start time is omitted without discarding a genuine PID/exit observation. The existing run record's `StartedUtc` describes request/preparation time; it is not an actual process-start timestamp. The optional `ClientObservation` carries the separately observed facts.

Only the dormant provider wait uses the new observer. A known exit/code is retained if subsequent local-record persistence or cleanup fails; the result discloses that uncertainty and blocks future operations pending review. This refactor does not establish the private log's durability or enable the unreachable servicing path. Exit code zero alone is never a Windows-integrity or repair verdict.

## Owned validation scope

The separate development fixture launches only the currently running, guarded `MadClean.WindowsSmoke.exe` apphost, using fixed inert child modes: exit zero, exit 23, or wait for an owned release signal. It accepts no arbitrary executable or command. Child arguments must identify an exact marked GUID fixture/child hierarchy in this repository's artifacts directory, with ordinary non-reparse ancestry and matching owner markers. Unknown arguments are rejected before parent output preparation or inventory.

Ready and release signals use fixed bounded contents and CreateNew writes. A waiting child has a cooperative ten-second self-deadline with a distinct exit code 42; parent cancellation never kills it. The planned runtime assertions cover actual zero/nonzero exits, already-exited plus pre-cancelled observation, pre-cancelled/live stop while the child remains alive, then an explicit owned release and actual exit. An unstarted process must remain unavailable. These are inert-process lifecycle assertions, not servicing behavior.

The parent retains bounded ownership of unconfirmed children and its apphost lease. Cleanup is confirmed only after actual exits, owned-file/marker audits and process/lease disposal; otherwise the marked tree is retained and later fixtures are skipped. Closing a process handle is not evidence that its process exited. No populated controls, PID, path, argument, owner marker or process output is exported by this fixture; only fixed states and validation booleans are reported.

## Validation limits

The coordinator reports a passing full solution build and 1,087 portable .NET tests on Linux. Python tooling results are 125 total: 124 passed and one Windows-only skip. Independent source review corrected optional-start metadata so it cannot erase an actual exit and found no remaining material blocker in the reviewed observer, dormant provider mapping, fixed child protocol or ownership guards. These local checks do not establish Windows process or fixture success. New source-bound Windows runtime evidence remains pending.

Private logs, durable reconciliation across application instances, bounded UAC behavior, actual servicing execution, Windows result-log interpretation and tool-specific interruption/recovery remain separate unverified gates. No real DISM/SFC/UAC/repair operation is authorized or exercised by the inert fixture, and this increment does not complete D2 or D3.
