# Windows integrity/repair descriptors and disabled execution boundary

This stage implements **read-only system-tool identity previews and a deliberately disabled production launch boundary**. It is not a validated repair feature. `SystemToolActionsProvider.ProductionExecutionValidated` is a get-only `false`; there is no setting, environment variable, consent checkbox, request field or test fixture that enables production execution. `LaunchAsync` returns before identity reads, log creation, UAC or process creation while that gate is closed.

No DISM, SFC, Defender scan, repair, elevation or restart was executed while developing this stage. Ordinary inspection, event evidence, cleanup/duplicate/shortcut discovery and Windows CI do not invoke these tools. A Windows smoke check may separately inspect their identity without launching them. Defender execution is not included; scan ownership and safe cancellation need a separate reviewed contract.

## Five fixed operations

The current official Microsoft [DISM operating-system package servicing reference](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options?view=windows-11), [DISM global options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-global-options-for-command-line-syntax?view=windows-11), and [SFC command reference](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc) were read before implementation.

| Operation | Exact future tool arguments | Meaning and important limit |
| --- | --- | --- |
| DISM CheckHealth | `/Online /Cleanup-Image /CheckHealth /NoRestart` | Reads whether corruption was previously flagged and repairable. It is not a fresh comprehensive component scan or PC-health verdict. |
| DISM ScanHealth | `/Online /Cleanup-Image /ScanHealth /NoRestart` | Scans the component store without requesting repair. Corruption flags and servicing logs may be written, so it is not zero-write passive inventory. |
| DISM RestoreHealth | `/Online /Cleanup-Image /RestoreHealth /NoRestart` | Requests component-store scan/repair; Windows Update or configured sources may be used under existing policy. It changes Windows components. |
| SFC verify-only | `/verifyonly` | Verifies protected Windows system files without requesting repair; diagnostic/CBS logs can be written. |
| SFC scan-and-repair | `/scannow` | Verifies protected Windows files and attempts repair. Despite “scan” in the name, this is a system-changing repair action. |

Only `Dism.exe` and `sfc.exe` in the OS-reported local system directory are considered. There is no PATH search, shell command text, offline-image target, custom repair-source path, `/Source`, `/LimitAccess`, component cleanup, `/ResetBase`, agreement acceptance, installer or reboot operation. DISM uses its documented `/NoRestart`; SFC receives no invented restart switch. No `/Quiet` or automatic elevated command is supplied. Any future reboot requirement remains evidence to review, not permission to restart.

## Identity preview

`PrepareAsync` is explicit and never runs the tool. It opens fixed-local, root-to-leaf no-follow directory leases and an executable read lease that denies modification/deletion, rejects reparse/offline/recall/encrypted/incorrect object attributes, and checks the bounded final handle path. It obtains the path from Windows, not an environment override or request string. The file is bounded to 32 MiB, fingerprinted with SHA-256, and its reported version is bounded/control-free. The signed OriginalFilename must match the selected fixed tool.

Identity uses documented [WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust) / [WINTRUST_DATA](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data) with Generic Verify V2, no UI, whole-chain revocation requested, and MD2/MD4 disabled. It does not turn off revocation, accept hash-only validation or force success from a stale local cache. The optional open file handle in [WINTRUST_FILE_INFO](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_file_info) ties validation to the leased object. The accepted signer is read from the trust provider's certificate chain, with exact Microsoft organization/common-name checks; version CompanyName text is not publisher authentication. Certificate loading uses the supported .NET certificate loader. All trust-provider state and owned handles are released.

Signature/revocation checking may contact Windows certificate infrastructure. This network possibility is included in each descriptor before a preview is requested. Offline/inaccessible revocation, unknown signer or catalog-only signatures that this probe cannot establish remain Unknown/Unavailable; no validation fallback grants permission. A fixed system path or a plausible Microsoft filename is not sufficient trust.

Only one signature worker can remain pending globally. A **45-second wait** bounds the caller, not an in-flight native WinVerifyTrust/network call. Cancellation or timeout does not claim that native work stopped: the worker retains its singleton slot and leases until it returns; another preview cannot spawn unlimited replacement workers. The preview contains an actual check time, two-minute expiry, version/hash, trust observation and the execution blocker. At most eight metadata previews are retained in memory. Checking a signature does not establish servicing runtime correctness or enable execution.

