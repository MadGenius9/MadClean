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


## Fresh official-source recheck

At2026-11-07T04:07:28Z the current official Windows PowerShell sources again returned HTTP200: Start-MpScan3,779bytes/SHA2567650f3eea372bf3964480c533f1987e098e4ff7b5ec4ebc00a519b013c7fcea4; Get-MpComputerStatus4,725bytes/SHA2565259368078fdf4bda92c996aef0a15c4fb0dbb303a676456d38ec6a5adc45d10; Get-MpThreatDetection8,262bytes/SHA25660c87f2e72fdf44dd6e334d7d0d9ba6cf3ab828f0ce822fe699af3bc08286077. These references still establish local custom-scan requests and active/past history, not a per-request no-remediation option, engine-owned cancellation/result identity or a full permission/terminal contract.

Current official MpCmdRun article candidates in defender-docs and legacy microsoft-365-docs returned404/14-byte error responses; the published Learn request failed proxy CONNECT403 with no document bytes. No remembered DisableRemediation/cancellation/executable/permission semantics were substituted. Access failure does not prove API unavailability. No scan, Defender setting, malware fixture, native operation or CI dispatch was performed by the research.

## Coordinated official-source review — 2026-11-11

The coordinating task successfully reviewed the current public Microsoft documentation at the URLs below and supplied these findings. This advances the earlier documentation-access gap; this checkout's direct Learn proxy remains blocked, and no new document bytes/hash or Windows execution are claimed here. No GitHub request was made.

