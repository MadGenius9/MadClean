# MadClean

A personal Windows desktop utility for understanding storage, startup entries, Microsoft Defender status, and software/driver update offers. **Phase 1 is read-only inventory.** It never deletes files, changes startup settings, starts antivirus scans, installs updates, elevates, or restarts Windows.

## Stack and architecture

C# / **.NET 10 LTS**, WPF, and a small MVVM presentation layer. WPF provides a maintainable Windows-native desktop surface without a web runtime. The verified cloud SDK is **10.0.403**; `global.json` pins that feature band and allows later servicing patches. No third-party runtime UI or integration libraries are required.

- `src/MadClean.App`: dark WPF UI, navigation, dashboard, background-operation orchestration and cancellation. Default state is unchecked; all production data comes from Windows. No sample/design data is injected.
- `src/MadClean.Core`: platform-independent models, conservative filesystem inventory, WinGet table parser, bounded cancellable process execution, and privacy-limited activity log.
- `src/MadClean.Windows`: read-only registry, disk, Windows PowerShell/Defender, WinGet, and Windows Update Agent adapters.
- `tests/MadClean.Tests`: core boundary tests. Windows-only runtime behavior is tracked separately in [validation](docs/VALIDATION.md).
- `.github/workflows`: Windows build/test/package workflow. No automatic release, install, signing, or deployment.

The app requests **asInvoker**, not administrator privileges. It reports inaccessible data rather than silently elevating. Disk space is available to the current user, not necessarily total free bytes under disk quotas.

## Build and run

Requirements: a supported Windows 11 x64 installation, .NET SDK 10.0.403 or a compatible servicing patch, and Internet access to NuGet for the first restore. Windows PowerShell 5.1 and the local Defender provider are needed for security status; Microsoft's App Installer / WinGet and configured source agreements are needed for app discovery. Organizational policy may restrict any provider. MadClean does not accept agreements for you.

From the existing checkout root:

```powershell
dotnet --info
dotnet restore MadClean.sln --locked-mode
dotnet build MadClean.sln -c Release --no-restore
dotnet test tests/MadClean.Tests -c Release --no-build
dotnet run --project src/MadClean.App -c Release --no-build
```

Click **Scan local** for disks, cleanup inventory, startup inventory and Defender status. Click **Check updates** to query WinGet and Windows Update. Online checks may refresh provider metadata and contact configured sources. **Cancel** stops unfinished inventory and terminates query child processes; provider-owned Windows services may finish an already-dispatched read-only search. Completed results remain timestamped. Switching tabs does not restart checks.

### Produce a Windows executable

```powershell
dotnet publish src/MadClean.App/MadClean.App.csproj -c Release -r win-x64 --self-contained true -p:MadCleanPackage=true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true -o artifacts/win-x64
Get-FileHash artifacts/win-x64/MadClean.exe -Algorithm SHA256
```

Normal development uses `packages.lock.json`; single-file `win-x64` packaging uses separate committed `packages.win-x64.lock.json` graphs, since the SDK adds runtime/linker build tooling. Both commands restore in locked mode. Refresh these graphs intentionally when servicing the SDK or dependencies.

`artifacts/win-x64/MadClean.exe` is an **unsigned**, self-contained Windows x64 executable. WPF native dependencies extract under the normal .NET bundle directory on first launch. Do not enable trimming for this reflection/binding-based WPF app. The [Windows workflow](.github/workflows/windows.yml) builds, runs the core tests and uploads a ZIP plus SHA-256 manifest. It must actually run before a CI artifact exists. Compilation/packaging alone does not validate executable behavior or Defender/WinGet/WUA integration. Signing and distribution are later release tasks; SmartScreen may flag unsigned builds.

On Linux, install the pinned SDK, use a writable `DOTNET_CLI_HOME` and `NUGET_PACKAGES` if home is read-only, then run the same restore/build/test commands. Windows targeting packs allow compilation; **WPF cannot run on Linux**. Do not substitute mock values and call Windows checks verified. No web server, database, API key or personal-PC access is needed to build.

## Safety and scope

See [integration contracts](docs/INTEGRATIONS.md), [validation limits](docs/VALIDATION.md), and [agent rules](AGENTS.md).

- Cleanup enumerates only `%LocalAppData%\Temp` and `%WinDir%\Temp`. It inventories old readable files, reports categories/bytes/exclusions/errors, and makes **no deletion-safety guarantee**. Downloads, documents, saves, credential stores and browser profiles are not supported cleanup roots. Recent files, known protected names/extensions, encrypted/offline/system files, locked files and reparse points are skipped. No browser cache/profile sweep is implemented in phase 1.
- Startup coverage is Run/RunOnce in HKCU/HKLM (32/64-bit views), plus the user/common Startup folders. These are registered entries, not proven enabled executions. Publishers and shortcut targets remain unknown; command lines are deliberately not collected. Services, scheduled tasks and packaged startup tasks are not covered.
- Defender results are actual provider flags and recorded detection IDs/actions. No new malware scan runs. An empty history does not mean a malware-free computer. MadClean is not an antivirus engine.
- WinGet matches only its configured `winget` source; unmapped installed apps are outside coverage. Table localization/version changes produce Unknown. Missing update information never becomes “up to date.” Windows Update returns offers from the configured service, including drivers; this is not an exhaustive OEM driver catalog or a recommendation to replace every driver.
- Activity events go to `%LocalAppData%\MadClean\activity.jsonl`, rotated into one `.previous` file at approximately 1 MB. Only timestamp + enumerated operation/outcome are persisted. No provider output, filenames, command lines, usernames, detection resources or secret values are logged. Inventories exist only in memory.

## Later milestones

Exact candidate review before any cleanup; reversible startup actions; explicit user-initiated Defender scans; individually selected software/driver installs using Windows Update, official manufacturers or trusted package sources. No registry cleaner, bulk driver replacement, forced restart or silent elevated action. Restore points may support eligible system changes; **they do not recover deleted files**. File recovery needs a separate backup strategy.
