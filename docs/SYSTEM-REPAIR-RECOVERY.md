# Existing-history recovery controller

This candidate reviews durable journal data in a fresh cooperating admission domain. It does not resume a repair, reopen a recorded PID, infer running/stopped state, clear records, authorize dispatch or repair history. The later guarded public review facade exposes an explicit history button; no automatic startup hook or enabled history read is supplied. Production review has its own literal false guard, separate from repair execution and production journal creation.

## One owned review, one shared lease

`SystemRepairRecovery` memoizes one owned run. A mutable owner is rooted before scheduling and shared admission; the actual open/read/release tasks stay owned through caller departure. The reader guard precedes context, gate and I/O access. Known pre-entry cancellation, unavailable UTC or contention invokes no reader and does not change another gate owner.

Recovery uses a distinct internal lease on the same servicing gate, not a fabricated tool operation. Only a free gate can admit it; pending, retained and history-only owners cannot be reset. Exact lease/resource-owner identity and first completion prevent stale ordinary or repair callbacks from clearing recovery ownership. Windows composition exposes an internal factory using the same gate; construction starts no read.

After the final pre-open stop check, caller cancellation is not passed into owned I/O. Open, read and actual release are awaited. A late stop preserves the observed history but prevents it from qualifying the domain for new work. Stop/result/admission use a single settlement snapshot; cancellation after that point does not rewrite the result.

The session must match the exact recovery context. Foreign sessions are neither read nor released. History is canonically re-reviewed with copied journal entries, bounded complete enumeration and a valid UTC time; reader messages and optimistic state labels are not evidence. Missing, contradictory, partial, malformed or unknown history stays blocked. Complete empty/terminal data removes one historical prerequisite only, after actual cleanup. It does not authenticate an author or establish repair success.

History and live resources remain separate. A bound unavailable reader can return Unknown and release definitely owned resources, producing a history-only block. Generic/unreturned ownership, foreign sessions, scheduling ambiguity or failed cleanup retains the owner and blocks admission without retry. No reset, tail salvage, archival or deletion is supplied.

## Disabled Windows reader

`WindowsSystemRepairRecoveryReader.ProductionReviewValidated` is literal false before token/context/profile/platform/worker access. The compiled candidate uses only `SystemToolJournalPrivateStore.OpenExistingForReview` and `ReadHistory`; it never invokes the production creator, creates directories/lease files, repairs ACLs or appends history. The existing strict fixed-location/ancestry/object/security/budget checks remain unchanged.

A static native owner roots the actual store/session/tasks before cold workers start. The store's exclusive cooperating filesystem lease stays held through reading and checked disposal. Typed source refusals with no returned store and confirmed cleanup yield bound Unknown; generic or cleanup ambiguity stays retained. This reports neither global absence nor a running foreign installer/repair. Release awaits actual open/read workers and checks native cleanup before retirement.

The writer's independent full-history reread under its own exclusive filesystem lease remains mandatory before a new intent. A startup observation is not an atomic future permission grant or a machine-wide lock. Production parent/namespace creation safety, actual restart/power-loss durability, Windows reader qualification and public workflow are still open.

## Validation scope

Controlled tests use newly owned temporary journal files and recreate managed reader/admission domains. They check complete readback, unresolved/malformed/terminal/empty states, unchanged bytes, competing admission, stale completion, cancellation, actual pending read/release, failed cleanup and caller departure. This is not an OS-process restart, Windows ACL/namespace test or crash/power-loss evidence.

Final local Linux SDK10.0.403 Release solution build passed with zero warnings/errors. Core1,984 passed/one Windows-only skipped (1,985 total), including13 new passed recovery cases confirmed in TRX. Python remains290 passed/one Windows-only skipped from the preceding unchanged tooling suite. Independent combined source review cleared the candidate after correcting pre-entry clock failure and post-acquisition cancellation/result timing. Windows reader/lease/ACL/namespace behavior and actual restart durability were not run. No App hook, gate activation, PC/VM operation, GitHub activity, transfer or final package occurred. Frozen cd1718b payload/manifest were verified unchanged after the build.
