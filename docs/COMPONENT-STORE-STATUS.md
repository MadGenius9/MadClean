# Explicit recorded component-store status

This D2 increment uses the supported **inbox Windows PowerShell DISM cmdlet** as its primary integration. It is not a native DISM DLL/ADK integration, a fallback from the older metadata probe, a fresh integrity scan or a repair. The existing DISM/SFC executor and private-journal production gates remain unchanged.

The only operation is the current online Windows image with these fixed parameters:

```powershell
Dism\Repair-WindowsImage -Online -CheckHealth -NoRestart -ErrorAction Stop
```

No offline path, repair source, scratch/log override, `ScanHealth`, `RestoreHealth`, component cleanup, reset-base or restart request can be supplied. No arbitrary command text or provider output is executed. Ordinary inspection, event reporting, cleanup, startup inventory, app discovery and release checking never request this operation.

## Review and permissions

The UI first shows a fixed preview and requires explicit acknowledgement before creating one one-shot intent. Preview expiry is two minutes; confirmation and final dispatch reservation are separate atomic steps. Invalidated, expired, repeated or pre-cancelled requests cannot start a host. Expiry is a launch-review limit, not a timer that discards an already running operation's actual result.

The current caller must already have effective administrator rights. The provider does not use `runas`, collect credentials, start an elevated broker or restart MadClean. A non-elevated caller receives Unavailable before host launch and must independently choose whether to relaunch as administrator and review the operation again.

The action is **not zero-write passive inventory**. Microsoft's cmdlet documentation identifies default `%WINDIR%\Logs\Dism\dism.log` logging and `.bak` archive replacement. Windows may also use temporary servicing metadata. The selected action discloses these effects. MadClean refers to the documented Windows log but does not read, copy or claim to have inspected its contents. `-NoRestart` requests documented restart suppression; no restart action is implemented.

## Host and source boundary

The Windows owner qualifies the actual fixed System32 Windows PowerShell executable with the existing held-file identity policy. It starts a fixed `-NoProfile` host with no user script input. A fresh private runspace uses a system-only module path and one explicit inbox DISM manifest import; normal OS dependency autoload is allowed within that private scope. The old passive metadata probe's deliberately different autoload restrictions remain unchanged and its earlier Unknown results are not retroactively promoted.

A small fixed encoded bootstrap reads the parent-authored fixed host body from its private redirected stdin using strict UTF-8, a 16,384-character limit and EOF. The parent checks the body and bootstrap command bounds before process start, writes once and owns the input close through real completion. There is no public caller/configuration script input, temporary script file, compression, execution-policy bypass or arbitrary command interface. The child uses a minimal system environment without inherited CLR hooks.

The host captures the actual inbox `Repair-WindowsImage` CmdletInfo and uses the supported `PowerShell.AddCommand(CommandInfo)` API, then adds only Online, CheckHealth, NoRestart and ErrorAction Stop. Command metadata and the OS module instance must qualify before invocation. It also resolves the advertised `Microsoft.Dism.Commands.ImageObjectWithState` type and its public enum property before the health pipeline starts. Command setup is completed before asynchronous invocation; the hosting API is not thread-safe for concurrent command mutation.

Returned data is read through the captured public PropertyInfo on the exact qualified base-object type, not an adapted/script property or arbitrary `ToString`. A recognized value must belong to the qualified enum. It is projected by the actual member name—Healthy, Repairable or NonRepairable—not by assuming PowerShell enum integers equal native DISM API integers. Unfamiliar, absent, ambiguous or changed metadata remains Unknown and never selects an alternate command or repair.

## Meaning of the result

| Qualified reported member | Current recorded-flag wording | Claim limit |
| --- | --- | --- |
| Healthy | No recorded corruption flag reported | No fresh scan was performed. This does not prove a healthy component store, Windows installation or PC. |
| Repairable | Recorded repairable corruption flag reported | Windows' reported component-store flag; not a diagnosis of a crash or a guarantee a repair will succeed. |
| NonRepairable | Recorded non-repairable corruption flag reported | Preserve the reported flag; do not automatically repair, reinstall or discard an installation. |
| Missing/unrecognized/failed/stopped/unsettled | Unknown | No enum default or client exit becomes an integrity verdict. |

Collection/request times refer to this operation. The API supplies no date for the earlier scan/process that established a recorded flag, so that date stays unknown. Event recurrence, file integrity, application health and hardware health remain independent sources. There is no health score, speed promise or automatic repair suggestion.

