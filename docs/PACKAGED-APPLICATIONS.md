# Current-user Windows package discovery

Installed apps now keeps **Desktop registrations** and **Windows packages** as separate sources in the existing page. Desktop ARP/MSI behavior, baselines and leftover comparisons are unchanged. The package source is explicit read-only discovery for the current Windows profile; it does not check updates or perform deployment, removal, repair, registration, activation or elevation.

## Source and collection

`WindowsPackagedApplicationInventory` launches the OS-reported system Windows PowerShell5.1 executable with no profile, no interactive prompts and a constant encoded metadata script. The child restricts PSModulePath to system modules, imports the absolute `$PSHOME\Modules\Appx\Appx.psd1` manifest, and invokes module-qualified `Appx\Get-AppxPackage -PackageTypeFilter Main,Bundle`. No caller text becomes script, arguments or filters.

Omitting `-User` and `-AllUsers` deliberately uses the current profile. No user SID, other-user scope, volume, manifest, package files, installation location, dependency collection, status payload or provisioning query is requested. Package identity strings can contain a public publisher identifier; that is not a device identifier or user SID, and no separate PublisherId field is collected. Package identity Name is a technical name, not necessarily a friendly/localized app title.

The custom PowerShell Appx wrapper is not assumed to be a direct WinRT Package object. Each fixed named property is probed with strict runtime types and caps; absent/unsupported values become explicit null, never default false. Public WinRT references establish property meaning, not an unsupported CLR-type assumption. NonRemovable is optional provider-reported metadata rather than a verified public Package property/removal permission. SignatureKind is reporting metadata, not an independent trust scan or security verdict; Developer does not mean developer-only or a development build.

The script requests one sentinel beyond1,000 records to detect truncation and projects at most1,000. The existing metadata-only process runner bounds each output stream to4,194,304 characters and the query to60 seconds, with cancellation/process-tree cleanup. These are consumer/process limits, not a claim about every internal provider allocation. Query warning **presence only** produces source Partial; no warning/provider exception text is serialized or logged. Import/query failures remain Unavailable/Failed; no source is installed or silently repaired.

## Portable contract

The strict Version1 JSON schema requires Scope=CurrentUser, explicit ordered Main/Bundle types, Status=Available/Partial/Unavailable/Failed, a truncation flag and an allowlisted package array. Required identities are Name/PackageFullName/PackageFamilyName. Display fields are Version, Publisher, Architecture and SignatureKind; nullable flags are IsBundle, NonRemovable, IsFramework and IsResourcePackage. Paths, manifests, UserInfo and unexpected properties are not admitted.

Identity/display limits are product bounds: Name/FullName256 UTF-16 units, FamilyName128, Version64, Publisher1,024 and Architecture/SignatureKind32. Unsafe Unicode, oversized identities, malformed records and known framework/resource rows outside the requested filter are omitted with Partial coverage. Every duplicate full identity is omitted case-insensitively, including ambiguity introduced by a malformed duplicate. Versions require four canonical UInt16 fields; unfamiliar architecture/signing values and missing optional flags remain Unknown/Partial. Unavailable/Failed responses cannot smuggle attached rows into a successful inventory.

A successful empty Available response means no accepted covered records were returned for this profile/filter. It does not establish no installed apps. Empty Partial remains Partial. Main and bundle records can represent the same software, and packages can overlap ARP entries; counts are **rows**, never a combined total of unique apps. Installed version is not update availability or an up-to-date claim.

## UI and privacy

The existing desktop view was extracted into a native UserControl without changing source commands or bindings. The default source tab remains Desktop registrations. Package data has its own collection, selected details, attempt/status and bounded cancellable publication. Source changes do not auto-query. Shared local Name/Publisher search filters both loaded sources while retaining separate full data and ARP baselines; hidden selections are cleared. An incomplete/cancelled package read clears partial displayed results instead of presenting them as complete.

No package values or search text enter the enum-only local activity log. The native validation check exports only state/count/provenance/schema booleans, never package names/IDs/versions/SIDs/flags/provider messages. Initial package-tab screenshots are taken with empty unchecked data, before the separate actual ARP search workflow. No images follow populated UI metadata.

The separate required `ui-packaged-app-search` development check exercises the actual production package read command and bound controls after every screenshot and the desktop search interaction. It requires usable real rows, then tests positive literal filtering, exact selected identity/details, a no-match filter clearing selection/details, Clear restoring loaded rows and unchanged desktop source/baseline/status. Filtering must add no activity or provider read. No package values or query text leave the harness, and no screenshot follows. Missing/empty/unavailable data and timeouts stay inconclusive, independent of a passing direct-provider check; initial empty-tab proof does not establish populated interaction. This additional gate needs its own source-bound Windows run.

## Validation and scope limits

Portable tests cover strict scope/schema/field privacy, successful empty and source-Partial responses, optional unknown flags versus false, duplicate-identity poisoning, versions/enums, unsafe text, limits, failure data and cancellation. The Windows fixture requires a genuine nonempty CurrentUser Main/Bundle projection for positive row evidence; a legitimate empty/unavailable query remains inconclusive. The genuine initial tab/command/view binding and fixed screenshot are checked separately without invoking the read command. Actual source-bound Windows results are recorded in the execution ledger; Linux compilation alone is not provider or GUI proof.

No uninstall action or eligibility token exists. NonRemovable=false, SignatureKind=Store/System or a known version never grants removal permission, establishes integrity/running state or proves a supported cleanup target. Other profiles, framework/resource/optional/dependency packages, provisioning, portable apps and unregistered software remain outside this source.

Actual [Windows run40721260816](https://github.com/MadGenius9/MadClean/actions/runs/40721260816), sourcecdf476b, returned48 actual Available package rows with valid projected schema and pre-cancellation observed. The initial empty package view and extracted desktop/MSI bindings passed, and genuine tool21/tool20 captures were inspected. The retained desktop search interaction also passed on59 loaded registrations/1 match. These checks export no package values and do not exercise populated package filtering, every provider policy or physical keyboard/accessibility. Core records reconcile878 Passed/10 NotExecuted.31 scoped checks passed; WER alone remains inconclusive, so aggregate validation is not a complete pass. Packaging was skipped.

## Official references

Current official Microsoft sources were retrieved successfully during implementation; Learn proxy failures were not treated as API unavailability:

- [Get-AppxPackage](https://learn.microsoft.com/en-us/powershell/module/appx/get-appxpackage) ([official source](https://raw.githubusercontent.com/MicrosoftDocs/windows-powershell-docs/main/docset/winserver2025-ps/Appx/Get-AppxPackage.md)): default current-user profile, explicit package-type filtering and custom wrapper output.
- [PackageId](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.packageid) ([official source](https://raw.githubusercontent.com/MicrosoftDocs/winrt-api/docs/windows.applicationmodel/packageid.md)): Name, FullName, FamilyName, Version, Publisher and Architecture meanings.
- [Package](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package) ([official source](https://raw.githubusercontent.com/MicrosoftDocs/winrt-api/docs/windows.applicationmodel/package.md)): bundle/framework/resource and signature-kind observations, with member-specific API/packaging qualifications.
- [PackageSignatureKind](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.packagesignaturekind) ([official source](https://raw.githubusercontent.com/MicrosoftDocs/winrt-api/docs/windows.applicationmodel/packagesignaturekind.md)): reporting-oriented None/Developer/Enterprise/Store/System classifications.
- [PackageTypes](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagetypes) ([official source](https://raw.githubusercontent.com/MicrosoftDocs/winrt-api/docs/windows.management.deployment/packagetypes.md)): Main/Bundle versus excluded types.
