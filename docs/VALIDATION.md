# Validation and release gate

## Cloud capabilities

Linux can compile all projects using Windows reference packs and run portable core tests. It cannot run WPF, registry/Startup-folder inventory, Defender, WinGet's Windows execution alias or Windows Update Agent. Build success is not a rendered-UI or executable smoke test. See the current result record below for actual checks rather than assuming the CI workflow has run.

The test suite exercises old/recent/protected files, sharing violations, unreadable and missing paths, reparse loops and ancestors, overlapping scopes, inventory limits, cancellation before/during scanning, process cancellation/timeouts, malformed/localized/compact updater tables, unknown/truncated update status, strict Windows Update offer metadata and cancellation, unknown update types, fail-closed empty/relative scopes, dispatcher-independent process cancellation and bounded metadata-only log rotation. Three filesystem-specific tests are explicitly skipped on Windows; junctions and ACLs still require Windows checks. New MadClean release tests use a fake HTTP handler to exercise missing configuration, pinned source/links, private/access/rate-limit responses, malformed metadata/tags, bounded notes/responses, network failures, cancellation and session reminder retention. No production provider is replaced or given placeholder success data.

## Independent parallel review

Three native sub-agents reviewed cleanup inventory, Defender/security, and software/driver update discovery under fixed shared contracts. Cleanup and update agents edited their owned core parsers/scanner and component tests; the Defender agent supplied separate contract tests and adapter recommendations. The coordinator integrated shared adapter, UI, process-lifecycle, logging and status fixes. A fourth read-only reviewer rechecked the combined implementation for correctness, permissions, failure handling and honest results, with no remaining material issue identified after fixes. This is a static/code review, not Windows runtime verification.

Regression additions cover duplicate cleanup-scope budgeting, root/issue limits, genuine detection-history contracts, one-space WinGet column layouts, ambiguous/truncated output, strict WUA metadata, cancelled/partial search states and dispatcher shutdown. Provider path/module choices and Windows ACL behavior still require the manual matrix below.

## Required Windows manual validation (not performed in cloud)

Use a disposable Windows 11 test account/VM, not the user's personal PC. Run unelevated. Record OS, architecture, SDK/build hash, provider versions and observed result. Do not enable installations or deletion to validate phase 1.

| Area | Required observation |
| --- | --- |
| Executable/UI | Extract package, start MadClean.exe, verify the dark seven-page UI, keyboard focus/navigation, resize at minimum size and 100/150/200% DPI, screen-reader labels, grid scrolling and long text. No sample data or UAC prompt. |
| Local inventory | Click Scan local. Compare fixed-disk totals with Windows APIs; recent/inaccessible files show exclusions. Startup list matches covered Run/RunOnce and Startup folders; publishers/targets not verified remain Unknown. |
| Cleanup safety | Use disposable old/recent/locked files, inaccessible ACL folders and junctions in supported temp roots. Inventory never changes bytes, deletes or traverses a junction. Review Partial errors; the UI stays responsive. |
| Defender | Compare flags and signature data with Get-MpComputerStatus. Verify unavailable/passive/disabled states and denial of detection history. No claim of a new scan or clean machine; no resource paths in the activity log. |
| App discovery | With supported WinGet and accepted source agreements, compare list results. Test missing alias, unavailable network, localized headers, pinned/unknown/unmapped apps and agreement-required source. No unchecked app becomes up to date. No packages installed. |
| Windows Update | Compare software/driver offers against the configured update service, including no offers, policy restrictions, service failure and partial search. No installs or restarts. A driver catalog outside that service is unsupported. |
| MadClean release checking | Verify default “Update checking not configured” and no startup request. With opt-in config, use the dedicated check button, verify private/offline/404/access/rate-limit/malformed/cancel states, newer-version banner, notes and failed-recheck reminder retention. Confirm current-channel result clears reminder. Validate browser review links, signed-in private review and no asset download/install/restart by MadClean. Restart after config changes; verify no external scheduled reminders. |
| Cancellation | Cancel during large temp inventory and each slow provider. UI responds, unfinished results say Cancelled, completed results retain timestamps, query child processes exit. Repeat the scan/check and close mid-check. |
| Privacy/logging | Log contains only utc/area/outcome, rotates to one previous file; log write denial is visible. No inventory paths, commands, raw stderr, threat resource paths or secret values persist. |

Windows CI compiles/packages and runs the portable test assembly on Windows; it is **not** this manual runtime gate. CI is configured but has not been triggered by this task. Before distributing, complete this matrix, verify the SHA-256 manifest, perform appropriate signing, and test the packaged executable itself on a clean VM.

## Current result record

Verified in the Linux cloud environment using SDK **10.0.403**, runtime **10.0.14**:

- Locked development restore and complete Release solution build: **passed**, zero warnings/errors.
- Core tests: **143 passed, 0 failed, 0 skipped**. Current-run results: `artifacts/test-results/core-linux.trx`.
- Locked self-contained single-file `win-x64` publish: **passed**. Development lockfile hashes remained unchanged by packaging. Packaging graph includes only the explicit `win-x64` RID.
- Produced `artifacts/win-x64/MadClean.exe`; inspected as **PE32+ Windows x64 GUI**. Unsigned; **not executed or runtime-tested on Windows**.
- ZIP and SHA-256 manifest: `artifacts/MadClean-win-x64.zip` and `artifacts/win-x64/SHA256SUMS.txt`. Generated artifacts are ignored by Git; reproduce with README commands.
- MadClean self-release checker: **default not configured**, no live API call made. Fake-handler and configuration/reminder tests passed; independent static review found no material issues.
- Windows CI: workflow added, **not run**. GUI/DPI/accessibility and registry/Defender/WinGet/WUA live behavior: **unverified**. No personal PC was accessed; no cleanup, Defender scan, software/driver installation or restart occurred.

The original publish attempt found a mismatch between the normal dependency graph and SDK-added single-file tooling. The final configuration uses a dedicated packaging graph. When intentionally refreshing packaging locks, specify **`-p:RuntimeIdentifier=win-x64`**, not restore's `-r` shorthand: restore adds to `RuntimeIdentifiers`, which otherwise lets single-file inference also include the Linux host RID. Production packaging now runs with locked restore and does not weaken verification.
