# Relative regular-file candidate: ownership and disabled framing

This increment implements the portable ownership protocol, guarded relative regular-file native binding, and a separate fixed inert journal roundtrip candidate. **It is not enabled, Windows-qualified or production journal admission.** The coordinator supplied exact current official contracts and verified SDK ABI/constants; all placeholders were replaced. No App/harness command or user writing entry point is wired, and no native operation ran.

## Narrow request and ownership contract

The internal profile accepts only `lease.bin` or an exact lowercase nonzero GUID in N format plus `.jsonl`, with CreateNew or OpenExisting. CreateNew cannot be read-only. There are no arbitrary paths, ADS, dot aliases, fallback dispositions, overwrite/truncate, permission repair or deletion operations. Invalid/busy/replayed frames are refused without claiming ownership of the caller's preparation.

A process-rooted cooperating domain roots the actual frame before claiming or invoking it. One frame cannot be opened again through another domain. The synchronous protocol must run on a separately owned future worker, not a UI thread; it does not itself schedule a Windows worker. Callbacks run outside state locks. Stopping before invocation permits one checked prefix release; caller stop after entry cannot manufacture rollback or abandonment.

Only exact immediate return zero reaches success capture. Every nonzero return (including pending or another success-category value) and thrown invocation retains the entire uncertain frame without output capture, handle adoption, cleanup or retry. This deliberately avoids inventing the incomplete failure/pending output-ownership guarantee. `EntryAttempted` records invocation of the collaborator, not proof that a throwing implementation entered NtCreateFile. `HasPendingWork` describes a currently active synchronous managed callback; false never proves native completion when RequiresRetention is true.

On known zero, the trusted adapter can capture and qualify its known success output. Capture failure may release only resources the frame actually knows it owns. Cleanup is memoized once; false/throw stays retained and blocks a successor. Releasing during an active callback refuses without consuming the later legitimate release. Successful cleanup concerns child/input ownership and the parent borrow, not deletion, rollback or closing the caller's original parent/tree.

## Disabled Windows candidate implemented

The new factory and store helpers reject through literal `NativeCandidateExecutionValidated=false` before native/profile/parent access or worker admission. The separate `SystemToolJournalPrivateStore.ProductionCreationValidated` stays false. There is no environment/configuration/acknowledgement bypass. Static bounded roots hold actual mutable native frame slots before preparation.

The frame accepts an already-held caller-owned filesystem parent and derives the fixed marked-fixture path `relative-regular-file-parent-v1`. It retains a strong parent reference and DangerousAddRef borrow, qualifies actual parent kind/path/identity/persistent-ACL/strict security and the fixed marker before any child invocation. It creates no directory and repairs no ACL. The caller must keep its original parent/tree owner alive and undisposed through this independent borrow; no hostile-user or first-parent security claim follows.

Explicit zeroed allocations hold the marker/name UTF-16, UNICODE_STRING, OBJECT_ATTRIBUTES, IO_STATUS_BLOCK and output slot. Pointer slots are published before subsequent initialization. Native marker/child wrappers are non-owning; raw ownership remains in frame fields. On known successful child output, the raw slot is adopted before wrapper/store allocations, then checked for expected information, actual disk/non-directory/no-reparse/single-link/full identity, same parent volume and strict security before attachment. No IOSB status is substituted for a non-pending function status.

The attachment helper gives the existing SystemToolJournalStore the qualified handle without reopening a path or copying its journal codec. Its actual FileStream is published before store allocation; unreturned marker/child stream construction remains tainted and prevents cleanup. The store stays private and supplies no raw caller writing API, avoiding concurrent raw use beneath release. A separate CreateOwnedFixtureJournalSampleFrame factory passes only a private fixed profile, through the same first false guard. It accepts fresh writable canonical GUID.jsonl creation only. Inside the still-owned Capture callback it appends one explicitly synthetic DismRestoreHealth/DispatchIntent/sequence1 entry with leaf-derived execution ID and UTC time, all client/error facts null, then independently reads and checks exact canonical Unresolved equality. Actual synchronous I/O finishes before release can proceed. Failure leaves any actual bytes in place; resource release is not rollback, valid history, retry or reset authority. This is inert fixture data, not a real repair record, workflow durability receipt or client result. The source entry is implemented but not invoked or connected to a harness; portable owned-file tests do not execute it.

Known stream/store disposal precedes one checked raw child/marker CloseHandle; descriptor/buffer and parent-borrow release follow only confirmed cleanup. Failed/ambiguous close/free/partial construction is retained without retry. The caller's original parent close/fixture teardown is a separate responsibility. Native factory, marker calls and all this framing have **not run** here.

## Coordinated official contracts and SDK binding

On2026-11-12 the coordinator retrieved:

- https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile — user-mode ntdll counterpart, held RootDirectory-relative name, FILE_CREATE/FILE_OPEN behavior, synchronous/SYNCHRONIZE requirement, reparse-open semantics, creation-only descriptor and success output descriptions. The page does not provide a complete failure/pending output-ownership guarantee.
- https://learn.microsoft.com/en-us/windows/win32/api/ntdef/ns-ntdef-_object_attributes — fields, RootDirectory kind qualification and broader OBJ_DONT_REPARSE guidance. NtCreateFile's narrower attribute prose cannot be silently expanded; this candidate does not depend on OBJ_DONT_REPARSE.
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/ns-wdm-_io_status_block — status/pointer union, Information, pending completion lifetime, and using the returned status on a non-pending return.

