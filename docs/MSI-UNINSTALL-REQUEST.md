# Selected Windows Installer product request

This milestone is a separate, explicitly confirmed request to Windows Installer for one registered ProductCode. Ordinary inspection, installed-app discovery, MSI metadata checks and opening this preview never start it. Non-MSI registrations and raw `UninstallString`/`QuietUninstallString` commands are outside this path. Existing read-only MSI reports retain `CanExecute == false`; they are evidence, not a launcher token or a configuration bypass.

## Shared contracts and ownership

The original `MsiUninstallRequestPolicy.CreatePreview(scope: MsiActionBindingScope, binding, sourceGeneration, nowUtc, token)` path returns a bounded `CheckResult<MsiUninstallRequestPreview>`. It requires the exact existing selected-source scope, canonical nonempty ProductCode, complete fresh ARP coverage, known `NoRemove == false`, complete current-user/machine MSI enumeration and exactly one installed matching context with unchanged registration/name/version/publisher. Partial historical ARP coverage remains visible as Partial; it cannot substitute for complete current ARP coverage. Advertised, missing, additional, mismatched or unknown contexts refuse the request.

A separate overload accepts `MsiSelectedProductReadScope` and its actual `CheckResult<MsiSelectedProductPreparation>`. It requires the exact selector object/generation and reassesses its frozen primitive facts rather than trusting success/reason labels: three distinct exact registry-source observations, one unrestricted matching selected registration, complete bounded current-user/machine native enumeration, no native issues, and one unique Installed native context with matching metadata. Target coverage must be Available/ObservedMatch. Historical global Partial stays Partial, and current global inventory remains explicitly Not checked. A source hive supplies provenance, not native installation context. The original scope mask remains 3/4 in the display scope; it is not changed to pretend that the legacy binding rule accepted an independently observed context. Old read-only reports, `CanExecute == false`, diagnostic reasons and old request validation remain unchanged.

New intents carry a get-only `SelectedProductScope`; old intents carry null. Before dispatch, the same existing backend owner uses the new exact-product reader only for that typed path, then `ValidateFreshPreparation`. Every registry lookup, selected evidence timestamp, native row and final check must postdate the intent preparation, use the exact original selector and retain the same unique context/metadata. Labels are reassessed again. There is no extra process launcher, broad registry inventory refresh, context-mask rewrite, raw uninstall command or new production qualification. Legacy intents keep their original fresh-binding path. Both metadata paths share the existing ARP/MSI pending ownership checks.

The preview owns a private-constructed `MsiUninstallRequestIntent`. It exposes read-only selected identity, reported display metadata, observed context, source generation, UTC preparation/expiry and fixed arguments. Expiry is no later than the original source scope's existing two-minute lifetime. The sole allowed arguments are four separate values: `/x`, the canonical brace-form ProductCode, `/qf` and `/norestart`. Full UI is explicitly requested rather than inferred from a default. Product names, publishers, registry command text, filesystem package paths and user-supplied properties never become arguments.

For the legacy path, before dispatch the Windows owner performs another actual source/context lookup and `ValidateFreshBinding(intent, freshReport, nowUtc, token)`. Its report and instance observations must postdate the preview and retain the exact selected source, context and metadata. It must independently qualify the actual fixed System32 `msiexec.exe` through the existing held-file Microsoft trust, embedded-version and catalog policy. Trust in this executable is not authentication of the registered product or its publisher. Do not add uninstall to the DISM/SFC command catalogue.

The UI performs explicit confirmation, locks selection/filter/refresh controls during preparation/request ownership and invalidates the intent on earlier source/selection changes. `Invalidate` and `TryReserveDispatch(userConfirmed, sourceGeneration, nowUtc, token)` share one lock. Only one reservation can win. Wrong generation, expiry or cancellation before the boundary prevents dispatch. After successful reservation, invalidation cannot revoke the request; the caller performs no intervening await before its actual process-start call. Reservation proves only application intent consumption, not that Windows started a process or that an MSI context remained unchanged.

For the selected-product path, direct read-selector invalidation and request commitment share its lifecycle lock. The request already holds its own dispatch lock before entering that scope lock; only a tiny internal Core callback rechecks managed time/generation/cancellation and sets reservation. No await, native call or externally supplied callback occurs there. If selector invalidation wins first, dispatch is refused. If commitment wins first, later invalidation cannot revoke it. One selector can be consumed by only one request even if multiple previews were prepared from its observations. Invalidating an unreserved intent also invalidates its selector; an already consumed selector cannot prepare another read/preview. This is application intent ordering, not a lock on Windows registry/MSI state or package-defined effects.

The backend uses the same caller and separate `ProcessStartInfo.ArgumentList` values, with no shell, `runas`, alternate user, quiet/passive flag, arbitrary environment override or automatic retry. Windows Installer controls its own supported permission/UI behavior. `/qf` requests Full UI, including authored dialogs; package restrictions such as LIMITUI or Windows policy can still limit the interface. This request does not prove which dialogs were displayed or accepted. If Windows requires additional consent or policy blocks the operation, that remains an actual result to show, never a reason to silently elevate or bypass a restriction.

## What selection means

Msiexec's ProductCode request has no installation-context selector. The accepted context is a unique current-user/machine **observation immediately before dispatch**, not an atomic context or immutable package lock. Windows Installer resolves the registered GUID. No all-user/global absence, uninterrupted source continuity, installed-package authenticity or package-payload hash is claimed.

The exact preview identifies the selected registration, ProductCode, reported name/version/publisher, observed context, mechanism and requested removal operation. It does not promise a complete per-file change list: a product's installer can perform package-defined custom actions and change files, settings or services. The user must review these limits and choose the specific request. MadClean does not force-delete files or automatically select shared dependencies, user data, saves or settings for cleanup. A restore point does not recover deleted files.