`ComponentStoreStatusResult.Flag` is the accepted **current** assessment. `Evidence.HealthState` is an independently observed fixed enum member and can remain as historical evidence after a stop request or cleanup uncertainty. Actual pipeline, host exit and cleanup facts are likewise retained separately. An actual Completed pipeline is not erased because optional output/log/OS metadata is unavailable, but it cannot by itself qualify a current recorded flag.

## Cooperative stop and ownership

The owned controller starts the health pipeline with BeginInvoke once. A private cancel event can request BeginStop once; the owner waits for the actual EndStop and EndInvoke outcomes before disposing the PowerShell object/runspace and observing the real child exit. Pre-cancellation before invocation is not described as cancelling Windows servicing. There is no process/service kill, Stop-Process, Stop-Job, PID reopening or metadata-query timeout runner in this operation.

PowerShell's documented StopProcessing behavior depends on the individual cmdlet: its default implementation does nothing and an override may throw. Therefore BeginStop/EndStop are **pipeline stop observations**, not proof that Windows servicing accepted native cancellation, stopped immediately or rolled back. A non-cancellable native stage may finish and return a normal result. The UI reports the actual request/observation and waits rather than promising a stop deadline.

The rooted session retains its host, event, output readers and leases while work or cleanup remains pending. Unknown ownership blocks another check and app close; no caller closes resources beneath the worker. Stdout/stderr are bounded/drained without exporting raw text. Warning counts remain nullable observations; nonzero or unknown warnings withhold the current flag while retaining the actual reported member and pipeline facts. Counts outside the representable bound remain unknown, never capped into invented exact counts. Any overflow, stderr, unknown drain or cleanup leaves the current flag Unknown even when actual host/pipeline completion is retained. If the caller requested stop before publication, a late completed result is historical and does not become a current flag.

This session has no durable crash recovery. A forced app exit, power loss or lost observation is not reconstructed as completion after restart. The Windows log reference is not proof that MadClean read or authenticated a particular log record.

## Primitive envelope

The shared parser accepts legacy version 1 with its exact 22-field contract only for RecordedFlags. Version 2 has 23 fixed primitive fields, adding the exact selected kind; both the active request nonce and expected kind must match. Missing kind is never defaulted for v2, and legacy evidence cannot establish a fresh ScanHealth result. See [the separate scan contract](COMPONENT-STORE-SCAN.md). It rejects extra, duplicate or missing fields, strings in place of booleans/integers, numeric/case-alias enums, unknown health members, inconsistent qualification/stop/completion facts and oversized responses. Nullable fields remain unobserved rather than false or zero. No raw output, errors, module/type/member paths or user information is part of the envelope. Any managed exception HResult is labeled managed metadata, never interpreted as a native DISM HRESULT or corruption state.

Current flag acceptance additionally requires the reserved intent, effective administrator observation, a completed qualified fixed-host identity observation, qualified completed source/EndInvoke with one output, no errors and an observed zero warning count, no caller/pipeline stop, actual zero-code host exit, completed drains and confirmed ownership release. Transport and source facts remain separate; successful parsing alone is not a successful Windows check.

The fixed body is invoked as a dynamic ScriptBlock rather than as a script file. Its refusal marker uses a shared mutable control hashtable, just as the result projection does, so a helper function and its enclosing catch observe the same marker. PowerShell's [5.1 scope rules](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_scopes?view=powershell-5.1) define `Script:` as the nearest ancestor script-file scope, or Global if none exists; they do not make that modifier synonymous with every dynamic local scope. A local scalar named `guard` could therefore hide a separate `script:guard` update and misclassify an intentional refusal as an unexpected managed failure. The shared-object correction removes that scope dependency without changing command selection, output acceptance or invocation behavior. This is a static correction; no earlier Windows report is retroactively credited to the corrected source.

The controller consumes the captured module, command and output objects directly, requiring their exact CLR types (`PSModuleInfo`, `CmdletInfo` and the captured output type). It does not infer that ordinary PowerShell member access on `PSObject.AsPSObject(...).BaseObject` necessarily addresses the wrapper's CLR property. The official [intrinsic-member documentation](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_intrinsic_members?view=powershell-5.1) distinguishes base, adapted/extended and `psobject` wrapper metadata; the reference-assembly API defines AsPSObject as returning a wrapper, not a guarantee about subsequent PowerShell adaptation. Health output is still read only through the previously qualified public CLR PropertyInfo, never an adapted/script property. Module path/binding/reference, command parameter and output enum checks remain unchanged. This removes a static wrapper-access assumption; the earlier module-binding refusal does not establish its actual runtime cause, and the corrected source requires its own Windows evidence.

