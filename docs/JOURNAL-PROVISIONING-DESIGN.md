# Proposed private journal namespace

Design assessment only, based on coordinator-retrieved official references on2026-11-12. No provisioning, service, broker, native call or guard activation is implemented here. All existing production creation/repair/native-candidate gates remain false. The validated history/UI development build is separately pinned at3b25787; this note does not qualify Windows behavior.

## Concrete limit of retained handles

A retained directory handle and deny-write/delete sharing are **not** a complete metadata/reparse-mutation barrier. Microsoft's [CreateFile3 reference](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfile3) says attribute/EA access is unaffected by sharing flags; [MS-FSA reparse-setting semantics](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/4aeefef8-92c3-4abc-af7a-a610caf8a165) permit FILE_WRITE_ATTRIBUTES or FILE_WRITE_DATA for FSCTL_SET_REPARSE_POINT. Earlier attribute-write handles are an additional concern. Post-open checks cannot retroactively prevent creation through a redirected namespace. Neither the managed owner graph nor an ACL snapshot fixes that gap.

## Smallest practical threat model to review

MadClean is an asInvoker desktop utility, not a security boundary against arbitrary malicious code running as the current user, SYSTEM or administrator. A current-user ACL cannot distinguish MadClean from other code with that same identity. Do not add a privileged broker/service to address an unrequested attacker model. If that resistance is required later, it needs a separately reviewed OS identity boundary and operational authorization.

This trust boundary does not waive ordinary data safety: another user must not gain namespace writes; normal concurrent changes, pre-existing handles, denied access, reparse changes and uncertain cleanup must refuse safely. Journal bytes remain historical consistency evidence, never standalone permission, native success, exactly-once dispatch or authority for automatic replay. Every later action still requires its own live exact-selection/permission/confirmation checks. No universal power-loss or atomic journal-plus-servicing transaction is claimed.

## Bounded architecture proposal

1. Provision a separate application-private operation-journal namespace outside cleanup/exclusion/log caches, protected from inception. Define owner and effective creation/removal, attribute-write, DACL/owner-change and journal-write rights explicitly. Current user, SYSTEM and explicitly trusted administrators are the proposed trust set, not an authenticated MadClean-only identity.
2. Keep provisioning distinct from ordinary inspection, recovery and action dispatch. Do not repair/adopt existing arbitrary ACLs or silently migrate old history. An existing namespace gets an existing-only, non-destructive qualification path; existing children need independent type/identity/security checks. Refusal/uncertain history cannot become permission to create a new empty history and dispatch.
3. Retain the qualified actual parent/ancestor owners; use fixed single-component child names, restrictive descriptors on creation, create-new for fresh GUID journals and a cooperating exclusive lease. Two processes must share admission through the real lease. None of these steps is independently a full namespace proof or a same-user tamper boundary.
4. Assess a supported no-redirection path profile for new Windows versions, combined with a protected stable namespace and exact opened-parent identity checks. Do not copy the unverified NtCreateFile directory/reparse option combination or add an Open/Create fallback after ambiguous failure. Graceful Unsupported is preferable to a fabricated guarantee on unsupported OS/filesystems.
5. Persist an intent before dispatch with checked flush/readback under that namespace. A crash between intent and observed dispatch/completion stays unresolved; recovery never reopens a PID, resumes, retries a repair or claims rollback. Hardware durability and native operation outcome remain separate.

The concrete location/provisioning API and caller/ACL contract still need a final design review. This proposal does not silently change the existing fixed production location or its strict acceptance rules. Portable ownership/storage/result code already exists; the missing source dependency is supported namespace provisioning/admission and real relative lease/history composition, not another generic preview.

## Candidate APIs and exact remaining reference work

Windows11 24H2 / Server2025 expose DISALLOW_PATH_REDIRECTS in the current [directory flags](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ne-fileapi-directory_flags) and [CreateFile3 extended parameters](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-createfile3_extended_parameters). [CreateDirectory2W](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createdirectory2w) has ABI/return/failure-sentinel inconsistencies in its prose. Do not bind it from that prose or presume path-based APIs are retained-parent-handle replacements. The existing SDK22621 numeric facts do not verify this newer ABI.

The next exact research dependency is a matching modern official SDK declaration (including CreateDirectory2W return/parameter types, failure sentinel/last-error contract, DIRECTORY_FLAGS and CREATEFILE3_EXTENDED_PARAMETERS layout/flags), plus the documented no-redirection scope and OS/filesystem availability. No modern SDK installation or native experiment is authorized by this note. A separately approved owned Windows fixture must then test the reviewed profile; an experiment does not invent an undocumented guarantee.

[OpenFileById](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid) supports existing directory opens with BACKUP_SEMANTICS and OPEN_REPARSE_POINT. It does not create a directory, establish present parent membership or authenticate a reused persisted ID. It can supplement qualification, not replace first-parent admission.

## Required evidence before any activation

Use exact owned disposable scopes for pre-existing attribute-write handles, parent/child reparse changes, ACL/owner changes, two-process admission/collision, denial and ambiguous native outputs/cleanup. Exercise crash/restart windows before/after persisted intent, dispatch and result observations. Record actual source, OS/SDK, flags, results and limits. Stress success is not universal contract proof. Then separately qualify the actual public selected workflow; neither source review, a neutral UI pass nor the new history-provider mode can activate repair/journal creation.

## Current source composition audit

The next code slice should integrate the actual namespace, exclusive lease and existing store rather than add another portable protocol. `SystemToolJournalPrivateStore.OpenScope` still uses legacy directory/path lease creation; `ReadHistory` enumerates by pathname and opens lifecycle records by absolute path; `BeginIntent` also uses an absolute lifecycle path. `WindowsSystemRepairJournal.Owner` already supplies the rooted serialized upper lifecycle. `SystemToolJournalStore.AdoptQualifiedRelativeLifecycleFile` is the existing held-file attachment point.

The current relative-file factory is explicitly an owned-fixture factory, not production parent admission. Its raw NtCreateFile frame uses NTSTATUS. A future Win32 BOOL/HANDLE-return API must retain its own documented output/ownership contract; it cannot be inserted into that frame by reinterpreting a return value or removing fixture guards.

Recovery also needs bounded enumeration through the qualified retained directory, because pathname enumeration can resolve a changed namespace independently. The exact directory-information class, record layout, restart/end/error behavior and supported filesystem contract must be checked before a `GetFileInformationByHandleEx`-based candidate is chosen. Reference to a candidate API is not a verified contract. Relevant official reference questions remain at https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandleex and https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ne-minwinbase-file_info_by_handle_class . No enumeration binding or native call was added by this audit.

Once declarations and behavior are established, use a separately marked closed owned fixture to connect fresh namespace creation, independently selected existing-only reopening, lease contention and the existing same-handle journal codec. Preserve unresolved history and keep production routing separate until qualification and review. Current CI SDK reference fragments can establish source declarations only; the remaining semantic questions and Windows evidence cannot be inferred from a symbol match.
