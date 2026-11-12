# Local Windows Update history validation

This is a separate development backend-observation mode, not an application action or permission to operate a VM. The coordinator owns the offline VM and must approve a source-bound transfer and invocation separately. No Windows run has been performed by implementing this mode. The original frozen cd1718b payload and UI evidence remain unchanged and unrelated.

## Exact scope

From a separately agreed, verified test-tree root on the disposable Windows 11 x64 guest, the sole documented mode is:

```powershell
& '<verified guest x64 dotnet.exe>' '.\tools\MadClean.WindowsSmoke\bin\Release\net10.0-windows10.0.22000.0\MadClean.WindowsSmoke.dll' '--local-update-history-validation'
```

The placeholders are not an execution-ready handoff. Pin the final reviewed commit and a complete output/source-context SHA-256 manifest before any transfer; record host/guest hash comparisons externally. The report deliberately does not authenticate its own source. Do not reuse the old neutral archive, invent CI environment variables or combine arguments. A mixed invocation containing this mode is refused before the CI/default fixture route.

The mode constructs the concrete `WindowsUpdateHistoryInventory` and requests exactly one bounded local history read. It never creates App/MainViewModel/window instances, invokes the WPF History button, loads MadClean exclusion/release settings or writes an activity log. It performs no offer Search, catalog refresh, download, install, uninstall, servicing, cleanup, registry modification, Defender scan, elevation or restart. Do not chain another mode automatically.

The prerequisites are Windows 11 build22000 or later, x64 process, compatible x64 .NET10 Core/Desktop runtimes required by the actual harness runtimeconfig, complete matching Release dependencies, and real matching source/context (`MadClean.sln`, App and Windows project files). Use a fresh ordinary fixed-drive guest test root with no reparse ancestry, shared folders or personal data; remain offline. No SDK, WinGet bootstrap or runtime installation is requested by this mode. Policy, PowerShell and local WUA availability may prevent a positive response. Windows may access its own caches/logs; no zero-OS-I/O or service-network-completion promise is made.

## Ownership and evidence

The actual adapter owner is rooted before its worker is scheduled. The adapter uses its existing bounded read-only query client; this harness does not pass a deadline cancellation token into the provider. A separate120-second observation timer cannot abandon, retry or kill that query. Even after an observation deadline or reporting failure, the harness awaits the original task before returning. A late positive result cannot erase expiration. A blocked synchronous call may keep the process alive beyond the budget; do not treat force-close/VM teardown as successful application cleanup.

Task settlement means the managed adapter task settled. It is not independent proof of every exceptional `Process.Start` ownership transition, COM server work, service cancellation, installation state or native failure cleanup. The mode never enables an action gate.

The only intended MadClean output is a fresh, at-most8192-byte file:

```text
<guest test root>\artifacts\local-update-history-validation\<fresh GUID>\report.json
```

Format1/context `local-update-history` contains UTC start/end, a fixed outcome, nullable settled/shape/acceptance/projection observations, whitelisted provider status, bounded counts, deadline state and optional numeric exception HRESULT. It contains no event title/time, update ID/revision, per-entry HRESULT, provider messages, output text, user paths or credentials. `windowsServiceCompletion`, `currentInstallation`, `maintenance`, `physicalInput` and `sourceAuthenticity` remain `unverified`.

A valid Partial provider response can pass the **bounded backend response contract**, including a legitimately empty or shorter collection. `fieldProjectionObserved` records whether any projected field was Known; false for empty/all-unknown records does not claim field getter coverage. Such a pass never establishes complete history, current installation, applicability, up-to-date software or MadClean ownership. Negative/unknown/unavailable/failed provider checks remain distinct and qualification is inconclusive. Invalid response shape or unexpected observer faults fail; incomplete observations cannot pass. Deadline expiration remains inconclusive after late settlement. UTC ordering/nonfuture checks apply, but no arbitrary maximum native completion duration is inferred.

Exit0 means that narrow response contract passed, exit1 failed and exit2 inconclusive/unsupported. A missing report or missing actual exit remains Unknown. Keep the exact invocation, source/output manifest and transfer checks, guest/runtime facts, original report bytes/size/SHA-256 and exit code. Run only the passive reader on supplied bytes later:

```text
python3 tools/read_local_update_history_report.py <report.json>
```

The passive reader checks consistency and bounded local-file schema; it cannot authenticate where the report came from. This backend phase does not qualify the WPF view, physical input, visual fidelity, other providers, published executable or maintenance actions. The separate prepare-only UI plan remains the smallest initial current-build observation.

## Reading acquisition separately from qualification

| Fixed report facts | What was actually observed |
| --- | --- |
| `providerState=Partial`, accepted true, all counts0 | Successful empty local count observation; no zero-count QueryHistory call. No current-update verdict. |
| Accepted true, totalBefore positive, returnedEntryCount0 | A valid empty returned window after the earlier positive count; not an atomic proof that all history is empty. |
| Accepted true, returnedEntryCount positive | A bounded historical response, with the recorded count/request limits and per-field uncertainty. Projection means any Known field, not every getter. |
| Provider Unsupported/Unavailable/Unknown/Failed/Cancelled, accepted false and no counts | That exact provider outcome remains explicit; retrieval is not qualified merely because the negative response was safely handled. |
| Budget expired, `queryTaskSettledWhenBudgetObserved=false` | The original task was still pending when expiration was first observed. Final `queryTaskSettled=true` later records actual settlement; it cannot erase that earlier ownership observation. |
| Budget expired, snapshot true | The task was already settled when expiration was noticed, possibly during final validation. This does not establish its state at the exact timer deadline. |
| Missing report or missing actual exit | Unknown observation/settlement; never empty history, successful acquisition or cancellation proof. The process retains the original task until it settles. |

A monotonic elapsed check supplements the timer task so a delayed timer callback cannot create a false pass. Clock failure/backward movement fails the observation without inventing measured expiration. The budget snapshot is nullable when not observed; all provider/service/current-installation limits remain separate.

## Source validation checkpoint

Independent review passed after count-consistency, monotonic-budget and first-expiry snapshot fixes. Release solution build zero warnings/errors. Core: 2,364 passed, one Windows-only skipped, zero failed; 50 new controlled evidence/lifetime cases passed. Python: 317 passed, one skipped, zero failed; 20 new passive-reader cases passed. No Windows mode, provider query, VM/host operation, transfer or release packaging ran. These results establish portable behavior/source compilation only.
