# Open executable-identity source gates

Actual diagnostic run [41306474471](https://github.com/MadGenius9/MadClean/actions/runs/41306474471), source787fbf6, returned:

| Operation descriptor (not executed) | Observed failure stage | Native trust result | Limit |
| --- | --- | --- | --- |
| DISM CheckHealth identity | VersionMetadata / OriginalFilenameMismatch | 0x00000000 | Windows trust succeeded before the original-filename check failed. No original-filename value was exported; missing, MUI-derived or another value is not inferred. |
| SFC verify-only identity | Signature / NativeTrustNotEstablished | 0x800B0100 | The selected file-verification route did not establish signature trust. This does not establish that the file is unsigned, tampered, catalog-only or affected by revocation access. |

Both remain unverified; no genuine-preview launch request, servicing command, elevation, repair or restart occurred. The fixed diagnostics are evidence, not permission.

## Version strings and MUI provenance

Fresh official [GetFileVersionInfoW](https://learn.microsoft.com/windows/win32/api/winver/nf-winver-getfileversioninfow) documentation says it merges fixed information from the neutral file and strings from the corresponding MUI file. [GetFileVersionInfoExW](https://learn.microsoft.com/windows/win32/api/winver/nf-winver-getfileversioninfoexw) and [GetFileVersionInfoSizeExW](https://learn.microsoft.com/windows/win32/api/winver/nf-winver-getfileversioninfosizeexw) do not make FILE_VER_GET_NEUTRAL an executable-only string guarantee: strings can still come from MUI. [VerQueryValueW](https://learn.microsoft.com/windows/win32/api/winver/nf-winver-verqueryvaluew) reads the returned version block, not a held executable handle.

The official [.NET 10 FileVersionInfo implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.FileVersionInfo/src/System/Diagnostics/FileVersionInfo.Windows.cs) uses those merged APIs and language/codepage fallbacks. The current filename comparison is conservative, but the metadata must not be described as proven to originate in the held executable's signed bytes. These API facts do not prove what DISM's actual mismatching value was.

The proposed next implementation is bounded resource inspection of the exact already-held signed executable, without a loader, MUI resolution, external path reads or executable activation. It needs checked PE/RVA/section/resource bounds, VERSIONINFO lengths/alignment/UTF-16, language/table consistency, cancellation and actual fixture proof. Missing or contradictory embedded names stay Unknown. No blanket MUI-suffix allowance, skipped name check or acceptance of any Microsoft binary is authorized by this research. Official references include [PE format](https://learn.microsoft.com/windows/win32/debug/pe-format), [VS_VERSIONINFO](https://learn.microsoft.com/windows/win32/menurc/vs-versioninfo), [StringFileInfo](https://learn.microsoft.com/windows/win32/menurc/stringfileinfo) and [StringTable](https://learn.microsoft.com/windows/win32/menurc/stringtable).

## Installed catalog verification

Supported APIs exist for explicit SHA-256 catalog lookup: [CryptCATAdminAcquireContext2](https://learn.microsoft.com/windows/win32/api/mscat/nf-mscat-cryptcatadminacquirecontext2), [CryptCATAdminCalcHashFromFileHandle2](https://learn.microsoft.com/windows/win32/api/mscat/nf-mscat-cryptcatadmincalchashfromfilehandle2), [CryptCATAdminEnumCatalogFromHash](https://learn.microsoft.com/windows/win32/api/mscat/nf-mscat-cryptcatadminenumcatalogfromhash), and [WINTRUST_CATALOG_INFO](https://learn.microsoft.com/windows/win32/api/wintrust/ns-wintrust-wintrust_catalog_info). A first-result-only query with NULL previous context followed by explicit catalog/admin context release avoids unresolved continuation ownership. Non-SHA1 verification requires the matching catalog administrator handle in the WinTrust catalog structure.

Lookup alone is not signature/member verification. Precise member-tag/hash binding remains to be established; neither a guessed uppercase hash tag nor an undocumented NULL tag may be used. The checked Microsoft classic code-signing sample enumerates catalog matches without demonstrating this WinTrust member verification. The checked PowerShell v7.5.4 source delegates its catalog-capable lookup to Microsoft.Security.Extensions1.4.0; the visible fallback uses file/blob verification, so it supplies no missing member-tag contract.

Any later implementation must bind the exact held member object/hash, keep a validated read-leased system catalog path stable through verification, preserve strict Microsoft signer/revocation checks and actual state closure, and bound all native buffers/candidates. The hash API's consulted documentation does not promise stream-position preservation. No catalog Add/Remove, downloads, SHA1 fallback, cache-only bypass or policy weakening is implemented here. Catalog support remains queued pending those contracts and disposable Windows proof; 0x800B0100 alone does not identify the applicable catalog or cause.
