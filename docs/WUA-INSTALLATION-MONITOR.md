# Owned WUA installation-job monitor

This portable component observes a job owned by a future trusted Windows adapter. It has **no BeginInstall, download, RequestAbort, install-eligibility or App entry point**. It starts no installation and grants no permission. Current driver discovery/review remains read-only with CanInstall false. Native implementation and all existing action gates stay separate.

## Ownership boundary

The adapter must construct the session before future native BeginInstall and independently root actual job/callback/native participants before entering that API. The session copies ordered update GUID/revision metadata and mints an opaque context. The one-to128 bound, unique GUIDs and nonnegative revisions are MadClean metadata policy, not provider applicability or permission for bulk drivers. They do not verify device/OEM matching, a fresh selected COM collection or restart safety.

A cooperating process-rooted domain roots the actual monitor owner before claiming the session once. The actual session/tasks stay owned through caller departure. Failed adoption does not mean no operation occurred: Adopted and OwnershipReleased are false, and the original native owner remains responsible. A consumed session cannot be observed/cleaned a second time through another domain. This is not cross-process installer admission; IsBusy is not a reservation.

Opaque completion/aggregate/item packets bind actual session and context references, plus the exact selected index. Matching GUID text cannot substitute for those references. Foreign completion/aggregate packets retain ownership without cleanup of foreign objects. Foreign/wrong-index per-item packets become unknown for that index only, without displaying foreign values.

## Completion, result reads and cleanup

Caller cancellation is observation only, even when already canceled at adoption. It cannot abandon an owned job, infer cancellation/rollback or call RequestAbort. Actual completion is awaited without a caller token. No timeout, process kill, forced COM release or new operation is supplied.

Every observe/EndInstall/per-index/CleanUp/final-release callback runs through a separately rooted cold task on TaskScheduler.Default. Scheduling only the initial worker or using ConfigureAwait(false) would still allow a continuation to run inline inside a WUA callback. The monitor posts later work instead; it never invokes cleanup on that callback stack. A future native adapter must additionally establish correct COM apartment/marshalling and callback lifetimes; portable scheduling is not that proof.

After exact completion, EndInstall must supply an owned aggregate packet. Its exception or foreign/missing aggregate retains all participants; no End retry or cleanup-after-failed-End guarantee is invented. With a valid aggregate, every selected index is read independently. One getter/index failure cannot erase other results or fill missing values from the aggregate. Raw ResultCode, signed HResult bits and nullable RebootRequired remain separate. Defined codes0–5 are recognized; missing/out-of-range values remain Unknown. NotStarted/InProgress never become success. Provider Succeeded is not installed-version verification, working hardware or Up to date; InstalledState always remains Unknown here.

Only actual successful cleanup completion permits one checked final release. Blocked/failed cleanup or release retains job/callback/result owners. Scheduling/registration/start uncertainty is sticky and prevents later cleanup or domain retirement; a late callback cannot rehabilitate it. Resource release and result certainty remain independent.

## Validation limits

Controlled sessions exercise early/inline callbacks, ThreadStatic callback depth, independent aggregate/per-index fields, foreign/wrong indices, missing/undefined results, stop/caller departure, pending cleanup, cleanup/release failure and repeated adoption. A callback-depth check tests stack context rather than assuming different thread IDs.

These are portable protocol tests, not COM or Windows installer evidence. No native adapter, source/ID/revision/device/OEM eligibility, prerequisite/download/consent, dependency, no-automatic-restart guarantee or public workflow is implemented by this monitor. The [official-source research and exact remaining native questions](WUA-INSTALLATION-CONTRACT.md) remain authoritative limits.

Final local Linux SDK10.0.403 Release solution build passed with zero warnings/errors. Core1,999 passed/one Windows-only skipped (2,000 total), including15 new passed monitor cases confirmed in TRX. Python remains290 passed/one Windows-only skipped from the unchanged tooling suite. Independent source review cleared the monitor after fixing pre-claim rooting and sticky scheduling/forwarding uncertainty. No WUA/COM/installer, Windows, PC/VM, GitHub, transfer or final-package action occurred. Existing execution gates and frozen neutral artifacts remain unchanged.


A separate [portable single-driver start/handoff candidate](WUA-INSTALLATION-START.md) can reserve the exact job before its controlled Begin collaborator and hand off with the matching session proof. Legacy public monitor adoption still works for unreserved sessions and refuses reserved ones; no claim can reset. The monitor itself still starts no operation, and no actual COM adapter or installation capability is added.
