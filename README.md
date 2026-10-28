# MadClean

A personal Windows desktop utility for understanding storage, startup entries, Microsoft Defender status, and software/driver update offers. **Phase 1 is read-only inventory.** It does not delete inventory candidates, change startup settings, start antivirus scans, install updates, elevate, or restart Windows. Explicitly selected exclusion preferences are saved only in MadClean’s local settings.

## Stack and architecture

C# / **.NET 10 LTS**, WPF, and a small MVVM presentation layer. WPF provides a maintainable Windows-native desktop surface without a web runtime. The verified cloud SDK is **10.0.403**; `global.json` pins that feature band and allows later servicing patches. No third-party runtime UI or integration libraries are required.

- `src/MadClean.App`: dark WPF UI, navigation, dashboard, background-operation orchestration and cancellation. Default state is unchecked; all production data comes from Windows. No sample/design data is injected.
- `src/MadClean.Core`: platform-independent models, conservative filesystem inventory, WinGet table parser, bounded cancellable process execution, and privacy-limited activity log.
- `src/MadClean.Windows`: read-only registry, disk, Windows PowerShell/Defender, WinGet, and Windows Update Agent adapters.
- `tests/MadClean.Tests`: core boundary tests. Windows-only runtime behavior is tracked separately in [validation](docs/VALIDATION.md).
- `.github/workflows`: Windows build/test/package workflow. No automatic release, install, signing, or deployment.

The app requests **asInvoker**, not administrator privileges. It reports inaccessible data rather than silently elevating. Disk space is available to the current user, not necessarily total free bytes under disk quotas.

## Build and run

Requirements: a supported Windows 11 x64 installation, .NET SDK 10.0.403 or a compatible servicing patch, and Internet access to NuGet for the first restore. Windows PowerShell 5.1 and the local Defender provider are needed for security status; Microsoft's App Installer / WinGet and configured source agreements are needed for app discovery. Organizational policy may restrict any provider. MadClean does not accept agreements for you.

From the existing checkout root:

```powershell
dotnet --info
dotnet restore MadClean.sln --locked-mode
dotnet build MadClean.sln -c Release --no-restore
dotnet test tests/MadClean.Tests -c Release --no-build
dotnet run --project src/MadClean.App -c Release --no-build
```

Click **Inspect this PC** for disks, cleanup preview (with saved exclusions), startup inventory and Defender status. Click **Check updates** to query WinGet and Windows Update. Update Center separates Apps, Drivers and BIOS / Firmware; **Read BIOS / PC identity** is a separate local read-only action. Online checks may refresh provider metadata and contact configured sources. **Cancel** stops unfinished inventory and terminates query child processes; provider-owned Windows services may finish an already-dispatched read-only search. Completed results remain timestamped. Switching tabs does not restart checks.

### Produce a Windows executable

```powershell
dotnet publish src/MadClean.App/MadClean.App.csproj -c Release -r win-x64 --self-contained true -p:MadCleanPackage=true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/win-x64
Get-FileHash artifacts/win-x64/MadClean.exe -Algorithm SHA256
```

Normal development uses `packages.lock.json`; single-file `win-x64` packaging uses separate committed `packages.win-x64.lock.json` graphs, since the SDK adds runtime/linker build tooling. Both commands restore in locked mode. Refresh these graphs intentionally when servicing the SDK or dependencies.

`artifacts/win-x64/MadClean.exe` is an **unsigned**, self-contained Windows x64 executable. WPF native dependencies extract under the normal .NET bundle directory on first launch. Do not enable trimming for this reflection/binding-based WPF app. The [Windows workflow](.github/workflows/windows.yml) builds and runs tests on pushes. Distribution publishing, ZIP creation and its SHA-256 manifest are disabled by default during roadmap development; the manual workflow has an explicit `prepare_package` input for the later distribution step. Test reports and scoped validation evidence remain available without a distribution ZIP. It must actually run before a CI artifact exists. Compilation/packaging alone does not validate executable behavior or Defender/WinGet/WUA integration. Signing and distribution are later release tasks; SmartScreen may flag unsigned builds.

On Linux, install the pinned SDK, use a writable `DOTNET_CLI_HOME` and `NUGET_PACKAGES` if home is read-only, then run the same restore/build/test commands. Windows targeting packs allow compilation; **WPF cannot run on Linux**. Do not substitute mock values and call Windows checks verified. No web server, database, API key or personal-PC access is needed to build.

## Additional explicit review tools

