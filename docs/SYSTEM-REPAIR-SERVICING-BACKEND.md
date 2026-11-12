# Shared admission and disabled native repair backend

The repair coordinator and stored journal now compose through `SystemRepairServicingBackend` and a concrete **disabled** Windows candidate. This is implementation progress, not a ready public repair operation. A guarded public review workflow now composes this candidate; no App repair execution or native qualification mode is activated. The existing legacy executor is not used.

## Shared coordination without a permission shortcut

The Windows composition supplies the same `SystemToolAdmissionGate` instance already used by selected CheckHealth, ScanHealth and SFC verify-only clients. The existing public `TryAcquire` still admits only those three non-repair kinds; repairs and unknown kinds remain refused. Their normal completion semantics are unchanged.

A separate internal candidate path requires the coordinator's actual opaque context, bound to the exact confirmed, reserved, current precommit request. It roots the actual repair resource owner before platform callbacks and admits one lease without queuing. Context/request GUID equality alone cannot replace reference ownership. Ordinary public completion cannot clear a candidate lease. Exact owner/context completion is one-shot; late callbacks cannot rehabilitate retention or unresolved history.

Pending resources, uncertain resource release and unresolved recorded history are distinct. A history-only block prevents another cooperating operation while reporting neither active work nor retained live-resource uncertainty. Existing refusal messages now mention unresolved history without implying a running client. The new backend's pending/retention properties describe its repair owners rather than unrelated non-repair work.

Known acquisition contention or cancellation before platform entry returns an exact-context no-IO refused journal. It cannot write or authorize dispatch and does not poison the outer protocol with invented native ownership. Once platform preparation, dispatch or cleanup is ambiguous, the owner/lease remain retained. All async callbacks occur outside status locks; registered tasks and forwarding completions retain ownership through caller departure.

## Journal, client and final-resource release

The backend accepts only prepared/journal/client sessions bound to the same actual context. Foreign sessions are never observed or cleaned as another owner's resources. Journal wrappers delegate the existing exact-entry persistence receipt; client wrappers await and validate actual bound observations. A dispatch is attempted once, never retried after an ambiguous result.

Final preparation/identity cleanup waits for both the journal and a qualified released client, or for a known no-dispatch path. Pending writes/releases are awaited. Invalid, stopped or missing client completion cannot release preparation resources. Actual client exit/code remains historical even if journal or cleanup later fails; zero is never successful repair or Windows integrity.

A recreated managed test domain can reread controlled journal files and refuse another repair when history is unresolved or malformed. This is **not an actual process-restart, crash or power-loss test**. Existing non-repair startup paths do not automatically read production repair history. Cross-process coordination and a separately gated startup reconciliation design remain missing; do not infer them from this in-process gate.

## Native candidate and permissions

`WindowsSystemRepairBackend.ProductionExecutionValidated` and its private platform flag are literal false. Existing private-journal creation also remains false. These guards precede platform/token/path/identity, shared admission, journal, worker and process access. No setting, environment variable, UI acknowledgement or fixture override enables native repairs. Default construction is managed composition only.

The compiled candidate behind those guards requires an already-effective, nonimpersonated administrator and rechecks that condition before claim/start. It requests no UAC, relaunch, alternate credentials or privilege change. This differs from the disabled legacy catalogue's future-UAC wording; any later public confirmation must describe this candidate's actual already-admin/no-UAC policy rather than copying that legacy elevation text.

Preparation retains the genuine fixed system executable through the existing held-file Microsoft signature/catalog/version/fingerprint checks. It opens the journal last, then returns the actual prepared owner without a cancellation throw that could discard an obtained session. Identity/journal workers remain owned through uncertainty. The two allowed commands are reconstructed from literal operation switches and cross-checked against the fixed catalogue:

```text
Dism.exe /Online /Cleanup-Image /RestoreHealth /NoRestart
sfc.exe /scannow
```

No source/offline target, custom path/argument, shell, runas, cleanup/reset-base, download helper or restart action is accepted. The fixed semantics were established by the repository's previously reviewed [official command references](SYSTEM-TOOL-ACTIONS.md). DISM can use Windows-managed repair sources under existing policy; no repair-success guarantee is supplied.

The candidate now uses the direct CreateProcessW controller under rooted ownership. It supplies explicit application/command/environment/cwd/startup/process-information buffers, exact inherited pipe ends and a minimal system environment. Pinned raw-byte discard workers and the original returned process handle remain owned without postcommit cancellation, PID reopening, process/service kill or restart. Potentially blocking calls, stream reads, scheduling and disposal occur outside locks used by status getters. The future selected MadClean workflow would have to display progress; none is currently enabled. No raw output or protected Windows log content is read/exported. Windows log paths remain references, not proof that logs were inspected or even available.

## Historical managed-launch contract and replacement

The earlier managed candidate owned the positive-Start process path, but **a thrown Process.Start exception had no established exhaustive post-launch ownership guarantee** and could leave work unmonitored/undrained. That route has now been removed rather than retained as a fallback. The new source uses documented direct creation results and explicit ownership; exceptional native/managed transitions still retain unknown outcomes and require Windows qualification. No undocumented probe, PID reopen, process kill or dispatch retry repairs uncertainty.

