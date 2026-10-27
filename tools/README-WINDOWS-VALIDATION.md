# Disposable Windows validation harness

`MadClean.WindowsSmoke` is a development/CI executable, separate from MadClean's production UI and providers. It requires Windows with the .NET/WPF runtime and an explicit opt-in. Run **only in a disposable Windows runner/VM**, from the existing repository root. It is not a personal-PC inventory tool or a servicing/repair test.

```powershell
$env:MADCLEAN_WINDOWS_VALIDATION = '1'
dotnet run --project tools/MadClean.WindowsSmoke -c Release --no-build
```

The coordinator adds the project to the solution/build and locked restore workflow. This document does not imply that Windows execution has occurred. Linux can cross-compile it; Linux cannot execute WPF or pass this Windows gate.

## What it does

1. Loads the real production `App` with an assembly-qualified `StartupUri`, lets WPF create its single startup `MainWindow`, waits until application-idle before inspecting it, and uses software WPF rendering inside this process. It does **not** inject sample values, run UI commands, query providers through the UI, click updates or start maintenance.
2. Captures actual WPF **client content** at the default 1420×1000 and minimum 980×650 window sizes. It checks the primary controls' visible bounds, exercises actual primary/More tools selection bindings, opens the real tool popup, visits all secondary pages and captures H1/new module pages. It asserts navigation generated no inventory activity. Physical keyboard/screen-reader behavior, true OS-DPI scaling, native DWM/title-bar composition and visual fidelity still need separate checks.
3. After closing WPF, clears the closed dispatcher synchronization context and creates a uniquely named, ownership-marked fixture tree under repository `artifacts/windows-validation-fixtures-<GUID>`. Fixture files are intentionally test data; they are never injected into the production UI.
4. Uses the public duplicate adapter on independent equal-content files, a different file with the same size, and (where supported) a real native hard-link alias. Checks content/identity relationships without a reclaimable-space or safe-delete claim.
5. Creates two local `.lnk` fixtures through system Windows Script Host COM **for development setup only**, targeting a fixture text file and an absent fixture path. It never launches or resolves links, creates network-target fixtures or activates installers. Production S1 remains read-only. It tests expected local availability states and records unsupported/access/metadata uncertainty explicitly.
6. Explicitly queries the disposable runner's Application/System event **metadata** for one day. It checks window/channel/recurrence/source-uncertainty invariants. It does not collect event messages/properties, raw report contents, Security logs, dumps, hardware identities or repair output, and no-event results are never health verdicts.
7. Removes only its ownership-revalidated fixture tree, refusing reparse ambiguity. No production cleanup/delete/uninstall action is tested. Provider calls have bounded cancellation/deadlines; the entire harness has a four-minute watchdog.

## Evidence and exit states

Outputs are fixed beneath `artifacts/windows-validation`:

- `report.json`: `formatVersion:1`, validated `sourceCommit` from `GITHUB_SHA` (or `Unknown`), timestamps, overall `passed`/`state`, individually named checks, and a screenshot filename/dimension/SHA-256 manifest. Details are bounded and contain no raw provider payload, user paths or unique machine/file identifiers.
- `overview-wide.png`, `overview-compact.png`, and `tool-NN.png`: actual client-content renders. Only captures listed with a hash in the **current** report are evidence for that run. A PNG alone, compilation or a source commit does not prove all behavior is verified.

Exit 0 means all implemented scoped checks passed; it does not certify the whole roadmap or personal-PC safety. Exit 1 means a failed check or harness error. Exit 2 means an unavailable/unsupported/incomplete native verification (or no opt-in): **inconclusive is not a pass**. A Partial provider result can still verify a particular fixture relationship when the returned evidence supports it; incomplete coverage remains stated. Failures and provider/access uncertainty remain distinct in the JSON report. The harness never turns a returned PID, empty list, missing artifact or suppressed exception into success.

CI should upload the report and captures even on failure. If publishing them to the private verification branch, preserve the report's source commit and screenshot hashes; never publish secrets, raw provider output or a public release as part of this harness.