- [MpCmdRun command-line reference](https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus) documents elevation, current platform-folder discovery and explicit ScanType 1/2/3. Exit 0 can describe no malware **or successful remediation**; exit 2 can describe unremediated malware, required user action **or scan errors**. Neither alone justifies a clean verdict or attributed detection list.
- The same reference documents `DisableRemediation` for **custom scans only**. It ignores file exclusions, suppresses detection event-log/UI records and puts detection information in command output. A future custom-scan preview must disclose these material differences. It cannot inherit normal exclusion/history assumptions, promise no provider/cloud effects or persist sensitive command output in the activity log.
- `Cancel` only attempts to cancel active quick/full scans. That does not establish cancellation of a specific MadClean-owned request and is not a supported substitute for custom-scan lifecycle ownership. Killing the client/pipeline is not proof of engine cancellation or rollback.
- [Troubleshooting/event reference](https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-microsoft-defender-antivirus) identifies Operational events 1000 started, 1001 finished and 1002 stopped before completion, each with a Scan ID. Detection/remediation events are separate. This supports future lifecycle correlation research, not attribution by nearest timestamp, complete event availability or proof that a finished scan found nothing. Exact event payload schema/marshalling and owned request-to-ID linkage still need their own implementation contract.
- [On-demand scan documentation](https://learn.microsoft.com/en-us/defender-endpoint/run-scan-microsoft-defender-antivirus), updated September 16, 2026, confirms supported MpCmdRun, Start-MpScan and MSFT_MpScan.Start entry points. Supporting an entry point does not supply complete permissions, provenance, request identity, cancellation or result semantics for an implementation.

Scan execution remains unimplemented/gated. The next contract can now specify an explicit custom-only request profile and the documented command/result limits, but must still establish executable provenance, exact selected scope/protection, output privacy, real client/engine lifecycle and Windows qualification. No scan, setting change, malware fixture, personal-PC action or release permission is created by this documentation review.

## First implementation checkpoint

Security now offers an in-memory custom scan scope review. The folder picker accepts one ordinary drive-qualified folder below the root. The displayed folder is a path selection. Drive type, existence and access at submission time, and redirect/reparse checks require an actual Windows admission step. The review explains possible effects under the user's existing Defender policy and can be cleared at any time outside a busy operation.

The portable request controller retains one admitted client in progress. It requires the exact current draft reference at confirmation, claims each admitted draft once, and refuses another submission while the client is pending. After admission it awaits the client through caller cancellation. An ambiguous client fault becomes Unknown. A returned command result covers submission only. The controller has controlled-client tests and the App has no scan client or dispatch control.

The next code dependency is an inbox Start-MpScan adapter with actual directory revalidation and verified module/command provenance. It must retain the host until settlement and prove any Rejected result was refused before submission. Windows validation of that adapter requires a separately scoped benign owned folder. Event correlation, a clean verdict and engine cancellation remain outside this request-only milestone. The documented Start-MpScan limitations above still apply.

Local validation used locked .NET 10.0.403 dependencies. Release compiled with zero warnings and errors. Core passed 2,762 cases with one Windows-only skip, including ten new scope, admission and lifetime cases. Windows UI and native command behavior remain unverified.

The Security page also exposes an explicit **Check folder scope** action. It reuses the fixed-drive, no-follow directory handle walk from system-tool identity verification, including its attribute and final-path checks. The worker holds one chain until the observation settles and closes it in reverse order. It opens metadata handles to the chosen directory and each ancestor. It enumerates no children, reads no file bodies and invokes no Defender command. Cancellation is observed around the synchronous handle walk, so normal close and a new check wait for the worker.

A successful observation stays Partial. It covers the path and observed attributes at the time of the check and grants no authority to dispatch. The opened chain does not establish Defender's later path resolution or the contents of any descendant directory. Submission still requires a separate command provenance and directory binding contract.

Local Release build passed without warnings or errors and Core passed 2,762 cases with one Windows-only skip. The selected-folder check itself requires Windows validation on controlled local paths.

## Candidate inbox command profile

The fixed metadata probe uses the existing verified Windows PowerShell executable lease. It also holds the checked inbox Defender directory chain and manifest handle until the child settles. A fresh noninteractive host imports that exact manifest with automatic module loading disabled. It checks for one exported Start-MpScan function bound to the imported module and one parameter set with the documented string ScanPath and enum CustomScan option without other mandatory parameters. The probe passes no path, scan parameter value or provider text across its output protocol. It invokes no scan command.

The documented ScanPath/ScanType declarations and the PowerShell 5.1 Get-Command -All/-ListImported parameter set were rechecked against Microsoft's published sources. The actual generated function's module reference and parameter metadata remain Windows observations to collect. The manifest lease does not verify the provenance of every imported dependency. A positive marker therefore describes the observed profile only and cannot authorize dispatch. The App and CI have no call site for this candidate. The next qualification should record whether actual inbox module import, function binding and parameter inspection survive the restricted host profile, on a disposable Windows runner with no scan invocation.

Locked restore and Release build passed with zero warnings or errors. Core passed 2,770 tests with one Windows-only skip, including eight new marker validation cases. An independent review completed. The candidate has not run on Windows.

## Hosted command profile observation

The ordinary disposable GitHub Windows harness now observes the fixed command profile after its existing Defender status read. It awaits the actual probe worker and the worker's host and handle cleanup. The harness deadline can record an incomplete result while the worker is held. The check runs only in the hosted Windows validation job and supplies no target folder or scan invocation.

Version 8 of the validation report requires a separate `defender-custom-command-profile` entry for an overall pass. A positive result has the fixed marker `profile=ProfileObserved`. Known rejection stages, missing or unrecognized evidence, and cancellation are inconclusive. The publisher accepts only the fixed markers and retains the earlier report versions. This check exports no paths, command definition, provider output or detection data. It cannot establish permissions, descendant folder behavior, scan ownership or submission readiness.

Local validation of the hosted integration passed. The Release build had zero warnings or errors. Core passed 2,770 tests with one Windows-only skip. The Python evidence suite passed 413 tests with two Windows-only skips. The independent review found no blocking issue. A hosted run is still needed to determine the inbox module's actual result.

## First hosted result and bounded lookup follow-up

Run [45063017053](https://github.com/MadGenius9/MadClean/actions/runs/45063017053), source `838d2eb541342132e047818003e549686d30fa12`, verification commit `aec344b`, compiled and passed the publisher and Core tests. The report recorded 90 passed, 11 inconclusive and zero failed checks. The new Defender profile was `CommandNotAccepted`. The probe had accepted the module import by its fixed manifest path. The record cannot distinguish a lookup error, an unexpected return shape, or a failure to bind the command to the imported module. The Defender status read passed separately.

The follow-up uses the documented Windows PowerShell 5.1 Get-Command name and module filters with All and ListImported. It still requires one actual FunctionInfo and the same imported-module reference and export membership. A name declared in the module export table alone is insufficient. Additional fixed inconclusive markers distinguish lookup, function shape, and module binding. They carry no command definition, file path, exception text, or provider output. One changed hosted run can now test the narrower lookup hypothesis. Success remains metadata evidence, and a rejection keeps submission gated. [Get-Command reference](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/get-command?view=powershell-5.1)

The lookup follow-up passed locked restore, Release build with zero warnings and errors, 2,773 Core tests with one Windows-only skip, and 413 Python tests with two Windows-only skips. Independent review found no blocking issue. Hosted behavior for this revised lookup is pending.

## Hosted profile and disabled request-host source

Run [45641969596](https://github.com/MadGenius9/MadClean/actions/runs/45641969596) at `dac59dc0ba3b9188c52ee301a6057026106c5a1f` published `profile=ProfileObserved` in verification commit `b715449`. The Windows build and both test stages passed. The overall report recorded 90 passed, 11 inconclusive and zero failed checks. This proves the fixed inbox metadata profile on that runner. The previous generic command refusal does not identify which part of the older lookup caused it.

The metadata selection now shares its fixed script with a separate request-host source candidate. The existing hosted probe still only writes its profile marker. The candidate accepts one bounded, canonical Base64 UTF-16 folder value through standard input. It passes that value separately to the already selected command with literal CustomScan and a terminating error policy. Command output streams are suppressed. Fixed markers describe either a refusal before invocation or command return. Any unclassified result, post-invocation exception, nonzero exit or mixed output remains Unknown. Command return establishes neither engine completion nor a clean result.

Development checked PowerShell's public CommandInfo call-operator implementation. That source does not establish actual Windows PowerShell 5.1 request behavior. The candidate has no launch call site. A future parent must supply the exact confirmed draft, revalidate and hold its directory, establish trusted host/module binding, and own pipe and process lifetimes through actual settlement. It must not reuse the read-only process runner's kill-on-cancel behavior. Actual submission qualification still requires a separately scoped owned Windows fixture. The public scan button remains unavailable.

Locked restore and Release build passed with zero warnings or errors. Core passed 2,780 tests with one Windows-only skip, including seven new path-transport and client-result cases. Independent source review found no blocking issue. The updated probe still needs a hosted regression run. The request script has not been parsed or invoked by Windows PowerShell in this checkpoint.