The source executor attempted these exact current official references on 2026-11-11:

- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.start?view=net-10.0
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw
- https://learn.microsoft.com/en-us/windows/win32/procthread/creating-a-child-process-with-redirected-input-and-output

All HTTPS CONNECT attempts failed through the environment proxy with403; no document bytes were retrieved. This is a proxy failure, not an approval-review rejection or proof the APIs are unavailable. No GitHub/mirror fallback was used. The unresolved managed-exception question is definitive original process/pipe ownership after a thrown start. The coordinating task subsequently reviewed the current official sources and established the supported direct-launch direction below; its full integration/qualification remains separate. The direct adapter was implemented only after the coordinating official-source/SDK review supplied the remaining definitions; it is not a managed-start fallback. The related Process.SafeHandle API must not be assumed to be a non-reopening probe on an ambiguous association.

## Validation and remaining integration

Controlled platform/client/journal tests cover mixed-route contention, repeated/foreign requests, callback reentrancy, caller GC departure, pre/postcommit cancellation, late exit0, pending release, uncertain writes/dispatch and file-history rereads. They launch no repair or Windows command. Existing normal admission tests remain part of the full local suite. Linux compilation and these fixtures do not qualify native ACL/namespace/durability, process launch/drains, administrator policy, actual repair effects, protected logs or public WPF behavior.

The private-parent/namespace creation contract, actual native/source-matched Windows process/pipe/failure qualification, cross-process/startup recovery and public confirmation/status workflow remain unfinished. No public repair action is ready. The frozen cd1718b neutral payload remains an older independent test artifact and must not be overwritten by these builds. No external transfer, VM/PC execution, GitHub/Actions or final packaging is part of this work.

## Coordinated official-source review after the proxy failure

The coordinating task retrieved and reviewed current Microsoft sources without GitHub. These are supplied official-source findings, not locally retrieved document bytes/hashes or Windows observations:

- The cited Process.Start documentation states true means a new process resource started; false means none started (for example, reuse). It does not establish an exhaustive post-launch exception/pipe-ownership guarantee. The historical managed candidate conservatively blocked its false-return path; this did not mean a child existed on false.
- The cited CreateProcessW reference documents nonzero success and zero failure with GetLastError. It returns before process initialization completes, so even successful creation can later end in DLL/initialization failure. An explicit lpApplicationName avoids null-name parsing ambiguity; the Unicode command-line buffer must be writable.
- [PROCESS_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-process_information) returns owned process and primary-thread handles, both to be closed when finished. IDs can be reused after handles close/object destruction; use returned handles, not PID reopening.
- The redirected-I/O example creates pipe pairs, marks parent-only ends non-inheritable and supplies child ends through STARTUPINFO/STARTF_USESTDHANDLES. Parent copies of child-side pipe handles must be closed after successful creation or EOF detection can remain blocked. The example's synchronous/fatal-error style is not a general UI lifecycle contract.
- [Creating processes](https://learn.microsoft.com/windows/win32/procthread/creating-processes) recommends STARTUPINFOEX with [UpdateProcThreadAttribute](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute) and PROC_THREAD_ATTRIBUTE_HANDLE_LIST for specific inheritance. [STARTUPINFOEXW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-startupinfoexw) embeds STARTUPINFO and the list pointer; cb is sizeof(STARTUPINFOEX). [Creation flags](https://learn.microsoft.com/windows/win32/procthread/process-creation-flags) documents EXTENDED_STARTUPINFO_PRESENT.

These findings support a separately reviewed direct-launch implementation. They do not activate this candidate, certify an ABI or qualify pipe/attribute-list cleanup and failure behavior. The new direct adapter replaces the managed route; neither route has run here. Native execution remains disabled.

## Typed direct-dispatch composition

The current working protocol accepts a dispatch-session base with the existing owned client and a sealed direct-kernel alternative. The latter must belong to the exact admitted kernel owner and permit; a same-context refused replay is insufficient. Shared admission roots that actual session and includes its live wait/read tasks in pending status even if its result task has already returned a retained outcome.

One internal policy checks context, permit, packet consistency, claim/attempt coherence and UTC bounds. It separately qualifies actual cleanup using completed stage, reader EOF/worker facts, process signal when created and live-session ownership. The coordinator and shared backend use that same policy and still await the kernel's idempotent ownership-release observation. Final journal/preparation release follows actual direct cleanup; uncertainty retains those owners and the servicing lease.

Known no child is a distinct NoClientCreated result, not a fake client or cancellation. It records ObservationLost under the unchanged journal grammar and keeps history blocked after actual resource release. Created processes missing PID/code keep raw evidence with DirectEvidenceConsistent instead of manufacturing the stricter ClientObservation. Raw foreign-context packets are withheld; bound uncertain packets carry false consistency. Numeric exit0 never proves repair. The existing client route keeps its previous behavior. These are protocol changes behind the same closed production gates, not App or native execution qualification.

The later [public review controller, guarded facade and App card](SYSTEM-REPAIR-PUBLIC-REVIEW.md) connect local review/history/confirmation/result states without enabling execution. Their portable model tests and WPF compilation do not qualify Windows controls or native actions.
