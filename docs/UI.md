# Native interface and reference concept

The user supplied the reference concept **inline in this conversation**. It is the visual reference for adapting the existing MadClean WPF application; it is not a repository image asset or an additional application. The earlier missing-reference assumption no longer applies. Screenshot numbers, drive names, findings and populated sample states are design examples, **not production data**. No design/sample mode is added.

The inspected concept establishes this hierarchy:

- A deep navy surface system with a blue-gradient MadClean brand treatment, restrained blue accents, rounded panels and clear typography.
- Primary **horizontal navigation**: Overview, Cleanup, Security, Startup and Updates.
- An Overview hero headed **“Your PC, clearly understood”** and a prominent **“Inspect this PC”** action.
- Four inspection rows for the existing cleanup, security, startup and update areas, keeping their real state and detail destinations visible.
- A right-hand column containing a selected-drive free-space ring and a **“Before you start”** explanation panel.
- A footer with a direct **Activity log** destination.

This is a native-window, responsive adaptation of the supplied layout. It does not replace standard Windows caption buttons with drawn lookalikes, create a frameless window, or copy a screenshot's dimensions at the expense of usable scaling. The native frame, system menu, resize/maximize/restore/close controls and keyboard behavior remain available. The existing `WindowAppearance` helper can request supported dark native-frame treatment; unsupported treatment falls back to the ordinary frame without changing Windows theme settings or requiring elevation. An accepted DWM attribute is not proof of a particular visible caption color.

## Existing navigation and commands

The five primary destinations retain their existing page indices and backing behavior. Visual order is not permission to renumber pages or accidentally swap Security and Startup. Every secondary destination remains reachable through **More tools**, including implemented discovery tools and clearly labeled queued modules. The footer Activity log is a direct route to the existing activity view. This redesign does not create replacement tools, duplicate provider tasks or remove the existing detail pages.

**Inspect this PC invokes the existing explicit local inventory action only.** It does not run online app/Windows/driver update checks, self-release checks, firmware-site requests or later queued scans. Update rows can remain Not checked after a local inspection; selecting an area to review its detail page must not silently query its provider. Online discovery keeps its own explicit action. Cancellation, busy-state command gating, provider status distinctions and actionable errors continue to apply.

The four inspection rows summarize real results or honest unchecked/unknown states. They must not invent a number of problems, mark a PC healthy/clean, or turn an unexecuted update check into Up to date. Detailed source, coverage, result state and attempt/result timestamps remain available as selectable/copyable text on the relevant detail views. A finished local inspection is not a guarantee that every provider succeeded or that every inventory is complete.

## Selected-drive storage ring

The ring is a display of the selected drive from the **actual local disk snapshot**, not a combined sum across drives and not a health indicator. Production starts at **Not checked**, with a neutral track and **no blue arc**. No drive capacity, sample C: value or percentage from the concept is seeded into the view.

Only a successful readable drive result from an explicit local inspection can populate the selector and its displayed capacity/free values. Changing the selected drive changes the display of that existing snapshot; it performs **no new query**. The visible drive identity, source and snapshot/attempt context must make that distinction clear. If completed results are retained after a later failed/cancelled attempt, their timestamps and the failed/cancelled state must remain honest; retained data must not be silently presented as a fresh result.

The blue arc and its **FREE** legend encode **available free bytes for the current user / drive capacity**. The remaining neutral portion is not a measured cleanup opportunity or a precise “used by your files” category. System API values can be affected by quotas, permissions and filesystem accounting: user-available space may differ from the drive's total unallocated free space, logical file sizes or physical allocation. Preserve those limits and the existing fixed-local-drive coverage. A valid result for one drive does not erase a Partial error for other drives.

For valid positive capacity and available bytes within `[0, capacity]`, the fraction is free/capacity. Zero free bytes is a known zero with no blue arc; fully free is a known full blue ring. Missing/invalid values, zero or invalid capacity, free bytes greater than capacity, or an unreadable selected drive remain Unknown/Not checked/Failed as appropriate, with a neutral ring rather than a fabricated percentage. Text carries the exact result and distinguishes known zero from unknown. Color or arc presence alone is never the result.

## Presentation organization

The reference adaptation stays within the existing app and MVVM structure:

