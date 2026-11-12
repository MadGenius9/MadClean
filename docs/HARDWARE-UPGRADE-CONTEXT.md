# Attached hardware inventory context

The Hardware Upgrades worksheet now has a separate **Attach detected inventory** action. Choose an upgrade area and explicitly use an already completed local hardware inventory. This attaches reported PC/baseboard facts plus RAM, CPU, GPU or storage facts for that area. It starts no inventory query, network request, source lookup, compatibility check, purchase or hardware action.

## Evidence boundary

The accepted source must be Available or Partial, nonempty and bounded to 4,096 facts. The loaded list must contain every actual source fact exactly once, with no clones, missing rows, foreign objects or duplicates. Field lengths, safe text, exact projected source/property/category mappings, duplicate field identities and consistent per-class enumeration flags are checked. Unexpected properties such as serial/UUID/device-instance identifiers are refused. Missing selected-area observations withhold context rather than showing only PC identity as if components were known.

Admission requires ordered explicit UTC attempt/response/review times; both the attempt and response must be at most 30 minutes old. This is MadClean review policy, not a manufacturer expiry or proof that the hardware is unchanged. The copied read-only container retains the actual observation references. Output includes only PC/baseboard context and the selected area. Incomplete PC identity or partial enumeration stays visibly incomplete; it does not manufacture an exact model/revision match. No BIOS namespace or verified PCB revision is inferred from a reported board Version field.

Reported values are not reinterpreted, totaled, ranked or compared to products. Raw SMBIOS memory codes and reported form factors remain provider observations. Detected capacity is not supported maximum capacity. Device counts are not free slots; reported sockets/interfaces do not prove replaceability or physical connectors. Actual PSU/connectors, clearance, physical occupancy, soldered designs, supported CPUs and required BIOS remain separate manufacturer/manual evidence requirements. Family service notes and bundled identity references cannot satisfy them.

The context report's compatibility state is get-only Unknown. CanRecommend, CanPurchase and CanInstall are get-only false. It supplies no verification switches or action token. The generic prerequisite worksheet keeps every compatibility requirement Unknown even after successful attachment; a Partial context result means bounded observations were attached, not partial compatibility approval.

## UI and lifecycle

Attachment is explicit and separate from generic prerequisite review. The table shows each reported property/value, source and source-enumeration flag. Query attempt, response receipt and attachment time describe the shared request, not per-class observations, current hardware state or an atomic snapshot. The report remains historical until cleared.

Changing the upgrade area or any planning input clears both reports before public input notifications. Hardware refresh/error/cancellation clears attached context before publishing a changed observation. Clear attached context changes only this in-memory report; Clear planning inputs also clears attachment. Neither alters the underlying hardware inventory. No planning text is passed into the context policy, persisted, logged or exported. No extra activity entry is written.

## Validation limits

Portable boundary tests and independent source review cover reference/schema/source/field/clock and non-promotion rules. Linux compilation does not qualify actual WPF selection/input/refresh/clear/layout/accessibility behavior. Existing Windows hardware-reader evidence does not automatically qualify this new attachment workflow. No personal PC or disposable VM is used during implementation.

This increment supplies inventory context, not a verified manufacturer upgrade-limit catalogue. Exact-model RAM limits/types, supported CPUs and required BIOS, expansion/storage options and actual part recommendations remain unimplemented until official applicable specifications are retrieved and checked. Existing source-access and exact-scope limits in [hardware sources](HARDWARE-SOURCES.md) remain in force; no new manufacturer claim is added.
