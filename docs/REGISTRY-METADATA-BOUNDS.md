# Bounded Add/Remove Programs registration metadata

The installed-app inventory remains read only. This increment replaces allocation driven by arbitrary registry arrays/value sizes with bounded native enumeration and a fixed metadata whitelist. It adds no registry changes, uninstall execution, elevation, remote-registry access or new source roots.

## Reader contract

The native adapter uses `RegEnumKeyExW` with a fixed 256-WCHAR name buffer and bounded indices, stopping honestly at existing source/entry limits. Input name-buffer capacity includes the terminator; a successful returned character count excludes it. `ERROR_NO_MORE_ITEMS` ends enumeration. `ERROR_MORE_DATA`, other errors or invalid names become coverage issues; the buffer is not grown. Enumeration can race external registration changes, so completion describes observed source coverage rather than an atomic registry snapshot.

Only fixed display/install metadata and flag names may be read. Text limits excluding one terminal NUL are DisplayName 1,024 UTF-16 code units, DisplayVersion 256, Publisher 512 and InstallLocation 8,192. Flags use four bytes. These are MadClean resource/display limits, not Windows registry storage limits. Read access does not establish trustworthy ownership or app authenticity.

The reader first queries type and byte count with `lpData = NULL`. Oversized or unsupported data is rejected before a payload allocation/read. It then uses only the approved bounded field buffer; changed type/count, `ERROR_MORE_DATA` or an unexpected error fails closed without buffer growth or partial decoding. Raw native return codes are distinguished from derived validation issues. Used buffers are cleared when released. Native output on failure is never decoded: Microsoft says the payload is undefined after `ERROR_MORE_DATA`.

`UninstallString` and `QuietUninstallString` are a separate **literal-name presence query only**, with `lpData = NULL`. Their contents are never read, allocated, decoded, parsed, logged or converted to commands. A presence-query error means unknown mechanism evidence, not absence; invalid/missing display data and malformed flags preserve incomplete coverage. No enumeration of arbitrary registry value names is required to discover these two fixed names.

## Portable decoder

`RegistrationMetadataDecoder.DecodeText(nativeType, declaredByteCount, data, maximumCharacters, token)` accepts only `REG_SZ`/`REG_EXPAND_SZ` from the W API, an exact successful native byte count matching the supplied data span, an even nonzero byte count within the approved cap, and valid UTF-16LE with exactly one terminal NUL. Embedded or extra NUL, truncated/unpaired surrogates, control or Unicode-format characters (including supplementary format characters) are invalid. It never guesses encodings, expands environment references, trims/normalizes content, repairs a missing terminator or parses commands. Bound arithmetic and declared/data lengths are checked before text allocation. Cancellation is checked before validation, periodically during code-unit inspection and before/after bounded string construction.

Microsoft explicitly warns that even successful `RegQueryValueExW` string results may be unterminated. **Requiring exactly one terminal NUL is MadClean's conservative acceptance policy, not an API guarantee.** Such registrations become unknown/partial rather than silently padded/repaired. An empty one-NUL string is valid empty text; the existing caller decides whether a blank display name is omitted or blank metadata displayed as Unknown. A UTF-16 BOM is not stripped into a guessed encoding; format-character policy rejects it.

`DecodeFlag` accepts only `REG_DWORD` with declared and actual lengths exactly four bytes, interpreted little endian, and a value of zero or one. Any other type, size or number is Invalid with a null boolean, never false by coercion. `MissingText`/`MissingFlag` are distinct typed results reserved for actual native `ERROR_FILE_NOT_FOUND`. The adapter retains documented existing missing-field defaults: absent display metadata is Unknown/unnamed, while absent registration flags mean no such flag was declared. Malformed flags are not absence. In particular malformed SystemComponent must not silently expose a possibly hidden entry, and malformed NoRemove/WindowsInstaller must not create support evidence.

`RegistrationTextMetadata` and `RegistrationFlagMetadata` preserve Valid/Missing/Invalid states with bounded generic issue descriptions. They do not contain native exception text, source-value contents in errors, raw uninstall data or execution authorization. `REG_EXPAND_SZ` metadata remains literal; existing install-path policy independently rejects environment-variable paths.

## Official documentation and verification limits

Fresh official HTTP 200 references reviewed on 2026-10-27:

- [RegQueryValueExW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regqueryvalueexw): NULL-data size probes, successful unterminated string possibility, missing-value errors and undefined payload on insufficient-buffer errors.
- [RegEnumKeyExW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regenumkeyexw): fixed buffer/returned count semantics, iteration, end-of-enumeration and insufficient-buffer errors.
- [Registry value types](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-value-types): `REG_DWORD` little endian and unexpanded environment references in `REG_EXPAND_SZ`.
- [Registry element size limits](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-element-size-limits): registry data can be much larger than these product acceptance limits; an arbitrary source size must not dictate allocation.

Portable tests cover missing versus valid empty text, literal environment references, exact field caps, giant declared lengths, mismatched/truncated buffers, unsupported types, missing/embedded/additional NULs, raw invalid UTF-16, control/bidi/supplementary formatting, exact flag sizes/endian interpretation/unknown values and cancellation. These tests do not verify native registry handles, Windows access behavior, enumeration races or GUI rendering. The native owner's fixtures and coordinator build/Windows validation remain separate evidence. No registry scans or mutations are performed while implementing/testing the portable decoder.

## Current actual Windows result

The diagnostic ran at source dafb241/run40693650636. It identifies the failing case as the owned unterminated seed: two bytes were requested, but an independent size/type query observed four stored bytes of REG_SZ, while the reader returned Valid. This establishes a seed-representation mismatch, not the exact four-byte contents or a production decoding defect. No production acceptance policy changes. The subsequent fixture correction independently reads only its own tiny bounded seed, verifies the exact stored representation before selecting assertions, and explicitly leaves native unterminated-value rejection unverified if Windows stored a terminated value. Portable raw-unterminated decoder tests remain mandatory. Any other stored shape or failed query remains a failure/unknown, never a normalization assumption.

This distinction follows the documented contracts: RegSetValueExW requires NUL termination and a byte count including that terminator; preserving an intentionally malformed seed is not guaranteed by that contract. RegQueryValueExW can nevertheless return unterminated existing strings, so the strict decoder is still necessary. Actual runtime results for the corrected fixture remain pending.

Historical run40503046113/sourcee24e090 built with799 Passed and10 NotExecuted core records. Actual59-entry Partial registration projection and Defender status contracts passed, but the new owned text-boundary fixture failed its then-generic bounded-text-state assertion; WER was separately inconclusive. Diagnostic-only e002144 was initially blocked by Git authentication, then successfully pushed through dafb241 using working HTTPS access. Its actual result above now identifies the case; the source-reviewed bounds are still not presented as exhaustive native malformed-value proof.
