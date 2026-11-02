# Present-device installed-driver properties

This separate, explicitly selected local query reads Windows SetupAPI properties for currently present device instances. It never runs with ordinary Inspect or online update discovery. The adapter requests no elevation and performs no driver installation, removal, update, scan, firmware action, binary/path read or network lookup. Windows may deny properties to the current token; unknown fields remain visible. Local friendly names may contain personal labels, so values stay in this in-memory view and must not enter activity logs, screenshots of loaded actual data, exports or telemetry.

## What the observations mean

Microsoft documents `DEVPKEY_Device_DriverVersion` as the version of the driver currently installed on a device instance. Its value comes from the installing INF's DriverVer directive. `DEVPKEY_Device_DriverDate` also comes from DriverVer: this is a reported driver date, **not when it was installed**. The UTC FILETIME is projected to a calendar date; a future valid date remains reported metadata, not a freshness claim. `DEVPKEY_Device_DriverProvider` is the INF Provider declaration, not authenticated publisher/signature trust.

The label is the separately reported FriendlyName; DeviceDesc is retained separately so a UI may label a fallback honestly. Neither label is a stable identity or exact hardware-model proof. Present devices can be virtual, and this inventory does not show every staged driver package or prove which binary is loaded, running, secure or compatible. Rows are observations, not unique driver packages. No names, versions or dates are joined to Windows Update offers, and there is no Up to date conclusion.

## Fixed query and limits

`SetupDiGetClassDevsW(NULL, NULL, NULL, DIGCF_PRESENT | DIGCF_ALLCLASSES)` obtains a local information set. `SetupDiEnumDeviceInfo` requests indices zero through 512; only `ERROR_NO_MORE_ITEMS` establishes enumeration completion. Index 512 is a sentinel, never a displayed row. At most 512 observations are returned. The private `SP_DEVINFO_DATA` necessarily contains class GUID, DEVINST and reserved context; none is read as an identity, formatted, serialized or exported.

Exactly five property keys are requested using `SetupDiGetDevicePropertyW` with flags zero:

| Field | SDK property key | Format GUID / PID | Expected type |
| --- | --- | --- | --- |
| Name | DEVPKEY_Device_FriendlyName | a45c254e-df1c-4efd-8020-67d146a850e0 / 14 | STRING |
| Description | DEVPKEY_Device_DeviceDesc | a45c254e-df1c-4efd-8020-67d146a850e0 / 2 | STRING |
| Provider | DEVPKEY_Device_DriverProvider | a8b865dd-2e3d-4094-ad97-e593a70c75d6 / 9 | STRING |
| InstalledVersion | DEVPKEY_Device_DriverVersion | same driver GUID / 3 | STRING |
| DriverDate | DEVPKEY_Device_DriverDate | same driver GUID / 2 | FILETIME |

A single zeroed 4096-byte buffer is reused by the owning worker. No size-query/growth/retry path is provided. Successful BOOL, bounded returned byte count and exact unmodified property type must all pass; portable decoders reject malformed UTF-16, unsafe controls, missing/embedded terminators, oversized text and invalid FILETIME. Up to 256 UTF-16 code units (514 bytes including the single terminator) are accepted; valid supplementary pairs count as two units. No DeviceID, instance ID, serial, hardware IDs, INF paths, driver binaries, signature material or registry values are requested. No `SetupDiSet*`, class installer, device-change, install/remove or reboot API is imported.

Missing, denied, oversized, invalid and failed properties have separate typed states. Error codes are actual immediately captured Windows errors, including a failed call whose error is zero; no native code is invented from a message. The API's documented ACCESS_DENIED case mentions administrator privileges, so successful standard-user coverage cannot be assumed. Access denial never triggers UAC. Available means a complete bounded enumeration with valid known requested fields. Missing fields, bounds or enumeration failures produce Partial; unavailable set creation produces Unavailable. Empty complete coverage is not an update/health claim. Every row carries a local query timestamp, not historical continuity. Enumeration and the five property reads are not an atomic system snapshot; hot-plug and property changes can occur between calls.

## Cancellation and ownership

