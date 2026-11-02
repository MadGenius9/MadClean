# Inbox DISM PowerShell metadata probe

The development harness separately inspects the fixed inbox DISM module's advertised metadata. It never invokes Repair-WindowsImage, DISM/SFC, UAC, a corruption check or repair. No output object is created and no output-property getter is invoked. Ordinary app inspection is unchanged.

Fresh official [Repair-WindowsImage documentation](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/Dism/Repair-WindowsImage.md) says CheckHealth checks whether an image was previously flagged as corrupted and whether it can be repaired; it is distinct from ScanHealth and RestoreHealth. It documents ImageObjectWithState output, NoRestart, default Windows DISM logs and archival writes. This is a possible future explicit corruption-flag check, not a zero-write inventory or a fresh complete integrity scan. The retrieved documentation does not fully establish output-member schema or elevation behavior. No actual cmdlet operation is authorized by this metadata probe.

The fixed System32 Windows PowerShell host runs without profiles/interactivity. The script disables autoload, restricts its own PSModulePath and imports only the exact inbox Dism.psd1. It qualifies ordinary local host/manifest ancestry, actual imported module path/instance, advertised public output type and derived assembly locations. It inspects only bounded public property/enum and parameter-set metadata. It does not guess DLL locations, load an alternative assembly, download ADK files or use PowerShell7 compatibility proxies.

The candidate contract checks an advertised readable enum property and a shared Online/CheckHealth/NoRestart switch parameter set without additional mandatory parameters. These are hypotheses until observed; unfamiliar or unavailable metadata remains Unknown. Path qualification is separate from authenticated publisher trust: PublisherTrustVerified and HealthCheckInvoked must remain false. Advertised metadata alone is not a stable documented result contract, sufficient privileges, component health or execution readiness.

The projection contains only fixed flags, nullable bounded counts and fixed statuses. Unobserved counts are null rather than invented zero. Duplicate/extra/missing fields, malformed JSON and unsupported projections cannot pass; oversized complete evidence remains Unknown rather than truncating a positive result. No paths, arbitrary type/member names, host identities or provider errors are exported. The bounded metadata-process runner may terminate its own query process on timeout; it must never be reused to cancel future servicing operations.

Independent source review found no material blocker after nullable-counter/export-bound corrections. Local compilation passed. Actual source-bound Windows metadata evidence is pending. No production corruption-check or repair control is enabled.


## Initial Windows result and typed follow-up

Source38ec883/run41369687422, immutable evidence aa401f3a4922bea1ed1af4ae684f55e833918c96, reported metadataStateUnavailable: host and manifest paths qualified, while imported-module binding and all later capabilities were unestablished. All counters were unobserved, publisher trust false and health-check invocation false. The initial result exposed no actual import category/exception data; it does not establish a missing DLL, execution policy, privilege or specific dependency cause.

The follow-up preserves all import/binding/autoload/acceptance rules and adds only fixed typed stages plus the first actual ErrorRecord category, outer/meaningful exception classifications and their observed HResults across at most four exception objects. The real import error is captured before any synthetic guard. Guard failures have no guessed native code; unfamiliar classifications remain Unknown. Raw identifiers, types, messages and paths are withheld, and complete evidence stays within the fixed export bound. New Windows evidence must identify the blocker before a behavior change is justified.


## Transport size correction

Sourceb975b7c returned only the generic transport/parse failure path; no typed module cause or native code was observed. Independently, its fixed script measured12,645 characters and33,720 UTF-16 Base64 characters before executable/arguments, exceeding the documented [CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw) Unicode command-line limit of32,767 including NUL. This static violation does not manufacture an observed native206 or explain the earlier source38ec883 import failure.

The correction changes formatting of the fixed constant only: deterministic LF, outer line whitespace/blank removal and ordinary full-line comments, preserving #requires and rejecting unexpected continuation/here-string/multiline-quote forms. PowerShell literals/statements, Schema2, module import, autoload and acceptance rules are unchanged. The fixed encoded payload is32,200 characters; a conservative normally quoted complete command is32,322 including NUL. A complete-command bound precedes launch; scripts are never truncated. Typed C# transport stages and actual optional exception/HRESULT/Win32/exit/output-length facts now distinguish launch, parse and projection failure without exposing raw output. Independent review found no blocker; corrected Windows runtime remains pending.


## Actual corrected transport and next diagnostics

Source44b7401/run41375513533, evidence155147941bede7a0b3f461ab260d63c84ac7acff, completed the corrected transport: actual process exit0 and776 output characters, with no transport exception/Win32 error observed. Module metadata remained Unavailable at ModuleImport. Actual category13 and outer/meaningful PowerShellStop HRESULT0x80131501 were observed; ModuleError was Unknown, all capability counts unobserved and trust/health-invocation false. These are exception facts, not a native error or proven root cause.

