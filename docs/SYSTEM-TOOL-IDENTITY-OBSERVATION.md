# Microsoft tool identity evidence

The Windows integrity-tools page now separates **tool identity** from **Windows integrity**. Verifying the genuine installed Microsoft executable does not run DISM/SFC, inspect component corruption, repair Windows or enable execution. The immutable production execution gate remains false.

The existing native provider still obtains the fixed system directory from Windows, leases the selected executable and its ancestry, uses WinVerifyTrust with the existing certificate/revocation policy, inspects the accepted Microsoft signer, fingerprints bounded bytes and reads the reported version. No catalogue fallback, signature bypass, cache-only trust setting, alternate executable or custom argument is added. Verification can retrieve certificate metadata and write normal Windows certificate caches; a 45-second caller wait does not force an in-flight native verification to stop.

The new portable observation policy validates that provider projection separately from launch policy. It checks the selected fixed catalogue descriptor and arguments, an independently OS-derived system directory, exact file path, MicrosoftSignatureVerified report, lowercase SHA-256 shape, bounded safe version/detail/blocker fields, fresh UTC times with at most two-minute lifetime, and CanLaunch=false. It does not itself perform Authenticode verification or authenticate a caller-supplied directory. No launch preview is forged or switched to CanLaunch=true to satisfy validation.

Available/Partial projections retain their source coverage. A verified signature with an unknown version can remain Partial and says so; Available plus an unknown version is inconsistent and remains Unknown. Missing/malformed/stale projections do not yield accepted identity metadata. Unsupported/unavailable/failed/cancelled source states remain distinct, with no accepted observation or zero-value health inference. Messages do not echo arbitrary provider data. The UI shows an observation time and expiry, and requires another verification for new evidence; it never treats the observation as a current integrity/repair verdict.

## Required real Windows evidence

The development harness explicitly prepares **DISM CheckHealth identity** and **SFC verify-only identity** through the existing provider. These operation names select which executable metadata to verify; their command lines are never run. Pre-cancelled preparation must stop before querying. Both real executables need genuine positive projected evidence for a positive check; missing, catalog-only, inaccessible or revocation-unavailable evidence stays inconclusive rather than becoming trusted by fallback.

The fixture independently compares the real OS system path and fixed descriptors as well as using the portable consistency policy. With a positive identity and the production gate confirmed false, a request bound to that exact preview must return Unavailable without a PID, exit code or elevation request. If the gate is unexpectedly true, the fixture fails and never calls LaunchAsync. This is a disabled-boundary proof, not a servicing test or dynamic instrumentation of every native/file API. Source review establishes the early return before launch/log creation; normal certificate-cache writes remain possible during identity checking.

Evidence exports only fixed operation/status/count/schema flags, never paths, versions, hashes, certificate data, provider messages or repair output. Initial UI evidence precedes populated metadata; the harness never injects tool identities into the UI. Actual source-bound results are recorded in the execution ledger. Linux compilation and fixture policy tests cannot establish Windows trust behavior.

## Official semantics checked again

The official WinSDK `WinTrust.h` and MicrosoftDocs SDK API sources were freshly read before this validation step. WinVerifyTrust returns a LONG and **only zero means success**. WTD_STATEACTION_VERIFY state must be closed with the matching action, while its pointers and file lease remain valid. Provider/signer/certificate pointers are borrowed and inspected before state closure; they are not independently freed. The current sequential managed layouts match the documented pointer/DWORD order; this source review is not a new compiled native ABI assertion.

- [WinVerifyTrust](https://learn.microsoft.com/windows/win32/api/wintrust/nf-wintrust-winverifytrust)
- [WINTRUST_DATA](https://learn.microsoft.com/windows/win32/api/wintrust/ns-wintrust-wintrust_data) and [WINTRUST_FILE_INFO](https://learn.microsoft.com/windows/win32/api/wintrust/ns-wintrust-wintrust_file_info)
- [WTHelperProvDataFromStateData](https://learn.microsoft.com/windows/win32/api/wintrust/nf-wintrust-wthelperprovdatafromstatedata), [WTHelperGetProvSignerFromChain](https://learn.microsoft.com/windows/win32/api/wintrust/nf-wintrust-wthelpergetprovsignerfromchain), [WTHelperGetProvCertFromChain](https://learn.microsoft.com/windows/win32/api/wintrust/nf-wintrust-wthelpergetprovcertfromchain)

The D2/D3 runtime, consent, cancellation, logs, recovery and repair gates in [SYSTEM-TOOL-ACTIONS.md](SYSTEM-TOOL-ACTIONS.md) remain open. No health score, arbitrary DLL, elevation, servicing command or forced restart is introduced.
