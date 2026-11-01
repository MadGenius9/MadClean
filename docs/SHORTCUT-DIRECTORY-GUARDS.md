# Owned directory-guard compatibility tests

The next S2 action gate is whether the intended relative move/restore works with directory leases that could support later namespace protection. This is a development experiment on disposable owned shortcuts, never a client executor. `CanMove` remains false.

The existing native NT baseline retains its original read/write sharing. Three separate fresh-child tests change only one existing directory lease to `FILE_SHARE_READ`: the source parent, destination parent, or outer owned fixture ancestor. Desired rights remain `READ_ATTRIBUTES | TRAVERSE`; no overwrite, different native operation, absolute-path or rights fallback is introduced. The replacement lease is acquired while the previous lease remains held, and both handles must match full native directory identity, volume, attributes/tag and final path before the old lease closes.

Each profile independently requires the same actual three-call sequence: verified move, verified restore and negative collision refusal, with identity/bytes/target preservation and safe teardown. A baseline pass cannot promote any profile. A failure remains unverified compatibility; a pending operation or uncertain teardown retains the entire owned tree and skips subsequent experiments. There is no automatic restore/fallback after failed validation and no personal file is involved.

Passing this test establishes only the tested rename compatibility. It does not establish resistance to concurrent reparse/rename changes, privacy, stable bytes, durable recovery or action readiness. A separate interference test needs positive unguarded controls before guarded refusals mean anything. Permission failures or unsupported capability are Unknown, not proof of protection.

Current official sources were read before designing the experiment. [CreateFileW](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/fileapi/nf-fileapi-createfilew.md) distinguishes delete/rename sharing from attribute access, which is not governed by the same sharing rules. [Native FILE_RENAME_INFORMATION](https://github.com/MicrosoftDocs/windows-driver-docs-ddi/blob/staging/wdk-ddi-src/content/ntifs/ns-ntifs-_file_rename_information.md) documents an internal target-directory open requesting `FILE_WRITE_DATA | SYNCHRONIZE`. Thus stronger target sharing might conflict with the intended rename itself.

[FSCTL_SET_REPARSE_POINT](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/winioctl/ni-winioctl-fsctl_set_reparse_point.md) documents privilege requirements. Its `FILE_SPECIAL_ACCESS` control-code bits in the official [winioctl.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/winioctl.h) and [devioctl.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/devioctl.h) do not establish which additional filesystem checks apply. No reparse mutation, privilege adjustment or policy change is performed by this first compatibility matrix.

Windows outcomes for these new profiles remain pending. Source review and compilation do not prove either compatibility or namespace protection.

The combined solution builds with zero warnings/errors. Python tooling checks pass121 tests with one Windows-only skip (122 total), including omitted-profile and baseline-not-promoting-unknown-profile guards. Portable Core logic is unchanged from1,039 passing tests. Independent combined review found no material blocker in the owned test's setup, lifetime, independent reporting or retained-tree behavior.

## Actual compatibility outcomes

[Run 41359970620](https://github.com/MadGenius9/MadClean/actions/runs/41359970620), source `6209215`, immutable evidence `80ef357ea89c2e521ef3960cf728e68d54ae0ac1`, established the following independent outcomes:

| Strengthened lease | Move / restore | Actual last NTSTATUS | Interpretation |
| --- | --- | --- | --- |
| Source parent | Move observed; restore not observed; 2 calls / 1 verified success | `0xC0000043` | Does not establish the required move-and-restore compatibility. |
| Destination parent | First move not observed; 1 call / 0 successes | `0xC0000043` | Does not establish rename compatibility. |
| Fixture ancestor | Move, restore and collision refusal observed; 3 calls / 2 successes | `0xC0000035` collision refusal | Only this single strengthened-ancestor configuration passed the owned sequence. |

All three reached safe teardown with no pending worker. The independent baseline NT check remained passing. Source/destination sharing failures were not promoted by the ancestor or baseline result. Core1,029 Passed/10 NotExecuted reconcile1,039;47 scoped passes/no failures/six inconclusive, SDK/control/build passed and packaging skipped. None of these results proves combined guards, reparse resistance or namespace protection. The separate [sharing observation](DIRECTORY-SHARING-OBSERVATION.md) examines permission relationships without a reparse operation.
