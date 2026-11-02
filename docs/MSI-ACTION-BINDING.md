# Selected MSI context binding

This explicit read-only preflight refreshes covered ARP metadata and enumerates the exact selected product code across current-user managed/unmanaged and machine contexts (mask7). The supported MsiEnumProductsExW/MsiGetProductInfoExW path uses NULL current-user SID input, no output SID buffers, fixed field buffers and the shared MSI worker gate. It reads no uninstall command or package path and invokes no installer, activation, configuration or elevation API.

An observed Bound result requires one complete matching installed context and fresh exact selected registration metadata, known unrestricted NoRemove, product/name/version/publisher continuity and the selected context. Additional contexts, restrictions, changed facts, advertisements and mismatches block it; incomplete or unknown coverage stays Unknown. Bound means observed selection/context correspondence only. CanExecute is always false; neither context uniqueness nor matching text establishes package authenticity or uninstall permission.

ARP and MSI queries are sequential, non-atomic observations. Current-user plus machine coverage is not an all-user/global absence statement. Partial source coverage remains visible. Selection changes, refreshes, stale scopes and cancellation invalidate UI results; stopping the caller does not stop a synchronous worker. The shared gate prevents overlapping old/new MSI lookups until worker-owned resources finish.

The actual Windows contract fixture performs genuine registration queries only, exports fixed states/counts and never seeds registry entries or executes an installer. Missing suitable real coverage stays Unknown. Windows runtime evidence remains pending. This preflight is a prerequisite for later reviewed actions, not an action executor.

Official supported API references and bounded metadata rules are shared with the existing MSI registration reader: [MsiEnumProductsExW](https://learn.microsoft.com/windows/win32/api/msi/nf-msi-msienumproductsexw) and [MsiGetProductInfoExW](https://learn.microsoft.com/windows/win32/api/msi/nf-msi-msigetproductinfoexw).
