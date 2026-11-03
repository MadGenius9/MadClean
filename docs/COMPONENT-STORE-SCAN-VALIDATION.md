# Separate fresh component-store scan qualification

The fixed `--component-store-scan-validation` mode runs only in its separate GitHub-hosted `windows-component-store-scan` job for this repository, source/run/attempt and feature branch. It requires the fixed scan-mode flag and an already effective administrator token. It does not request elevation or act on a personal PC. Ordinary inventory and the recorded-flag job never select this mode.

The only fresh operation is:

```powershell
Dism\Repair-WindowsImage -Online -ScanHealth -NoRestart -ErrorAction Stop
```

Microsoft documents ScanHealth as scanning the component store for corruption and taking several minutes. It is distinct from CheckHealth's previously recorded flag and RestoreHealth's automatic repair. No RestoreHealth, SFC, offline image, repair source, cleanup, log/scratch override, download, installation or restart option is provided. Windows-managed logs, temporary servicing metadata and recorded health-state changes are disclosed. No log contents are read or exported. See the current official [Repair-WindowsImage reference](https://learn.microsoft.com/powershell/module/dism/repair-windowsimage) and [product contract](COMPONENT-STORE-SCAN.md).

## Shared owner and source boundary

The same provider, held qualified PowerShell host, captured command/type/enum qualification, nonce-bound parser, private stop channel, bounded pipe drains and actual-exit/cleanup owner serve both kinds. The scan authorization binds the exact intent nonce and ScanHealth kind to a fresh marked `component-store-scan-fixtures-<RequestId>` scope. A recorded-flag authorization cannot authorize the scan kind. No host-kill/watchdog runner is reused; a stop request is not native servicing cancellation or rollback. The outer disposable CI VM lifetime is independent.

Pre-cancellation must invalidate the unreserved intent without host start, proof transfer or pending ownership. One separately prepared and acknowledged candidate then invokes the actual fresh check. Current scan assessment requires the exact expected kind, actual qualified module/cmdlet/output contract, complete EndInvoke/pipeline, one known output, zero errors/warnings, no stop, zero-code host exit, completed drains and released ownership. Any missing, mismatched, failed, warning, stop or cleanup uncertainty remains Unknown while actual historical source/transport facts are preserved.

## Backend and public UI are separate milestones

The immutable provider initially qualifies only RecordedFlags for production. `uiRequired` is captured from `IsCheckKindValidated(kind)`, not supplied by environment/configuration or JSON. With ScanHealth still false, this mode performs one backend candidate only; it never invokes the public scan confirmation. A passed backend report does not qualify the public UI.

Only a later explicit code promotion, supported by source-matched backend evidence and independent review, can make the scan UI required. Then the same real WPF helper uses actual fresh-scan Prepare/acknowledgement/Confirm controls and a fresh assessed typed result, waiting for actual settlement and window closure. No injected result or direct candidate-provider call substitutes for UI proof. Ordinary passive smoke still never confirms an operation.

## Report and publication

Version 3 extends the existing report with `checkKind`, `sourceKind`, `scanFinding` and `uiRequired`; its optional UI object carries the same kind/finding fields. Transport and evidence projections retain their fixed primitive schemas. Kind and source kind come from actual result/evidence observations; unavailable fields stay null. The host protocol separately uses version 2 with its exact 23 fields. Legacy report versions 1 and 2 remain recorded-only and cannot qualify ScanHealth.

Recorded flags and fresh findings cannot substitute for each other. For a fresh scan, `recordedFlag` is Unknown; Healthy maps only to NoCorruptionReported, and Repairable/NonRepairable map to the corresponding fresh scan finding. This is not SFC/file-integrity coverage, antivirus coverage, a crash diagnosis or overall PC health. False/empty/unchecked/failed observations never become healthy.

Only the bounded fixed report is uploaded, without nonce, paths, identities, raw output, warning/error text or logs. The atomic publisher writes a separate optional `component-scan.json` and independent backend/public-UI qualification fields. Missing/rejected scan evidence does not suppress recorded-status, passive, MSI or core evidence. Failed/cancelled jobs cannot be overridden by a successful-looking report. Source-matched Windows execution remains required; Linux compilation and fixture tests do not qualify ScanHealth.
