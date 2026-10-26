# Read-only integration contracts

Official Microsoft documentation was checked before implementation, using Microsoft's official GitHub documentation sources because `learn.microsoft.com` was denied by the cloud proxy. Links below include the browsable reference and the source consulted. No third-party scraping or undocumented driver updater is used.

## WinGet

- [list command](https://learn.microsoft.com/windows/package-manager/winget/list) — [consulted source](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/hub/package-manager/winget/list.md)
- [upgrade command](https://learn.microsoft.com/windows/package-manager/winget/upgrade) — [consulted source](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/hub/package-manager/winget/upgrade.md)
- [export command / JSON schema](https://learn.microsoft.com/windows/package-manager/winget/export) — [consulted source](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/hub/package-manager/winget/export.md)

Implementation calls **only** `winget list --source winget --disable-interactivity`. Microsoft's documented list output includes installed version and, where available, offered version/source. Upgrade supports both listing and actual installation, so MadClean deliberately avoids that command. Export gives structured matched inventory but not available update versions, so it is not used as an update oracle.

WinGet currently exposes a localized human table in this path, not a documented list JSON contract. The portable parser accepts only recognized English headers/column positions and complete IDs, including WinGet's legitimate single-space header boundaries. It currently accepts ASCII rows plus WinGet's one-cell ellipsis; rows with other Unicode characters fail closed to Unknown because terminal display width cannot safely be inferred from UTF-16 indexes. Truncated/unknown installed or offered versions stay Unknown. Empty tables do not become successful inventory determinations.

The independent update audit also checked Microsoft's [TableOutput.cpp](https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerCLICore/TableOutput.cpp) and [WorkflowBase.cpp / ReportListResult](https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerCLICore/Workflows/WorkflowBase.cpp) for column widths and source-filtered listing behavior. Unknown or malformed layouts fail closed to Unknown; no package is marked up to date. Packages with a real nonempty offered version are Available; others are Unknown. A successful overall provider state means an inventory was returned, not that every listed app was checked successfully. Source access, matching, pinning, localization, version and agreement limitations remain visible. A future version can use Microsoft's deployment APIs with independently tested contracts instead.

No arbitrary executable is searched on PATH. The adapter uses `%LocalAppData%\Microsoft\WindowsApps\winget.exe`, the App Installer execution alias. If absent or inaccessible it reports Unavailable/Failed; it does not install WinGet. The user's configured `winget` source is trusted as their package-manager configuration; MadClean does not add or replace sources. No agreement-acceptance, install, upgrade, download, source modification or reboot flags are used.

## Defender

[Get-MpComputerStatus](https://learn.microsoft.com/powershell/module/defender/get-mpcomputerstatus) — [consulted source](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/Defender/Get-MpComputerStatus.md).

[Get-MpThreatDetection](https://learn.microsoft.com/powershell/module/defender/get-mpthreatdetection) — [consulted source](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/Defender/Get-MpThreatDetection.md). This reports active and past detections; action success alone must not be interpreted as a current malware verdict.

System Windows PowerShell imports the Defender manifest from its absolute `$PSHOME/Modules/Defender` path and uses module-qualified Defender commands. Both provider scripts restrict PSModulePath inside their child process to system PowerShell modules, leaving user/system configuration unchanged. System Windows PowerShell runs a constant query script with `Get-MpComputerStatus` and `Get-MpThreatDetection`. It projects booleans, mode, signature version/timestamp, and detection ID/action/time into bounded JSON. It excludes detection resource paths. Missing flags remain Unknown; unreadable detection history makes the result Partial even if protection flags are available. A passive/disabled/third-party-protected system must not be presented as actively protected by Defender. An empty detection history is not a clean bill of health. No `Start-MpScan`, remediation or quarantine method exists in phase 1.

## Windows Update / drivers

- [IUpdateSession](https://learn.microsoft.com/windows/win32/api/wuapi/nn-wuapi-iupdatesession) — [consulted source](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/wuapi/nn-wuapi-iupdatesession.md)
- [IUpdateSearcher.Search](https://learn.microsoft.com/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search) — [consulted source](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/wuapi/nf-wuapi-iupdatesearcher-search.md)

[OperationResultCode](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/wuapi/ne-wuapi-operationresultcode.md) and [UpdateType](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/wuapi/ne-wuapi-updatetype.md) define the result/type values used below.

A system PowerShell child creates `Microsoft.Update.Session`, sets the client application ID, and calls the supported WUA search interface with `IsInstalled=0 and IsHidden=0`. It leaves service selection and policy intact and projects title, update ID, type and current reboot-required flag. Type 2 identifies a Driver offer. Search result code 2 is successful; 3 is partial; others fail. No downloader, installer, reboot, registry tweak or OEM web scrape is used. Search may contact the configured Microsoft/managed update server and refresh Windows-owned metadata. No results means no offers returned by **this search**, never that all drivers/software are current.

## Startup / disks / files

[Run and RunOnce reference consulted](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/SetupApi/run-and-runonce-registry-keys.md).

.NET DriveInfo provides fixed-disk capacity and per-user available space. Registry keys are opened read-only. Shared HKCU Run/RunOnce keys are read once; redirected HKLM keys are read in both explicit views. Inaccessible Startup folders produce Partial rather than silently disappearing. Known-folder locations and executable paths must be fully qualified; unresolved folders never fall back to the working directory. Startup folder entries are names only; shortcuts are not executed or resolved. Unknown publishers are explicit. Inventory covers a subset of startup mechanisms and does not infer enabled state.

Cleanup's two fixed roots are derived from Windows special folders, not arbitrary user-supplied paths. It checks reparse-point ancestry and entries, excludes known sensitive names/extensions, and uses a read-only exclusive open to exclude sharing violations. Files must be at least seven days old. Metadata reads are transient snapshots; file replacement and directory reparse races cannot be eliminated by path checks alone. Nothing is deleted and no contents are read. A future deletion implementation must use handle-based identity/reparse checks and a fresh exact preview, rather than reuse these candidate paths blindly. Limits: 100,000 visited entries and 1,000 reported issues; reaching either yields Partial.

## MadClean release source

The official GitHub [latest release API](https://docs.github.com/rest/releases/releases#get-the-latest-release) was checked against GitHub's [OpenAPI source](https://github.com/github/rest-api-description/blob/main/descriptions/api.github.com/api.github.com.json), operation `GET /repos/{owner}/{repo}/releases/latest`. It returns the latest published non-draft, non-prerelease full release, ordered by `created_at` (the source commit's date), rather than a scan of every semantic-version tag/channel.

An optional adjacent `madclean-updates.json` opts in only to `MadGenius9/MadClean`. Missing configuration returns NotConfigured without any request; invalid/unreadable configuration returns Unknown with no current-version claim. The file is loaded once at startup, permits exactly one repository field, rejects duplicate/extra/credential fields, and reads at most 4 KiB. No configuration file is distributed by default.

The dedicated check button queries only `https://api.github.com/repos/MadGenius9/MadClean/releases/latest`, with a dedicated anonymous HttpClient (no redirects, cookies or server credentials), 15-second request deadline and 256 KiB response bound. It does not use injected Git credentials, read PAT environment variables or sign into GitHub. A 404 cannot distinguish a private/inaccessible/missing repository from no published full release; 401/403/429 are unavailable access/rate-limit outcomes, other HTTP/network/timeouts fail, cancellation is distinct, and malformed metadata is Unknown. No error-response bodies or exception details enter the UI/log.

Only published stable three-part numeric tags (optional v/V prefix, no prerelease/build suffix) are compared with the running assembly's major/minor/build; assembly revision is ignored. Valid equal/older latest metadata yields “No newer stable release found,” scoped to this configured endpoint. It never establishes the state of other release channels or installed software.

Newer metadata produces a session reminder and bounded plain-text notes (8 KiB, control/bidirectional format characters removed). No Markdown/HTML is executed. Review actions accept only HTTPS github.com on the default port, without user info/query/fragment, and exact pinned repository `/releases` or safe `/releases/tag/<tag>` paths. Encoded/dot/backslash ambiguity and asset/download URLs are rejected. The app opens this page only on explicit user action and does not fetch or execute assets. Private access can be reviewed in the user's separately authenticated browser without credentials entering MadClean.

The reminder retains an already confirmed newer release and original confirmation time after unsuccessful rechecks, labeling the latest outcome. A successful current-channel comparison clears it. Reminder state is in memory; no scheduled job, external notification, background poll, startup request, silent installer or restart is created. Activity logging remains enums/timestamp only under MadCleanUpdates.

## Failure / permissions / cancellation

The app stays at the current user's privilege level. Windows policies, absent providers, disabled services, offline sources and missing agreements produce actionable messages plus safe error codes, not raw error text that may contain secrets. Defender and update processes are bounded (45/90/180 seconds) and their stdout/stderr each capped at 4 MiB. Cancellation registration terminates the query process tree independently of WPF dispatcher shutdown; internal process awaits avoid capturing the UI context. Cancellation kills the query process tree; an OS service may complete its already-dispatched read-only work. No process is assumed to survive cloud publication.