The next diagnostic-only Schema3 compares nullable qualified/unqualified import-command metadata without invoking an alternative. It follows actual InnerException/ActionPreferenceStop ErrorRecord links through at most four reference-unique objects, preserves outer facts and adds only fixed verified command-discovery classifiers. Private variable/helper identifiers and exact guard-string storage are shortened to preserve the full command bound; all17 guard texts, original qualified import, autoload and acceptance conditions remain intact. Normal fixed command length is32,674 including NUL; longer paths/source still fail closed. No fallback, additional import, execution-policy change or health check is introduced. New actual evidence must establish the cause before a behavior change.


Sourcec575b6e/run41377870124/evidence6b5029dcc939a8438cd4ce8168c68efc018f5530 completed transport(exit0,848 output characters) and observed both qualified/unqualified import-command metadata lookups true. Actual import remained Unavailable/category13/PowerShellStop HRESULT80131501, now with fixed CommandNotFound classification. These observations do not establish that the qualified import command itself is absent, or identify the missing command/dependency. Trust/health invocation stayed false and all later capability counts unobserved. No route or acceptance rule was relaxed.


## Independent captured-built-in import control

A separate always-run fresh-process check tests a documented [PS5 command-object invocation](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_command_precedence?view=powershell-5.1) route. It does not retry or promote the original qualified-command result. The fixed Desktop5.1 host captures an actual Import-Module CmdletInfo before any DISM import, requiring exact Core snap-in metadata and implementation in the same already-loaded PSObject assembly. Autoload remains disabled; the call operator receives only this qualified object and the same fixed validated inbox manifest. No alternate path, module, output getter, health/repair command or execution-policy change occurs.

The projection distinguishes import attempted, returned module and actual binding. It preserves nullable numeric exception observations and labels an allowlisted actual typed missing command separately from invocation context; context alone is not a proven missing-command cause. Raw messages, names and paths are withheld. Short fixed encoded input has the same complete-command bound and independent report/export validation. Static review found no material blocker. Runtime remains pending; success could establish only this import route, not publisher trust, health-result schema, elevation or servicing readiness.


## Captured-control result and target-context follow-up

Source4316ddd/run41379784575, evidence4d3fc3b2a117d589e31e2581719fa99e4c6498f6, completed transport (exit0,479 output characters). Host/manifest/built-in Core/assembly qualification and attempted import were true; import did not return, module binding was unobserved. Actual category13 and outer/meaningful PowerShellStop HRESULT0x80131501 persisted. Typed missing command and invocation context were unobserved. The independent route remains Unknown and does not alter the original unavailable capability.

Diagnostic-only Schema2 now compares bounded actual string TargetObject or CategoryInfo.TargetName against a fixed helper-command allowlist, across the same bounded outer/nested error traversal. Opaque targets are never enumerated or stringified. Target context/source is distinct from CommandNotFoundException.CommandName and cannot prove which command is missing. No raw error text, new imports, autoload change, fallback or acceptance relaxation is introduced. Independent review found no blocker; new Windows context observations remain pending.


## Independent fresh-error sample

Diagnostic-only Schema3 adds five independent fields for a bounded sample of the fresh child error collection. Official [PowerShell5.1 automatic variables](https://raw.githubusercontent.com/MicrosoftDocs/PowerShell-Docs/main/reference/5.1/Microsoft.PowerShell.Core/About/about_Automatic_Variables.md) define $Error as recent error objects, newest first; [common parameters](https://raw.githubusercontent.com/MicrosoftDocs/PowerShell-Docs/main/reference/5.1/Microsoft.PowerShell.Core/About/about_CommonParameters.md) distinguish Stop, SilentlyContinue and Ignore behavior. A catch record/inner-exception walk need not reveal every separately retained error record.

Only this independent metadata child clears its own ephemeral $Error collection immediately before its existing single captured-built-in import. No Windows Event Log or caller history is cleared. The catch saves its original record first, then snapshots at most4 fresh references before helper traversal. Each search uses at most4 cycle-checked exception nodes, classifies only an actual CommandNotFoundException.CommandName through the existing fixed allowlist, and exports no raw messages, paths or identifiers. Null sample fields remain unobserved when no catch/sample occurred. Sampling is not a guarantee that a record identifies the failure's cause.

Original category/exception/context/import facts stay separate; the original capability route and acceptance rules are unchanged. No helper preload, extra import/query, autoload change, health operation or capability promotion is introduced. Strict projection/range/index/provenance and full command/export bounds remain enforced. Independent review found no blocker and the full local solution build passed zero warnings/errors; fresh Windows observations remain pending.
