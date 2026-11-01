# Explicit selected-shortcut recheck

After a separately requested selected-folder shortcut scan, select one row and choose **Recheck selected shortcut**. This reads only that `.lnk` path's current guarded metadata and, when supported, local target availability. It does not enumerate siblings or rescan the folder. The original row remains a historical observation; a separate panel shows the prior and new evidence with source/time/coverage.

The Core factory binds an exact entry reference from one bounded Available/Partial report to a normalized `.lnk` strictly beneath the selected local root. Equal-looking stale clones, neighboring paths, malformed Unicode, relative/network/device scopes, missing source/time and failed inventories cannot produce a scope. All prior observation states can be rechecked: accepting a selection is permission for another read only, never an obsolete-shortcut or removal verdict. Partial source coverage remains Partial. The immutable scope copies prior evidence; it does not establish that the same file object still occupies the path.

Windows folder discovery and the selected read share one process-wide STA worker gate and the same per-link reader. Current root/selected ancestry and read leases remain worker-owned through the supported Shell metadata and guarded target checks. The 1 MiB link bound and 60-second caller wait remain. Cancellation/timeout stops further work and returns no completed selected observation; synchronous native calls may finish later, holding the gate until cleanup. Successful worker completion is published after COM cleanup and gate release. No forced thread termination, caller handle closure or overlapping worker is introduced.

The reader still uses `IPersistFile.Load` with read-only access, `IShellLinkW.GetPath` with `SLGP_RAWPATH`, and stored flags. It never calls Resolve, target launch, icon retrieval, MSI activation, search/tracking/save, network authentication or removal. Missing/inaccessible shortcut files and roots produce Unknown target evidence, never a missing-target verdict. Offline/network/access/unsupported target contexts stay unverified. A newly existing current stored target makes an earlier missing observation historical; it does not prove that the original target or shortcut object is unchanged.

Changing selection, scope or inventory invalidates prior recheck display. Generation/reference guards discard results and errors from an older selection while its native worker retains ownership. The in-memory folder snapshot is not rewritten into a fresh complete baseline. Only operation/outcome/time enums enter the activity log; no paths, target text, provider messages or shortcut values are persisted or exported.

This advances read-only S1 review; S2 removal remains absent. Exact fresh entry/target evidence, object binding, recovery limitations and separately selected reviewed actions still need their own implementation and Windows validation. A restored target must clear a future stale missing-based action, but no action is enabled by this feature.

## Validation

Portable boundaries cover reference/provenance/counter/scope/time/cancellation and malformed Unicode that must not reach a target probe. An isolated native check reuses the existing owned shortcut snapshot, reads its missing target, creates only that exact previously absent test target to simulate an external restoration, and reads the same selected path again. It checks the historical snapshot and `.lnk` bytes remain unchanged by rechecks. No fixture data enters the production UI. A separate genuine initial `tool-23.png` capture checks the empty, disabled recheck controls before populated metadata. Actual source-bound runtime results belong in the execution ledger; compilation does not prove them.

## Current official semantics

These Microsoft references were freshly retrieved from the official MicrosoftDocs SDK source on 2026-11-01 before this integration. No new COM/native API is added; the existing read-only methods are reused:

- [IPersistFile.Load](https://learn.microsoft.com/windows/win32/api/objidl/nf-objidl-ipersistfile-load): existing STGM read-only/deny-write access request.
- [IShellLinkW.GetPath](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-getpath): raw stored path rather than contextual resolution.
- [IShellLinkDataList.GetFlags](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkdatalist-getflags): stored flags and genuine HRESULT handling.
- [IShellLinkW.Resolve](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-resolve): moved/renamed-target search, tracking, update and installer behavior remains excluded.
