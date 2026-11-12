# Build and test locally

Development is local only at the user's request. Do not push, fetch, upload, publish or trigger GitHub Actions. Local commits do not spend Actions minutes. The helper below never invokes Git, GitHub, a CI qualification mode, the application, an installer, servicing, publishing or packaging.

With Python 3 and the pinned .NET SDK already available, run from the checkout:

```powershell
python tools/validate_local.py
```

It reads the selected SDK version, restores the existing solution in locked mode, builds Release, runs the portable Core test project, then runs the Python tooling tests. It stops after the first failed or unavailable command. Restore can contact configured NuGet sources; it does not install an SDK or change package locks. No administrator access is requested. If restore assets are already available, skip the explicit restore:

```powershell
python tools/validate_local.py --no-restore
```

Select an existing SDK executable when it is outside PATH. For this Linux cloud checkout:

```bash
DOTNET_CLI_HOME=/workspace/.dotnet-home NUGET_PACKAGES=/workspace/.nuget/packages \
python3 tools/validate_local.py --dotnet /workspace/.dotnet/dotnet --no-restore
```

The helper uses argument arrays, not shell command text. Only the existing build/test commands are supported; it cannot forward arbitrary workflow modes or command-line switches. Each attempt creates a fresh `artifacts/local-validation/<run>/` directory. The JSON report records platform, SDK and actual stage exit outcomes; unfinished stages remain not run, and a successful command is not an invented test count. The Core TRX contains the actual development-test results. Existing outputs are never reused or overwritten; known symlink/reparse output ancestry is refused. These path checks do not protect against a concurrent adversarial namespace replacement.

Console output and TRX are local development evidence and may contain checkout or temporary paths. The JSON summary contains no command output, package/hardware inventory, paths or credentials. Generated report/TRX files stay under ignored `artifacts/`; nothing is uploaded. Keep them local unless separately reviewed for sharing.

Even if every command succeeds on Windows, **Windows UI, live providers and maintenance remain unverified by this helper**. It does not run the separate disposable-runner native/MSI/servicing qualifications or bypass their context/permission guards. Linux can compile WPF but cannot run it. Test counts, skips and source/runtime limitations must still be reported from the actual results. The [observational home-test checklist](HOME-TEST-CHECKLIST.md) covers later manual UI/provider checks; it requests no maintenance actions. No distribution ZIP is created.

## Optional local Windows neutral-window check

A separate development mode can check ordinary WPF startup, coarse wide/minimum-size shell bounds, programmatic navigation bindings and normal window closure without Actions. **It is not run by `validate_local.py`**, and cannot run on this Linux environment. It requires Windows 11 x64 and the existing checkout/build dependencies. From that checkout, after the Release build:

```powershell
dotnet run --project tools/MadClean.WindowsSmoke/MadClean.WindowsSmoke.csproj -c Release --no-build --no-restore -- --local-ui-validation
```

Use exactly that sole mode argument. Do **not** set `MADCLEAN_WINDOWS_VALIDATION=1` or invoke the other harness modes on a personal PC. Existing CI-only native/MSI/servicing/online qualifications keep their separate restrictions. This mode opens the actual normal MadClean window and disables its input while performing programmatic navigation; it invokes no inventory, provider, browser, installer, scan, repair or cleanup command. Normal startup can read MadClean's own existing exclusion/release settings. Their contents are not exported, and there are no screenshots. It writes only a fresh `artifacts/local-ui-validation/<run>/report.json`. No Git/GitHub call, upload, SDK installation, package or signing operation is performed.

Unexpected activity/pending work makes the check fail and retains the model/dispatcher until actual settlement; it does not cancel/kill a worker or overwrite a failed report with a late success. Coarse layout checks can fail on a display that constrains the requested size. A pass does not certify all text clipping, visual fidelity, physical keyboard/screen-reader input, policy/provider workflows, a published executable or action readiness. Forced termination and OS/dispatcher shutdown are separate limitations. Do not interrupt an unexpected active worker to manufacture a test result.

The helper prints no loaded personal settings or provider data. The report includes only fixed nullable observations, times, state and optional numeric exception code; provider, maintenance and physical-input coverage stay `unverified`. You can inspect its consistency on either platform:

```powershell
python tools/read_local_ui_report.py artifacts/local-ui-validation/<run>/report.json
```

Replace `<run>` with the fresh directory actually created; do not paste the placeholder literally. The reader launches nothing and rejects malformed, contradictory, oversized, linked and unsupported-scope reports. Exit 0 means its neutral-UI facts report a pass, 1 means failed/invalid evidence, and 2 means inconclusive. **Reports are not authenticated or bound to a source/build**: record the exact local commit/build separately before running and do not treat an old or supplied JSON file as proof of current runtime behavior. Portable evidence/parser tests check only consistency rules and do not observe Windows. This route cannot enable any disabled action gate or substitute for its separate qualification.
