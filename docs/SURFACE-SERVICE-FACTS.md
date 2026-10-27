# Explicit Surface service-document observations

This is a deliberately narrow, read-only H2 slice beyond the bundled model/SKU reference. It retrieves and interprets three fixed Microsoft Learn HTML articles only after an explicit action. It is **not a general hardware upgrade compatibility engine**, a BIOS catalogue, a part recommendation or repair execution. No PDF, image, video, executable, firmware, recovery image, shop or partner page is downloaded.

## Supported scope and official evidence

Only the exact expected Microsoft PC manufacturer identity plus `Surface Laptop 6 for Business` and one of these System SKUs is supported:

- `Surface_Laptop_6_for_Business_2033` — the live System SKU reference must label it **13.5-inch**.
- `Surface_Laptop_6_for_Business_2035` — the live System SKU reference must label it **15-inch**.

A nearby model, missing SKU, motherboard resemblance, BIOS vendor or unknown manufacturer is Unsupported **before any HTTP request**. The Windows entry point accepts identity strings from the completed local PC-identity observation, rechecks the bounded catalogue, and sends only the same three fixed documentation URLs for either supported identity. No PC identity, serial, UUID, service tag or recovery key is put into a URL, header or body. This check is separate from ordinary Inspect and from local hardware inventory.

The pinned sources are:

1. [Surface System SKU reference](https://learn.microsoft.com/en-us/surface/surface-system-sku-reference) — an exact authored table row must confirm the current model/SKU/product label.
2. [Surface Laptop 6th for Business Device Information and Service Parts](https://learn.microsoft.com/en-us/surface/service-guides/surface-laptop-6/surface-laptop-6-business-device-information-and-service-parts) — its **Illustrated Service Parts List** section labels item 4 “Removable Solid-State Drive” and item 7 “Motherboard Module ( includes main processor and main memory).” Both 13-inch and 15-inch service rows must remain present in their respective categories.
3. [Surface Laptop 6 for Business Component Disassembly and Reassembly](https://learn.microsoft.com/en-us/surface/service-guides/surface-laptop-6/surface-laptop-6-business-component-disassembly-and-reassembly) — its **Storage (rSSD) Replacement Process** must contain the authored removal/installation and required-tools context, including the named Surface Battery Cover and USB SDT/BMR tool, and model-specific BMR imaging qualification.

The links were discovered from Microsoft's [Surface documentation table of contents](https://learn.microsoft.com/en-us/surface/toc.json) and [Service Guide Center](https://learn.microsoft.com/en-us/surface/service-guides/surface-service-guides), rather than guessed specifications or third-party summaries. Those index sources are development research; the production check requests only the three pinned articles.

## Exactly what may be reported

The result has three **published service observations**: the family rSSD replacement category, the family motherboard-assembly description, and the existence/limited qualifications of the official rSSD service procedure. Each retains its source URI, section, actual retrieval time, scope and uncertainty. The report's check time is separate from a bundled developer review date. Successful retrieval of these articles is not an “up to date” result.

The guide uses **13-inch** service labels while SKU2033 is **13.5-inch** in the identity table. The app does not round these values or silently establish exact-part equivalence. It explicitly reports the mismatch as nominal-variant uncertainty. SKU2035's shared 15-inch label also does not prove a particular part/revision is compatible. Facts remain family-level quotations; part numbers, listed capacities and CPU/RAM assembly variants are never converted into user-specific upgrade choices.

The motherboard-assembly wording **does not establish soldering or individual RAM/CPU upgradeability**. Listed factory/service assemblies are not a supported upgrade limit or CPU support table. SSD form factor/protocol/maximum capacity, free physical slots, RAM type/maximum, CPU/required-BIOS support, PSU/connectors and clearance remain Unknown. There is no budget/price/purchase advice. The source's repair steps may require physical precautions, other components, recovery/reimaging, tools and qualified help; a short app observation is not a complete repair checklist or authorization to disassemble, reimage, install firmware or modify encryption.

## Transport and parsing boundary

`ManufacturerServiceFactsClient` has a locked default transport: normal platform TLS validation, no automatic redirects, cookies or credentials, a 15-second per-request deadline, 45-second batch deadline, 32 KiB response-header limit and 512 KiB decoded UTF-8 HTML limit per article. Responses must remain at the exact pinned URI. Redirects are not followed; unsupported media/encoding, missing/denied/rate-limited sources, offline errors and cancellation do not produce facts. All three sources must succeed before any report is accepted. A fixture-only handler seam allows deterministic network contract tests; `SurfaceServiceFactsInventory` uses only the default production transport, never a configurable source/client.

The parser is bounded and non-rendering: it executes no script/CSS, resolves no external entities and loads no linked assets. It validates a unique authored main/H1, content body, exact source-specific headings and table shape/section/category/variant context. Navigation, template, script, explicitly hidden or unrelated-section mentions cannot substitute for article evidence. This is authored-source parsing, not browser-computed CSS/render verification. Stack depth and element/document sizes are bounded; cancellation propagates. A malformed or changed source becomes Unknown with no facts, rather than a guessed specification. This parser authenticates no arbitrary input by itself; production evidence depends on the default transport's fixed-source/TLS boundary.

## Validation evidence and limits

An isolated portable test project using the repository's existing test-package versions passed **34 service parser/transport cases, zero failures/skips**. These are synthetic authored-article/network fixtures, not live manufacturer verification. They cover exact identity rows, different models/duplicate evidence, missing sources, shifted section tables, nav/script/template/hidden markup, variant/prerequisite loss, structure/depth/byte bounds, ambiguous origins, redirects, no credential/identity transmission, failures/offline/invalid UTF-8 and cancellation.

A separate isolated development source-scope probe used the **actual locked-default client and current public articles** with known catalogue identities explicitly supplied as fixtures. It did not query a PC/CIM identity. The final probe verified actual HTTP and authored-source parsing, including the corrected section-bound parts table. Its UTC results appear below; they do not establish any user's hardware configuration, compatible replacement or Windows runtime behavior.

| Development scope fixture | Actual UTC batch check | Result |
| --- | --- | --- |
| `Surface_Laptop_6_for_Business_2033` | `2026-10-27T05:14:10.6600023+00:00` | Available: 3 narrowly scoped service observations |
| `Surface_Laptop_6_for_Business_2035` | `2026-10-27T05:14:10.9245119+00:00` | Available: 3 narrowly scoped service observations |

The successful probes did not validate Windows CIM, the user’s device identity, physical configuration, current-part suitability, repair execution, BIOS support or a complete upgrade recommendation. All unresolved limits described above remain Unknown. Raw source HTML was temporary development material, not bundled production/sample data.
