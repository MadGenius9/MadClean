# Creation-parent exclusion before journal-directory writes

The disabled private-store creation path now inspects its actual held creation parent before each directory creation. Previously it checked volume/object shape and validated only the eventual private child ACL. A private child ACL cannot retroactively protect a write made through unsupported ancestry.

`OpenScope` now retains a no-follow `READ_CONTROL | FILE_READ_ATTRIBUTES` parent handle without delete sharing. The existing object/path and persistent-ACL checks run on that handle, followed by `GetSecurityInfo` owner/DACL projection. Production composition checks LocalAppData before attempting the MadClean directory and then retains/checks the actual MadClean directory before attempting the journal directory. The owned fixture path checks its actual parent too. Cancellation is rechecked before the first parent creation. Existing-only review retains its previous metadata-only branch and creates nothing.

## Narrow policy and trusted principals

The portable `JournalCreationParentSecurity` policy accepts an exact current-user owner and a present, non-NULL DACL with 1–32 entries. Every entry must be an ordinary noncallback allow ACE. Trustees are restricted to that user, SYSTEM (`S-1-5-18`) and explicitly trusted Builtin Administrators (`S-1-5-32-544`). Even a foreign read-only or inherit-only allow entry is conservatively refused. Deny, callback, object-specific, compound and unknown entries are unsupported rather than interpreted as an effective-access result.

Masks must be nonzero concrete file rights within the existing `FILE_ALL_ACCESS` mask; generic/reserved/additional rights are refused. Only ordinary inheritance flags are accepted. `InheritOnly` excludes the current object; `NoPropagateInherit` instead limits propagation to later generations and must not be treated as making a current-parent grant ineffective. This policy conservatively rejects foreign grants in either case rather than calculating effective rights. Inherited entries and unprotected DACLs are permitted for this **namespace parent exclusion check only**. The existing protected, exact user+SYSTEM full-access descriptor predicate for private files/directories is unchanged. No ACL is repaired, normalized or replaced. Duplicate legitimate ACEs are not collapsed into a permission inference. A NULL DACL grants unrestricted access while an allocated empty DACL grants none; both forms are unsupported here, without claiming they mean the same thing.

The Windows adapter projects only the actual returned descriptor, bounds it to 64 KiB, and clears copied descriptor bytes. It does not accept caller-provided grants as native proof. A known-success allocation is released through the existing checked descriptor cleanup. A failed call's nonzero output is neither dereferenced nor freed as an assumed owned allocation: its raw value is retained and sticky cleanup uncertainty blocks later creation. No failed-output ownership guarantee is invented.

Both namespace and strict private-object validators now use the same descriptor acquisition/error path. The older strict reader's unconditional free of a failed-call nonzero output has been removed; its successful ACL acceptance/validation body is unchanged. Cleanup uncertainty is latched before retention-list allocation can fail, including a failed `LocalFree`. These are conservative source ownership changes. Native failure injection/Windows behavior has not been exercised by portable tests.

## Claim ceiling and remaining admission work

This is a structural security snapshot, not authorization, an effective-rights calculation or a private-parent proof. There is no required user/SYSTEM grant: the actual operation must still enforce caller access. The model trusts the current account, SYSTEM and Administrators; it does not resist those principals, concurrent DACL changes, previous handles or privileged interference. Ordinary upstream profile ancestry is still shape-checked ancestry, not a foreign-rights qualification of every ancestor.

`CreateDirectoryW` is still path based. Retention/path checks do not bind that API atomically to the qualified parent. Relative directory creation, relative lease/lifecycle composition, complete-history admission under those exact owners, existing-object collision handling and native uncertainty/recovery still require implementation or qualification. Existing relative regular-file source accepts only a marked owned fixture parent and is not production composition. Flush/readback is not power-loss durability or an atomic journal-plus-repair transaction.

All production writer, native candidate, repair/recovery and public execution guards remain unchanged. This code is not invoked by ordinary inventory, startup, public repair or a new harness mode. No native operation was executed for this increment.

## Validation and references

Portable tests exercise descriptor exclusion boundaries, bounded collections and malformed projections. They do not exercise `GetSecurityInfo`, normal Windows profile ACLs, first-create ordering, actual namespace contention, Windows cleanup or source-bound native behavior. Final build/aggregate results are recorded in the completion ledger after validation.

On 2026-11-12 the following current official references were requested with normal TLS verification; the configured proxy returned CONNECT 403 before page bytes arrived:

- https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo — returned allocation lifetime, failure outputs and concurrent-security caveats.
- https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights — concrete file-right mask and its limits as an effective-access inference.
- https://learn.microsoft.com/en-us/dotnet/api/system.security.accesscontrol.aceflags?view=net-10.0 — ordinary inheritance versus audit/unknown flag values.

The coordinator subsequently retrieved the official pages through its permitted web tool. `GetSecurityInfo` returns a copied descriptor with component pointers into that allocation, freed with `LocalFree`; owner/DACL reads require `READ_CONTROL`. Return zero means `ERROR_SUCCESS`; a nonzero value is the actual WinError rather than a Boolean or `GetLastError` contract. Microsoft explicitly excludes protection against concurrent security-descriptor changes. The source uses those narrow facts and does not assume a complete failed-output ownership guarantee.

The .NET enum confirms inheritance bits 1/2/4/8, inherited bit 16, and audit bits 64/128. The misleading short .NET `NoPropagateInherit` description is resolved by the native ACE header and inheritance rules:

- https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-ace_header
- https://learn.microsoft.com/en-us/windows/win32/secauthz/ace-inheritance-rules
- https://learn.microsoft.com/en-us/windows/win32/secauthz/null-dacls-and-empty-dacls
- https://learn.microsoft.com/en-us/windows/win32/fileio/file-access-rights-constants

The file-security reference distinguishes default inherited descriptors on new objects from an existing child's own descriptor and explains generic-right mapping. The named constants include add-file 2, add-subdirectory 4, traverse 32, delete-child 64, read-attributes 128, write-attributes 256 and write-EA 16. These contracts support the exclusion-only design, not effective-access, frozen ACLs, namespace atomicity or Windows qualification.

## Current owned-fixture precondition

The old broad harness created its outer GUID root with inherited default security, which did not guarantee the current-user owner/known-ACE profile required here. Actual source 0cd8b2c reported CreateOwnedScope/SecurityUnsupported, not a precise owner/ACE diagnosis. The harness now requests a protected current-user/SYSTEM descriptor at fresh root creation, independently audits the actual held object/security/volume, and closes that audit before later sharing/namespace probes. It never adopts collisions or repairs an existing ACL. Its owner marker is create-new, fixed ASCII and flushed before probes.

Preparation failure retains the scope and does not enter normal fixture cleanup. The fixture descriptor audit now treats failed GetSecurityInfo outputs as opaque and never dereferences or frees them; uncertainty is latched before retention allocation. This is stronger test setup and failure handling, not production first-parent provisioning, namespace confinement, secure recovery or writer activation.
