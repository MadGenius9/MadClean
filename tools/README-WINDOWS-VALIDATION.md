# Disposable Windows validation harness

`MadClean.WindowsSmoke` is a development/CI executable, separate from MadClean's production UI and providers. It requires Windows with the .NET/WPF runtime and an explicit opt-in. Run **only in a disposable Windows runner/VM**, from the existing repository root. It is not a personal-PC inventory tool or a servicing/repair test.

```powershell
$env:MADCLEAN_WINDOWS_VALIDATION = '1'
dotnet run --project tools/MadClean.WindowsSmoke -c Release --no-build
```

The coordinator adds the project to the solution/build and locked restore workflow. This document does not imply that Windows execution has occurred. Linux can cross-compile it; Linux cannot execute WPF or pass this Windows gate.

## What it does

1. Loads the real production `App` with an assembly-qualified `StartupUri`, lets WPF create its single startup `MainWindow`, waits until application-idle before inspecting it, and uses software WPF rendering inside this process. It does **not** inject sample values, run UI commands, query providers through the UI, click updates or start maintenance.
2. Requests actual WPF **client content** at 1420×1000 and 980×650 window sizes, verifies observed bounds, and keeps constrained captures inconclusive. Its sizing helper changes only the already shown window’s own maximum tracking bounds/size, never global display settings. The modified native limits are passed to WPF’s original handler so its layout cache observes the same actual bounds; a successful API return alone cannot pass the wide check. It checks the primary controls' visible bounds, exercises actual primary/More tools selection bindings, opens the real tool popup, visits all secondary pages and captures H1/new module pages. It asserts navigation generated no inventory activity. Physical keyboard/screen-reader behavior, true OS-DPI scaling, native DWM/title-bar composition and visual fidelity still need separate checks.
3. After closing WPF, clears the closed dispatcher synchronization context and creates a uniquely named, ownership-marked fixture tree under repository `artifacts/windows-validation-fixtures-<GUID>`. Fixture files are intentionally test data; they are never injected into the production UI.
4. Uses the public duplicate adapter on independent equal-content files, a different file with the same size, and (where supported) a real native hard-link alias. Checks content/identity relationships without a reclaimable-space or safe-delete claim.
   Separately, `disk-identity-fixture` creates a real `DiskUsageScanner` map of three independent equal-size files (two have equal content) and an actual hard-link alias. It checks the public native identity adapter returns four path observations, three independent identities, correct alias/link counts and distinct path/unique logical totals. A fixture-only byte audit checks unchanged contents; the accounting adapter itself is metadata-only. Reports export no paths, file IDs or contents. Failed hard-link setup or incomplete native coverage stays inconclusive; no allocation/reclaimable-space/content-deduplication claim follows.
5. Creates two local `.lnk` fixtures through system Windows Script Host COM **for development setup only**, targeting a fixture text file and an absent fixture path. It never launches or resolves links, creates network-target fixtures or activates installers. Production S1 remains read-only. It tests expected local availability states and records unsupported/access/metadata uncertainty explicitly.
6. Explicitly queries the disposable runner's Application/System event **metadata** for one day. It checks window/channel/recurrence/source-uncertainty invariants. It does not collect event messages/properties, raw report contents, Security logs, dumps or repair output, and no-event results are never health verdicts.
7. Separately reads the disposable host’s projected H1 hardware inventory to validate source/property schema and enumeration-completeness metadata. It exports only fixed states/counts/booleans, never actual hardware/identity/property values, and does no manufacturer matching, online-source check or hardware action. A complete PC source is required for this narrow metadata proof; it is not proof of accurate identity or compatible parts.
8. Creates inert test registrations only under ownership-marked `HKCU\Software\MadClean.WindowsSmoke\<same fixture GUID>`, never real startup keys. Verifies read-only inventory/type/byte-count previews, fixture scope rejection, unconditional mutation gates and unchanged bytes/journal/files. Removes only that exact owned registry subtree; this proves no disable/restore, TxR isolation or durable recovery.
9. Verifies the system-tool provider’s immutable execution gate, then submits a dummy acknowledged request that must return before identity, logging, UAC or process creation. It never executes DISM/SFC or enables the gate.
10. Removes only its ownership-revalidated fixture tree, refusing reparse ambiguity. No production cleanup/delete/uninstall action is tested. Provider calls have bounded cancellation/deadlines; the entire harness has a four-minute watchdog.