| Source location | Responsibility |
| --- | --- |
| `src/MadClean.App/MainWindow.xaml` and its existing code-behind | Native WPF shell, horizontal primary navigation, More tools routing, reference Overview layout and Activity log link. Existing page destinations and commands stay connected. |
| `src/MadClean.App/OverviewViewModel.cs` | Overview presentation and selected-drive snapshot state; no hidden provider refresh when changing a displayed drive. |
| `src/MadClean.App/StorageRing.cs` | Native WPF drawing for the selected-drive free-space ring, including known zero/full and unknown states. Drawing is not a provider or a health/cleanup calculation. |
| `src/MadClean.App/Themes/ReferenceStyles.xaml` | Shared navy/blue reference-concept presentation resources, spacing, typography and navigation/panel styling. |
| `src/MadClean.App/Themes/DarkControls.xaml` | Existing dark native-control treatment for scrollbars, tooltips, read-only selection, tables, focus and related surfaces. Reference styles must remain coherent with these controls. |

The wide layout uses the main inspection area plus the storage/explanation column. At narrower sizes and increased text/display scaling, panels and actions adapt or scroll without overlap, clipped results or inaccessible commands. Stable text contrast, visible active/inactive selection and keyboard focus take priority over a pixel-identical screenshot at one size. Native scrollbars and copyable read-only detail text remain functional.

The “Before you start” panel explains the current explicit, read-only workflow. It must not imply that clicking Inspect grants cleanup, installation, elevation, malware scanning, firmware changes or restart permission. The visual increment adds none of those capabilities, no automatic provider startup and no synthetic health score.

## Required Windows visual comparison

Linux compilation, portable tests and source inspection cannot render this WPF interface. **Windows rendering, DPI behavior and fidelity to the supplied inline concept remain unverified until the packaged executable is actually inspected on Windows.** The screenshot is a comparison reference, not evidence of a rendered MadClean build. Use the existing [Windows validation matrix](VALIDATION.md) and add the following visual checks in a disposable Windows environment, unelevated:

| Check | Required observation |
| --- | --- |
| Reference comparison | Compare the rendered Overview with the supplied inline concept: navy/blue-gradient brand, horizontal navigation, hero/Inspect action, four inspection rows, right-hand storage and Before you start panels, and footer Activity log. Record intentional native-frame/responsive differences; do not claim pixel fidelity from compilation. |
| Initial state | Launch without invoking providers. All unexecuted checks remain Not checked/Unknown; the ring is neutral with no blue arc and no seeded drive/sample capacity. No fake health or issue count appears. |
| Local versus online action | In a disposable fixture environment, invoke Inspect this PC explicitly. Verify the existing local providers/statuses and cancellation; online update/firmware/release checks remain unrun unless their separate explicit action is selected. |
| All tools remain reachable | Navigate every primary page, every More tools item and the footer Activity log. Verify old page mappings, queued-versus-implemented labels and retained detail commands. No route starts a second provider task or a queued feature. |
| Drive selector and ring | With controlled test results, check available-free fractions 0 and 1, a normal fractional value, unavailable/unknown and invalid values. Confirm FREE legend, full/empty geometry and exact text. Changing selection must use the same snapshot with no new query. Test quota-limited values and global Partial/failed/cancelled checks; errors and uncertainty must remain visible. Fixtures must not become production sample data. |
| Responsive layout and DPI | Inspect the minimum supported size (currently 980×650), default size and wider layouts at 100%, 150% and 200% scaling. Check hero/action wrapping, row labels/statuses, ring text and right-column stacking/scrolling, long messages and absence of overlap. |
| Keyboard and accessibility | Use keyboard-only primary/More tools navigation and the drive selector, including opening/closing the dropdown, focus restoration and selected item announcement. Verify visible focus, active/inactive table selection, tooltip contrast, UI Automation names and text selection/copy for full paths, source, status and timestamps. A color-only gauge/status is insufficient. |
| Scrollbars and native frame | Drag vertical/horizontal scrollbars, page tracks, and use wheel/keyboard scrolling in every detail grid/long page; disabled controls remain legible. Verify standard title-bar system menu, resize/maximize/restore/close and dark-frame fallback. |

These are required manual runtime checks, not passed results. No new production sample-data, browser mockup or screenshot-generation feature is introduced to satisfy them. No personal-PC access or system-changing action is part of the visual review.
