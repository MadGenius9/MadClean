# Separate read-only update-discovery validation

The fixed `--update-discovery` WindowsSmoke mode observes the existing actual WinGet and Windows Update providers on a disposable Windows CI runner. It is separate from the ordinary GUI/native harness and its four-minute deadline. This class is not invoked by inventory, app startup or normal smoke navigation; Program/workflow wiring invokes it as a separate process and passively merges its source-bound result. No network request was made while implementing this fixture.

This mode validates provider observations, **not populated Apps/Drivers UI selection**. It uses no sample offers, private view-model injection, WPF window or screenshots. An actual empty Windows Update result can establish only that this configured search returned no offers. WinGet's parser keeps empty/unrecognized/localized listings Unknown. Offline, missing App Installer alias, denied source agreements, policy, timeouts and failures retain their genuine states and produce inconclusive evidence rather than an application-invariant failure or fabricated success. Nonempty results are not package-authenticity, hardware-compatibility or installation-readiness proof.

## Exact queries and side effects

There is one call each to the existing `WindowsInventory.AppsAsync` and `SystemUpdatesAsync`:

- WinGet uses the current-user App Installer execution alias and fixed `list --source winget --disable-interactivity`, with its existing 90-second provider deadline. No agreement-acceptance, source-add/change, upgrade, installation or asset-download option is added. Existing source agreements must already permit the query; the fixture never accepts them.
- WUA uses fixed system Windows PowerShell and the existing `Microsoft.Update.Session` / `Search("IsInstalled=0 and IsHidden=0")`, with its existing 180-second provider deadline. Configured Microsoft/managed service selection and policy remain unchanged. No downloader, installer, repair, firmware file, elevation or restart is requested.

Windows and WinGet can contact configured servers and refresh normal provider metadata/cache state. This is not an offline or zero-write operation. The fixture introduces no custom package/identity upload and does not claim that provider-controlled traffic transmits no inventory metadata. Microsoft/provider behavior remains separate from MadClean's minimized evidence output.

The existing query runner bounds each captured stdout/stderr stream to 4,194,304 characters. It captures output privately for parsing and never forwards it into this report. The new projection inspects at most 20,000 package rows or 256 WUA rows; excess or inconsistent typed results are not accepted as source coverage. The existing WUA producer is not claimed to have a new per-record native allocation cap; this fixture does not change that producer or its supported interfaces.

## Pending ownership and cancellation

Both actual provider tasks are started through `Task.Run(..., CancellationToken.None)`, so their synchronous prefixes, including process startup, remain owned by worker tasks. A static owner retains the inventory, linked cancellation source, both actual tasks and any asynchronous cancellation request. A 210-second observation wait is followed, on timeout/caller cancellation, by a cancellation request and at most ten seconds of additional observation. It never forces a native handle closed or substitutes a partial result as complete.

The existing `ReadOnlyProcess` cancellation attempts to stop its **own read-only query host/tree**, then awaits its direct child's exit in `finally`; that final wait has no independent deadline. If startup, cancellation callbacks or exit waiting cannot settle, tasks remain rooted and the report says Pending. Cancellation is initiated asynchronously so a blocked stop callback does not prevent reporting. No servicing process, Windows Update service, TrustedInstaller or unrelated process is targeted by this fixture. Ending a provider task is not proof that Windows Update/provider services or their background metadata work stopped.

No further query, fixture or cleanup follows in this standalone mode. An external job timeout can end the disposable runner; it is not cancellation, service completion or result proof. No production execution gate changes.

## Evidence and merge boundary

Output is fixed to `artifacts/update-discovery-validation/report.json`, outside the standard `artifacts/windows-validation` evidence directory. This report is at most 8 KiB, with exact versioned fields, strict lowercase 40-hex source commit, bounded decimal run ID and positive canonical run attempt, canonical UTC start/end, task outcomes, source states, schema acceptance and coherent nullable counts. Unreturned observations keep counts null. Zero is retained only as an observed typed count.

The writer checks fixed local repository/ordinary ancestry and reparse status before creation/write, refuses stale `report.json` or `report.json.new`, writes the temporary file with CreateNew, flushes and moves without overwrite. It never repairs, resets or overwrites existing evidence. These checks are not a hostile-user namespace or recorded-authorship guarantee.

`ReadIntoReport` is existing-only and never starts queries. It requires ordinary held directory/file leases, exact JSON fields with duplicate-field rejection, at most 8 KiB, exact source/run/attempt match, valid canonical UTC times (duration at most four minutes and merge age at most ten minutes), fixed task/state values and count coherence. Malformed, absent, stale, mismatched or pending evidence becomes one inconclusive `update-discovery-provider-contract` check. Both returned, accepted Available/Partial providers are required for a positive provider-contract check, including a genuine zero-offer WUA result. Failed/offline providers are never metadata success.

Only fixed task/source-state labels and counts enter the standard report. No raw provider messages, package names/IDs/versions/source labels, update identities/descriptions, paths, environment values, credentials or screenshots are merged. The artifact's source/run identifiers bind development evidence; they do not authenticate provider content. No healthy/up-to-date verdict or populated UI proof is made.

Return codes are 0 for observed positive source contracts, 2 for inconclusive/pending observations and 1 for this fixture's own preparation/report invariant failure. Workflow handling must retain valid inconclusive reports without converting ordinary offline/provider failures into application bugs. Actual execution evidence remains pending until the separately integrated mode runs on Windows.


Actual source39d5fad/run41478096814, evidenceab9e7627efca51919a997f04d0878ba0b5be852c: WinGet returned Unknown with accepted schema and zero rows; WUA returned Available with three software offers and zero driver/firmware/unclassified rows. The observation wait did not expire. The combined provider check remains inconclusive because both sources were not positive. Stage exit2 and no raw output were observed. No offer names/identities, installation, source agreement, populated UI selection or Windows-service completion proof is exported or inferred. Driver/firmware zero is only this configured search result, never Up to date.


Version2 evidence adds optional-metadata coverage counts with exact source/run/attempt binding and a separate required wua-offer-metadata-contract result. Actual field values stay private. Both the existing source check and optional metadata check retain independent outcomes; software rows do not validate driver-only getters or populated driver/firmware UI. [Field semantics and current limits](WUA-OFFER-METADATA.md). New runtime observations are pending.
