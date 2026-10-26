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

Click **Scan local** for disks, cleanup preview (with saved exclusions), startup inventory and Defender status. Click **Check updates** to query WinGet and Windows Update. Update Center separates Apps, Drivers and BIOS / Firmware; **Read BIOS / PC identity** is a separate local read-only action. Online checks may refresh provider metadata and contact configured sources. **Cancel** stops unfinished inventory and terminates query child processes; provider-owned Windows services may finish an already-dispatched read-only search. Completed results remain timestamped. Switching tabs does not restart checks.

### Produce a Windows executable

```powershell
dotnet publish src/MadClean.App/MadClean.App.csproj -c Release -r win-x64 --self-contained true -p:MadCleanPackage=true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/win-x64
Get-FileHash artifacts/win-x64/MadClean.exe -Algorithm SHA256
```

Normal development uses `packages.lock.json`; single-file `win-x64` packaging uses separate committed `packages.win-x64.lock.json` graphs, since the SDK adds runtime/linker build tooling. Both commands restore in locked mode. Refresh these graphs intentionally when servicing the SDK or dependencies.

`artifacts/win-x64/MadClean.exe` is an **unsigned**, self-contained Windows x64 executable. WPF native dependencies extract under the normal .NET bundle directory on first launch. Do not enable trimming for this reflection/binding-based WPF app. The [Windows workflow](.github/workflows/windows.yml) builds, runs the core tests and uploads a ZIP plus SHA-256 manifest. It must actually run before a CI artifact exists. Compilation/packaging alone does not validate executable behavior or Defender/WinGet/WUA integration. Signing and distribution are later release tasks; SmartScreen may flag unsigned builds.

On Linux, install the pinned SDK, use a writable `DOTNET_CLI_HOME` and `NUGET_PACKAGES` if home is read-only, then run the same restore/build/test commands. Windows targeting packs allow compilation; **WPF cannot run on Linux**. Do not substitute mock values and call Windows checks verified. No web server, database, API key or personal-PC access is needed to build.

## Read-only discovery extensions

- **Cleanup preview:** select an eligible file to inspect/copy its complete path and observation, then optionally exclude that exact file. Choose a folder exclusion for that folder and descendants. Preferences persist in `%LocalAppData%\MadClean\cleanup-exclusions.json`; saving/removing preferences changes only MadClean settings and requires an explicit rescan. Invalid/unreadable/reparse settings are preserved and block cleanup discovery. No candidate is deleted.
- **Startup apps:** source-aware inventory remains available. Reversible selected enable/disable is explicitly queued in the in-app Capability queue.
- **Installed apps:** Refresh registrations reads covered Add/Remove Programs metadata. Mechanism hints are not proof an uninstaller works; NoRemove restrictions remain visible. No uninstall command or application is launched. Portable/unregistered apps and packaged apps lacking these registrations are outside coverage.
- **Disk space map:** choose one fixed-local folder. The original treemap and linked list show known logical bytes, allow exact selection and drill/up/reset of the snapshot, and retain unknown/skipped/limited states. This is not physical allocation or safe-to-delete space. It reads metadata, not contents, and does not automatically scan all drives. Cleanup exclusions apply only to cleanup previews.
- **Leftover review:** explicitly Capture baseline before manually using Windows or an application's supported uninstaller outside MadClean. Later choose Review registration changes. Only complete inventories are compared, and only previously recorded local install locations are probed. A surviving directory is a **candidate**, not proven junk or proof that uninstall completed. Shared files, data, saves and settings are never automatically removed. Snapshots/results remain in memory and are lost on exit.

[Capability queue and action gates](docs/CAPABILITIES.md) · [Official workflow benchmark](docs/WORKFLOW-BENCHMARK.md). The existing phase-one features remain intact; cleanup execution, startup changes, supported uninstall execution and deeper leftover ownership analysis are queued explicitly.

## BIOS / Firmware and later Hardware Upgrades

Update Center's **Apps**, **Drivers**, and **BIOS / Firmware** categories keep distinct evidence and coverage. The BIOS page reports available CIM PC/model/SKU, motherboard and installed BIOS/date fields as **firmware-reported**; the reported board version is not necessarily a verified PCB revision. Windows Update firmware-class offers include source, package date/description and attempt time, but can concern device firmware rather than the system BIOS. Installed/available versions are never guessed from titles or dates: available BIOS version, exact-model applicability, release notes and prerequisites remain **Unknown** until verified.

The first official guide catalog is generic **Microsoft Surface only**, via explicit browser actions; it is not exact-model/revision matching. Other manufacturers are Unsupported until their official references/adapters are verified. OEM PCs/laptops require OEM firmware, never a similarly named retail board's BIOS. No assets, flashes, silent installs, encryption changes or restarts occur. See [firmware source/precaution contracts and staged matching](docs/FIRMWARE-UPDATES.md).

A later [Hardware Upgrades tab](docs/HARDWARE-UPGRADES.md) is explicitly queued: read-only CPU/RAM/GPU/storage inventory, then exact-model official RAM/CPU/required-BIOS/expansion/storage specifications, then budget/use-case-gated advice. Detected facts, manufacturer-verified limits and manual checks stay separate. PSU/connectors, clearance, free physical slots and soldered-memory upgradeability are never inferred. This request authorizes no purchase or BIOS update.

## MadClean updates and the in-app reminder

The **MadClean updates** page and Overview card show **“Update checking not configured”** in the default build. No release source is enabled or contacted automatically. The existing Check updates action continues to cover installed apps and Windows/driver offers; **Check MadClean updates** is a separate explicit action, with progress and cancellation.

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

[Duplicate-file and shortcut review](docs/FILE-REVIEW-ROADMAP.md) are queued separately: user-selected-folder content verification (not names), hard-link/reparse/protected-area handling and later manual last-copy-safe actions; then read-only shortcut-target findings with reasons, unknown offline/network/temporary states, and later selected shortcut-only removal. Neither module is part of the first milestone or ordinary inventory.

See the explicit [capability queue](docs/CAPABILITIES.md), including the later [Windows Diagnostics module](docs/WINDOWS-DIAGNOSTICS.md): evidence-based health/crash reporting, explicit non-repair corruption checks, then guided selected repairs. Diagnostics is queued, not part of phase-one inventory; ordinary scans run no SFC/DISM or repair commands. Fresh exact candidate review before any selected cleanup; reversible startup actions; supported selected uninstall and ownership-evidence leftover review; explicit user-initiated Defender scans; individually selected software/driver installs using Windows Update, official manufacturers or trusted package sources. No registry cleaner, bulk driver replacement, forced restart or silent elevated action. Restore points may support eligible system changes; **they do not recover deleted files**. File recovery needs a separate backup strategy.