More tools includes duplicate content review, shortcut target observations, Windows Diagnostics (event metadata and separately requested minimized WER store metadata), guided Windows pages, startup recovery previews, and Microsoft integrity-tool identity previews. Each starts unchecked and requires its own selection/action; ordinary Inspect does not invoke them. Startup changes and DISM/SFC execution remain disabled. Opening a Windows Settings page does not prove a task completed. See [the execution ledger](docs/ROADMAP-STATUS.md) for implemented behavior, Windows evidence and unresolved action/source gates.

In Hardware Upgrades, a separate online action can verify narrow official Surface Laptop 6 for Business family service observations after a complete supported PC identity is read. It requests three fixed public Microsoft articles without sending device identifiers. It does not establish RAM/CPU/BIOS limits, exact replacement-part fit or upgrade advice; other models and missing evidence remain Unknown/Unsupported. See [source scope and validation](docs/SURFACE-SERVICE-FACTS.md).

## Interface

The supplied concept is implemented as a native navy/blue WPF workspace: horizontal Overview/Cleanup/Security/Startup/Updates navigation, an Inspect this PC hero, four inspection rows, a per-drive storage ring, Before you start guidance and an Activity footer. More tools retains every additional module. Inspect this PC runs local inventory only; online update discovery remains explicit. The ring uses real returned capacity or a neutral unchecked/unknown state, never the concept’s sample numbers. [UI structure and visual validation](docs/UI.md) records the inspected reference and remaining Windows rendering, keyboard/DPI and visual-comparison checks. Linux compilation does not verify visual fidelity.

## Read-only discovery extensions

- **Cleanup preview:** select an eligible file to inspect/copy its complete path and observation, then optionally exclude that exact file. Choose a folder exclusion for that folder and descendants. Preferences persist in `%LocalAppData%\MadClean\cleanup-exclusions.json`; saving/removing preferences changes only MadClean settings and requires an explicit rescan. Invalid/unreadable/reparse settings are preserved and block cleanup discovery. No candidate is deleted.
- **Startup apps:** source-aware inventory remains available. Reversible selected enable/disable is explicitly queued in the in-app Capability queue.
- **Installed apps:** Refresh registrations reads covered Add/Remove Programs metadata. Mechanism hints are not proof an uninstaller works; NoRemove restrictions remain visible. An explicit selected-registration preview requires fresh complete typed evidence, respects removal restrictions and offers a separately reviewed general Windows Settings handoff. No app identifier or uninstall command is passed to Windows; no uninstall is executed. Portable/unregistered apps and packaged apps lacking these registrations are outside coverage.
- **Selected MSI verification:** choose a reported Windows Installer app and explicitly verify its product/context and display registration through supported MSI metadata APIs. Only that exact product and current-user/source-matching machine contexts are queried; no SIDs, installer packages or uninstall commands are retrieved. Partial source coverage, advertised/mismatched/ambiguous metadata and removal restrictions remain visible. A metadata match never authorizes uninstall or authenticates a package. [Scope and limits](docs/MSI-REGISTRATION-VERIFICATION.md).
- **Installed-app search:** filter loaded app names or publishers, then clear the filter to restore the list. Search performs no new scan, preserves inventory coverage and full baselines, and invalidates a hidden app's selected review/MSI details. Search text is not saved or logged. No match does not prove an app is absent. [Behavior and validation](docs/INSTALLED-APP-SEARCH.md).
- **Windows packages:** the existing Installed apps page separates Desktop registrations from a current-user Main/Bundle package inventory. Read package identities and provider-reported version/type/signing/removal flags without requesting manifests, installation paths, dependencies or user SIDs. Unknown fields and provider warnings retain Partial coverage. Package rows are not unique apps or update/removal eligibility; desktop baselines remain separate. [Source and scope](docs/PACKAGED-APPLICATIONS.md).
- **Disk space map:** choose one fixed-local folder. The original treemap and linked list show known logical bytes, allow exact selection and drill/up/reset of the snapshot, and retain unknown/skipped/limited states. This is not physical allocation or safe-to-delete space. It reads metadata, not contents, and does not automatically scan all drives. The optional **Analyze snapshot file identities** action revalidates bounded metadata for the original map and distinguishes observed path-logical from unique-file logical bytes using native volume/file IDs. Outside-scope links, unobserved bytes, physical allocation and reclaimable space remain unknown. It does not read contents or change the map totals. Cleanup exclusions apply only to cleanup previews.
- **Leftover review:** explicitly Capture baseline before manually using Windows or an application's supported uninstaller outside MadClean. Later choose Review registration changes. Only complete inventories are compared, and only previously recorded local install locations are probed. A surviving directory is a **candidate**, not proven junk or proof that uninstall completed. Shared files, data, saves and settings are never automatically removed. Snapshots/results remain in memory and are lost on exit. Select one surviving directory and choose **Preview selected folder contents** for a separate bounded metadata review after another real registration refresh. Known shared/protected roots are blocked; protected descendants and unsafe metadata are excluded. Filenames/logical sizes are observations, not proven junk, exclusive ownership, inactive-use or removal eligibility. No contents are read and no item is deleted.

