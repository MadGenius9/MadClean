# Proposed recorded component-store flag query

Research only: this query is **not implemented**. No DISM API, DISM command, SFC command, image scan or repair was executed during this research. The existing Microsoft-tool identity preview remains separate, and its production execution gate remains false. Ordinary inspection does not run servicing operations.

## What the supported API would observe

Microsoft documents [`DismCheckImageHealth`](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dismcheckimagehealth-function?view=windows-11) with `ScanImage=FALSE` as checking whether an image has already been flagged as corrupted. `TRUE` instead scans and flags corruption; it is outside this proposal. Neither flag inspection nor an empty event log is a whole-PC health assessment.

The documented [`DismImageHealthState`](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dismimagehealthstate-enumeration?view=windows-11) values are `DismImageHealthy=0`, `DismImageRepairable=1`, and `DismImageNonRepairable=2`. In this flag-only context, proposed presentation is:

| API result | Proposed observation wording | Required limitation |
| --- | --- | --- |
| `DismImageHealthy` | No recorded corruption flag reported | No fresh scan was performed. This does not establish that the component store or PC is healthy. |
| `DismImageRepairable` | Recorded repairable corruption flag reported | A Microsoft image flag, not a diagnosis of a crash or a guarantee of successful repair. |
| `DismImageNonRepairable` | Recorded non-repairable corruption flag reported | Preserve the reported image assessment; do not automatically reinstall, discard an online installation or perform repairs. |
| Unrecognized value or unsuccessful operation | Unknown / unavailable / failed, as supported by the actual result | Never substitute a default enum value or interpret an output parameter after failure. |

The observation time would be the time of this query. This API does not supply the date of the previous scan that established a flag, so a scan date must remain unknown.

## Implementation blockers

1. **Deployment support is unresolved.** Microsoft's current [Using the DISM API](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/using-the-dism-api?view=windows-11) requires the Windows ADK on the target PC, matching architecture, and application binaries colocated with the ADK DISM binaries. [Creating a DISM application](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/creating-a-dism-application?view=windows-11) identifies the ADK's `DismApi.h`/`DismApi.lib`. An officially supported deployment contract for this application's use of an inbox DLL has not been established. An available system DLL alone would not establish that contract. No ADK package, binary or DLL was downloaded or installed.
2. **Unelevated operation is unavailable.** [`DismInitialize`](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/disminitialize-function?view=windows-11) documents `HRESULT_FROM_WIN32(ERROR_ELEVATION_REQUIRED)` when the process is not elevated. The proposed adapter must not request elevation, restart the app as administrator, bypass policy or silently fall back to a CLI. It must report the restriction honestly. Running an already elevated application would still require the deployment and runtime gates below.
3. **The SDK constant must be verified.** [`DISM_ONLINE_IMAGE`](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dism-api-constants?view=windows-11) is the documented symbolic argument for the current online Windows installation. Its exact literal representation has not been verified against an official ADK header during this research. No guessed string, third-party wrapper constant or downloaded replacement DLL is an acceptable substitute.
4. **Windows runtime and lifecycle validation remain required.** No native call, permission behavior, returned flag, logging behavior or cleanup path has been validated on Windows for this proposed integration. Portable model tests and successful compilation cannot satisfy this gate.

## Proposed contract and fixed operation boundary

These are design proposals, not shipped types or callable functionality. A portable observation could carry an observation timestamp, a recorded-flag enum, the raw recognized image-state value, fixed scope/uncertainty text, and a stage-specific numeric HRESULT when present. `CheckResult` availability/failure is separate from the reported image flag. Raw native exception messages, log contents, file paths and command output would not enter the activity log.

An eventual explicitly selected action would target only the current online image. It would expose no arbitrary image path, repair source, scratch/log override, mount, scan, commit, restore or restart operation. The fixed proposed native sequence is:

1. `DismInitialize(DismLogErrors, NULL, NULL)` exactly once for the adapter-owned process lifecycle.
2. `DismOpenSession(DISM_ONLINE_IMAGE, NULL, NULL, ...)` after successful initialization.
3. `DismCheckImageHealth(session, FALSE, cancelEvent, NULL, NULL, ...)` after successful session creation.
4. `DismCloseSession` for the successfully opened session, then `DismShutdown` matched only to the initialization this adapter successfully owns.

The documentation defines [`DismSession`](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dismsession?view=windows-11) as `UINT`, not a pointer-sized handle. The proposed interop must verify a 32-bit session value, signed 32-bit HRESULT, 32-bit Windows BOOL, native HANDLE, `WINAPI` calling convention and output enum against the actual ADK header before implementation. Only the documented `S_OK` result permits interpreting the image-state output; other results retain their stage and numeric code without inventing an integrity conclusion. An already-initialized API is not permission to take ownership of another component's sessions or shut them down.

## Effects, cancellation and worker ownership gates

This would be an explicit non-repair servicing query, **not zero-write passive inventory**. Initialization's documented default log is `%windir%\Logs\DISM\dism.log`, and its default scratch location uses the Windows temporary directory. Windows may write logs or servicing metadata; the selected action must disclose these effects. [`DismLogErrors=0`](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dismloglevel-enumeration?view=windows-11) limits requested log detail but does not eliminate logging or establish that no system state can change.

A proposed implementation would allow only one process-wide native worker, with a bounded caller wait (for example 45 seconds). Timeout or cancellation would stop the caller's wait and request cooperative cancellation, not kill the worker or servicing processes. The worker would retain its session, event handle, initialization ownership and singleton slot until native work and cleanup actually return. Initialization, opening, closing and shutdown have no cancellation argument; [`DismCloseSession`](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dismclosesession-function?view=windows-11) can wait for in-flight operations. There is no promise of bounded native completion.

Microsoft states that a signaled cancellation event can arrive during a non-cancellable stage, in which case the operation continues and can return success. If cancellation is accepted, the image state is unknown. A cancelled caller must not receive a late success as its current observation. No other thread may race the worker by closing its session, disposing its cancellation event, calling shutdown or starting overlapping work. If cleanup cannot establish a completed lifecycle, further queries in that process must remain unavailable rather than assume it is safe to initialize again. [`DismShutdown`](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dismshutdown-function?view=windows-11) requires all owned sessions to have been closed.

Before implementation can be enabled, disposable-Windows validation and independent review must cover the supported deployment, architecture, unelevated denial, actual HRESULT/enum handling, repeated calls, pre-cancellation, cancellation during native work, bounded caller timeout, retained worker ownership and cleanup failure. No test should scan, repair, install dependencies or act on a personal PC to make this query appear usable. [DISM API troubleshooting](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism/dism-api-troubleshooting?view=windows-11) supplies named lifecycle errors; messages and Windows logs are diagnostic evidence, not automatic health verdicts.
