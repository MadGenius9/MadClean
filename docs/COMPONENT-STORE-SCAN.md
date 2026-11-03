# Explicit component-store scan

This D2 increment extends the existing qualified component-store owner with a second fixed operation. It reuses the same preview, one-shot intent, exact system host/cmdlet qualification, response correlation, cooperative stop, actual exit/drain and retained-ownership boundaries. It introduces no second executor and never runs through ordinary inspection.

## Two distinct scopes

| Selected kind | Fixed request | Accepted current result |
| --- | --- | --- |
| RecordedFlags | `Dism\Repair-WindowsImage -Online -CheckHealth -NoRestart -ErrorAction Stop` | Previously recorded flag only; no fresh scan. Existing `Flag` result is used and `ScanFinding` stays Unknown. |
| ScanHealth | `Dism\Repair-WindowsImage -Online -ScanHealth -NoRestart -ErrorAction Stop` | This component-store scan's finding only. `ScanFinding` is separate and `Flag` always stays Unknown. |

The scan can take several minutes and use system resources. Windows may record scan results/component-store health metadata and write default DISM logs, archives and temporary servicing metadata. It requests no repair, source, offline path, scratch/log override, reset-base, component cleanup or restart. It is not SFC system-file verification, an antivirus scan, a disk/hardware test or an overall health score. No per-file coverage or corruption cause is supplied by this projection.

Microsoft's actual Healthy, Repairable and NonRepairable members map to NoCorruptionReported, RepairableCorruptionReported and NonRepairableCorruptionReported **for ScanHealth only**. Healthy from CheckHealth cannot stand in for a fresh scan. An absent, mismatched, failed, stopped or unsettled response remains Unknown. Actual returned members and pipeline/exit facts remain historical when current acceptance is withheld.

## Intent and permissions

The user must separately select the scan preview, review its exact scope/effects and acknowledge before confirmation. Kind is immutable on preview and intent; changing operation requires a new preview. Confirmation and final one-shot reservation remain separate. Expiry limits launch review, not the duration of an already owned scan.

The current caller must already have effective administrator rights. MadClean never requests elevation, collects credentials, relaunches itself, changes policy or starts a repair. The provider has a separate literal qualification decision for each kind; a qualified recorded-flags backend does not qualify the new scan. There is no environment/configuration/client override.

Stop requests target only the owned PowerShell pipeline and wait for real outcomes. Windows may continue a native stage before returning. No immediate servicing cancellation, rollback or forced-termination safety is promised, and neither process nor service is killed. A caller stop before publication withholds both current assessment domains while retaining actual source history. Pending or uncertain cleanup continues to block overlapping work and app close.

## Kind-bound protocol and acceptance

Host envelope version 2 has the existing 22 primitive fields plus the exact canonical `kind` field. The parser checks both the active request nonce and expected kind; missing, duplicate, extra, numeric, aliased or mismatched kind data is rejected. Legacy version 1 is accepted only for RecordedFlags, because its original fixed command was CheckHealth. It cannot establish ScanHealth evidence. Legacy parser overloads still expect RecordedFlags.

Accepted current findings retain all prior requirements: reserved intent, effective administrator observation, completed signed fixed-host identity, qualified module/cmdlet/output type and actual CLR enum property, matching source kind, completed pipeline/EndInvoke with one output, zero errors and observed zero warnings, no stop, real zero-code host exit, completed bounded drains and confirmed source/native ownership release. Parsing, zero host exit or a shared Windows enum is never sufficient alone. Actual UTC request/completion observations bound this invocation; they are not an atomic machine snapshot or fabricated per-file scan times.

## Validation and current limits

Portable tests cover legacy/v2 kind separation, source nonce and kind mismatch, strict schema, invalid kinds, immutable confirmation/replay, separate result mapping, scan errors, stop/warning/cleanup uncertainty, and completion after review expiry. They run no Windows operation. The fixed ScanHealth backend passed at source `88038b6c`, immutable evidence `6b5970a1619d85e6a4ad679257e09a52ea1bd61d`, [run 41703092192](https://github.com/MadGenius9/MadClean/actions/runs/41703092192). It returned one actual Healthy member with zero errors/warnings, complete pipeline/EndInvoke, actual zero-code host exit, drained streams and released ownership. This establishes NoCorruptionReported for that component-store scan. Independent review cleared the unchanged common execution path for literal production activation in the next public WPF qualification candidate. The initial backend report had `uiRequired=false` and no UI result. The later real public fresh-scan cycle passed at source `56e97f0f`, immutable evidence `de97c52e1a9f8d30d76141109deecea171b3dae5`, [run 41709738590](https://github.com/MadGenius9/MadClean/actions/runs/41709738590): actual Prepare/acknowledgement/Confirm, fresh assessed result, settled ownership and window closure were observed, with the same complete host/pipeline/drain/cleanup facts. The result was NoCorruptionReported for that invocation only. Prior recorded-flags evidence alone does not qualify this action. Physical Windows policy, standard-user denial, long native waits and live stop behavior need their applicable runtime validation.

Current official [Repair-WindowsImage documentation](https://learn.microsoft.com/powershell/module/dism/repair-windowsimage), [retrieved Microsoft source](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/Dism/Repair-WindowsImage.md), was checked on 2026-11-03. It explicitly distinguishes CheckHealth (previous corruption flag), ScanHealth (component-store scan taking several minutes), RestoreHealth (scan plus automatic repair) and NoRestart (restart suppression). Retrieved SHA256 `6a50227c0e72c78af5fee33070e4f32bb78dcc9557c41274b5136a78d4fba027`; local research copy `/tmp/madclean-d2-repair-windowsimage.md`. Existing supported hosting/StopProcessing references remain in [the recorded-status contract](COMPONENT-STORE-STATUS.md).

The actual scan above ran only on the disposable Windows runner. No repair, requested elevation, dependency installation or personal-PC action was performed.
