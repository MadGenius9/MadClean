# Research: explicit Microsoft Defender scan requests

Research only, reviewed 2026-11-03. No scan, remediation, quarantine, signature update, provider change or personal-PC operation ran. No provider, UI control or new production gate is implemented by this document. Existing status/history remains read only.

## What current official documentation establishes

The current official [Start-MpScan reference](https://learn.microsoft.com/powershell/module/defender/start-mpscan) ([retrieved source](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/Defender/Start-MpScan.md)) documents QuickScan, FullScan and CustomScan, with an optional ScanPath. Omitting CimSession uses the local session. AsJob returns a **PowerShell job object**; the reference does not establish that its identity is a Defender engine scan identity, or provide per-engine-scan cancellation or attributed findings.

The help file is `MSFT_MpScan.cdxml-help.xml`. A future integration must qualify the actual inbox command and generated module/proxy provenance; it must not assume it is the same compiled CmdletInfo shape as the DISM command. The supported PowerShell hosting AddCommand(CommandInfo) API can pass a captured command object, but does not authenticate an arbitrary function or module by itself.

[Get-MpComputerStatus](https://learn.microsoft.com/powershell/module/defender/get-mpcomputerstatus) ([retrieved source](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/Defender/Get-MpComputerStatus.md)) reports protection configuration and scan-history timestamps. Those timestamps may concern different scans and do not prove ownership or completion of a particular new request. Missing/sentinel values remain Unknown, not never-scanned or healthy.

[Get-MpThreatDetection](https://learn.microsoft.com/powershell/module/defender/get-mpthreatdetection) ([retrieved source](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/Defender/Get-MpThreatDetection.md)) explicitly covers **active and past** threats. A before/after history difference is not automatically a detection attributed to the selected scan. Do not substitute global history or a PowerShell job ID for an engine-owned scan result.

All three official reference sources were successfully retrieved and read for this research. The Start-MpScan reference does not state a per-request no-remediation switch or a sufficient permission/terminal/cancellation contract. That does not prove such functionality is unavailable through every Microsoft interface; it means those claims are not established by this cmdlet reference.

## Smallest implementable next contract

A bounded next product feature could be **Request a local custom scan for one explicitly selected folder**, followed later by a separately qualified QuickScan request. CustomScan allows disposable development validation to use a fresh owned benign folder rather than scanning an entire build host.

The request would:

1. Show the exact selected local scope, fixed scan type, source and limitations before explicit confirmation. No remote CimSession, wildcard, arbitrary script, signature update, preference change, automatic selection or scheduled scan is part of the request.
2. Revalidate the selected scope and actual inbox Defender command under the same caller. Use parameter values through the supported hosting API, not interpolated command text. Normal provider permissions apply; do not invent an administrator requirement from an undocumented assumption or silently elevate on denial.
3. Disclose that Defender remains governed by its existing Windows policy, including possible remediation, quarantine and cloud-protection behavior. The cited Start-MpScan interface has no per-request no-remediation parameter. If a scan-only/no-remediation contract is required, this proposal must wait for an independently verified supported interface rather than promise that behavior.
4. Report only RequestReturnedWithoutCommandError, Rejected or Unknown as established by actual command observations. A returned command with no error is not presented as an engine-owned ScanCompleted, clean verdict or attributed detection list.
5. Keep protection status and detection history visibly independent, with their actual sources and times. Do not manufacture progress, elapsed work, scan coverage or a new antivirus engine claim.

This is a request-submission milestone, not complete engine-owned scan lifecycle support. It must not be described as completing all later scan, quarantine or cancellation capabilities.

## Stop and lifecycle limits

No current source here establishes how to cancel only the requested Defender engine scan. Do not wire a shared/global scan-cancel command as if it were an owned cancellation token. Stop-Job, killing the PowerShell client and stopping a hosting pipeline do not prove that the Defender service cancelled the engine operation or rolled anything back.

An initial request-only implementation should retain actual request-client ownership without killing it. Any stop-waiting or detached-observation behavior must explicitly say Defender may continue and preserve Unknown engine state. Windows Security may provide independent manual controls; opening it would not prove that this request was cancelled. App close, process exit, timeout and restart must not reconstruct scan completion or reopen a PID as identity.

The current official SDK metadata tree's WindowsDefender.h exposes WDStatus/WDEnable, not a qualified engine-owned scan-start/cancel contract. That limited header inspection is not proof that every other Defender API is unsupported. No MpClient signature or DLL deployment contract was assumed.

The known public Microsoft 365 documentation repository now redirects/moves Defender content and does not contain the current MpCmdRun article. The separate public Defender documentation repository was unavailable to this session. Therefore this research does not claim current MpCmdRun Cancel/DisableRemediation semantics from memory or third-party summaries. Documentation access failure is not an API availability result.

## Validation needed before implementation can ship

Require independent review of command/module provenance, exact selection and policy effects, rejected permissions, cancellation/observation wording, output bounds and secret-free activity records. Any actual candidate validation must be a separate explicit disposable-Windows stage, limited to its fresh benign owned custom-scan target. It must not enable Defender, change preferences, submit test malware, install dependencies, run ordinary scans during inventory or touch a personal PC.

Provider unavailability, passive mode, missing coverage, unrecognized output and unavailable engine attribution remain genuine outcomes. Do not label them up to date, clean or scan complete merely to produce a passing report.
