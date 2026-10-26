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

- Phase 1 is read only. No cleanup/deletion, registry modification, startup toggles, malware scans/remediation, installation, elevation or restart APIs. Writes are limited to MadClean's local activity log and development build outputs/provider-owned metadata refresh.
- Only explicitly documented temporary roots. No Downloads/documents/saves/credentials/browser-profile sweeps. Never traverse known reparse points; treat locked/inaccessible/recent files conservatively. This inventory does not authorize later deletion. Filesystem races require renewed validation at any future action boundary.
- No executing discovered startup commands, resolving arbitrary scripts, loading plugins, accepting source agreements, PowerShell policy bypass, or executing package installation commands.
- Fixed read-only PowerShell scripts use system PowerShell, encoded arguments, no profiles and no interactive prompts. WinGet uses the user's App Installer alias, not PATH/current-directory lookup. Preserve cancellation, timeouts, process-tree cleanup and output bounds.
- Unknown/unavailable/unsupported/failed/partial/cancelled are different from a completed check. Never infer “up to date” or “clean” from an empty/error/unparsed response.
- MadClean self-release checking is opt-in and user-triggered. Missing configuration must say “Update checking not configured.” Keep the pinned repository/HTTPS browser-path validation, anonymous no-redirect/no-cookie client, timeout/response bounds, stable-version validation and private/offline failure states. No automatic polling, external reminders, credential embedding, asset downloads, self-installation or restarts. Preserve confirmed session reminders through failed rechecks, with honest timestamps.
- Production uses real Windows data. Any future design/sample mode must be visibly marked everywhere it is active.
- Never persist raw provider output, full startup commands, detection resource paths or secrets in activity logs. Do not add telemetry or remote inventory upload without explicit user authorization.
- Later mutations require exact previews and explicit user intent. Restore points cannot restore deleted files. No registry cleaner, bulk drivers, forced restarts or silent elevation.

## Validation and review

Run tests relevant to each change and the full solution build. Keep meaningful filesystem, cancellation and malformed-provider-response coverage. Report test counts, skips, errors and platform. Windows-only integration and GUI checks cannot be claimed from Linux compilation. The Windows CI core tests do not launch the UI or validate live providers; use docs/VALIDATION.md's manual Windows matrix before a release. Do not access the user's personal PC while building. Do not push, trigger CI or publish releases unless authorized. Commit on a feature branch with a concise review summary and concrete validation evidence.