The separately invoked D1b WER probe reads only the disposable host's existing machine queue/archive through `WindowsWerMetadataInventory`, with a 30-day creation-time window. `wer-store-read-contract` verifies fixed sources, UTC bounds, minimized GUID/time observation schema and bounded counts. Report evidence contains only states/counts/booleans and fixed explanations, never the GUIDs, creation dates, coverage text, signatures, keys, report paths or contents. Existing usable metadata verifies only that narrow returned path. Completed accessible empty stores can pass the store-read contract while explicitly recording `populatedMetadataVerified=false`: populated-buffer marshalling, complete history and crash/health conclusions remain unverified. Missing/denied/unsupported/incomplete store reads are inconclusive. No report is created, submitted, archived, deleted or exported; no forced crash, dump read or store policy change occurs. Native calls remain cooperatively cancellable only, under the provider's existing bounded caller wait and single-worker gate.

`wer-v1-managed-layout` independently checks CLR sequential layout against documented 64-bit V1 offsets. It is **not** a native SDK ABI proof. The compile-only [WerAbiAssertions.cpp](MadClean.WindowsSmoke/WerAbiAssertions.cpp) separately asserts the actual installed Windows SDK header layout and store enums. From an **x64 Windows SDK developer command prompt** at the repository root, create an owned `artifacts/windows-validation-abi` folder and compile without linking or executing:

```text
cl /nologo /c /W4 /WX /Fo:artifacts/windows-validation-abi/WerAbiAssertions.obj tools/MadClean.WindowsSmoke/WerAbiAssertions.cpp
```

Record SDK/compiler version and actual command outcome separately. The managed JSON check never claims this optional compile-only command ran. The declarations were checked against Microsoft's current [official SDK WerApi.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/WerApi.h), [V1 metadata documentation](https://learn.microsoft.com/windows/win32/api/werapi/ns-werapi-wer_report_metadata_v1) and existing `docs/WER-METADATA.md`. A static assertion passing cannot prove a populated live report response, lifetime races or cancellation inside a synchronous native call.

The hosted-runner entry point discovers the installed latest Visual Studio x64 tools using the standard Installer `vswhere.exe`, initializes that installation's `vcvars64.bat`, and invokes its exact default Hostx64/x64 compiler. It downloads nothing and compiles `/c /W4 /WX` to `artifacts/native-abi/WerAbiAssertions.obj`; that object is not a distributed artifact. Its fixed system-PowerShell wrapper accepts no command input:

```text
python tools/ci_command.py native-abi -- python tools/compile_wer_sdk.py
```

Compiler discovery/configuration/exit failures fail closed. This separately recorded command must actually pass before claiming native installed-SDK ABI proof; it does not change the scope of `wer-v1-managed-layout`.

## Evidence and exit states

Outputs are fixed beneath `artifacts/windows-validation`:

- `report.json`: `formatVersion:1`, validated `sourceCommit` from `GITHUB_SHA` (or `Unknown`), timestamps, overall `passed`/`state`, individually named checks, and a screenshot filename/dimension/SHA-256 manifest. Details are bounded and contain no raw provider payload, user paths or unique machine/file identifiers.
- `overview-wide.png`, `overview-compact.png`, and `tool-NN.png`: actual client-content renders. Only captures listed with a hash in the **current** report are evidence for that run. A PNG alone, compilation or a source commit does not prove all behavior is verified.

Exit 0 means all implemented scoped checks passed; it does not certify the whole roadmap or personal-PC safety. Exit 1 means a failed check or harness error. Exit 2 means an unavailable/unsupported/incomplete native verification (or no opt-in): **inconclusive is not a pass**. A Partial provider result can still verify a particular fixture relationship when the returned evidence supports it; incomplete coverage remains stated. Failures and provider/access uncertainty remain distinct in the JSON report. The harness never turns a returned PID, empty list, missing artifact or suppressed exception into success.

CI should upload the report and captures even on failure. If publishing them to the private verification branch, preserve the report's source commit and screenshot hashes; never publish secrets, raw provider output or a public release as part of this harness.

Roadmap-development validation does not require a distribution ZIP. Keep build, portable tests, compile-only ABI validation where configured, and native/UI evidence enabled on ordinary pushes; leave publish/compress/distribution-upload steps conditional on a separately selected `workflow_dispatch` package input (default false). Development report/test artifact transport is separate from packaging the application for delivery. Do not treat an old executable/ZIP as an artifact of the current validation run.