## Validation and staged release

Portable tests cover strict nonce/schema boundaries, fixed member mapping, request/confirmation replay, expiry, cancellation races and preservation of actual facts without a late current assessment. Linux tests do not run Windows PowerShell, invoke DISM or establish system state.

The fixed backend qualified at source `2e0c0160`, immutable evidence `c93b096047efb42e95fdef0f766a79a45b7cb51d`, run `41686453518`: genuine module/cmdlet/output qualification, one completed Healthy observation, zero errors/warnings, actual host exit zero, drains and ownership release. Independent review supports literal production activation for the separately selected check. The actual public WPF Prepare/acknowledgement/Confirm cycle also qualified at source `969bd3bd`, immutable evidence `c61b858acc002029e07bac21e8aae43f80f2c860`, run `41690480690`: a fresh assessed result, complete host/pipeline/cleanup facts, settled ownership and closed window were observed. Mid-native-call stop, standard-user denial and broader Windows policy/runtime scenarios remain separate limits. Ordinary passive smoke does not run it. That mode must establish pre-cancel/no-host behavior and one actual non-repair status request, genuine cmdlet/output qualification, pipeline/host completion and owned cleanup. It must not scan, repair, install ADK dependencies or act on a personal PC. Cancellation, permissions/policy, standard-user denial and physical UI behavior require applicable real Windows checks; compilation alone is insufficient.

## Official sources rechecked on 2026-11-03

- [Repair-WindowsImage](https://learn.microsoft.com/powershell/module/dism/repair-windowsimage) ([current official source](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/Dism/Repair-WindowsImage.md)): Online/CheckHealth distinctions, output type, NoRestart and default log/archive writes. Retrieved source SHA256: `6a50227c0e72c78af5fee33070e4f32bb78dcc9557c41274b5136a78d4fba027`.
- [Adding and invoking commands](https://learn.microsoft.com/powershell/scripting/developer/hosting/adding-and-invoking-commands) and [Windows PowerShell02 sample](https://learn.microsoft.com/powershell/scripting/developer/hosting/windows-powershell02-sample): supported asynchronous BeginInvoke/EndInvoke hosting, including the older Windows PowerShell hosting API. Official documentation tree `MicrosoftDocs/PowerShell-Docs`, commit `ebaa235784a688ec72ab642186c4c17460fb438b`.
- [PowerShell 5.1 about_Intrinsic_Members](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_intrinsic_members?view=powershell-5.1) ([official reviewed source](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/ebaa235784a688ec72ab642186c4c17460fb438b/reference/5.1/Microsoft.PowerShell.Core/About/about_Intrinsic_Members.md)): `psbase` members exclude extension/adaptation, while `psobject` exposes wrapper metadata including BaseObject. The PS5 reference XML separately documents AsPSObject and BaseObject ownership/meaning. No PowerShell or Windows operation was executed for this analysis.
- [PowerShell 5.1 about_Scopes](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_scopes?view=powershell-5.1) ([official source at the reviewed tree](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/ebaa235784a688ec72ab642186c4c17460fb438b/reference/5.1/Microsoft.PowerShell.Core/About/about_Scopes.md)): current-to-parent lookup, hiding by same-named local variables, and Script scope as the nearest script file or Global. The reference XML also distinguishes ordinary scriptblocks from script-file scopes; no Windows script was executed to research this correction.
- Microsoft's [PowerShell 5 reference-assembly documentation](https://www.nuget.org/packages/Microsoft.PowerShell.5.ReferenceAssemblies/1.1.0): actual XML member contracts for AddCommand(CommandInfo), BeginInvoke/EndInvoke, BeginStop/EndStop and Cmdlet.StopProcessing. The package was read only for research in `/tmp`, not installed, executed, redistributed or added as an app dependency. Research package SHA256: `2bc5ab45f20bfa5d9d141611cf8b68c23a8d94371f01bcea65e5f71a86deea45`.

No component-store operation, repair, elevation or personal-PC action ran while authoring the Core contract and portable fixtures.
