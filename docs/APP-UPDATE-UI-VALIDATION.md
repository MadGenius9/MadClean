# Actual application update-discovery workflow

The separate `--app-update-ui-validation` development mode qualifies MadClean's real Check updates control after adopting the structured WinGet provider. It requires the exact disposable GitHub-hosted Windows x64 job, repository, feature branch, source/run/attempt and validation environment. The ordinary passive harness only reads its completed report; that read cannot start a provider query.

The helper starts the actual WPF App and MainWindow on an owned foreground STA. It checks the neutral model, selects the actual Updates navigation item, locates the real toolbar button and verifies its UpdatesCommand binding. Its automation peer invokes that control. No fake provider, injected result, modified collection or direct SDK call substitutes for the application workflow.

After invocation it waits for the actual model's busy and pending owners to settle. The typed AppUpdateLastObservation must come from the real provider, postdate the invocation and satisfy the shared observation policy. The real app-update grid must retain its model binding and match actual returned counts and per-row Available/Unknown totals. The visible state, count, checked-UTC and ownership fields must match their model bindings. The helper requests normal window close only after settlement and observes the actual Closed event. An exception after invocation still waits for ownership before shutdown; there is no process/server kill or invented cancellation.

Check updates also starts the existing Windows Update read-only discovery. The helper waits for the complete App busy lifetime but qualifies only the structured app-update workflow. No software/driver installation, source agreement acceptance, credential flow, browser navigation, MSI request, repair or restart is invoked. No screenshot is captured after real provider data loads.

## Fixed evidence and acceptance

The bounded version-1 private report is published atomically without overwrite under `artifacts/app-update-ui-validation/report.json`. It contains exact source/run/attempt context, UTC observation bounds, nullable actual control/invocation/result/freshness/rendered-count/settlement/window-closure observations, an exception HRESULT when observed, and closed typed query state, counts, pending status and diagnostic fields. It exports no package names, IDs, versions, source URLs, provider messages, startup commands, user identifiers or raw Windows data.

The app observation is not the SDK-only pre-cancellation probe: this report makes **no pre-cancellation assertion**. Its diagnostic projection carries only closed stage/outcome names, an optional HRESULT, actual attempt/completion times, bounded counts and nullable source/agreement/authentication/Connect/Find/limit facts. Readback rejects unknown or duplicate fields, invalid types/enums, inconsistent counts, context or time mismatch, and contradictory pass claims.

A pass requires the real app result to be Available or Partial with all supported source/control facts established, a completed native query, matching actual rendered counts and a settled, closed window. Partial stays Partial. Unknown rows, gaps and empty results never become all-software coverage or Up to date. Provider unavailability, offline/policy failures or missing evidence remain inconclusive; an observed UI invariant/invalid typed result fails qualification. Client/worker completion does not establish that the shared WinGet server, Windows Update services or provider background cache work stopped.

## Remaining validation

This workflow requires its own source-matched Windows result. SDK-only development/published-harness success and neutral MadClean executable startup do not qualify this app-control flow. Linux compilation cannot establish WPF interaction, actual source access, cancellation or physical accessibility/DPI behavior. The actual published MadClean executable's selected update workflow is a separate validation increment; the in-process helper does not claim to have exercised that executable path. The test reports no performance benchmark or installation eligibility.