## Dormant launch and monitoring path

The compiled future path is unreachable while the production gate is false. Before any later enabling code change, the separate Windows runtime/review gates below must pass.

The request binds to a cached preview GUID, operation and executable hash, with explicit exact-operation selection and effects/elevation acknowledgement. Disabled/unknown-version/expired/untrusted/changed descriptors are refused. No request carries an executable path or custom arguments. The genuine executable is reopened/reverified under fresh leases immediately before launch, and path/version/hash must match the checked preview. The provider reconstructs arguments from the immutable fixed catalogue.

A future launch uses the genuine executable directly with `UseShellExecute=true`, `Verb=runas`, visible normal window and the fixed system working directory, following the supported [ShellExecuteEx](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecuteexa) / [runas verb](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfoa) consent boundary. There is no arbitrary privileged broker, hidden console, downloaded helper, PowerShell wrapper or administrator-credential storage. A Windows cancellation of the UAC request is recorded as consent denied; a returned process handle/PID means only that a client was observed.

**The dormant launch path is not claimed bounded/cancellable.** Fresh native trust verification and visible UAC/process creation can block before a monitorable client exists; an observation token cannot interrupt those native calls. This is a required enablement/runtime gate, not something hidden by a generic timeout or fake Cancelled result.

Once a client exists, stopping observation records **MonitoringLost**, not successful cancellation/rollback. No process tree, servicing client, TrustedInstaller, CBS or Windows Update service is killed. The elevated operation may continue after the UI closes or monitoring is lost. An uncertain monitored operation blocks overlapping launches in that app process; restarting the app does not establish that Windows servicing stopped. Persistent outstanding-operation reconciliation and cross-instance/user coordination must be validated before enabling execution.

Observed client exit codes are recorded literally. **Exit zero, process completion and operation success are not an integrity/image-health/repair verdict.** SFC has no complete stable documented exit-code-to-integrity mapping here; DISM operation outcome is separate from image assessment. The app does not scrape localized output into a canned “healthy” result. Actual elevated console output is not redirected/captured through an unelevated arbitrary-output broker; the user must inspect the genuine tool output and protected Windows logs. No result parser or healing guarantee is invented.

## Private execution records, if execution is later enabled

The dormant path creates a current-user private metadata file under `%LocalAppData%\MadClean\system-tool-actions\<ExecutionId>.jsonl` before requesting a launch. It never overwrites an existing file or accepts an alternate destination. Local ancestry is validated/leased; unavailable logging blocks launch. Each file is bounded to 128 KiB and 128 retained files; reaching retention capacity blocks further launch until records are manually reviewed/archived. There is no background deletion or automatic retention purge.

Records contain the selected enum/fixed arguments, checked tool path/version/hash, actual request/start/end times, requested elevation, observed PID/exit code, monitoring state and protected Windows log reference. They contain **no captured tool output, event payloads, command input, detection paths, credentials or invented findings**. Fixed system tool paths are metadata for an explicitly selected operation, separate from the application's enum-only activity log. Windows DISM (`%windir%\Logs\DISM\dism.log`) and SFC/CBS (`%windir%\Logs\CBS\CBS.log`) remain protected system-owned evidence, not fake application-generated logs. Their availability/content/privacy must be handled separately if detailed export is later added. Closing/crashing before a final record leaves completion unknown.

## Enablement gates still open

Portable tests validate descriptor/request boundaries, not native trust/UAC or repairs. The default execution blocker must remain until disposable Windows validation and independent review establish:

- genuine embedded/catalog signature behavior, signer extraction, revocation/offline behavior, read-lease and path-race protection on supported OS/architectures;
- actual per-operation command semantics, required elevation, denied consent, source policy, localization and actual protected log/result interpretation;
- safe native launch waiting, monitoring loss/close/crash, persistent outstanding-operation reconciliation and no overlapping servicing;
- private record creation/ACL/retention/error behavior, exact preview revalidation, and no calls from ordinary inventory/CI;
- actual selected repairs and separately selected follow-up checks on disposable VMs only, with no forced restarts or personal-PC repair tests.

This stage is a reviewable gated boundary, **not completion of working D2/D3 repairs**. A BIOS update, registry cleaner, DLL download or repair-all workflow is not authorized or implemented by it.
