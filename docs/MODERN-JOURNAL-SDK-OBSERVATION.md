# Installed modern SDK reference observation

Source `221bd3ed24890871ca8b12d33220111b4f53e7dc`, run `43297534847`, attempt 1 published bounded existing-header observations from SDK `10.0.26100.0`. Verification commit `af0339b35d49adde42326956d212cc59d6816b04` contains the separately wrapped `sdk-journal-reference.json`. Its original-report hash is `4d51c6533fc6a86201364a8dcca3f0d2c265c3b0c0f06fdae24b487854eed459`. These are recorded CI reference facts, not an independently authenticated SDK distribution or a native operation.

The report declares `um/fileapi.h` size 40,460 bytes and SHA-256 `f8927178c75de487c0e57f044a215e455522bf6eaa0b660421be09cd06ae05a1`. Captured source lines 1555–1564 declare `CreateDirectory2W` as `HANDLE WINAPI`, with ordered parameters LPCWSTR path, DWORD desired access, DWORD share mode, DIRECTORY_FLAGS and optional LPSECURITY_ATTRIBUTES. This supplies a concrete installed-header return/parameter declaration where the Learn prose was inconsistent; it does not establish a named failure sentinel, last-error/output ownership or successful object qualification.

Captured lines 1533–1542 declare `CreateFile3` as `HANDLE WINAPI`, with ordered parameters LPCWSTR filename, DWORD desired access, DWORD share mode, DWORD creation disposition and optional LPCREATEFILE3_EXTENDED_PARAMETERS. The surrounding guard references `NTDDI_WIN11_GE`. This is preprocessor source text, not evaluated runtime availability.

Lines 1513–1519 show these fields in CREATEFILE3_EXTENDED_PARAMETERS order: DWORD dwSize, DWORD dwFileAttributes, DWORD dwFileFlags, DWORD dwSecurityQosFlags, LPSECURITY_ATTRIBUTES lpSecurityAttributes, HANDLE hTemplateFile. Packing, native size/offsets and a managed binding have not been compile/runtime-qualified by this report.

Two different flag declarations were observed; they must not be interchanged:

| Header and line | Declaration | Value |
| --- | --- | --- |
| um/fileapi.h:1524 | DIRECTORY_FLAGS_DISALLOW_PATH_REDIRECTS | 0x000000001 |
| um/winbase.h:171 | FILE_FLAG_DISALLOW_PATH_REDIRECTS | 0x00010000 |

The reported winbase.h hash is `1e27239cda673a4b6c8f4295a3a0a8c3b87892eb7a593922eb11e0f22e602318`. No header bytes were installed or executed by the collector.

The exact remaining semantic questions are unchanged: named failure/last-error and owned-return rules for CreateDirectory2W/CreateFile3; no-redirection coverage of intermediate/final components and supported OS/filesystems; created-versus-existing security semantics; protected first-parent admission; and supported handle-bound bounded directory enumeration for recovery. See [the composition audit](JOURNAL-PROVISIONING-DESIGN.md). A constant/declaration match does not answer these questions or enable a gate. Current production/native candidate guards remain false, and no namespace/lease/journal operation was run by this observation.

## Compile-only contract check

`python tools/compile_journal_sdk.py` selects the closed `-Journal` profile of the existing installed-toolchain launcher. It accepts no arguments, searches no PATH compiler, uses the installed x64 Visual Studio/SDK only, and emits `artifacts/native-abi/JournalSdkAbiAssertions.obj`. No linker, executable, API call, SDK installation or native sample is requested. WER's default profile and object remain separate.

The translation unit requires the SDK's `NTDDI_WIN11_GE` symbol and asserts the actual five-argument function-pointer types, x64 extended-parameter/security-attribute sizes and offsets, and the distinct no-redirect constants. An older SDK or mismatched declaration must fail compilation. The optional CI step records that outcome under `journal-native-abi`; `continue-on-error` preserves other observations and is not a qualification claim. Portable tests cover launcher refusal, fixed command construction, nonzero results and no retry after uncertain process outcomes. They do not execute PowerShell/MSVC or establish Windows header compilation.

The modern journal execution candidate is still absent and every existing production/native action gate remains unchanged. Header compilation cannot resolve failure-output ownership, namespace security, concurrency, recovery or API runtime behavior.
