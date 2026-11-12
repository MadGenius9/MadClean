# Proposed current-build offline Windows observation

**Plan only, 2026-11-12.** The coordinator owns the existing offline MadClean-Test VM and any transfer, runtime check, execution or snapshot restoration. None is authorized or performed by this document. Keep the original frozen `cd1718b` payload unchanged; its earlier neutral pass does not qualify the reconstructed branch or this newer mode. No GitHub/Actions or final release package is needed.

## Recommended bounded next check

Use the existing sole-argument `--local-repair-review-validation` mode in `tools/MadClean.WindowsSmoke/Program.cs`. It enters before the disposable-CI environment gate. No `MADCLEAN_WINDOWS_VALIDATION`, fabricated `GITHUB_*` context, configuration override or gate change is required or permitted. The source contract is [LOCAL-REPAIR-REVIEW-VALIDATION.md](LOCAL-REPAIR-REVIEW-VALIDATION.md).

The mode starts the normal App/MainWindow, disables physical input and observes neutral state, 1200×900 and minimum 980×650 logical shell bounds, primary/More-tools navigation and normal close/managed ownership settlement. It then observes the real Windows integrity tools view and actual bound controls. For the two fixed repair catalog rows it invokes only **Prepare local effects review**, exercises the four acknowledgment bindings, checks actual History/Confirm command readiness stays false independently of the disabled window, and observes selection invalidation before restoring neutrality.

It never invokes History, Confirm, identity preview, Inspect, any provider inventory, online checks, browser links, Defender scans, cleanup, registry changes, installers/uninstallers, update installation, DISM, SFC, repair or restart. It injects no provider data and takes no screenshots. Preparing the fixed effects draft is managed review, not dispatch or Windows action qualification. It reads managed busy/pending flags rather than creating synthetic native activity; no real active-worker cancellation scenario is exercised.

## Identity and prerequisites before a coordinator-approved run

1. Pin the final clean local commit and a SHA-256 manifest of the **complete current** Release harness output plus matching source/context/reader files after final build and review. This plan intentionally supplies no stale binary hash: the handoff remains **not execution-ready until that manifest and exact guest destination are agreed**. Record the recovered-source provenance; do not claim byte equivalence to historical `b3bd541`.
2. Verify all received bytes in host staging and guest against that manifest through the separately supported private transfer. Do not reuse or mutate the old neutral archive. No transfer/archive is prepared here.
3. Use only the clean, offline, x64 Windows 11 guest (build 22000 or newer), with no host folders/shared folders or personal data exposed. Place the actual matching tree under a fresh ordinary fixed-drive guest directory with no reparse ancestors. Retain real `MadClean.sln` and tracked `src/MadClean.App` context; never fake empty marker files. Use a distinct fresh root for this check.
4. Verify the guest has the existing compatible **x64 .NET 10 Core and Windows Desktop runtimes** required by the copied runtimeconfig/deps files. An SDK or a host-PC runtime report is insufficient evidence of guest prerequisites. This plan authorizes no installation. Preserve all harness/App/Core/Windows/WinGet assemblies and dependencies in their original output layout; the managed DLL route needs no guest restore/build/publish. Linux ELF apphosts must not be renamed or launched as Windows executables. This mode does not activate WinGet or require a package-manager bootstrap.
5. The coordinator should verify agreed clean guest MadClean settings by metadata checks before startup. Normal construction can read `%LocalAppData%\MadClean\cleanup-exclusions.json` and adjacent deployment `madclean-updates.json`. Stop if unexpected settings/configuration are present; do not read, rename, delete or redirect them to manufacture a clean profile. Do not include deployment release configuration, credentials or unrelated data in the transfer.
6. Provide an interactive guest display sufficient for both tested logical window bounds; 1600×1200 at 100% guest scaling is a conservative suggestion, not an observed VM fact or authorization to change host settings. No elevation/UAC request is needed. Leave all native execution, repair and journal-creation gates unchanged.

## Prospective command and expected writes

After the coordinator approves the exact root, runtime path and pinned manifest, set the working directory to that root and use only:

```powershell
& '<verified guest x64 dotnet.exe>' '.\tools\MadClean.WindowsSmoke\bin\Release\net10.0-windows10.0.22000.0\MadClean.WindowsSmoke.dll' '--local-repair-review-validation'
```

