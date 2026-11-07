# Current-user startup registration milestone

This increment is read only. `StartupRegistrationActions` inventories names in the current user's shared `Run` and `RunOnce` keys and privately reads only an explicitly selected value to validate its type and byte count. The preview never exposes or retains its raw command. Supported preview data is bounded, NUL-terminated UTF-16 `REG_SZ` or `REG_EXPAND_SZ`; environment variables are not expanded. Missing, inaccessible, malformed, oversized or registry-link sources are reported honestly. Enumeration is bounded and cancellable; partial source coverage is not a complete inventory.

Both `DisableAsync` and `RestoreAsync` are **unconditionally unavailable**, including fixture instances. They create no backups, files, keys or values. `ProductionMutationValidated` is false with no configuration, environment or UI override. Preview tokens identify metadata previews; they do not authorize execution. This does not complete reversible startup actions. Use the separately previewed Windows Settings handoff for Windows-controlled management.

A registration is only one startup source. Its presence does not prove enabled execution; its absence would not prove an application globally disabled, uninstalled or stopped. Policy and other processes can recreate entries. `RunOnce` normally removes its registration before executing; the documented `!` prefix changes deletion timing. Restoring such work could replay it. There is no invented `NoRemove` equivalent or ownership inference for these keys.

## Current selection and preview evidence

Covered registrations and existing recovery records retain separate request/response provenance and source coverage. Refreshing or failing one source clears its old rows/selection and any shared preview; the other inventory remains separate. A Partial inventory is never promoted to complete coverage by a successful selected metadata read.

Only an exact reference from the current appropriate collection can supply a selection or preview request. The App checks that selection again when its read returns. The portable `StartupPreviewEvidence.IsConsistent` helper validates an Available singleton response, exact source/value-name match, UTC attempt/observation/receipt ordering, supported registry type, bounded even byte count, opaque preview-token format and bounded descriptive metadata. Nonpositive, mismatched, malformed or interrupted results cannot retain a previous successful-looking preview.

These are consistency checks on already-returned data, not authentication of a fabricated caller object, registry object/namespace binding, transaction isolation, ownership or action permission. The token is not shown, logged or accepted as an execution capability. An old observation may remain historical metadata; this display check does not invent an execution-expiry guarantee or make it current registry state. No private command, backup identifier, payload, path or command fingerprint is added to the UI/activity log.

Both registration-removal and recovery previews describe hypothetical future effects only. Structural recovery metadata cannot establish who created it or that an action committed. No backup, registry write, restoration or discovered command execution follows selection or preview. Both production mutation methods remain unconditionally unavailable.

Portable boundary tests and Linux compilation cover the consistency contract only. Actual Windows inventory/read/selection/refresh/cancellation rendering still requires validation; no new remote workflow runs while GitHub pushes are paused.

## Why execution remains blocked

Microsoft documents TxR through `RegOpenKeyTransacted` and KTM. This is different from discouraged TxF. However, the API's options are reserved and do not expose `REG_OPTION_OPEN_LINK`. A trustworthy implementation still needs a proven way to bind the exact previously validated no-follow registry objects into a transaction, including ancestor identity and concurrent registry-link changes. `CompareObjectHandles` has not been established as suitable for this registry reopening contract. This version contains no transaction, registry-write, delete or commit primitives and makes no atomicity claim.

Before execution can be considered, independent native fixtures and review must establish exact-object isolation, fresh selected-value comparison, concurrent-writer conflicts and source/journal transactional consistency. Private CurrentUser-DPAPI backup creation must be restricted to current-user/SYSTEM ACLs, no-follow locked fixed-local storage, flushed and read back before registry commit. The encrypted committed/restored journal must participate in the same registry transaction as the source action; filesystem and registry are not a cross-resource atomic transaction. Cancellation before commit must roll back; uncertain commit results must remain unknown. Recovery must require validated committed evidence and fresh source absence, never overwrite any existing value, and never infer commitment from RunOnce absence. There is no automatic restoration.

## Existing evidence inspection

The read-only recovery inspector checks bounded existing private records using CurrentUser DPAPI, schema/Origin marker/user/scope fields, protected current-user/SYSTEM owner/DACL, fixed-local non-reparse ancestry and matching encrypted backup data. These are structural and privacy checks, not proof of MadClean authorship or that a real registry action committed: another process running as the same user can construct matching records. Public recovery summaries preserve that uncertainty, and both mutation APIs remain unavailable. It never creates such evidence. Missing journals mean no journaled recovery evidence, not proof an operation occurred. Unjournaled files are excluded, malformed or inaccessible records reduce coverage, and restored records cannot be previewed for replay. No command text, digest, SID, filesystem backup path or provider exception text is returned in public models. Same-user malicious processes are outside DPAPI's privacy boundary.

## Native fixture contract and limits

`StartupRegistrationActions.ForFixture(regBase, backupDir)` is internal and accepts only `Software\MadClean.WindowsSmoke\<GUID>` with the same GUID as `<repo>/artifacts/windows-validation-fixtures-<32hexGUID>/startup-backups`. The directory must already exist with the repository fixture's stable regular `.madclean-fixture-owner` marker in its parent. Only `Run`, `RunOnce` and a private `Journal` child are mapped under that owned subtree. This is a test guard, not a security boundary against current-user reflection. Production construction does no registry or filesystem queries or creation.

Native fixtures should verify real inventory and selected type/byte previews, malformed/oversized metadata refusal, bounded inaccessible/link coverage, cancellation and disposal safety, exact fixture topology rejection, and that both mutation APIs leave source, journal and files unchanged. Such tests do **not** demonstrate removal, restore, TxR conflict isolation, durable backup creation or crash recovery. Linux portable policy tests verify scope and malformed metadata boundaries; they cannot verify Windows integration. No startup changes are made while building or testing portable code.

`Dispose` requests cancellation without blocking the UI or closing handles owned by an in-flight worker. Active read calls finish or observe cancellation and release their own leases/buffers; disposal performs no registry writes. Callers may cancel and defer disposal until their task settles for simpler ownership.

## Official references

- [Run and RunOnce registry keys](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys)
- [RegOpenKeyExW and REG_OPTION_OPEN_LINK](https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regopenkeyexw)
- [Registry symbolic links](https://learn.microsoft.com/windows/win32/sysinfo/registry-key-security-and-access-rights)
- [RegOpenKeyTransactedW](https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regopenkeytransactedw)
- [Kernel Transaction Manager](https://learn.microsoft.com/windows/win32/ktm/kernel-transaction-manager-portal)
- [TxF alternatives and limitations](https://learn.microsoft.com/windows/win32/fileio/deprecation-of-txf)
- [CompareObjectHandles](https://learn.microsoft.com/windows/win32/api/handleapi/nf-handleapi-compareobjecthandles)
- [CryptProtectData](https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)
- [RegOpenCurrentUser profile behavior](https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regopencurrentuser)

Current official documentation research in [STARTUP-BINDING-RESEARCH.md](STARTUP-BINDING-RESEARCH.md) confirms that RegOpenKeyTransacted accepts an existing ordinary handle with a NULL/empty subkey. The missing OPEN_LINK option alone therefore does not establish that enlistment is impossible. Exact source/link/namespace race binding and durable recovery proof remain unresolved; no production mutation gate is weakened by that research.