[Capability queue and action gates](docs/CAPABILITIES.md) · [Official workflow benchmark](docs/WORKFLOW-BENCHMARK.md). The existing phase-one features remain intact; cleanup execution, startup changes, supported uninstall execution and deeper leftover ownership analysis are queued explicitly.

## Additional explicit discovery

Under **More tools**, **Duplicate review** reads bounded selected-folder contents, confirms matches by full byte comparison and distinguishes hard-link aliases using actual Windows file identity. Protected/application/credential/save locations, links, unknown streams and active/unverifiable files are skipped; no item is removed and no reclaimable-space promise is made.

**Shortcut review** inspects stored `.lnk` metadata without Resolve, execution, tracking, saving, icon retrieval or installer activation. A missing supported local target is evidence for review; offline/network/access/reparse/unsupported cases remain unknown. No shortcut is removed.

**Windows Diagnostics** reads existing minimized Application/System event metadata for an explicitly chosen 1/7/30-day window, grouping event records with source, time, coverage, uncertainty and impact. No records does not mean healthy; no event message/dump content, DISM/SFC or repair command is read/run. D1b/D2/D3 remain separate gated work.

Its **Observed recurrence** summary shows counts from the returned event evidence, with an optional repeated-groups-only list filter. Counts mean event records, not unique crashes. Unknown checks have unknown totals; Partial evidence retains its coverage gaps. Filtering does not query Windows again, change the UTC window or combine WER store references with event counts.

These modules never run with ordinary Inspect this PC. Linux tests validate portable policies, not Windows native behavior. The opt-in development [Windows validation harness](tools/README-WINDOWS-VALIDATION.md) and [execution ledger](docs/ROADMAP-STATUS.md) distinguish actual scoped evidence from pending roadmap gates.

## BIOS / Firmware and Hardware Upgrades

Update Center's **Apps**, **Drivers**, and **BIOS / Firmware** categories keep distinct evidence and coverage. The BIOS page reports available CIM PC/model/SKU, motherboard and installed BIOS/date fields as **firmware-reported**; the reported board version is not necessarily a verified PCB revision. Windows Update firmware-class offers include source, package date/description and attempt time, but can concern device firmware rather than the system BIOS. Installed/available versions are never guessed from titles or dates: available BIOS version, exact-model applicability, release notes and prerequisites remain **Unknown** until verified.

A separate **Check supported official history** action reads two fixed Microsoft pages for the exact supported Surface Laptop 6 for Business 15-inch SKU. It shows dated release notes and an explicitly published UEFI version where present; driver-only releases leave that version unknown. It does not determine the latest applicable BIOS or compare installed versions. The ambiguous 13/13.5-inch variant remains Unknown and other unsupported models send no request. [Source coverage and limits](docs/SURFACE-FIRMWARE-HISTORY.md).

The first official guide catalog is generic **Microsoft Surface only**, via explicit browser actions; it is not exact-model/revision matching. Other manufacturers are Unsupported until their official references/adapters are verified. OEM PCs/laptops require OEM firmware, never a similarly named retail board's BIOS. No assets, flashes, silent installs, encryption changes or restarts occur. See [firmware source/precaution contracts and staged matching](docs/FIRMWARE-UPDATES.md).

The [Hardware Upgrades tab](docs/HARDWARE-UPGRADES.md) starts with a separate **Read hardware inventory** action for allowlisted local CIM PC/motherboard/CPU/RAM/GPU/storage facts, with exact provider sources and query-attempt time. It does not run with Scan local, contact manufacturers, or collect serial numbers/UUIDs/service tags. Missing, malformed, inaccessible or truncated provider data stays Unknown/Partial/Failed; these observations are not compatibility advice. Exact-model official RAM/CPU/required-BIOS/expansion/storage specifications and budget/use-case-gated advice remain queued. Detected facts, manufacturer-verified limits and manual checks stay separate. PSU/connectors, clearance, free physical slots and soldered-memory upgradeability are never inferred. This request authorizes no purchase or BIOS update.

## MadClean updates and the in-app reminder

The **MadClean updates** page and Overview status show **“Update checking not configured”** in the default build. No release source is enabled or contacted automatically. The existing Check updates action continues to cover installed apps and Windows/driver offers; **Check MadClean updates** is a separate explicit action, with progress and cancellation.

