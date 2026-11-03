# Separate recorded component-store flag qualification

The explicit development mode `--component-store-status-validation` runs only in its separate GitHub-hosted Windows x64 job for this repository, source/run/attempt and feature branch. It requires the fixed mode environment flag and an already effective administrator token. Neither the app nor this mode requests elevation, credentials or a relaunch. It is outside the ordinary passive harness and its process watchdog. The immutable public qualification guard cannot be enabled by these variables.

The one supported operation is the primary inbox Windows PowerShell 5.1 DISM cmdlet with exactly:

```powershell
Dism\Repair-WindowsImage -Online -CheckHealth -NoRestart -ErrorAction Stop
```

This asks about a **previously recorded corruption flag**. It requests no fresh scan, repair, reset-base or component cleanup. It supplies no source, offline path, scratch, log override or dependency-install option. `-NoRestart` suppresses restart. The supported cmdlet can write the default Windows DISM log, archives and temporary servicing metadata. Those writes are disclosed and allowed only for this explicit qualification job; log contents are not read or exported. It is not a zero-write inventory, a native DISM DLL/ADK integration or fallback from the older module metadata experiment.

## Qualification and ownership

Each intent gets a fresh fixed marked scope under `artifacts/component-store-status-fixtures-<RequestId>` with its exact 32-byte marker. The private internal authorization verifies GitHub-hosted/repository/ref/job/source/run/attempt context, effective administration and the fixed owned namespace, then holds no-follow directory and marker read leases. Ownership can transfer to its one matching request once; caller disposal cannot close a worker's proof. No request ID, named event nonce or path is exported.

The first candidate request has a pre-cancelled token. It must return Cancelled with an actual no-host-attempt/no-host-start observation, no pending/retained owner and no authorization transfer or dispatch reservation, and an invalidated one-shot intent that cannot be replayed. This tests prelaunch cancellation only; it does not establish Windows servicing cancellation.

One separate fresh acknowledged intent then runs the actual non-cancelled check through the same candidate provider. The mode waits for actual session completion, source-correlated primitive output, pipeline EndInvoke observations, PowerShell/runspace/event cleanup, bounded drained stdout/stderr and the real host exit. Stop is not requested for this normal qualification. BeginStop/EndStop, if observed in other scenarios, are pipeline observations and are never proof that Windows servicing stopped or rolled back. There is no local process/tree/service kill, forced timeout completion, PID reopening or use of the inventory-query kill runner. A pending native/host operation stays owned until it returns. The disposable CI job's outer VM lifetime is independent of MadClean's cancellation semantics.

Positive qualification requires the actual signed/held host identity to complete, effective administration, matched request, typed module/cmdlet/output qualification, a completed pipeline and EndInvoke, exactly one output, observed zero errors and zero warnings, no stop request or signal, completed source cleanup and actual zero-code host exit with released ownership. Warning counts aggregate the actual owned metadata, lookup and invocation pipelines. Unknown or nonzero warnings preserve the typed reported member and historical pipeline facts but withhold a current accepted flag. Counts beyond the representable bound remain null; no capped exact count is invented. Partial metadata, malformed output, failed drains or uncertain cleanup never qualify.

The published flag maps Windows' actual `Healthy` member to **NoRecordedCorruptionFlag**, and the other accepted members to **RecordedRepairable** or **RecordedNonRepairable**. None is a PC health score or proof of a fresh integrity scan; the prior flag/scan date is unknown. Seeing a repairable flag authorizes no repair.

The mode removes only the exact newly created marker scopes, without recursive deletion, after proof/session/provider ownership is settled. It never purges Windows servicing logs, clears diagnostic evidence, modifies component data directly or claims rollback. Unknown ownership retains its exact scope. No durable servicing recovery or completion reconstruction is supplied after a crash.

## Evidence boundary

Only the bounded fixed `artifacts/component-store-status-validation/report.json` is uploaded. It contains source/run/attempt/time, nullable primitive observations, fixed enums, the recorded-flag classification and actual integer statuses/counts. It excludes raw stdout/stderr, error/warning text, cmdlet/type/member assembly paths, identities, requests/nonces, credentials, log contents and executable files. A managed exception HResult is explicitly managed metadata, not a DISM native HRESULT or a health finding.

The existing atomic verification publisher validates this as a separate optional fixed `component-store.json`. Its schema, canonical enum labels, nullable types, source context, completion/stop/warning coherence and flag mapping are checked independently. Missing or rejected component-status evidence cannot suppress the independent passive, MSI or portable test reports. The latest summary keeps job outcome, artifact outcome and qualification separate; a successful report cannot override a failed job. Older/passive results do not qualify this new operation.

## Validation limits and sources

Portable tests exercise malformed/duplicate/unapproved fields, unavailable facts, source mismatch, warning uncertainty, stop history, missing exit codes and incomplete cleanup without invoking PowerShell or servicing. Linux compilation is not Windows runtime proof. A matching Windows report is still required before production qualification. This one non-cancelled job does not qualify mid-native-call cancellation, arbitrary Windows policy, every locale or standard-user elevation behavior.

Current Microsoft [Repair-WindowsImage documentation](https://learn.microsoft.com/powershell/module/dism/repair-windowsimage) was rechecked on 2026-11-03, including CheckHealth versus ScanHealth/RestoreHealth, NoRestart and default log/archive writes. [The product contract](COMPONENT-STORE-STATUS.md) records the official hosting, captured CommandInfo, asynchronous invocation and StopProcessing references used for the primary provider. There are no downloaded ADK dependencies or guessed native DISM exports in this mode.

The ordinary passive WPF harness separately exercises only the real Prepare and acknowledgement controls, validates fixed scope/permission/log/stop wording and renewal reset, and requires confirmation/stop to remain disabled. It checks activity and existing source observations remain unchanged; it never invokes Confirm, Stop, a host or servicing. This preview check cannot qualify the actual operation.
