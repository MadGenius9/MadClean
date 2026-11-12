# WUA installation contract research

The coordinating task retrieved these current Microsoft references on2026-11-11. This executor did not retrieve/hash their bytes. The findings support future source implementation and controlled tests only; no installation/download/query, restart, elevation or Windows action is authorized here. Existing discovery is not installation qualification, and all future execution must remain gated.

- [BeginInstall](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateinstaller-begininstall) accepts progress/completion callbacks, caller state and an output job. S_OK means asynchronous work started, not update success. Missing/empty Updates yields WU_E_NO_UPDATE. [EndInstall](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateinstaller-endinstall) takes that job and returns the aggregate installation result.
- [RequestAbort](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iinstallationjob-requestabort) requests cancellation; S_OK is not a terminal cancellation result. [CleanUp](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iinstallationjob-cleanup) waits for completion and must not run inside a callback. [Asynchronous WUA guidance](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/guidelines-for-asynchronous-wua-operations) recommends separate job/callback ownership to avoid cycles and establishes no completion-speed guarantee. UI timeout cannot release active native ownership.
- [IsBusy](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateinstaller-get_isbusy) is a point-in-time observation, not admission or a reservation. Concurrent activity can still produce WU_E_OPERATIONINPROGRESS.
- [GetUpdateResult](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iinstallationresult-getupdateresult) supplies each selected index's result. [OperationResultCode](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/ne-wuapi-operationresultcode) is NotStarted0/InProgress1/Succeeded2/SucceededWithErrors3/Failed4/Aborted5. SucceededWithErrors can have incomplete results. Preserve individual ResultCode, HResult and RebootRequired independently of aggregate values; [individual properties](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/iupdateinstallationresult-properties).
- [ForceQuiet](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateinstaller2-put_forcequiet) is not universally supported; unsupported quiet action returns WU_E_UH_DOESNOTSUPPORTACTION. [RebootBehavior](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/ne-wuapi-installationrebootbehavior) is NeverReboots0/AlwaysRequiresReboot1/CanRequestReboot2. These declarations do not independently establish a universal no-automatic-restart contract.

Remaining selected-driver questions include exact fresh update ID/revision and COM collection binding, installed-device/OEM compatibility, trusted source/download and agreement state, privilege/consent, dependencies, supported restart controls and post-install verification. Current snapshot review can record provider metadata revision, but still lacks exact installed/offered driver version evidence. A job-lifecycle controller can advance using the documented ownership/result contracts without enabling installation. No callback, aggregate result, quiet flag or manifest declaration may bypass those prerequisites or turn unchecked software into Up to date.

## Native handoff questions still open

The following exact questions remain for supported native implementation; a portable monitor does not answer them:

- BeginInstall: on failed HRESULT or thrown COM invocation, what is guaranteed about work entry and output-job ownership? Do not infer no operation or safe adoption/release from an unchecked output value.
- [Completion callback Invoke](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iinstallationcompletedcallback-invoke) and [progress callback Invoke](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iinstallationprogresschangedcallback-invoke): callback timing before Begin return, reentrancy, apartment/thread and argument-reference lifetime guarantees. Root the native callback/job owner before entry; managed wrapper equality alone does not prove COM job identity.
- EndInstall/CleanUp: completion prerequisites, permitted calling apartment, cleanup after failed End, and callback-quiescence/reference-release guarantees. Keep exceptional End ownership retained until that path is established; no retry or forced COM/process teardown.
- [Installer and derived interfaces](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iupdateinstaller): which supported control prevents automatic reboot for the exact selected driver/handler/policy, and how to refuse a path that cannot honor it? Current ForceQuiet/RebootBehavior facts are insufficient.

These are specific native integration/qualification blockers, not reasons to postpone independent portable lifetime/result code. No native installation implementation or Windows job evidence is supplied here.

## Next read-only metadata contract check —2026-11-12

After the public-review/prepare-only validation source milestones, the next discovery increment considered provider identity revision and declared driver hardware identifiers. These exact current Microsoft references were requested with normal TLS verification:

- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateidentity-get_revisionnumber
- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iwindowsdriverupdate-get_driverhardwareid

Both requests failed at the configured HTTPS proxy with Tunnel connection failed:403 Forbidden; no document bytes were retrieved. This was a proxy failure, not an automatic approval rejection or evidence that the properties are unsupported. No GitHub/mirror/proxy bypass or repeated retry was used. No discovery change was implemented from memory.

The outstanding questions are exact type/null/unavailable/bounds behavior and identity semantics: revision is provider metadata identity rather than an installed/offered driver version; a declared hardware identifier must not be confused with a machine instance/serial identifier or complete device/OEM applicability. Current documentation is required before adding these fields or matching logic. Missing metadata remains Unknown and installation remains disabled.


## Coordinated identity contract retrieval —2026-11-12

The coordinating task subsequently retrieved the exact official pages above and [IUpdateIdentity.UpdateID](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateidentity-get_updateid). This resolves the documentation-access blocker for this read-only mapping, not native installation or runtime qualification.

RevisionNumber is a read-only LONG describing the update revision. UpdateID is a read-only BSTR identifying the update independently of revision. Neither is an installed software/driver version or establishes cross-provider version ordering. DriverHardwareID is a read-only BSTR containing a hardware ID **or compatible ID** that must match for installability; one matching declaration cannot establish full device/OEM suitability, safety or current installation.

The additive IdentityMetadata version1 payload records these independently of OfferMetadata version1. Missing/malformed/unreadable optional fields remain Unknown or Failed without discarding the offer or valid companion. Native software offers mark hardware metadata Unsupported. MadClean accepts only nonnegative int32 revisions and bounded safe Unicode hardware text of at most512 characters; these are local admission limits, not a documented complete native range. Placeholder text is withheld, never normalized into a plausible identifier. No matching, version ordering or eligibility logic is added. Driver review retains exact-reference/GUID/snapshot safeguards and firmware refusal; CanInstall remains false. Current official declarations plus portable mapping tests do not qualify actual WUA/COM behavior.
