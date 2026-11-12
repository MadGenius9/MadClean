# Pinned development build for the neutral VM check

The original handoff below prepared the exact frozen `cd1718b` build. The coordinator subsequently verified its scoped neutral test **passed with exit0** on offline MadClean-Test; it then restored Windows11-Clean-Offline and shut the VM off. [Evidence attribution, report hash and limits](COORDINATED-NEUTRAL-VM-EVIDENCE.md). This cloud task did not run it or receive the report JSON. No current-source/provider/action qualification or permission to run on the host/VM follows. The frozen payload and archive are unchanged.

## Exact source and build identity

- Source commit: `cd1718b8a989e5d21b535a6d17db1d2d09bf1113` on the existing `feat/phase-one-foundation` branch. The tree was clean when the build and manifest were produced. This handoff document is a later documentation-only record; do not confuse its commit with the pinned binary source.
- SDK:10.0.403 on Linux; Release; cached assets, no restore, no GitHub/network/Actions.
- Full solution build: zero warnings/errors. Exact rebuilt assemblies:1,881 Core tests passed, one Windows-only skipped (1,882 total). Earlier same-source Python suite:290 passed, one Windows-only skipped (291 total). These were preparation-time Linux checks; the subsequent limited Windows neutral pass is recorded separately and adds no provider/action qualification.
- Target: `net10.0-windows10.0.22000.0`, x64 for App and harness. Minimum supported neutral route: Windows11 build22000, interactive x64 desktop. The coordinator later reported the scoped Windows11 neutral pass; exact runtime/display values have not been supplied here.
- Original build output (mutable): `/workspace/MadClean/tools/MadClean.WindowsSmoke/bin/Release/net10.0-windows10.0.22000.0/` —20 files, 31405799 bytes. Preserve its entire contents and relative paths.
- App output: `/workspace/MadClean/src/MadClean.App/bin/Release/net10.0-windows10.0.22000.0/MadClean.dll`. Its hash equals the App DLL in the harness directory.
- Full local manifest: `artifacts/neutral-vm-test/cd1718b8a989e5d21b535a6d17db1d2d09bf1113/manifest.json`; SHA-256 `bdf64578325c4e5cdb95cac6651f2315bc44272dadf740d0e681ce5477b07439`. It records all20 build files and111 actual tracked source/context/reader files. It is ignored local development metadata, not a signed artifact or release package.

The stable local development snapshot is `/workspace/MadClean/artifacts/neutral-vm-test/cd1718b8a989e5d21b535a6d17db1d2d09bf1113/payload/`, containing all131 manifest-listed build/source/context files (32,162,491 bytes total) in their original relative layout. Use this frozen snapshot for a later authorized copy; ordinary bin/Release outputs may change during continued development. Every copied byte was verified against the manifest. This is an unarchived local test tree, not a publish, installer or final release package.

The current extensionless `MadClean` and `MadClean.WindowsSmoke` apphosts are **Linux ELF executables**, not Windows EXEs. No Windows apphost executable was produced. Do not rename or invoke them on Windows. The x64 managed PE DLL entry points can instead be launched by Windows' existing x64 .NET host. No additional guest build/restore or publish step is needed for this neutral DLL route once its framework dependencies are present. A separately requested Windows EXE would require a normal win-x64 apphost build and separate identity/validation; none is prepared or claimed here.

| File in harness output | SHA-256 | Observed format |
| --- | --- | --- |
| `MadClean.WindowsSmoke.dll` | `2a814b9ca8254bb2687897140095c45a005860c2115e6cf3c383e9dd47d7f19c` | PE machine 0x8664 |
| `MadClean.dll` | `00998947373fb610966cf2e71eff4e7cd5b46d8e3fa4edba1f98872561f9894a` | PE machine 0x8664 |
| `MadClean.Core.dll` | `f2e849f5f3a08ed1335b0655ea542c76b6426d415c00820f843cd995b3c613f7` | PE machine 0x014c |
| `MadClean.Windows.dll` | `a3e4ce9cdb00cba75fe49e4b055a0934f3e22372bb8063749da9a0e857fe38aa` | PE machine 0x014c |
| `MadClean.Windows.WinGet.dll` | `f826ea546a36705d6e2a32d1b66501243f153c8c48d96244135ce924adeaf604` | PE machine 0x8664 |
| `MadClean.WindowsSmoke.deps.json` | `53309840beff0768a70ffde372611c3b4cf2ff5b013c591e8b8649b5b5ebf3db` | data |
| `MadClean.WindowsSmoke.runtimeconfig.json` | `30d0dc491056efaaa5e3816f68a688b28e32ff6ca4d3bc85c148c1708d23a453` | data |
| `MadClean.WindowsSmoke` | `e2a7532066828d6e74369d62d582e7d4c9af241ba77a5246c1d256725ce2b6c6` | Linux ELF apphost; do not execute on Windows |
| `MadClean` | `43341694f0766e2605601be77b069e059dbecbfee83206c2d8e6b9893fba25ef` | Linux ELF apphost; do not execute on Windows |

Relevant source/reader identifiers (these source files are not alternate execution entry points):

| File | SHA-256 |
| --- | --- |
| `tools/MadClean.WindowsSmoke/LocalUiValidation.cs` | `8be4f00104663e8224280de147b1dd144ced48520ba63b6dbb81225bcc84007d` |
| `tools/LocalUiValidationEvidence.cs` | `5db72e3b91a32117dfac0a6a5278d0593708341b6ccdfa8bfc967d2e655aef23` |
| `tools/read_local_ui_report.py` | `7c175af0c397dc03c4daa2d2fa2379ae430e9670b07aeb0b6a6b1b4cbafe4b94` |

