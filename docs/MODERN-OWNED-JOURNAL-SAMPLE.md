# Closed modern journal sample contract

This is a source-development dependency for the existing repair workflow. It is not production namespace provisioning, restart recovery, an authorized repair, or a new user feature. Keep the independent modern candidate gate false, the older relative NtCreateFile gate false, and all action gates unchanged. No App, command-line or CI execution hook is part of this source slice. Compile-only SDK assertions are separate.

## Supported evidence and limits

The installed SDK 10.0.26100.0 declarations and the actual source-05c01da compile-only assertion pass are described in [the SDK observation](MODERN-JOURNAL-SDK-OBSERVATION.md). Official reference text was retrieved from Microsoft's `MicrosoftDocs/sdk-api` repository at immutable commit `656e9656bfaef34dd29cd7ee9e1146aec17e924c`. Its locally captured nine-file reference manifest SHA-256 is `92ac5d786c93d4b67eb2d94b5726f3d28eb9e6dee207a617ca11d7fde4f4b396`. These are source contracts, not native Windows results.

CreateDirectory2W's reference prose disagrees with its HANDLE-return sample about failure, and that sample's parameter count disagrees with the installed header. The five-parameter installed declaration governs the candidate binding. A conservative usable positive handle still needs independent object qualification. Null, minus-one, other invalid/alias shapes or a throwing creation call cannot establish that no directory exists: retain the actual operation owner and inputs without adoption, retry or assumed cleanup.

CreateFile3 documents INVALID_HANDLE_VALUE on failure. Existing-only reads must use OPEN_EXISTING, while both fresh files must use CREATE_NEW. Never substitute OPEN_ALWAYS, CREATE_ALWAYS, truncation or a path-based fallback. Creation security descriptors do not authenticate existing objects. DISALLOW_PATH_REDIRECTS is not an atomic retained-parent namespace operation and does not freeze concurrent metadata changes; ordinary sharing restrictions do not cover all attribute/EA access.

## Fixed scope and ownership requirements

The sample must bind the exact current repository's `artifacts/windows-validation-fixtures-<canonical GUID>` root and its exact 32-byte ownership marker. It may create only one internally named fresh private directory, one exclusive `lease.bin` and one same-request GUID lifecycle file. There is no caller-selected filename, open mode, production location, existing-history adoption, process launch or path deletion. The candidate requires x64 Windows build 26100 or later, available required exports, a fixed NTFS volume with persistent ACLs, a root at most 160 characters, at most 16 ancestor components and full child paths at most 258 characters. These are local profile limits. Same-handle object/security/identity checks precede sample acceptance.

Register the actual mutable owner before any OS call, not a descriptive task or replacement owner. Publish raw owned slots before allocating wrappers. Retain pointed-to buffers, descriptors, wrappers and the exact ancestor/parent/lease graph across incomplete or uncertain calls. Failed native outputs are opaque unless that API's documented return grants ownership. A failed or ambiguous cleanup is sticky; never erase the graph, retry a close, claim cancellation or turn uncertainty into permission to proceed.

A store attachment permit must be opaque, one-shot and bound to the actual registered private frame, exact qualified wrappers, active phase and live exclusive lease. No caller Boolean, raw handle adoption or configuration override may authorize attachment. The stream/store construction interval is itself owned; a constructor exception before output publication must retain the frame. Existing old-gate wrappers stay guarded while only private mechanics may be shared.

The only sample record is a synthetic DispatchIntent for DismCheckHealth. Exact same-handle codec/readback acceptance leaves it unresolved. It must never fabricate a client start, PID, exit, successful repair or reusable dispatch receipt. Release is checked, one-shot and reverse-ordered, and refuses while a callback/native/store operation is active without consuming a later legitimate release. Releasing resources is not deletion or rollback.

## Remaining qualification

Portable owner tests can establish controlled state transitions, not filesystem security or Windows behavior. Installed-header compilation establishes declarations/layout only. This candidate still needs separate reviewed Windows qualification before any execution hook, and first-parent production provisioning, existing-only recovery/enumeration, durability/crash boundaries and public repair integration remain distinct incomplete work. See [the namespace design](JOURNAL-PROVISIONING-DESIGN.md) and [completion assessment](END-TO-END-COMPLETION-ASSESSMENT.md).

## Source validation checkpoint

The concrete source candidate and one-shot store-mechanics bridge passed independent code review. All 24 controlled owner cases passed, including competing/replayed/foreign requests, callback-thread authority, caller stop during a blocked creation, known refusal versus uncertain/undefined/throwing returns, partial preparation, failed codec observation and sticky cleanup failure. The first targeted run was 23 passed/one failed because a thread-pool observer could inline; the final tests use dedicated threads for thread authority and cross-thread lock observations. No production assertion was weakened.

The final Release solution build passed with zero warnings/errors, including the explicit x64 OS/process restriction. Final aggregate Core: 2,444 passed, zero failed, one Windows-only skipped. Python: 367 passed, zero failed, one skipped. These runs invoked no Windows native sample. Source 05c01da's earlier installed-header compilation is independent evidence for ABI declarations/layout only. Public provisioning, recovery, real repair execution and new native sample qualification remain incomplete.