One static rooted worker owns the set, native context and property buffer. A 30-second caller wait is bounded; cancellation/deadline checks occur between native calls. Windows API calls themselves cannot be interrupted or force-closed. A timed-out caller receives Unknown, and a pending worker blocks subsequent queries. Caller cancellation does not imply that the native call has stopped. No extra worker is spawned while one is pending.

The owner makes exactly one checked `SetupDiDestroyDeviceInfoList` attempt; `CloseHandle` is not an alternative. False destruction, even with Windows error zero, withholds observations, retains the uncertain raw set privately and blocks further queries until process exit. There is no finalizer retry. A hanging destruction remains owned by the pending task. This is metadata-only ownership, not servicing cancellation or permission for driver actions. Native DLL loading is restricted to System32.

## Official sources and validation

Official references were retrieved on 2026-11-02. SDK API pages: [GetClassDevsW](https://learn.microsoft.com/windows/win32/api/setupapi/nf-setupapi-setupdigetclassdevsw), [EnumDeviceInfo](https://learn.microsoft.com/windows/win32/api/setupapi/nf-setupapi-setupdienumdeviceinfo), [GetDevicePropertyW](https://learn.microsoft.com/windows/win32/api/setupapi/nf-setupapi-setupdigetdevicepropertyw), [DestroyDeviceInfoList](https://learn.microsoft.com/windows/win32/api/setupapi/nf-setupapi-setupdidestroydeviceinfolist). Their current official [SDK API source](https://github.com/MicrosoftDocs/sdk-api/tree/docs/sdk-api-src/content/setupapi) was read directly; GetDevicePropertyW supports Vista and later, and its buffer sizes are bytes.

Property/type articles were retrieved from official [windows-driver-docs](https://github.com/MicrosoftDocs/windows-driver-docs/tree/b1f6aa5c1275777b7b4be30c4bbff86dc614e0b0/windows-driver-docs-pr/install): `devpkey-device-driverversion.md`, `devpkey-device-driverdate.md`, `devpkey-device-driverprovider.md`, `devpkey-device-friendlyname.md`, `devpkey-device-devicedesc.md`, `devprop-type-string.md`, and `devprop-type-filetime.md`. The FILETIME type page's setting example inconsistently says TYPE_DATE; the property article, type description and actual SDK declaration establish FILETIME for this read-only query. No setting code is used.

Actual current Microsoft [SDK headers](https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders) `um/SetupAPI.h`, `shared/devpkey.h`, and `shared/devpropdef.h` supplied prototypes, key GUID/PIDs, flags PRESENT=2/ALLCLASSES=4 and types STRING=0x12/FILETIME=0x10. The adapter uses the platform's native sequential struct layout and cbSize, not a guessed fixed pointer-width size.

Portable decoder and consistency tests are separate from runtime validation. This implementation was not run against Windows while being authored in the Linux workspace. Required Windows checks include standard-token and denied/missing properties, actual driver-property output types, hot-unplug/enumeration changes, bounds, cancellation during a slow native call, pending ownership, destruction failure and UI stale-result handling. Compilation alone does not verify any of these. No personal PC was queried during development.


Review corrections preserve the full DWORD error bit pattern even when Marshal represents it as a signed int. Native results and EOF are checked against cancellation after each returned call; the owner clears its fixed buffer in finally. A known Failed/Unavailable state cannot become positive Partial merely because the elapsed budget also expired. Worker faults are observed, and any returned destruction exception latches uncertainty before retaining the set.

Local validation: full solution build has zero warnings/errors; all 1,285 portable tests pass. The Python evidence suite has155 total /154 passed /one Windows-only skip. Independent combined review found no remaining static blocker. SDK assertion compilation, actual native device properties and populated UI behavior remain unverified until source-bound Windows evidence arrives.

The development harness additionally invokes the actual production driver-read button after all screenshots, checks real returned row/field bindings and explicit selection/clearing, and verifies display operations preserve source rows and separate WUA offers without extra activity. Missing installed-version rows or unsettled native ownership remain inconclusive; unsettled work blocks later native fixtures and published startup. Only fixed states/counts leave the loaded view. This new interaction is Windows-unverified until a matching-source report arrives.
