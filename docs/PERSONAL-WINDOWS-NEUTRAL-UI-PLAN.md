# Proposed neutral UI check on the available Windows computer

**Plan only. No files have been transferred, no project registered, and no MadClean command has run on that computer.** The coordinating task reports Windows build 26300 with .NET 9/10 SDKs and desktop runtimes. This is potentially suitable for a narrowly scoped ordinary-window check; it is not a disposable environment for action testing. Keep the existing cloud task/repository. No GitHub, Actions, cloud provisioning, release ZIP or general deployment is authorized by this plan.

## Scope and startup file access

The exact sole `--local-ui-validation` mode introduced at `0eb4bdd` starts the normal MadClean App/Window, disables that window's input, observes initial/final neutral state, coarse wide/minimum-size bounds, programmatic navigation and normal closure. It invokes no inventory, online check, browser handoff, installer/uninstaller, Defender scan, registry modification, repair or cleanup command. It captures no screenshots or provider fields. Its only intentional tool output is a fresh local report directory/file. DWM dark-title-bar changes concern its own window, not global Windows settings.

**It is not unconditionally isolated from existing MadClean settings.** Normal MainViewModel construction calls the cleanup-exclusion store's Load and the adjacent release-configuration Load:

| Location | Ordinary neutral-run behavior |
| --- | --- |
| `%LocalAppData%\MadClean\cleanup-exclusions.json` | Can read existing MadClean-owned exclusions, which may contain user-selected paths. It does not enumerate those paths or change the file. Missing settings are not created by Load. |
| `madclean-updates.json` beside the loaded App/harness binaries | Can read the bounded deployment setting. The current loader does not perform the exclusions store's no-link check. It makes no release request; omit this optional file and any link at that name from a later approved validation copy. |
| `%LocalAppData%\MadClean\activity.jsonl` / `.previous` | No write is expected during the neutral mode: startup/navigation invoke no Event command, and Activity must remain empty. An unexpected independently started operation could create normal activity records; that would fail the neutral check. |
| `<owned checkout>\artifacts\local-ui-validation\<fresh GUID>\report.json` | Intended write: fixed nullable facts/times/state/optional numeric exception only. No package names, settings contents, inventory, credentials or screenshots. |

Ordinary .NET/WPF may also consult Windows/framework/user preferences and caches as part of displaying any application; this source review is not an exhaustive OS I/O trace. Therefore do not promise zero filesystem access or blanket profile isolation. Under a strict no-personal-file-content condition, first establish (with separately authorized minimal metadata checks only) that the MadClean exclusion file is absent and no optional deployment configuration is in the validation copy. If existing settings are present, **stop and report the condition** rather than read, rename, move, delete or modify them. Do not redirect known folders, change registry/environment settings globally, create another user or bypass production settings through a hidden App mode.

## Required files and prospective invocation

Only after separate transfer/run authorization, use a fresh owned validation directory as a copy of this existing task's relevant development files. Do not clone GitHub, register a replacement project/task, install prerequisites or build/restore on the personal PC. Required contents:

- The **entire** existing `tools/MadClean.WindowsSmoke/bin/Release/net10.0-windows10.0.22000.0/` build tree, preserving subdirectories, DLLs, `.deps.json`, `.runtimeconfig.json`, WinMD and runtime/native dependencies. A single DLL or renamed binary is insufficient.
- The actual repository marker `MadClean.sln` and actual `src/MadClean.App` source directory in the corresponding checkout layout. The mode checks these markers; do not create fake marker files to evade its context check.
- An existing **x64** `dotnet.exe` with compatible .NET 10 `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` frameworks. The checked build's runtime configuration requests version `10.0.0` for both. Architecture and framework resolution still need verification before any proposed run; do not install anything automatically.

Record the source commit and hashes of the copied build/dependency files before and after any approved transfer. These are build identifiers, not signature trust or proof of every loaded byte. Linux-generated apphosts in the current output are not Windows executables; a future approved check must invoke the managed harness DLL with Windows' installed x64 .NET host, **not** the Linux apphost or an installer:

```text
Working directory: the owned copy of the existing checkout
<existing x64 dotnet.exe> tools\MadClean.WindowsSmoke\bin\Release\net10.0-windows10.0.22000.0\MadClean.WindowsSmoke.dll --local-ui-validation
```

This is a proposed command, not an instruction already authorized to execute. Do not set `MADCLEAN_WINDOWS_VALIDATION`, pass additional mode arguments, invoke a CI/native/online qualification, or click application controls. No personal folder is selected. If the neutral invariant fails or pending ownership is unknown, the tool reports failure and retains the owner; do not force cancellation/kill to manufacture success. This plan does not authorize inspecting or terminating unrelated processes.

## Evidence and boundaries

If separately approved and actually run, retain the fixed local report and exact build identifiers. The portable reader can check consistency, not authenticate the report/source or certify the whole application. A pass would qualify only that neutral hosted-WPF run, coarse layout/navigation and normal closure on that machine. It would not validate providers, new installer/MSI reads, a published MadClean executable, physical input/accessibility, mutation behavior or action readiness.

Cleanup/startup/duplicate/shortcut mutation, uninstall/install, Defender scans and repairs still need their own supported contracts and appropriate disposable Windows qualification. Availability of this personal computer does not reopen those gates or authorize personal-PC maintenance. The smallest next step is review/approval of this narrow plan and its settings-access preconditions; until then, retain all files and testing in the cloud checkout.
