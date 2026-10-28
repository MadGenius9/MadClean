# Selected installed-application review plans

This increment prepares a read-only review plan for one uniquely selected current Add/Remove Programs registration. It neither uninstalls an application nor launches Windows. The only named follow-up is the existing fixed `GuidedWindowsTask.InstalledApps` handoff: a separately reviewed user action opens `ms-settings:appsfeatures` with no selected-app identifier, product code, command, argument or discovered path. Users find and review the application inside Windows. Page opening is not evidence that removal started or completed; completion remains Unknown.

## Evidence and scope

`InstalledApplication` retains its existing fields and adds optional immutable `RegistrationEvidence`. The existing Windows inventory records the exact hive/view/subkey identity, matching source label, UTC observation time and typed `NoRemove`, `WindowsInstaller`, registered-uninstaller value-name presence and, when applicable, product-code metadata. It still never reads `UninstallString` or `QuietUninstallString` contents. It never collects an executable/arguments, uses `Win32_Product`, validates an installer package, calls an MSI action or launches an uninstaller.

Missing `NoRemove` means no restriction was declared in this registration, not a global permission guarantee. DWORD 0/1 map to known false/true; malformed flag types/values remain unknown and reduce inventory coverage. `NoRemove=1` takes precedence and blocks a selected removal plan. Missing `WindowsInstaller` is a negative flag observation, not proof the software is unrelated to MSI. Presence of registered-uninstaller value names is only a presence hint; values may be empty, stale, broken or blocked.

`InstalledAppActionPlans.Preview(snapshot, selectedIdentity, now, token)` requires an Available complete bounded snapshot (at most 20,000 entries), exactly one matching registration, a recognized hive/view/source pairing, a single bounded uninstall subkey name, safe bounded display metadata, matching embedded evidence and a known false removal restriction. Evidence older than two minutes, future-dated or invalid is blocked. This freshness window is a MadClean review policy, not a Microsoft guarantee. An incomplete snapshot never becomes permission to infer selected-app absence or removal support. The application record and evidence are immutable metadata; caller-supplied records are not an authenticity boundary and are never executable authority.

A Windows Installer flag additionally requires a nonempty brace-form product-code GUID derived from that exact registration subkey and canonicalized uppercase. Microsoft documents product codes as GUIDs and uppercase product-code letters, and the Windows Installer uninstall registration subkey as the product-code GUID. A matching GUID is still only a registration hint. This review-plan path does not query installed product contexts/database state or infer successful, permitted, available or safe MSI removal. It emits no `msiexec` command. Registered non-MSI presence hints produce the same general Windows Settings review route, without interpreting raw command content.

The separate [selected MSI metadata verification](MSI-REGISTRATION-VERIFICATION.md) now queries source-matching current-user/machine context and installed/advertised display metadata when explicitly requested. This does not run as part of this plan or loosen its complete-snapshot/NoRemove gates. A context/display match remains an observation rather than package trust, removal permission or action eligibility; no uninstall/database-open/repair action is enabled.

Unknown/missing/ambiguous/unsupported/stale evidence produces no plan. No direct executor or confirmation token is introduced. No installation, forced restart, elevation, registry mutation, uninstall-success detection or automatic leftover deletion is part of this module. Existing leftover review remains a separate complete-snapshot comparison of prior install paths; registration disappearance does not prove uninstall completion or that surviving user/shared/save/settings data is junk.

## Validation limits

Portable tests cover selection disappearance and duplicates, incomplete snapshots, mismatched source/identity/time, restricted/unknown flags, missing mechanism evidence, malformed/nonmatching product GUIDs, unsafe metadata, bounds and cancellation. They do not establish Windows Settings rendering, product registration accuracy or uninstall behavior. The Windows adapter uses real local registration reads when explicitly invoked by inventory; development does not access the user's PC. No commands or uninstall operations are run while implementing this plan.

## Current official references

Freshly retrieved with HTTP 200 during development on 2026-10-27:

- [Product Codes](https://learn.microsoft.com/en-us/windows/win32/msi/product-codes): principal product identification is a GUID; product-code letters must be uppercase. GUID syntax alone is not installer-product validation.
- [Windows Installer Properties for the Uninstall Registry Key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key): MSI writes uninstall metadata under a product-code GUID subkey; this does not authorize reading/executing its uninstall command.
- [ARPNOREMOVE](https://learn.microsoft.com/en-us/windows/win32/msi/arpnoremove): suppresses supported Add/Remove Programs removal and writes `NoRemove`.
- [Launch Windows Settings](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings): Apps & Features uses `ms-settings:appsfeatures`. MadClean does not use the separate package-family-parameter route.
