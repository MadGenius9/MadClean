# Actual published MadClean update workflow

This separate disposable Windows job exercises the actual self-contained `MadClean.exe`, not only the SDK library or an in-process WPF window. It reuses the existing [owned startup publication](PUBLISHED-STARTUP-VALIDATION.md) preparer, source-bound descriptor, fresh marked scope, leased executable, bundled-runtime observation, own-process UI association, graceful close and bounded owned teardown. A fresh job/VM keeps its descriptor separate from ordinary passive startup validation.

The default `PublishedExecutableStartupFixture.RunAsync` remains neutral: it invokes no query or navigation. Only the explicit Smoke-host `--published-app-update-validation` mode may select `RunAppUpdateQueryAsync`. No switch, environment permission override, fake provider or test endpoint is added to the published application. No ZIP or executable distribution is produced.

## Actual product controls and observations

After observing the real neutral Overview and bundled runtime, the query owner selects the actual Updates navigation item and invokes the actual `AppCheckUpdates` button. That product command requests read-only WinGet and Windows Update discovery; provider-owned metadata/cache/network work may occur. It requests no source agreements, account, installation, uninstall, elevation, firmware change or restart.

The observer reads only the fixed product status controls: query state, Available/Unknown/catalog counts, last attempt UTC and pending-query status. It reads no package rows, names, identifiers, versions or provider messages and takes no captures after loading real data. A positive observation requires Available or Partial, canonical bounded counts whose sum matches the catalog count, a new UTC attempt after this invocation, the actual idle ownership status and a still-associated live owned window. Zero rows never establish that every application is current; Partial remains Partial.

Failed, Unknown, Unavailable, Unsupported and Cancelled are observed but unqualified results, with no positive counts inferred. They do not wait out a timeout once the actual query ownership has settled. Reading an incomplete or malformed UI snapshot cannot produce a successful result.

The fixed metadata WinMD sidecar is separately held read-only, validated by final path/ordinary attributes and bounded to 1 MiB for the same child/query lifetime. This is runtime metadata from the pinned SDK, not an injected native activation factory.

## Ownership and stopping

The explicit query worker has a four-minute cooperative observation budget; the old neutral worker/caller bounds are unchanged. The query entrypoint awaits its actual worker rather than abandoning a background owner when a caller deadline expires. UI Automation or a native operation may delay reaching a cooperative checkpoint; the outer disposable CI VM limit is independent.

Once invoking the update command has been attempted, cleanup keeps the exact spawned process and retries only its associated window's WM_CLOSE while waiting for actual exit. MadClean's native-query close guard can refuse that request until its real worker finishes. The fixture never kills the app, WinGet server, Windows Update or a service, and never removes artifacts below pending work. A timed-out or failed observation is not later promoted to success just because closure eventually settles. If window association, posting or exit observation fails, the first uncertainty is retained and the worker still waits for the exact owned process to actually exit; it cannot return and drop active process/lease ownership. A disappeared window after an already successful close request can be ordinary shutdown and is never a reason to post elsewhere. Conservative inconclusive reports may retain positive historical facts, but those facts do not override the report's unqualified state.

## Version-one report

The bounded atomic report contains only source/run/attempt, observation times, fixed state/stage, nullable native error, existing startup/runtime/close/cleanup proof, the new requested-update-command count, nullable query facts, a closed result-state label, nullable counts and the actual displayed attempt UTC. It does not serialize the neutral fixture's legacy zero-invocation getters as evidence that this query mode made no calls. The exact schema is defined by `PublishedAppUpdateQualification.Observation` and independently checked by the evidence publisher.

A pass requires the neutral and runtime proof, metadata lease, actual command, fresh positive query/counts, settled query ownership, actual owned client exit zero, confirmed cleanup and no retention. Missing files, failed jobs, malformed reports, stale context or compilation alone cannot qualify it. Standard-user/policy/offline and mid-native cancellation scenarios require their own applicable evidence.

This implementation has not yet run on Windows. Local compilation and portable tests are not a published-query result.


## Observed qualification

Source `b670db51`, immutable evidence `d257f165b9a02b5b3a66c19eb09e741c0b015cb3`, [run 41784447861](https://github.com/MadGenius9/MadClean/actions/runs/41784447861): the separately owned actual published MadClean executable passed this workflow. It reported Partial with 68 rows, 19 Available and 49 Unknown after the real update command; runtime/metadata ownership, fresh result, settled query, graceful window close, actual exit zero and owned cleanup were observed. No retention remained. This is development runtime evidence, not signing, distribution, complete app coverage or all Windows-policy qualification. No executable artifact or ZIP was distributed.