The coordinator then inspected the installed **official Windows SDK10.0.22621.0** read-only. This task did not access the host/VM or change files there. Those definitions now bind the actual source:

| Definition | Verified value | SDK header/line |
| --- | --- | --- |
| OBJ_CASE_INSENSITIVE | 0x40 | shared/ntdef.h1800; um/winternl.h960 |
| SYNCHRONIZE | 0x00100000 | um/winnt.h9778 |
| FILE_CREATE / FILE_OPEN | 2 /1 | um/winternl.h975 /974 |
| FILE_CREATED / FILE_OPENED | 2 /1 | um/winternl.h1025 /1024 |
| FILE_NON_DIRECTORY_FILE | 0x40 | um/winternl.h992 |
| FILE_OPEN_REPARSE_POINT | 0x00200000 | um/winternl.h1010 |
| FILE_SYNCHRONOUS_IO_NONALERT | 0x20 | um/winternl.h991 |
| FILE_WRITE_THROUGH | 2 | um/winternl.h986 |
| FILE_TYPE_DISK | 1 | um/WinBase.h788 |

NtCreateFile at um/winternl.h457–471 returns NTSTATUS and uses NTAPI. The11 ordered parameters are PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, optional PLARGE_INTEGER AllocationSize, ULONG FileAttributes, ULONG ShareAccess, ULONG CreateDisposition, ULONG CreateOptions, optional PVOID EaBuffer, ULONG EaLength. NTSTATUS is LONG (winternl.h51), ACCESS_MASK is DWORD (winnt.h9757), ULONG is unsigned long (shared/minwindef.h51), PVOID is void pointer (winnt.h425), PHANDLE is HANDLE pointer (winnt.h681). NTAPI resolves to __stdcall under the documented MSVC/_STDCALL_SUPPORTED condition (winnt.h429–435). The source uses signed32-bit return, unsigned32-bit scalar inputs and explicit pointer arguments through Winapi convention.

Only OBJ_CASE_INSENSITIVE is passed, not OBJ_DONT_REPARSE. Access includes SYNCHRONIZE/READ_CONTROL and generic read plus write only for writable requests; options require regular/non-directory, reparse-open and synchronous-nonalert, with write-through only writable. Sharing is zero; allocation size/EA pointers and EA length are zero. Disposition is exactly CREATE or OPEN; nonpending status0 alone admits the expected CREATED/OPENED Information. Other statuses retain. Full structure and binding definitions are not actual Windows runtime proof.

The coordinator also verified the [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew) return contract: success returns an open handle; failure returns named INVALID_HANDLE_VALUE, with GetLastError for extended information. The coordinator then verified SDK10.0.22621.0 um/handleapi.h27 defines it as ((HANDLE)(LONG_PTR)-1), duplicated in um/Pdh.h85. Under _WIN64 LONG_PTR is __int64 (shared/basetsd.h124–128); otherwise _W64 long (133–137), and HANDLE is a pointer (um/winnt.h671/678). The source now recognizes exactly pointer-width minus one, not NULL or a generic nonpositive result. This exact returned failure may release its known prefix without CloseHandle on the invalid value. Other nonpositive/parent-alias/throwing results remain retained; no broader output ownership is inferred. [GetFileType](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfiletype) uses the verified FILE_TYPE_DISK definition. Existing [CloseHandle](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-closehandle) facts were supplied earlier by the coordinator. Missing complete Nt failure/pending ownership guarantees remain deliberately unresolved; the retained-frame policy does not invent them.

Selected SDK layout assertions plus these definitions support the source binding, not first-parent/namespace security, compatibility across all Windows policies or an approved production internal-API fallback.

## Portable validation and claim ceiling

The linked portable source is tested with controlled callbacks and owned temporary FileStreams using the actual Core journal codec. Cases cover rejected names/modes, cancellation, nonzero/pending/throw output non-adoption, failed prefix/success cleanup, replay across domains, concurrent release, callback locks, GC rooting, real owned create-collision byte preservation, readback and malformed journal tails without repair. Fixture statuses are explicitly simulated. These tests do not execute or validate the Windows frame/store attachment/inert sample, Nt ABI/flags, ACLs, namespace races, native pending completion or power-loss durability.

Final Linux SDK10.0.403 full Release build: zero warnings/errors. Core2,162 passed/one Windows-only skipped, including38 new cases; Python297 passed/one Windows-only skipped. Independent source/test/framing review cleared this limited increment. The current concrete completion matrix remains REMAINING-WORK-AUDIT.md.

The candidate binding and inert roundtrip source now exist; native admission remains closed pending separately scoped owned Windows qualification. Even a qualified primitive would not solve production first-parent admission, effective/prior rights, local-history authenticity, crash durability or atomic journal-plus-repair dispatch. All production gates and the frozen neutral bundle remain unchanged. No native operation, host/VM action, transfer, GitHub/Actions or final package occurred.
