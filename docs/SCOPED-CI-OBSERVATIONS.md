# Current-source Windows observations

The user reauthorized source pushes and standard hosted Windows Actions on 2026-11-12. The exact `feat/phase-one-foundation` and `recovery/validated-20261112` refs may publish development evidence to the existing `madclean-verification` branch. Pull requests, forks and other refs cannot use this publisher. No force push, final release ZIP, action-gate change or host/VM operation follows.

The source SHA, actual source ref, run ID and attempt accompany every newly published summary. The existing destination manifest remains a legacy branch marker, not authority for the current source. Old reports must never qualify newer code. A successful source push proves neither workflow start nor Windows test success.

The separate `MadClean-local-observations-and-sdk-reference-<run-attempt>` artifact contains three independently assessed scopes:

- `localRepairReview`: actual neutral window/navigation and fixed repair-draft controls, with history/confirmation and production capabilities closed. It proves no provider or repair execution.
- `localUpdateHistory`: one bounded local WUA history read. Accepted empty history, bounded history, negative provider responses and observation expiry remain distinct. No online Search or installation occurs. Historical results do not establish current applicability or installation.
- `sdkJournalReference`: bounded fragments from allowlisted already-installed SDK headers. No SDK acquisition, compiler or native call runs. Source text, hashes and lexical matches are reference material; definition interpretation, ABI, OS support, namespace safety and native qualification remain unverified.

The publisher downloads only this run's named artifact for its exact run attempt, with digest mismatch treated as an error. The attempt suffix is used at both upload and download so a publisher rerun cannot relabel an earlier attempt's unbound local report as new evidence. Each accepted scope is wrapped separately in `local-repair-review.json`, `local-update-history.json` or `sdk-journal-reference.json`. The wrapper records the original report byte hash and validated CI context; the inner report's source authenticity remains unverified. Passive schema checks authenticate neither Windows services nor reported SDK declarations.

A failed or missing artifact is not read as trusted leftover evidence. Multiple GUID reports, unexpected files within a scope, unsafe paths and invalid schemas are refused rather than selecting a convenient report. Rejection of one scope preserves independent evidence. Every latest summary records presence, reported state, original report hash and a fixed error reason for each scope; absent current evidence removes stale generated observations.

These optional observations never replace the separate general WPF/native report or Core tests, and never upgrade repair, MSI, SFC, DISM, driver or software-install qualifications. Native qualification jobs retain their original exact branch restrictions. The enclosing workflow can continue after an inconclusive observation, so inspect the per-scope states rather than treating a green job as universal success.

The frozen `3b25787` internal payload is unrelated immutable test material. New CI observations must not be represented as a rerun of those frozen bytes.

## Explaining a layout failure

The repair-review mode may write one optional `layout-observations.json` beside its unchanged primary report. It records bounded requested/actual/client sizes, DPI, primary-display work-area size and at most three fixed shell-kind geometry observations. It contains no control text, paths, screenshots or monitor identifiers. Primary work area does not prove which monitor hosts the window.

The companion hashes the exact closed primary-report bytes. Its absence, rejected hash/schema or failed write cannot change the primary verdict. The publisher adds accepted measurements and their original byte hash only to the repair wrapper; invalid diagnostics receive a fixed companion error while the valid primary remains available. A failed wide-layout fact stays failed. This separates an unachieved requested viewport from real shell bounds only after actual numeric evidence arrives; it does not presume either cause. Neutral-mode primary output and frozen payloads remain unchanged.

Source `76f31c4c44d7c822b3e7a357417f6e0d24b64e3f`, run `43301352066`, measured the failed wide request at 1200×900 DIP: actual 1044×788, client 1028×749, primary work area 1024×720, DPI 1. All three shell controls were visible and within the actual client. Compact 980×650 was achieved exactly. This supports the hosted-window tracking-limit diagnosis; it is not a generic claim that UI bounds failures are harmless.

The repair-review mode now reuses the existing `OwnWindowSizing` helper around only its wide/compact requests. It adjusts tracking limits and calls SetWindowPos only for the harness's own live HWND, then disposes the hook before navigation and normal finish. It changes no desktop/display settings, does not reparent visuals, and keeps exact 1200×900/980×650 and shell-bound assertions. A failed native resize does not count as success: the actual measured dimensions still decide. The legacy neutral mode acquires no sizing helper. This qualifies neither physical on-screen visibility nor DWM/DPI/accessibility; a new matching Windows report is required.

The corrected logical client-size scenario is a development-harness observation. It does not certify ordinary App screen-fit/max-tracking behavior or physical visibility on the hosted desktop.
