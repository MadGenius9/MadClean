# Working on MadClean

## Architecture and commands

Use the existing isolated checkout. Do not create a Git worktree unless the user explicitly asks.

Read README.md and docs/INTEGRATIONS.md before changing Windows behavior. Keep presentation in `src/MadClean.App`, portable policy/models/parsers in `src/MadClean.Core`, Windows adapters in `src/MadClean.Windows`, and boundary tests in `tests`. Use the supported SDK pinned in global.json. Avoid unnecessary runtime dependencies and update package lockfiles deliberately when dependencies change.

From the repository root:

```text
dotnet restore MadClean.sln --locked-mode
dotnet build MadClean.sln -c Release --no-restore
dotnet test tests/MadClean.Tests -c Release --no-build
```

Windows run/package commands are in README.md. On this cloud machine the SDK is `/workspace/.dotnet/dotnet`; set `DOTNET_CLI_HOME=/workspace/.dotnet-home` and `NUGET_PACKAGES=/workspace/.nuget/packages`. .NET runtime IPC may require execution outside the filesystem sandbox. Do not disable TLS, package signatures, artifact verification or assertions to make builds pass.

## Safety invariants

- Phase 1 is read only. No cleanup/deletion, registry modification, startup toggles, malware scans/remediation, installation, elevation or restart APIs. Writes are limited to MadClean's local activity log, explicitly selected persistent cleanup exclusions under its local settings folder, and development build outputs/provider-owned metadata refresh.
- Only explicitly documented temporary roots. No Downloads/documents/saves/credentials/browser-profile sweeps. Never traverse known reparse points; treat locked/inaccessible/recent files conservatively. This inventory does not authorize later deletion. Filesystem races require renewed validation at any future action boundary.
- No executing discovered startup commands, resolving arbitrary scripts, loading plugins, accepting source agreements, PowerShell policy bypass, or executing package installation commands.
- Fixed read-only PowerShell scripts use system PowerShell, encoded arguments, no profiles and no interactive prompts. WinGet uses the user's App Installer alias, not PATH/current-directory lookup. Preserve cancellation, timeouts, process-tree cleanup and output bounds.
- Unknown/unavailable/unsupported/failed/partial/cancelled are different from a completed check. Never infer “up to date” or “clean” from an empty/error/unparsed response.
- MadClean self-release checking is opt-in and user-triggered. Missing configuration must say “Update checking not configured.” Keep the pinned repository/HTTPS browser-path validation, anonymous no-redirect/no-cookie client, timeout/response bounds, stable-version validation and private/offline failure states. No automatic polling, external reminders, credential embedding, asset downloads, self-installation or restarts. Preserve confirmed session reminders through failed rechecks, with honest timestamps.
- Production uses real Windows data. Any future design/sample mode must be visibly marked everywhere it is active.
- Never persist raw provider output, full startup commands, detection resource paths or secrets in activity logs. Do not add telemetry or remote inventory upload without explicit user authorization.
- Disk maps are metadata-only selected-local-folder views of known logical bytes. Skipped/unknown sizes must remain incomplete; never imply physical allocation, safe deletion or speed/repair improvements. Installed-app inventory must not use Win32_Product, read/execute raw uninstall commands, or force-delete applications. Leftover comparisons require complete in-session snapshots, probe only prior validated local install paths, and label results as candidates rather than junk. No profile/AppData sweeps or automatic shared-dependency/data/save/settings removal.
- Windows Diagnostics is roadmap-only until explicitly implemented in stages (docs/WINDOWS-DIAGNOSTICS.md). Never attach SFC/DISM/repair commands to ordinary inventory or infer health from missing events. Each finding requires source/time range/coverage/uncertainty/impact. Recheck current official Microsoft command semantics before any future tool implementation; repairs require explicit selected actions, elevation consent, safe cancellation/status and actual result logs. No arbitrary DLL downloads, scalar health scores or performance promises. Do not reuse the metadata-query process-tree kill runner for servicing repairs.
- Duplicate/shortcut review is roadmap-only (docs/FILE-REVIEW-ROADMAP.md). Duplicate proof requires full content verification, not names/size/hash alone; distinguish hard-link aliases, skip reparse/protected/system/application areas by default, and require supported handle-based consistency and protection of a verified independent retained copy through any later selected action, or block it. Shortcut inspection must not activate/search/track/save targets; offline/network/temporary/access-denied cases are unknown, not broken. Later removal is explicitly selected and shortcut-only. Do not add these scans or deletion to the original milestone.
- BIOS/firmware discovery is read-only and explicit: use local projected CIM facts and supported WUA class metadata, never title/date-to-BIOS-version inference. Firmware-reported board Version is not necessarily PCB revision. Keep installed BIOS identity, WUA package metadata and manufacturer-verified model/revision/version sources separate. OEM firmware overrides any retail-board resemblance; unidentified/unsupported/offline/ambiguous sources never become Up to date. Guides must be allowlisted official pages, with vendor power/recovery-key prerequisites and their uncertainty disclosed. No firmware assets, flashes, installs, encryption changes, restarts or purchases. Hardware Upgrades H1 is an explicit local read-only allowlisted CIM inventory under docs/HARDWARE-UPGRADES.md; manufacturer specifications and advice remain queued. Never collect serial/UUID/service-tag identifiers or infer PSU/connectors/clearance/free slots/soldered-memory upgradeability or manufacture compatibility advice.
- Later mutations require exact previews and explicit user intent. Restore points cannot restore deleted files. No registry cleaner, bulk drivers, forced restarts or silent elevation.

## Visual reference and presentation

The supplied navy/blue concept is a visual reference only. Keep horizontal primary navigation, all secondary modules reachable through More tools, and the existing native WPF architecture. Inspect this PC runs local inventory only; online discovery is a separate explicit action. Never copy the concept’s sample capacity/status values into production. The storage ring represents free space for a named selected drive only after a valid Available/Partial result; preserve quota, partial, unknown, failure and timestamp/source information. Decorative geometry is not measured health. Native-frame differences and Windows rendering/fidelity limits belong in docs/UI.md; Linux compilation is not a screenshot comparison.

## Validation and review

Run tests relevant to each change and the full solution build. Keep meaningful filesystem, cancellation and malformed-provider-response coverage. Report test counts, skips, errors and platform. Windows-only integration and GUI checks cannot be claimed from Linux compilation. The Windows CI core tests do not launch the UI or validate live providers; use docs/VALIDATION.md's manual Windows matrix before a release. Do not access the user's personal PC while building. Do not push, trigger CI or publish releases unless authorized. Commit on a feature branch with a concise review summary and concrete validation evidence.
