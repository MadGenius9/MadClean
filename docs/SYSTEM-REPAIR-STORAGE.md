# Repair journal storage integration

The managed repair coordinator now has a concrete storage-session bridge, plus an internal Windows adapter to the existing private journal owner. This advances storage integration; its later [native candidate connection](SYSTEM-REPAIR-SERVICING-BACKEND.md) remains disabled, and the later public review facade stays independently guarded. This storage increment adds no startup, inventory or validation-mode hook.

Production creation remains closed by the existing `SystemToolJournalPrivateStore.ProductionCreationValidated` guard. The Windows production factory checks that immutable false property before platform, profile/path, identity, worker admission or native IO. There is no setting, environment variable, caller acknowledgment or fixture flag that enables it. Existing repair execution, MSI execution, non-repair servicing admission and private-store gates are unchanged.

## Exact readback before acknowledgment

`SystemRepairStoredJournalSession` derives from the coordinator's existing journal session and owns an `ISystemRepairSnapshotStore` for the same opaque execution context reference. A foreign context is refused before callbacks. Later mismatches or throwing context getters permanently lose ownership qualification; restoring an equal GUID or the earlier reference cannot rehabilitate it or authorize cleanup of a foreign owner.

Each write submits a copied read-only whole snapshot extending the confirmed prefix. Existing journal grammar and size limits remain unchanged. The returned review must be internally consistent and match the exact canonical bytes for every proposed entry, including execution/operation, sequence, phase, time and optional client/error facts. Unknown, missing, stale, shortened, extra, conflicting or malformed-tail evidence cannot acknowledge persistence. Store messages are not parsed or used as evidence. An unsuccessful callback becomes sticky uncertainty; there is no retry, tail salvage, truncation or reset.

Only after this comparison does the base session mint its existing exact-session/entry receipt. No public caller supplies a durability boolean or decoded-history permission. The store remains a trusted implementation boundary: typed semantic readback cannot authenticate a dishonest writer or inspect bytes that writer conceals. The actual Windows owner must provide its strict whole-file readback, not a reconstructed expected review.

Release is one memoized attempt that waits for actual pending persistence. False, exceptions and context loss remain uncertain. A concurrent close/refusal cannot race an in-flight write into a positive acknowledgment. The coordinator retains the actual store/session/task owner through these operations. Confirmed resource cleanup does not clear an unresolved persistent-history block.

## Windows adapter

`WindowsSystemRepairJournal` admits one statically rooted storage owner before scheduling any work. That holder keeps the context, private store, session and actual open/write/release tasks. Cold worker tasks are registered before start; allocation, continuation-registration or scheduling failure retains the uncertain owner. No replacement workers are spawned while an owner is pending or unreleased.

The first snapshot uses the existing `BeginIntent`, including its bounded complete-history review. Unknown or unresolved history blocks a new intent. Later snapshots use existing `AppendSnapshot`, preserving exact prior entries, same-handle append, flush-to-disk request and whole-file readback. An empty/terminal-only history removes one historical block; it does not authenticate a record, prove global absence or grant action permission.

The adapter independently freezes and validates snapshots before worker access and compares the actual returned prefix/readback. Invalid input becomes sticky, and it rechecks closing/failure/cleanup uncertainty after IO before accepting a result. Writes are serialized; there is no overwrite, deletion, source fallback or new native API. Once a write is admitted its owning worker finishes actual IO rather than force-closing on caller cancellation.

Release waits for actual open/write tasks, makes one checked private-store disposal attempt, and clears the static owner only after cleanup and task settlement are confirmed. Any native descriptor/handle cleanup uncertainty stays rooted and blocks another owner. In-flight waits occur on owned workers, not the UI thread. This is storage ownership, not servicing cancellation or machine-wide exclusion.

The separate internal fixture factory accepts only structurally present root/marker arguments and delegates to the existing GUID-marked owned fixture scope. It cannot silently select the production route. It is not wired to a new harness mode or authorized for an uncoordinated VM run. The production guard, namespace/parent-security claim ceiling and CI-bound fixture restrictions remain intact.

## Controlled tests and remaining work

Portable tests exercise the actual coordinator through the new bridge, exact readback mutations, immutable snapshots, foreign/away-back contexts, pending writes/releases, cleanup failure and owned temporary FileStream append/Flush(true)/same-handle readback. Partial writes and malformed tails must prevent dispatch. These files are development fixtures, never personal data. Linux flush/readback is not Windows ACL/namespace, hardware durability, power-loss recovery or native storage qualification.

The new Windows adapter is compiled and independently source-reviewed; its actual native behavior remains unverified. Existing private-store evidence does not automatically qualify this integration. A future production bridge still needs the private parent/namespace creation contract, source-matched owned Windows tests and explicit qualification. The repair dispatcher additionally needs qualified shared native admission, executable/privilege/consent ownership, actual client/result/log handling, crash/restart reconciliation and public workflow validation. No unsafe legacy dispatcher or unsupported internal-API fallback is connected by this increment.

The frozen `cd1718b` neutral test tree and manifest are independent of these newer changes and must remain byte-for-byte unchanged. No external transfer, Windows/VM/PC execution, GitHub activity, ZIP or release package is part of this work.

The later shared/native candidate composes this storage bridge with process-rooted servicing admission and fixed client ownership. It does not clear the private production-creation guard or qualify native dispatch; see SYSTEM-REPAIR-SERVICING-BACKEND.md for current direct-launch source and remaining native qualification/security limits.


The subsequent [relative regular-file candidate](SYSTEM-TOOL-RELATIVE-FILE-CANDIDATE.md) advances only the missing held-parent file primitive and scoped inert codec test source. Its native guard stays false and no new fixture runs. Production first-parent admission and this adapter's actual Windows qualification remain separate unfinished dependencies; no production storage path is redirected to the candidate.
