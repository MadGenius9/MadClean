# Selected Windows Installer metadata verification

This is a separately selected, read-only prerequisite for later supported uninstall tools. **Verify selected MSI metadata** checks the selected product's Windows Installer context and display registration. It never uninstalls, installs, repairs, configures, activates or opens an installer package. Ordinary inspection and registration refresh do not invoke it. Existing Windows Settings handoffs and action-plan restrictions remain unchanged.

## Exact scope and meaning

`MsiRegistrationReview.CreateScope` requires a bounded Available or Partial in-memory ARP snapshot, one uniquely observed selected identity, exact hive/view/source provenance, fresh UTC registration evidence (at most two minutes), WindowsInstaller=true and a canonical product-code GUID from that exact 38-character uninstall subkey. This lookup concerns a present selected record; Partial inventory does not establish absent software. Allowing Partial here does not weaken the complete-inventory requirement for action plans or leftover comparisons.

HKCU scope permits current-user managed/unmanaged contexts (1/2); HKLM scope permits machine context (4). Registry32/64 view does not prove MSI architecture. No fallback switches users or contexts to obtain a match. Removal restrictions are carried visibly: NoRemove=true or unknown does not prevent reading metadata, but nothing in this module can override it or authorize removal.

The native worker calls `MsiEnumProductsExW` with the exact non-NULL product-code filter, NULL input SID (current user) and **both output SID pointers NULL**. It never retrieves a SID or enumerates all products/users. Repeated indexed enumeration and property reads run on the same synchronous worker thread. At most three contexts are retained, with a fourth bounded probe to detect unexpected additional entries. Every returned code/context must match the selected scope; repeated or ambiguous contexts remain Unknown.

`MsiGetProductInfoExW` uses that exact product and returned context. The fixed property whitelist is:

| SDK literal | Read and interpretation |
| --- | --- |
| `State` | One-character payload:1 means advertised,5 installed in the queried context. Other/missing values remain Unknown. These are this API's values, not imported semantics from another product-state interface. |
| `InstalledProductName` | Installed context only; bounded to1,024 UTF-16 code units. |
| `VersionString` | Installed context only; bounded to256 units. |
| `Publisher` | Installed context only; bounded to512 units. This is registration text, not publisher authentication. |
| `ProductName` | Advertised context only; bounded to1,024 units. Installed-only version/publisher remain Unknown. |

Buffers include space for a terminal NUL. Returned lengths/termination, Unicode, identity and context are validated; `ERROR_MORE_DATA` never triggers growth/retry. Native errors remain numeric coverage issues, not provider/exception text in logs. Used buffers are cleared. No package/source/install paths, URLs, raw uninstall commands or credentials are queried. In particular there is no `MsiOpenProduct`, `Win32_Product`, `msiexec`, installation/configuration action or source resolution.

## Results and cancellation

The portable assessment requires complete bounded exact-product enumeration, one installed instance and exact Ordinal name/version/publisher agreement with known selected ARP metadata before reporting a match. Missing, advertised, changed, ambiguous or mismatched evidence remains Unknown. An empty response is never treated as proof of absence or completed uninstall. A match from Partial input stays Partial; unknown NoRemove also retains uncertainty. Reported restrictions, source state and ARP observation/scope-validation/MSI-check times are distinct in the UI.

Reads are sequential and non-atomic. Matching metadata does not authenticate the app/package, establish ownership, lock an action target, prove health, establish permission/removal support or monitor uninstall completion. There is no executor or confirmation token. Supported selected uninstall remains later work; shared files, dependencies, user data, saves, settings and leftovers cannot become disposable through a metadata match.

A process-wide singleton worker has a20-second cooperative budget and30-second cancellable caller wait. Cancellation/timeout ends waiting, not a synchronous Windows Installer call. The worker retains ownership and blocks another query until it actually returns; no thread/native-resource abort or force-close occurs. Interrupted waits publish no completed verification. Selection/snapshot generation guards suppress stale UI publication; selection changes and registration refreshes invalidate prior results. Only enum operation/outcome/time enter the local activity log.

## Validation

Portable tests cover source/context identity, GUID aliases, duplicate selection, stale/future/invalid timestamps, NoRemove preservation, Partial coverage, advertised/multiple/missing contexts, exact metadata mismatches, bounds/Unicode and cancellation. Linux compilation/tests do not validate Windows Installer behavior, UI interaction or uninstall actions. The development native check uses only already-installed CI-runner registrations; it never installs a fixture product, repairs software or changes actual ARP data. Missing usable coverage remains inconclusive. Actual source-bound results are recorded in the execution ledger when available.

Actual source84cccf9/run40699262668 verified the first selected product's unique Installed context and exact display/source binding, with Partial source state, original restrictions, pre-cancellation and no pending worker. Core outcomes:829 Passed/10 NotExecuted.27 scoped checks passed, WER stayed inconclusive and packaging was skipped. Only fixed status/count/provenance fields were exported; no SID, product code or metadata values left the runner. This does not establish package authenticity, uninstall eligibility, every context/policy or general cancellation behavior. Actual initial command/empty-row binding checks passed; the first genuine image exposed only the panel heading, so body capture is being refined independently.

## Official references

Current Microsoft documentation/SDK sources were retrieved successfully during implementation; Learn's proxy403 response was not treated as API unavailability:

- [MsiEnumProductsExW](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msienumproductsexw) ([official source](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/msi/nf-msi-msienumproductsexw.md)): exact product filter, current-user SID semantics, optional SID outputs and same-thread enumeration.
- [MsiGetProductInfoExW](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msigetproductinfoexw) ([official source](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/msi/nf-msi-msigetproductinfoexw.md)): context/property availability, installed/advertised State values and character-count/error semantics.
- [Microsoft SDK Msi.h](https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/Msi.h): property literals, context values and function declarations.
