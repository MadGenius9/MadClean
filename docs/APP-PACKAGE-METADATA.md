# Selected app catalog metadata

Update Center offers **Read selected catalog metadata** for one current Available WinGet update. This is a separate user-controlled online read, never an installation or part of ordinary Inspect. The source may refresh/cache manifest metadata. Select a currently available offer after checking apps, then request its details. Missing API support, denied/private/offline sources, changed versions and ambiguous matches remain Unsupported, Failed or Unknown; none establish that software is up to date.

## Evidence and boundaries

The Core policy requires the actual selected row reference exactly once in the accepted discovery snapshot, a unique package ID, the pinned `winget` source and known installed/offered versions. It prepares a two-minute read selector, not an action capability. The Windows bridge reconnects using the existing pinned source tuple, authentication None and agreement acceptance false. One ID/Equals selector with a two-result limit must return exactly one match without truncation. The fresh installed version, default offered version, update-available flag and offered-version catalog must still match before metadata is requested. A different version is never silently substituted.

Only contract-6 `PackageVersionInfo.GetCatalogPackageMetadata()` is used. The projection includes Publisher (512 UTF-16 units), ShortDescription with Description fallback (8,192), License (1,024) and ReleaseNotes (8,192). Invalid UTF-16, format/control characters, oversized or missing fields remain Unknown; only description/notes allow tab and line breaks. Some known fields yield Partial; no known fields yield an empty Unknown result. Values are copyable plain text, not executed Markdown/HTML or links. Publisher identity, official release-note authenticity, compatibility and installation readiness are **not verified** by these manifest claims.

The bridge never calls installed `GetMetadata`, reads raw uninstall commands, requests installer/download APIs, accepts agreements or opens metadata URLs. `CanInstall` is always false. Source and observation time accompany the result. Metadata failures do not erase the original update inventory or promote its coverage.

## Ownership and stale results

Discovery and metadata share one process-wide SDK admission gate and the same configured provider instance. The MTA worker retains COM wrappers through actual completion, including synchronous manifest retrieval. Caller cancellation or the 90-second wait limit does not stop that native getter or kill the shared server. The app retains busy/close ownership until the actual worker settles; late success cannot replace an interrupted result. Selection changes and discovery snapshot changes invalidate details before notification. Response request/ID/version/time/field reconciliation and generation/reference checks reject stale or inconsistent results. Activity logging contains only the existing timestamp/module/state enums, not metadata text, identifiers or credentials.

## Official sources checked before implementation

Microsoft's pinned `winget-cli` **v1.10.340** sources were retrieved on 2026-11-07. This pin documents the implemented contract, not a claim of coverage for every installed App Installer release.

| Official source | SHA-256 of consulted bytes |
| --- | --- |
| [PackageManager.idl](https://github.com/microsoft/winget-cli/blob/v1.10.340/src/Microsoft.Management.Deployment/PackageManager.idl) | `565d91c6cb07392980010deb4870accb7e28bd507115cd3dde48e7a4ae864b31` |
| [PackageVersionInfo.cpp](https://github.com/microsoft/winget-cli/blob/v1.10.340/src/Microsoft.Management.Deployment/PackageVersionInfo.cpp) | `4dae4162e27bc397529fdeb14fe9b1a794a406fa5c0b0d4ca7c2b8bd8abf6d49` |
| [CatalogPackageMetadata.cpp](https://github.com/microsoft/winget-cli/blob/v1.10.340/src/Microsoft.Management.Deployment/CatalogPackageMetadata.cpp) | `4bd67cd9e7b9742923b7b12eedd6f2239be64a9b7dffc14c9d5a6363492c0cf2` |
| [ComClsids.h](https://github.com/microsoft/winget-cli/blob/v1.10.340/src/Microsoft.Management.Deployment/Public/ComClsids.h) | `989717cb54c90e32a9801ec56702b4ced51941db8ca13c46bb031fdc0926e3ce` |

The implementation retrieves a manifest and applies locale synchronously. The fixed production out-of-process PackageMatchFilter CLSID is `D02C9DAF-99DC-429C-B503-4E504E4AB000`; no development/in-process factory or source fallback is added.

## Validation limits

The Release solution build passed with zero warnings/errors, and all 1,669 portable tests passed, including 23 new metadata policy cases covering foreign/cloned/duplicate selection, stale/expired selectors, source/version mismatch, Unicode/control/bounds handling, inconsistent responses and permanent non-installability. Independent combined source review found no material blocker. These checks do **not** qualify actual Windows COM metadata retrieval, offline/policy behavior, cancellation during the synchronous getter or physical UI behavior. Earlier discovery qualification does not qualify this new route. The existing passive harness must never invoke this online action; any automation requires a separate explicit online qualification scope.