## Required runtime and files

Use an already verified x64 `dotnet.exe` with compatible installed .NET10 `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` frameworks. The runtimeconfig requests10.0.0 for both, with normal framework resolution/servicing rules. The host PC's reported runtimes do not establish guest runtime availability. Runtime installation is not performed or authorized by this preparation. Do not assume that a .NET9 runtime or a Windows SDK supplies .NET10 WPF.

The copied tree must include the harness/App/Core/Windows/WinGet DLLs, both deps/runtimeconfig pairs, all PDBs and bundled dependencies. The manifest includes `Microsoft.Management.Deployment.CsWinRTProjection.dll` from pinned ComInterop1.10.340, `Microsoft.Management.Deployment.winmd`, `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll`. There is no bundled independent antivirus engine or driver installer. No separate native WinGet activation DLL is copied; the neutral route does not activate the package manager. Its ordinary WPF/.NET/Windows UI libraries come from the guest OS and installed desktop runtime. No WinGet installation or package-manager bootstrap is needed or authorized for this neutral check.

Also retain the real `MadClean.sln` and actual tracked `src/MadClean.App` source tree in the matching repository layout. The tool checks those context markers; do not create fake empty markers or register a replacement project. The manifest records the exact files. Do not include `.git`, cloud caches, credentials, personal settings, build obj directories or unrelated host files. The report reader/source files are included for provenance and optional offline report review, not required provider execution.

The proposed guest root below is only a plan: `C:\MadCleanNeutralTest\cd1718b`. The coordinating task must approve and establish a fresh owned ordinary fixed-drive directory with no reparse ancestors before placing files there. An interactive guest display large enough for1200x900 and980x650 WPF logical window bounds is required;1600x1200 at100% guest scaling is a conservative configuration suggestion, not a host setting change or an observed VM fact.

## Narrowest existing check and exact prospective command

After separate approval, transfer/runtime/settings preconditions and coordination, the sole existing `--local-ui-validation` mode checks normal hosted WPF startup, coarse wide/minimum layout, programmatic primary/More-tools navigation and normal close. It disables that window's input. It invokes no cleanup, inventory, online request, browser, installer/uninstaller, repair or Defender scan. No screenshots are captured. A pass is limited to that hosted neutral run, not the published executable, full accessibility, providers or actions.

For the proposed guest root and only if the x64 runtime host is verified at the stated path:

```powershell
Set-Location -LiteralPath 'C:\MadCleanNeutralTest\cd1718b'
& 'C:\Program Files\dotnet\dotnet.exe' '.\tools\MadClean.WindowsSmoke\bin\Release\net10.0-windows10.0.22000.0\MadClean.WindowsSmoke.dll' '--local-ui-validation'
```

This was the prospective command in the original handoff. The coordinator later reported the approved neutral mode ran with exit0; this task has not inspected its actual command/report bytes. Do not invoke the App DLL directly, use `dotnet run`, restore/build on the guest, set `MADCLEAN_WINDOWS_VALIDATION`, add other mode arguments, click controls, or launch a CI/native/online qualification. If paths/runtime differ, coordinate a revised explicit command first. A missing runtime, constrained display or early startup failure is not a passing test.

## Settings, logs and output

Normal App construction can read its own existing `%LocalAppData%\MadClean\cleanup-exclusions.json` and adjacent `madclean-updates.json`. For the narrow check, establish by separately authorized guest metadata checks that the exclusions file is absent and omit the optional release configuration/link from the copied output. The current cloud output has no such configuration file. If guest settings exist, stop and report rather than read, rename, delete, redirect or modify them. No source or provider command is started merely by constructing these loaders.

The intentional write is a newly reserved `C:\MadCleanNeutralTest\cd1718b\artifacts\local-ui-validation\<fresh GUID>\report.json`. It contains fixed nullable observations, UTC bounds, state and optional numeric error; no inventory, settings contents, source identities, command output, secrets or screenshots. No activity/settings write is expected: Activity must remain empty. An unexpected operation fails the neutral invariant and may invoke ordinary activity logging; it must not be hidden or treated as a pass. Normal Windows/.NET/WPF preferences and caches may be accessed by displaying an app, so do not promise zero OS IO.

Unknown/pending ownership retains the model/dispatcher/CLR; do not kill workers or force closure to manufacture success. Exit0 means the neutral report passed;1 failed;2 unsupported/inconclusive. An optional later reader invocation is `python tools/read_local_ui_report.py <actual report path>`; it launches nothing and checks consistency only. Reports are unauthenticated and not intrinsically bound to this source/build. Preserve the manifest and compare copied hashes separately.

## Original private transfer plan — historical

No enabled private upload/VM transfer connector is available in this cloud task. Do not invent a public download URL or claim delivery. The proposed approach is a coordinator-approved **private byte-for-byte file-tree copy**, preserving the manifest layout and verifying every received hash before execution. If the chosen hypervisor supplies a documented guest file-copy facility, the coordinator can verify and use that after authorization; otherwise an authenticated private file channel must be arranged. The actual hypervisor/channel has not been verified here. A raw tree requires no ZIP, GitHub, release upload, public HTTP server or embedded credentials.

Only this development payload should enter a fresh guest-owned directory. Any host-side staging, shared-folder integration or runtime setup must be separately coordinated; no personal host folder is a test target. Only the verified local development snapshot copy was created. No external file transfer, host change, guest setting change, deployment, VM use or external upload has occurred in this task. Action qualification remains separately gated even when the VM becomes ready. Existing CI-bound native qualification modes are not implicitly approved for this VM; never fabricate GitHub/CI environment context to activate them.