## Process lifecycle and results

`MsiUninstallRequestResult` records request ID, request state, preparation/request UTC, actual process-start/exit UTC when observed, actual owned process ID and actual exit code. `ClassifyExit` supplies limited documented client-result categories:

| Observed exit | Meaning retained by MadClean |
| --- | --- |
| 0 | Windows Installer client reported success; not a global absence or complete-effects verdict. |
| 1602 | User cancellation reported; no guarantee every effect was rolled back. |
| 1605 / 1614 | Product-not-installed/uninstalled condition reported in the engine's scope; verify current registration coverage separately. |
| 1618 | Another installer operation is in progress; do not kill it or automatically retry. |
| 3010 | Success with restart required reported; let the user decide later. |
| 1641 | Restart initiation reported unexpectedly despite suppression; preserve it distinctly and do not relabel it ordinary success. |
| Other positive code | Failure reported; exact code is retained and effects may be partial. |
| Other signed/high-bit result | Unknown classification; preserve the actual code. |

`/norestart` requests Windows Installer restart suppression. MadClean never requests a restart or treats a restart result as permission to do so. Package-defined behavior is not an independently enforced sandbox.

After dispatch, use Windows Installer's own Cancel control if offered; package UI and policy may differ. Otherwise wait for the actual result. Normal/default full UI is requested by omitting quiet/passive flags, not guaranteed for every authored package. MadClean does not reuse the metadata process-tree killer, kill the MSI client/service, or report caller cancellation as installer cancellation. A rooted singleton owner retains the actual child/process and executable leases through its observation. A pending or uncertain owner blocks another request and application close. Actual exit evidence must survive later cancellation, query, recording or cleanup errors.

A later complete ARP/MSI refresh is a separate observation. Registration no longer observed means absence only in that covered source/context; it does not prove all product files were removed, user data is junk, or an operation completed globally. Never refresh an expired action scope by inventing new source timestamps. Existing leftover review still requires actual complete before/after snapshots and reports candidates only.

This MVP tracks ownership in the current app session. It does not claim durable crash recovery or reconstruct an operation from a PID after restart. Forced process termination, power loss or lost observation remains Unknown. Windows Installer's own transaction/rollback behavior is separate from MadClean's record.

## Validation and release boundary

Portable tests qualify policy boundaries, source/context/refusal, expiry, cancellation and concurrent one-shot reservation. Compilation does not establish Windows launch, UI prompts or uninstall behavior.

Any actual installation/removal qualification belongs to a separate explicit disposable-Windows mode, never ordinary inventory/native smoke. It may create only its exact fresh inert owned ProductCode and payload, with no custom actions, real installed-app selection, update installation, servicing/repair or client-PC operation. Verify actual registration, exact supported request, real exit, bounded post-observations and checked owned teardown. If qualification has not passed, report it as unverified rather than claiming that an enabled request is tested. Standard-user/UAC denial and physical Windows Installer UI still need their own applicable Windows validation.

## Official semantics rechecked

Current official sources were retrieved successfully and read on 2026-11-03:

- [msiexec command](https://learn.microsoft.com/windows-server/administration/windows-commands/msiexec) ([consulted source](https://github.com/MicrosoftDocs/windowsserverdocs/blob/main/WindowsServerDocs/administration/windows-commands/msiexec.md)): product request forms, full versus quiet/passive UI and restart control.
- [Command-Line Options](https://learn.microsoft.com/windows/win32/msi/command-line-options) ([consulted source](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/Msi/command-line-options.md)): `/qf` selects Full UI, and `/q` can be combined with `/x`. [User Interface Levels](https://learn.microsoft.com/windows/win32/msi/user-interface-levels) describes authored modal/modeless Full UI and the LIMITUI restriction.
- [Standard Installer command-line options](https://learn.microsoft.com/windows/win32/msi/standard-installer-command-line-options) ([consulted source](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/Msi/standard-installer-command-line-options.md)): `/uninstall` is `/x`, ProductCode is supported, `/norestart` maps to `REBOOT=ReallySuppress`, quiet/passive modes remove user interaction/cancellation.
- [Windows Installer error codes](https://learn.microsoft.com/windows/win32/msi/error-codes) ([consulted source](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/Msi/error-codes.md)): cancellation, unknown/uninstalled product, contention, success/restart-required and restart-initiated distinctions.
- [MsiEnumProductsExW](https://learn.microsoft.com/windows/win32/api/msi/nf-msi-msienumproductsexw) ([consulted source](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/msi/nf-msi-msienumproductsexw.md)): NULL SID restricts per-user enumeration to the current caller; the combined context mask also covers machine registrations. This is not all-user enumeration or an atomic selection mechanism for msiexec.

No actual installer, uninstall, elevation, repair or personal-PC operation ran while authoring these portable contracts.

## Portable result-observation admission

The App now uses `MsiUninstallResultPolicy.IsConsistent` instead of a private UI-only validator. Exact request binding, valid ordered UTC observations and defined states/classification are required. Actual client claims additionally require this intent's committed reservation and ClientStartObserved=true. Pure negative and fact-free ambiguous-start observations do not fabricate either. Missing optional PID, OS start time or exit code stays missing even after an actual observed exit; cleanup uncertainty may retain that exit under MonitoringUnknown. An already-run client result is not erased when its earlier preview expires. Supplied exit codes must retain the existing exact classifier. Tool diagnostics and result Message are not authorization inputs.

This adds portable boundary tests and one App admission call, not an executor or new preview. No launcher, production gate, qualification fixture or result-display contract is enabled. Actual client exit/installer-reported success remains separate from current registration absence, completed uninstall, leftover ownership, rollback and restart permission.
