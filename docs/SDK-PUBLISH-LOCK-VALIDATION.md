# Pinned SDK and Windows publish dependency graph

The source-bound verification branch report for commit `2d51aba230c02505335d98e623235380b815d291`, run `43295261530`, attempt 1 recorded successful Windows restore/build and Core tests: 2,385 Passed and 13 NotExecuted among 2,398 records. No test failure was recorded. The broader verification job failed before its general harness report: the internal structured-WinGet test publish returned NU1004 because Microsoft.NET.ILLink.Tasks requested 10.0.15 while the publish locks requested/resolved 10.0.14. No general WPF/provider pass follows from the build/test result.

Local SDK 10.0.403's installed `Microsoft.NETCoreSdk.BundledVersions.props` explicitly declares net10.0 runtime/linker 10.0.14; all five `packages.win-x64.lock.json` files match that linker. The old `global.json` latestPatch policy allowed a newer preinstalled SDK in the same feature band despite setup installing 10.0.403. This is consistent with the observed drift. The old CI did not record its selected SDK version, so the exact runner patch is not established.

The fix selects 10.0.403 exactly with `rollForward: disable` and records `dotnet --version` through the bounded SDK log stage after all seven .NET setup steps. That stage is allowed in both capture and minimized diagnostic projection. Resolver failures keep their nonzero result; logs remain scrubbed. No package lock, TLS, audit, warning or signature policy is relaxed. Deliberate future SDK changes must regenerate/review affected locks. This build selector does not install an app runtime or alter MadClean's update behavior.

Local validation used a separate tracked-source copy with the new global.json. `dotnet --version` selected 10.0.403. The locked restore matching the publish profile passed for Smoke, App, Core, Windows and WinGet:

```text
dotnet restore tools/MadClean.WindowsSmoke/MadClean.WindowsSmoke.csproj -p:RuntimeIdentifier=win-x64 -p:MadCleanPackage=true -p:SelfContained=true -p:PublishSingleFile=true -p:PublishTrimmed=false --locked-mode
```

An initial local check using restore's `-r win-x64` failed because that option sets plural RuntimeIdentifiers and the Linux SDK also inferred linux-x64. The corrected explicit singular RuntimeIdentifier matches `dotnet publish -r` semantics. No lock was regenerated and the isolated source hashes were unchanged after both checks. The failed first check remains in the task's validation evidence.

These are dependency-graph checks, not a Windows publish/run or executable qualification. No publish command, final ZIP, distribution or native operation ran. The next source-bound Windows run must confirm the publish blocker is resolved; its selected-SDK record will remove the earlier diagnostic ambiguity.
