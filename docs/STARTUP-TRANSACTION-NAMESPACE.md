# Owned startup transaction namespace observations

This development-only experiment records registry rename/replacement behavior inside four fresh GUID-owned inert subtrees. It never opens real startup entries, writes startup commands, commits interference transactions, creates registry links or enables production startup actions. Ordinary inspection is unchanged.

Existing owned fixtures separately established the documented ordinary-handle/NULL-subkey `RegOpenKeyTransactedW` route, rollback/commit controls and ordinary same-value interference. Those observations leave ancestor changes and same-path replacement unresolved. This matrix compares the following fresh cases:

| Case | Handles enlisted | Controlled ordinary operation |
| --- | --- | --- |
| SourceRenameSourceOnly | Source | Rename Source through its retained ordinary handle |
| AncestorRenameSourceOnly | Source | Rename its owned ancestor |
| AncestorRenameAll | Fresh owned root, ancestor and Source | Repeat ancestor rename |
| SourceReplacementAll | Fresh owned root, ancestor and Source | Rename Source away, then create a new Source with the same inert DWORD/owner marker |

Only the root, ancestor and source created for that case are described as owned ancestors. The shared development prefix and outside registry hierarchy are not enlisted or claimed isolated. Every original ordinary handle stays retained after enlistment. Initialization establishes exact owner markers and child/value topology before the transaction. During a transaction, intentional ordinary operations are limited to the selected interference sequence; no ordinary metadata/value/topology read occurs until terminal settlement and checked transaction/enlisted-handle closure.

The first `GetTransactionInformation` outcome is queried before any explicit rollback or handle closure, and timed against the five-second transaction timeout from before creation. Outcome 1 is **Undetermined**, not proven active. Outcome 3 is an observed abort, without attributing its cause solely to a particular operation. Case four's first outcome follows the entire controlled replacement sequence; rename/create/write results are retained separately. Missing, failed, timed-out or incomplete interference remains inconclusive. An aborted transaction alone does not establish namespace protection or immutable key identity.

Interference transactions are never committed. Explicit rollback and final outcome checks establish a terminal state before ordinary readback or teardown. Unknown outcome, unowned replacement, unexpected contents, close failure or incomplete initialization retains the worker's native handles, registry evidence and whole outer fixture tree. There is no force-close/deletion fallback. The caller waits at most thirty seconds while an owned worker has a cooperative twenty-five-second budget; blocked native calls remain worker-owned beyond the caller wait. A later invocation cannot clear prior retained evidence.

A Recorded case requires successful controlled interference, first outcome 1 or 3 observed before timeout, later verified terminal settlement, original inert baseline and exact nonrecursive teardown. The aggregate check records four such sequences; it does not require the hypothesis that every interference should abort. Each case's first result and flags survive the fixed report export bound; exceeding that bound produces inconclusive evidence instead of silently truncating a pass. No registry path, owner nonce, command, SID or value contents are exported.

## Official semantics

Official Microsoft documentation was rechecked during this increment:

- [RegRenameKey](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regrenamekey): NULL selects the handled key itself; subtree renaming requires relevant write/create/delete access.
- [RegOpenKeyTransactedW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regopenkeytransactedw): handle-relative enlistment and key-specific interference language; subkeys need their own enlistment.
- [GetTransactionInformation](https://learn.microsoft.com/en-us/windows/win32/api/ktmw32/nf-ktmw32-gettransactioninformation) and [TRANSACTION_OUTCOME](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-transaction_outcome): first observed outcome is distinct from later rollback cleanup.
- [RegDeleteKey](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regdeletekeyw): deletion remains pending while handles remain open; the fixture does not close original handles to force interference.

Source review found no material correctness/ownership blocker. Actual Windows evidence is pending for this source. These observations cannot satisfy all production startup source-binding, REG_LINK/ancestor, durable private journal, reversible action or recovery gates. Both real startup mutation methods remain unavailable.