The placeholder runtime path is deliberately not executable as a final handoff. Do not use `dotnet run`, launch the App directly, add mode arguments, restore/build in the guest, or click controls. A missing runtime, insufficient display or startup refusal is not a pass.

The intentional writes are newly reserved directories and one fresh file at `<guest test root>\artifacts\local-repair-review-validation\<fresh GUID>\report.json`. The report has format 1, context `local-repair-review`, UTC bounds, fixed nullable neutral/repair observations, state and optional numeric HRESULT. It contains no provider text, settings contents, paths, command output, secrets or screenshots. No activity/settings write is expected; Activity must stay empty. Unexpected work fails neutrality and may trigger ordinary activity logging; do not hide that failure. Normal .NET/WPF/Windows caches or preferences may be accessed, so this is not a promise of zero OS I/O.

Retain actual process ownership through exit. Unknown/pending ownership deliberately holds the dispatcher/model/CLR; do not force close, kill workers or repeat the invocation to obtain success. Exit 0 means all scoped report observations passed, 1 failed, and 2 unsupported/inconclusive. Missing report or exit evidence is unknown. A coordinator should preserve any observed failure before separately deciding VM teardown/restoration; stopping a VM is not proof of application cleanup.

## Evidence and practical limits

Retain the source commit, manifest/hash comparisons, guest Windows/runtime/display facts, exact command/working directory, start/end times, actual exit code and the original report bytes with size/SHA-256. The optional passive reader is `python3 tools/read_local_repair_review_report.py <actual report.json>` and can run later in the development workspace without a guest Python installation. It starts no application/query. Reader consistency is not runtime authenticity; the report does not intrinsically authenticate or bind its source, so the separately recorded manifest and chain of custody matter.

A matching pass would resolve **current recovered-build** hosted WPF startup, coarse shell bounds, programmatic navigation, actual fixed repair-draft/acknowledgment bindings, selection invalidation, blocked History/Confirm commands and normal neutral closure. It would not resolve pixel fidelity, text clipping, physical input, accessibility, published executable startup, any read-only provider's behavior, native descriptor failure handling, journal namespace/security/crash durability, request execution or action-gate activation. It does not qualify repair success or system integrity. The fields `windowsProviders`, `maintenance` and `physicalInput` must remain `unverified`.

## Why no broader provider run is included

`--update-discovery` is a separate online WUA/WinGet observation. It can contact configured services and refresh provider-owned metadata; it also requires real `GITHUB_SHA`, `GITHUB_RUN_ID` and `GITHUB_RUN_ATTEMPT` context. The offline VM and current no-GitHub scope do not satisfy that contract. Do not invent CI variables, enable networking or treat offline empty/failure responses as coverage or up-to-date evidence.

The no-argument harness includes broad native/owned-file/registry fixtures and later observations. It is unsuitable for a no-cleanup/no-registry-change/no-maintenance test simply because it is called passive. Explicit MSI, component-store and SFC modes are also excluded. Published/app-update/structured-WinGet qualification modes require their separately reviewed contexts and may perform online provider work; they are not substitutes for the local mode.

There is currently no proposed provider invocation in this bounded handoff. A later current-build read-only-provider check needs its own reviewed fixed allowlist, explicit local context, output privacy/bounds, worker lifetime, expected Windows-managed writes and unknown/offline criteria. That is a separate source/approval step, not permission to broaden this run. The recommended prepare-only control observation is useful independently while those provider and native contracts remain open.

## Separate candidate backend phase added after this plan

A dedicated [local history-only backend validation mode](LOCAL-UPDATE-HISTORY-VALIDATION.md) is now implemented in source, separate from this prepare-only UI proposal. It invokes the real new local-history adapter once, not online update discovery or the broad harness. This is a new separately pinned build/mode requiring its own reviewed manifest, transfer and coordinator-approved invocation; it does not broaden the original UI run or qualify the WPF history control. No VM/provider run or export occurred while adding it. Use the prepare-only UI phase first; consider the bounded backend phase separately after its source/validation checkpoint is agreed.

Both documented local modes now compile in the same reviewed development tree. Local history validation/controller tests do not run either Windows mode. The final commit and current output identities are recorded outside the source tree after committing; no frozen payload or transfer is prepared by that identity record. Recheck every output hash before any future staging rather than assuming a mutable build directory remains unchanged.
