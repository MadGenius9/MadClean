# Existing-only synthetic reader qualification

Development-only implementation. **Windows execution has not occurred.** This does not qualify production journal creation, recovery, repair, MSI or a release. The [original reviewed proposal](EXISTING-READER-FIXTURE-PROPOSAL.md) remains preserved.

## Instruction and scope

The assistant asked: “May I implement the reviewed reader fixture and, after independent code review, run it on a disposable GitHub Windows runner using only owned dummy files and ACLs?” The user's subsequent replies were “Lets keep  going and finish this” and, during implementation, “Keep going.” Those continuations were interpreted in the specific proposed scope as authorization to implement and execute after review. AGENTS records the narrow exception. Ordinary `ExecutionValidated`, ordinary factories, fresh writing, old Nt, public recovery/repair and MSI gates stay closed. No personal-PC action is included.

Only the recovery ref, manual workflow_dispatch, dedicated `windows-existing-journal-reader` job and `qualify_existing_journal_reader=true` select this mode. Package and MSI inputs must both be false; a preflight rejects conflicts. The input defaults false. Ordinary pushes/PRs cannot invoke it. The selected route skips broad smoke/online discovery, uses standard windows-latest with read-only repository permission, locked dependencies and the pinned SDK. No distribution package is selected.

The actual runtime requires x64, Windows kernel build at least 26100, fixed local NTFS/persistent ACLs and actual exports. The report records build/process architecture; a hosted Windows Server result is not physical Windows 11 qualification. The development-only command is:

```text
dotnet run --project tools/MadClean.WindowsSmoke/MadClean.WindowsSmoke.csproj -c Release --no-build -- --existing-journal-reader-qualification
```

This is not a local-PC test instruction. Exact source fences require the repository, ref/event/job/hosted Actions context, SHA/run/attempt, workspace/current-directory agreement and `MADCLEAN_EXISTING_READER_QUALIFICATION=1`. These are not cryptographic host authentication or freshness proof. Actual create-new setup, marker, ACL and held-object checks are independent requirements.

The private authority binds one actual ExistingReadOnly frame. Preparation/acquisition/known-prefix release do not require a not-yet-acquired lease; attachment/read observation require the real exclusive lease and actual phase/thread/handle graph. A separate one-shot bridge fixes created=false/readOnly=true. There is no caller Boolean/path/mode/raw-handle override. Captured authority is not revoked beneath a live owner on timeout or environment changes.

## Owned fixtures

Seven fixed cases use separate fresh GUID-marked roots: valid, missing lease, missing journal, extra file, extra directory, case mismatch and seed missing LF. Setup authors only dummy files with the proposal's protected current-user/SYSTEM directory and separately protected leaf ACLs. Core encodes one unresolved synthetic CheckHealth intent plus LF; **no health command executes**. Reparse coverage remains unverified.

Actual setup/reader/post/teardown owners and workers are rooted before effects. Native handles, descriptors and explicitly owned unmanaged I/O/path buffers survive uncertainty. Failure/successful-null descriptors cannot authorize creation or dependent access. Output reservation is create-new; collided directories cannot receive reports. Existing ancestor reparses are checked before enumeration. None of these checks claims an immutable namespace or hostile-same-user boundary.

Baseline handles close before the reader's exclusive opens. Actual phase/name facts must match each case. Earlier unrelated refusal is inconclusive; names-complete/codec-refused cannot substitute for namespace refusal. Confirmed reader release precedes independent byte/identity/security comparisons and teardown. Known unexpected acceptance or measured mutation stays failed through later cleanup uncertainty.

Teardown is nonrecursive, limited to exact actually created names with identity/security/kind bookends. It is trusted-disposable-fixture path cleanup, not atomic namespace defense or a production cleaner. Unknown/reparse/identity/delete outcomes retain ownership and prohibit retry, deletion and later cases. Sequential observations do not prove durability, authorship or snapshot isolation.

## Lifetime and evidence

The observation budget is 60 monotonic seconds. An initial conservative report-timeout.json is persisted before draining an unfinished task, without concurrently reading worker lists. Timeout stays historical through later settlement. The harness awaits actual work and keeps the CLR alive on unsettled ownership; static roots alone do not establish lifetime protection.

The qualification step's three-minute external limit sits within a 15-minute job. External termination is not native cancellation, cleanup or rollback. Always-upload may preserve a written initial report after step failure; cancellation/whole-job termination can still leave artifacts missing. Missing evidence remains unverified.

The artifact `MadClean-existing-journal-reader-qualification-<attempt>` contains final report.json when available, conservative initial report and bounded command logs. Reports contain fixed labels/enums/booleans, bounded source/run context and timestamps; no paths, markers, SIDs, journal bytes or native exception text.

The existing publisher job keeps its existing permission. In isolated mode it publishes only existing-reader-qualification.json on the verification branch, preserving ordinary latest/report evidence. Strict validation checks exact source/run/attempt, schema and case matrix. A fallback is permitted only when the primary is absent and may claim only an inconclusive empty-case unsettled observation. An invalid primary cannot fall back. Known failure, timeout or cleanup uncertainty cannot pass. Push is one non-force attempt, with no credential extraction or uncertain-outcome retry.

Passive inspection starts no provider/native work:

```text
python tools/read_existing_reader_qualification.py report.json --source <actual-reviewed-SHA> --run <actual-run-id> --attempt 1
```

Schema consistency is not source authentication. Correlate with the actual source-bound job and preserved artifact/verification identity. A pass covers only the observed runner/build's bounded synthetic existing-only reader and cases, not production provisioning/recovery, full-history admission, fresh writing, crash durability, servicing or distribution.

## Validation and current execution blocker

Final independent combined source review found no blocking issue after corrections to native-buffer lifetime, descriptor-null handling, output collision, reparse ordering and sticky failure evidence. Release solution build: zero warnings/errors. Core: 2,742 passed/one Windows-only skipped, including 30 profile and ten controlled-owner matrix cases. Python: 398 passed/one skipped (399 total), including strict-reader and controlled local-Git publisher tests; push tests are mocked. These checks run no Windows fixture or remote publication. YAML parses with the installed parser. Logs/TRX remain separate existing-reader-* recovery evidence.

Cloud API dispatch is blocked: sandbox curl cannot reach its proxy bridge; the supported escalated read-only comparison receives CONNECT HTTP 403 for api.github.com. Desired host presets include Git transport hosts but omit this API host. Reported enforcement state is unknown; the proxy response is the concrete observation. No live network setting changed, and no callable workflow dispatcher/request-changes tool is available. The earlier CLI “invalid token” label is not proof of GitHub rejecting a credential when the connection never reaches GitHub.

After the environment owner permits api.github.com, test the normal client afresh; network access does not establish credential scope. Alternatively, an authorized owner can manually dispatch windows.yml on the reviewed recovery branch with only the reader input true and package/MSI false. Check the actual run source SHA before interpreting evidence. No manual dispatch, Windows fixture, production activation or distributable has occurred here.
