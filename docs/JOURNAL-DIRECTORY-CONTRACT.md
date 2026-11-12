# Remaining native directory binding

The current parent-security and managed dependency slices do not implement directory `EnsureExists` or complete private-store namespace admission. This note records verified inputs and the narrower unresolved contract before native arguments are encoded.

## Verified reference facts

The coordinator read the installed official Windows SDK 10.0.22621.0 header `um/winternl.h` without changing the host:

| Definition | Value | Header line |
| --- | --- | --- |
| `FILE_OPEN_IF` | `0x00000003` | 976 |
| `FILE_DIRECTORY_FILE` | `0x00000001` | 985 |

The previously verified shared bindings include `FILE_SYNCHRONOUS_IO_NONALERT=0x20`, `SYNCHRONIZE=0x00100000`, `FILE_OPENED=1` and `FILE_CREATED=2`, plus the existing NtCreateFile/OBJECT_ATTRIBUTES/IO_STATUS_BLOCK ABI. Numeric definitions are not Windows runtime evidence.

The coordinator retrieved the official user-mode [NtCreateFile](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile) reference. `FILE_OPEN_IF` opens an existing target or creates an absent one; a supplied descriptor applies only when creating. Existing type/security therefore requires independent qualification, with actual opened/created information kept separate. Directory mode permits Create/Open/OpenIf and lists synchronous alert/nonalert, write-through, backup-intent and open-by-ID as compatible options. The [kernel reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntcreatefile) confirms directory/synchronous values but its kernel-only requirements must not be imposed or assumed for a user-mode call.

## Unresolved confinement contract

`FILE_OPEN_REPARSE_POINT` is described separately and absent from the directory compatibility list. Do not copy the regular-file option combination into a directory adapter or claim that its supported directory contract is verified. A single-component name plus a retained RootDirectory handle is not by itself proof against reparse redirection or concurrent namespace/security changes. Post-open type/path/security checks cannot retroactively prevent an unintended creation.

The earlier C++ owned directory control uses CreateNew, broader object attributes and deletion/teardown observations. It does not prove OpenIf production semantics, arbitrary existing-parent containment or a no-reparse guarantee. A fallback from failed Open to Create would also conflict with the mandatory unknown-output retention contract; no fallback is authorized.

Next native implementation requires a documented supported directory profile with the intended confinement, chosen access/share/options and RootDirectory requirements. Alternatively, a narrower independently reviewed contract must state what it actually proves; no unsupported action capability or claim follows merely from supplied constants. The existing managed state machine/dependency graph can be reused once that boundary is selected. Actual relative lease/lifecycle/history composition and source-bound Windows qualification remain separate missing work.

No new directory call, fixture route, host/VM operation, guard activation or native argument profile was added here.
