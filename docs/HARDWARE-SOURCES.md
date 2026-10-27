# Hardware evidence sources and strict scope gates

This module includes a small **bundled identity reference**, portable matching/advisory gates, and a separate explicit live checker for three narrowly scoped Microsoft Surface family service observations. It does not implement a verified upgrade-limit catalogue, current BIOS catalogue, recommendation engine, shopping or firmware actions. See [SURFACE-SERVICE-FACTS.md](SURFACE-SERVICE-FACTS.md) for the limited online adapter. Hardware limits/CPU support/required-BIOS/expansion compatibility remain **Unknown or Unsupported** until an exact-product manufacturer document is actually retrieved and its claim verified. A source URL, a matched name or a test fixture is not that verification.

## What was directly inspected

The official [Microsoft Surface System SKU reference](https://learn.microsoft.com/en-us/surface/surface-system-sku-reference) was read during development on **2026-10-26 (America/Chicago)**. Its visible table distinguishes devices with the same SMBIOS System Model by System SKU and states that listed SKUs are commercial unless marked Consumer. The selected table rows are:

| System Model | System SKU | Documented device label |
| --- | --- | --- |
| Surface Laptop 6 for Business | `Surface_Laptop_6_for_Business_2033` | Surface Laptop 6 for Business 13.5-inch |
| Surface Laptop 6 for Business | `Surface_Laptop_6_for_Business_2035` | Surface Laptop 6 for Business 15-inch |

`HardwareCompatibilityIdentityCatalog` stores only these two developer-reviewed associations and their official reference URI/review date. It requires the expected Microsoft PC manufacturer identity plus exact model/SKU; it never uses a motherboard or BIOS-vendor fallback. Its scope explicitly says the **bundled association itself is not a live retrieval**. A separately selected online source check records any actual current retrieval; the static catalogue cannot assert whether that check ran. A catalogue match is not a fresh online check and supplies no RAM maximum, RAM upgradeability/soldered design, CPU replacement/support list, physical-slot availability, BIOS-version approval or compatible-part verdict. The System SKU is a model/configuration identifier; no serial, UUID or service tag is sent anywhere.

The published article identifies its official documentation source as [MicrosoftDocs/devices-docs, surface-system-sku-reference.md](https://github.com/microsoftdocs/devices-docs/blob/public/surface/surface-system-sku-reference.md). The published Learn table, rather than a guessed hardware property, was the consulted evidence.

## Limits encountered during source review

- Microsoft's exact [Surface Laptop 6 for Business product page](https://www.microsoft.com/en-us/surface/business/surface-laptop-6) was blocked by the cloud proxy (403). Guessed Learn technical-specification paths returned 404 and are not evidence. No factory RAM option is relabeled an upgrade maximum.
- Framework's official knowledge-base/product origins (`knowledgebase.frame.work`, `frame.work`) and [Lenovo PSREF](https://psref.lenovo.com/) were proxy-blocked. Their current specifications are unverified here; no limits were copied from memory, search snippets or third-party pages.
- [Dell Support Home](https://www.dell.com/support/home/en-us) was readable, but is a generic landing page, not an exact-model/revision/configuration specification. A guessed OptiPlex manual URL returned 404. Neither establishes a RAM/CPU/firmware claim.
- [HP Support](https://support.hp.com/us-en) returned a minimal client application shell without readable product facts. HTTP success alone is not successful specification retrieval.

These development access results do not predict whether a user's browser can open the sites. The subsequently discovered exact Surface service guides are accessible and support a narrow live family-service adapter, not a manufacturer-verified RAM/CPU/BIOS/expansion/part-compatibility catalogue. The current supported BIOS workflow remains the separate read-only identity/WUA evidence and reviewed official guide links; no version ordering or flashing is introduced.

## Portable policy contracts

The new `HardwareCompatibilityPolicy.MatchTarget` returns only **Unknown** or **ScopeMatched**. ScopeMatched means that supplied identities and source metadata passed exclusion/mismatch gates. It is explicitly **not** source authentication, document-content verification, a verified manufacturer specification, a compatible candidate, or an approval to purchase/install/flash. Future trusted adapters must still retrieve the actual document, verify the particular claim and retain its qualifications.

The gate requires exact OEM manufacturer/model/SKU, or exact retail board manufacturer/product plus an independently verified hardware revision. It rejects OEM-to-retail substitution, close spellings, placeholders, missing data, conflicting evidence, ambiguous URIs, mismatched issuer/origin, generic landing pages and unsuccessful retrieval. It performs no generic BIOS numeric comparison. A required firmware condition needs an independently verified vendor rule tied to the current exact observed version/namespace and an evidence reference.

The **30-day source-retrieval review limit is MadClean policy**, not a manufacturer document expiry or a promise that a recently fetched page is current. Future/unknown/stale retrieval times block the gate. The URI-origin list is a conservative routing/matching check, **not a network connector or evidence that content at every accepted vendor URL was read**. Query strings, fragments, userinfo, nondefault ports, escaped/backslash ambiguity, trailing-dot hosts and lookalike domains are refused. The Surface SKU reference cannot be reused for memory/CPU/expansion/storage claims, including its locale aliases.

`AssessAdvice` returns **Unknown** or **ReadyForReview**, not a product recommendation. It requires an explicit positive budget, supported currency, ISO region and intended use case, plus all area-specific evidence checks. GPU advice cannot pass without independently verified PSU/connectors, case/GPU clearance and actual occupancy; RAM advice cannot pass with unknown replaceability/soldered design or occupancy. CPU and storage checks retain exact vendor/firmware/protocol/lane/power/mounting qualifications. Duplicate/conflicting check entries are rejected. A fresh UI checkbox or inventory-derived count must not manufacture a verified manufacturer or physical-check record.

No prices, currency conversion, performance promises, carts, purchases, downloads or installations are supplied. A scope gate for one component cannot substitute for another component or identity-only evidence. A complete checklist merely allows the user to review already verified evidence; it does not execute or authorize an action.

## Validation scope

Portable tests use **clearly named fixture identities and fixture documents** for policy behavior. They cover OEM/retail confusion, exact SKU/revision, URI ambiguity, inaccessible/stale/conflicting sources, identity-only catalogue limits, current firmware evidence, missing budget/use case and unresolved PSU/clearance/occupancy/soldered-memory checks. Fixtures are not production manufacturer verification. The limited live source client/parser was separately probed against its actual three official articles using a known catalogue development scope, not a detected PC. That does not verify Windows CIM identity, physical applicability or candidate compatibility, and does not make H2/H3 complete.

## Source-specific identity completeness

`HardwareFact.SourceEnumerationComplete` records only whether its individual allowlisted CIM source returned one available, untruncated, bounded, valid-object enumeration. It is not evidence that a value is accurate or interpretable. Unknown raw memory-type meanings still make overall hardware inventory Partial. `HardwareIdentitySnapshotPolicy` permits the separate online guidance action only for a unique completed PC source with one exact manufacturer/model/SKU; unrelated unknown RAM fields cannot make that action permanently unreachable. Missing, truncated, duplicate, malformed, unrecognized or unattested identity remains blocked. No human coverage text is parsed into permission.
