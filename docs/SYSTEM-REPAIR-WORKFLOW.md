# Guided repair orchestration protocol

This increment implements executable **managed orchestration** for the two fixed future repairs: DISM RestoreHealth and SFC scannow. It is not a repair preview feature, Windows repair backend, enabled UI action or runtime qualification. The command semantics remain the already reviewed fixed [system-tool catalogue](SYSTEM-TOOL-ACTIONS.md); no new command, argument, source, script, log destination, offline target, restart or elevation mechanism is introduced.

A concrete [Windows candidate backend](SYSTEM-REPAIR-SERVICING-BACKEND.md) now implements the interface behind literal false guards; there is no qualified production backend or App/startup/inventory execution wiring. Tests use controlled in-memory collaborators only. Existing Windows repair execution and production journal creation remain literal false. The existing qualified non-repair `SystemToolAdmissionGate` is unchanged and still excludes repairs. A test collaborator cannot enable those Windows gates. The optional TimeProvider seam controls managed protocol admission tests only; it supplies no Windows observations, native permissions or gate override.

## Exact request and admission

`SystemRepairWorkflow.Prepare` accepts only DismRestoreHealth or SfcScanNow and a bounded UTC review lifetime of two minutes. The immutable request carries the fixed catalogue definition, unique identity and source generation. Confirmation requires the exact request/operation/generation plus separate explicit operation, effects, elevation and observation-limit acknowledgments. Wrong, invalidated, stale, unconfirmed, repeated or pre-cancelled requests cannot dispatch. An admitted attempt consumes its request once, including a later refusal.

The backend's closed execution gate is checked before admission, identity, journal or dispatch access. A managed cooperating admission domain serializes these protocol owners. It is separate from native permission, machine-wide exclusion and existing non-repair servicing admission. The new candidate composes its managed domain with the existing process-rooted shared servicing gate through a separate reserved-context route. Native qualification and broader coordination remain missing; creating a fresh domain is not recovery from a blocked one.

The mutable owned state is allocated before admission and rooted immediately by that domain. It retains the actual backend, opaque context, journal/client sessions and in-flight open/write/dispatch/observation/release tasks before awaits. Caller-task abandonment cannot discard resources while the domain remains rooted. The same owner is retained on uncertain cleanup; there is no reset, disposal shortcut or late callback that clears a blocked domain.

## Durable intent before dispatch

The coordinator creates the execution context and exact DispatchIntent record. A journal session must belong to that actual context reference. Its base wrapper validates the existing bounded journal grammar and mints an internal receipt only after its trusted implementation reports durable persistence. The receipt binds the exact session, context, record reference, execution ID, operation, sequence and intent phase; a caller cannot submit a success boolean or decoded historical record as dispatch permission.

This is a trusted implementation boundary, not cryptographic proof that a virtual writer performed IO. A real writer still needs qualified same-handle persistence, namespace/security, readback, history reconciliation and durability semantics. RefusedBeforeWrite is appropriate only when the implementation can establish no unresolved persistent effects; an empty created file or ambiguous write cannot be called a safe refusal. Failure/uncertainty is never retried or repaired by salvaging an earlier prefix.

Only a current one-shot request and its exact durable receipt admit dispatch. The opaque permit is claimed once for the same context. Cancellation, invalidation or expiry during the preceding asynchronous journal work prevents dispatch. After commitment, a caller stop cannot revoke the permit, kill Windows work or promise rollback. An ambiguous dispatch is never automatically retried.

## Observation, history and ownership

Actual client observation starts immediately after accepting a bound session and remains owned through journal/publication failures. Postcommit cancellation does not cancel the underlying dispatch or completion task. Foreign sessions are neither observed nor released as another owner's resources. Missing, faulted, stopped or inconsistent client completion cannot qualify actual settlement; uncertain client ownership remains retained rather than calling a cleanup method as if exit had been established.

Existing journal phases remain unchanged: DispatchIntent, then ClientObserved and ClientExited/ObservationLost; or DispatchIntent followed by ObservationLost. No synthetic native denial code1223 is invented. Cancellation after durable intent but before native dispatch may truthfully report DispatchAttempted=false while history remains Unresolved. That historical block is distinct from pending native resources; clean resource release does not erase it.

Client exit/code, caller stop, release confirmation and journal/history state remain independent. Cleanup or persistence failure preserves accepted historical client observations while blocking later work. Exit zero never becomes successful repair, healthy Windows, rollback or a restart authorization. The result's RepairSucceeded and Integrity are permanently Unknown. Numeric codes and record validity cannot authorize another action.

## Remaining native work and validation

A disabled native adapter is implemented in source; production readiness remains missing. It must establish actual privilege/consent and executable/host provenance, production private-parent/namespace security, shared admission with other servicing routes, durable dispatch/restart reconciliation, supported result semantics and protected-log privacy. No PID reopening is an ownership substitute. DISM restart suppression and SFC's distinct lack of an invented restart flag remain as documented in the fixed catalogue.

Portable asynchronous fixtures can verify protocol ordering, race handling, exact ownership, request replay refusal, pending tasks and outcome uncertainty. They cannot establish real Windows execution, storage durability, power-loss recovery, UAC, policy, native servicing cancellation, actual repair success or final public UI behavior. No VM is available; none was provisioned, and no action ran on a personal PC or GitHub runner. Native/action/release gates remain open and the roadmap incomplete.

The new [storage-session bridge and internal Windows adapter](SYSTEM-REPAIR-STORAGE.md) now connect this protocol to complete canonical readback and the existing private-store lifecycle. Production creation remains disabled and native/runtime integration remains unqualified; no repair backend or public execution hook is supplied.
