# Development-only startup transaction primitive probe

This probe tests a narrow public API prerequisite for later reversible startup actions. It is compiled **only into the opt-in Windows validation harness**, not MadClean's executable or Windows integration assembly. Both production startup mutation methods remain Unavailable. No real Run/RunOnce entries, Startup folders, commands, journal, backup or recovery action are exercised.

## Isolated operation

The worker validates the harness's fixed fixture-directory GUID and ownership-marker file, refuses known filesystem reparse/offline ancestry, and uses the already-existing `HKCU\Software\MadClean.WindowsSmoke` development prefix. Only a newly created exact fixture-GUID child is accepted; an existing child is not overwritten. The root and its single `TxrHandleProbe` child receive fixed owner markers. The only test payload is a fixed inert DWORD under that child. No executable command, user SID or real startup registration is requested.

The source handle is an ordinary non-predefined `RegOpenKeyExW` handle. For each of two fresh local transactions, the worker calls documented `RegOpenKeyTransactedW` with that existing handle, NULL subkey, options0 and NULL extended parameter. It sets/reads the inert DWORD through only the returned enlisted key, closes that key with `RegCloseKey`, then requests rollback or commit.

Both branches query `GetTransactionInformation` and require the expected terminal outcome (Aborted3/Committed2) before closing the transaction and reading the ordinary anchor again. Rollback must preserve the original inert value; commit must expose the selected new inert value. Ordinary reads, owner/topology queries and cleanup do not occur while the transaction outcome is unknown. The documented interference behavior means ordinary key reads during an active transaction would invalidate this test.

CreateTransaction's five-second timeout is not a native-call deadline: its documented automatic abort applies before preparation. Native failure, unavailable support or unknown outcome is inconclusive, not a reason to assume rollback. Closing a transaction handle after an uncertain commit is not proof of abort. No concurrent-writer, link, rename or adversarial namespace case is attempted by this initial probe.

## Resource and teardown ownership

One worker owns every registry/transaction handle, fixed buffer and owned-registry cleanup path. A20-second cooperative budget and30-second cancellable caller wait do not stop synchronous native calls. Caller timeout/cancellation never disposes pending worker resources or starts a second probe. The worker performs terminal-state cleanup even if the caller cancelled; inability to establish a terminal outcome preserves the registry subtree for review.

Registry teardown is nonrecursive and requires exact owner markers, expected single-child topology, fixed value kinds/sizes and no active/uncertain transaction. It closes handles with their correct APIs and verifies removal of only its exact newly created GUID root. If the worker is pending or registry teardown is unverified, the outer harness also retains **all** filesystem fixture contents and the ownership-marker file. It does not remove that evidence underneath a pending worker. The existing whole-harness watchdog remains a development-process limit, not evidence of successful cancellation or cleanup.

The required `startup-transaction-handle-probe` report exports fixed outcome/stage/error codes and rollback/commit/teardown/pending booleans only. It emits no paths, GUIDs, DWORD contents, SID, commands or raw exception/provider text. A scoped pass requires both expected terminal outcomes, ordinary readbacks and completed exact teardown. Missing/pending/inconclusive proof cannot become an overall pass. Windows runtime results must be source-bound; compilation alone is not transaction proof.

## Remaining production gates

A successful probe would establish only the tested sequential ordinary-handle enlistment and inert rollback/commit behavior on that Windows build. It does **not** establish no-follow source binding, ancestor/rename/delete-recreate stability, same-value replacement/concurrent-writer isolation, correspondence between an object and a path-defined startup registration, private durable DPAPI journals, crash/uncertain-commit reconciliation, restore-only-when-absent, stale one-shot previews or cross-resource atomicity. These remain the explicit gates in [binding research](STARTUP-BINDING-RESEARCH.md) and [startup actions](STARTUP-ACTIONS.md). No production action is enabled by a passing fixture or a Windows Settings handoff.

## Official Microsoft contracts consulted

Current official SDK/API sources were rechecked during implementation:

- [RegOpenKeyTransactedW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regopenkeytransactedw): ordinary existing handles, NULL/empty subkeys, transaction association, options and interference semantics.
- [CreateTransaction](https://learn.microsoft.com/en-us/windows/win32/api/ktmw32/nf-ktmw32-createtransaction), [CommitTransaction](https://learn.microsoft.com/en-us/windows/win32/api/ktmw32/nf-ktmw32-committransaction), [RollbackTransaction](https://learn.microsoft.com/en-us/windows/win32/api/ktmw32/nf-ktmw32-rollbacktransaction): local transaction creation, terminal requests and timeout limits.
- [GetTransactionInformation](https://learn.microsoft.com/en-us/windows/win32/api/ktmw32/nf-ktmw32-gettransactioninformation) and the SDK `TRANSACTION_OUTCOME` declaration: real terminal outcome observations.
- [CloseHandle](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-closehandle) and [RegCloseKey](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regclosekey): separate native ownership/close requirements and transaction-handle caveats.
- [RegCreateKeyExW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regcreatekeyexw), [RegDeleteKeyExW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regdeletekeyexw): new-versus-existing key disposition and exact nonrecursive owned teardown.