To opt in after a trustworthy release channel exists, create `madclean-updates.json` **beside MadClean.exe** with exactly:

```json
{"repository":"MadGenius9/MadClean"}
```

Restart MadClean after changing this deployment configuration. This build accepts only that pinned GitHub repository; arbitrary hosts, repositories and extra fields (including credentials) are rejected. The configuration is optional, read only, bounded to 4 KiB, and is not shipped/enabled in the package. Do not put a GitHub token or password in it.

A user-initiated check anonymously queries GitHub's latest published full-release metadata. Supported stable tags use `vMAJOR.MINOR.PATCH` or `MAJOR.MINOR.PATCH`. A successful comparison is scoped to that configured stable channel; malformed versions, failed requests and unchecked sources never become an up-to-date claim. Offline/timeouts, denied/rate-limited access, malformed responses and cancellation have explicit results. GitHub may return 404 for a private/inaccessible/missing repository **or** for no published stable release; MadClean reports that ambiguity honestly.

A confirmed newer version shows an **in-app reminder** across pages, its confirmation time, and plain-text release notes. Failed/cancelled rechecks retain that previously confirmed reminder with the latest check status; a successful no-newer-release comparison clears it. This knowledge lasts for the current app session. There are no startup checks, polling timers, scheduled tasks or external reminder services.

**Release notes / download** opens a validated HTTPS GitHub release page in your browser for manual review/download. If anonymous access fails for the private repository, the configured releases page can still be opened in your signed-in browser. MadClean does not collect/store/embed credentials, retrieve release assets, install itself, elevate, or restart Windows. Review the publisher/signature and release notes before choosing a download.

## Safety and scope

See [integration contracts](docs/INTEGRATIONS.md), [validation limits](docs/VALIDATION.md), and [agent rules](AGENTS.md).

- Cleanup enumerates only `%LocalAppData%\Temp` and `%WinDir%\Temp`. It inventories old readable files, reports categories/bytes/exclusions/errors, and makes **no deletion-safety guarantee**. Downloads, documents, saves, credential stores and browser profiles are not supported cleanup roots. Recent files, known protected names/extensions, encrypted/offline/system files, locked files and reparse points are skipped. No browser cache/profile sweep is implemented in phase 1.
- Startup coverage is Run/RunOnce in shared HKCU and both 32/64-bit HKLM views, plus the user/common Startup folders. These are registered entries, not proven enabled executions. Publishers and shortcut targets remain unknown; command lines are deliberately not collected. Services, scheduled tasks and packaged startup tasks are not covered.
- Defender results are actual provider flags and recorded detection IDs/actions. No new malware scan runs. An empty history does not mean a malware-free computer. MadClean is not an antivirus engine.
- WinGet matches only its configured `winget` source; unmapped installed apps are outside coverage. Table localization/version changes and rows with ambiguous Unicode display widths produce Unknown; the current table parser supports ASCII plus WinGet ellipsis characters. Missing update information never becomes “up to date.” Windows Update returns offers from the configured service, including drivers; this is not an exhaustive OEM driver catalog or a recommendation to replace every driver.
- Activity events go to `%LocalAppData%\MadClean\activity.jsonl`, rotated into one `.previous` file at approximately 1 MB. Only timestamp + enumerated operation/outcome are persisted. No provider output, filenames, command lines, usernames, detection resources or secret values are logged. Inventories/baselines exist only in memory. Explicitly chosen exclusion paths and scope flags persist separately in the local cleanup-exclusions settings file; no file contents, raw uninstall commands or scan inventories are saved there.

## Later milestones

[Duplicate-file and shortcut discovery](docs/FILE-REVIEW-ROADMAP.md) now ship as separate explicit read-only modules. Later last-copy-protected duplicate actions and selected shortcut-only removal remain gated; neither runs during ordinary inspection.

See the explicit [capability queue](docs/CAPABILITIES.md), including the later [Windows Diagnostics module](docs/WINDOWS-DIAGNOSTICS.md): evidence-based health/crash reporting, explicit non-repair corruption checks, then guided selected repairs. D1a event evidence is implemented separately; deeper WER metadata, integrity checks and repairs remain gated. Ordinary scans run no SFC/DISM or repair commands. Fresh exact candidate review before any selected cleanup; reversible startup actions; supported selected uninstall and ownership-evidence leftover review; explicit user-initiated Defender scans; individually selected software/driver installs using Windows Update, official manufacturers or trusted package sources. No registry cleaner, bulk driver replacement, forced restart or silent elevated action. Restore points may support eligible system changes; **they do not recover deleted files**. File recovery needs a separate backup strategy.
