# Workflow benchmark and original implementation

This comparison uses official product documentation to identify useful interaction patterns. It is a workflow benchmark: no competing executable was installed/run, no timed performance comparison was performed, and no repair or speed improvement is claimed. MadClean's C#/WPF code, geometry, controls and visual composition are original; no competitor UI, assets, cleaner definitions or implementation code was copied.

| Reference inspected | Useful workflow | MadClean implementation / explicit boundary |
| --- | --- | --- |
| [BleachBit official README](https://github.com/bleachbit/bleachbit/blob/master/README.md) | Select scope, preview exact files, review choices before deletion. | Exact-path/size/category observations with saved user exclusions. Saving an exclusion invalidates the current preview; an explicit scan rebuilds it. Deletion is queued and has no execution API. |
| [WinDirStat official README](https://github.com/windirstat/windirstat/blob/master/README.md) | Choose drives/folders; link sized visual marks with an inspectable list; navigate to large contributors. | Explicitly selected fixed-local-folder metadata walk, original weighted 2D treemap, linked keyboard-accessible list, drill/up/reset and exact selected-path inspection. Areas encode known logical bytes. Skipped/unknown bytes, hardlink/physical-allocation limits and Other aggregation are visible. No NTFS/elevated acceleration or speed claim. |
| [BCUninstaller official README](https://github.com/Klocman/Bulk-Crap-Uninstaller/blob/master/README.md) | Inventory installed apps and review potential leftovers in relation to uninstall workflows. | Read-only Add/Remove Programs registrations, publisher/version/source and removal-support hints. Complete in-session before/after registrations allow review of surviving prior install paths only. Missing registrations do not prove uninstall; candidates are never labeled proven junk. Uninstall execution and deeper ownership analysis are queued. |
| [Microsoft Autoruns official documentation](https://github.com/MicrosoftDocs/sysinternals/blob/live/sysinternals/downloads/autoruns.md) | Make source locations visible, distinguish categories and inspect an entry before changing it. | Existing Run/RunOnce and Startup-folder inventory preserves source/unknown publisher and enabled-state limits. Reversible selected enable/disable is queued; services/tasks/extensions remain outside present coverage. No checkbox silently changes startup. |

## Disk-map semantic contract

Question: which observed children account for the known logical bytes beneath the selected folder?

A rectangle's area is its measured logical byte share in the current snapshot. Color separates entries and does not encode risk, repair need, deletion safety or health. The largest 119 positive-size children have individual tiles; remaining measured children form a labeled Other area. Every child remains in the linked list, including zero-size entries. Selected path and exact known bytes are copyable. Clicking selects; drill/up/reset navigates the already collected tree without new filesystem actions. Small tiles have list/button alternatives; resizing does not change the values.

A missing/inaccessible/skipped descendant is unknown, not zero usage. The page visibly reports Partial whenever there are issues/skips/bounds. A bounded scrolling viewport keeps the map/list accessible under long status/path text. This Windows desktop view has no mobile/web deliverable or generated concept image requirement. Windows rendering, accessibility, focus and double-click interactions still require the manual QA matrix.

## Evidence and claims

Official Microsoft ARP/registry-view documentation is linked in INTEGRATIONS.md. Portable tests exercise policy and real temporary-filesystem boundaries. Linux cross-compilation is not evidence of Windows provider correctness, faster startup, more free space, application repair or malware removal. Benchmark comparisons do not establish feature parity with the referenced utilities.
