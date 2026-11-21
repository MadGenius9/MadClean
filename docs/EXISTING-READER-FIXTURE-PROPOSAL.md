# Existing-only reader fixture: review-only proposal

Date: 2026-11-16. Source reviewed: `346dd02e7fbda0be434325cbd935b8ac92af4cd8`, recovery branch. This document proposes future changes; it implements and authorizes none. AGENTS, source guards, workflows and runtime entries remain unchanged. No build, CI run, fixture, installer or distributable is requested by writing this proposal.

## Decision requested

Review a candidate-only exception for reading freshly authored synthetic fixture files on one standard disposable GitHub Windows runner. Keep implementation approval separate from authorization to execute the eventual pinned test build. No personal PC/VM access, production journal, servicing or MSI operation is included.

Current AGENTS rules say the modern sample has `ExecutionValidated=false` and **“no App/CLI/CI execution hook”**, and the existing-only factory stays **“behind the same false guard with no App/CI hook.”** Those restrictions are binding today. The following is proposed exception wording, not an edit to those rules:

> Exception solely for the separately reviewed existing-only owned-fixture qualification: permit one explicit, default-off, manual disposable-runner test entry for the exact reviewed recovery source. The entry may read only the freshly authored, marked repository fixture bound to its actual private ExistingReadOnly frame and held qualification proof. `ExecutionValidated` remains false; both ordinary modern factories, FreshWrite, old Nt/private-store creation, public recovery/repair and all other production guards remain closed. The exception extends only to the phase checks, one-shot read-only attachment and separate store bridge needed by that same qualified frame; it supplies no general bypass or caller-selected mode/path/store API. Ordinary inspection, passive/default CI, pushes, PRs and package publishing must not invoke it. No maintenance command, journal append, installer, elevation, restart or first-parent production provisioning is allowed. Retain actual ownership on uncertainty, prohibit retry/teardown while unsettled, and qualify each result only from actual reached-boundary and settlement facts. Implementation and execution require the separately recorded scope and review below.

## Narrow proposed implementation boundary

Names below are proposed, not callable interfaces or workflow inputs today:

- A new manual input `qualify_existing_journal_reader`, default false, and dedicated job `windows-existing-journal-reader` on the existing standard runner class. Require exact recovery ref, `workflow_dispatch`, opt-in and reviewed source identity. Keep `prepare_package=false`; other optional qualifications, including MSI, stay off. Push/PR/default/passive paths remain excluded. Use read-only repository permission and no paid runner.
- One development-harness entry for this job, separate from default smoke, App arguments and local validation modes. It accepts no arbitrary root, mode, journal, command, record or source/ref selector. Reject conflicting/mixed arguments before fixture/native access.
- A private constructor/issuer mints a one-shot existing-only fixture proof only for the actual fresh owned scope. Bind it to the exact source/run/attempt/job context, fixture marker/root, immutable ExistingReadOnly mode and actual registered frame. A flag, environment string or supplied Boolean alone cannot mint authority; CI fields are fences and claimed context, not cryptographic host authentication. Independently preserved source/run evidence is still required.
- Preserve `WindowsModernOwnedJournalSample.ExecutionValidated => false` and the current ordinary entry guards. Do not make a shared `Guard` accept a test Boolean. Add only a separately reviewed qualification authority path for this frame's phase checks, callback thread, exclusive lease and read-only attachment, preserving all existing platform/global-uncertainty checks. Authority must be phase-specific: preparation, directory/lease acquisition and known-prefix release cannot require a lease that has not yet been acquired. Journal attachment and read observation require the actual live exclusive lease and qualified predecessor handles. Bind authority before callbacks; retain the real frame and inputs through all work and cleanup. Observation timeout must not revoke cleanup authority while its owner is still live.
- The existing store bridge currently also checks the shared guard. Keep that bridge and the old Nt bridge unchanged. Propose a distinct one-shot fixture attachment/bridge that can derive only `created=false`, `readOnly=true` from the actual qualified frame. No raw caller handle adoption, writable attachment, public store creation, copied permit or foreign frame may qualify. The exact mechanics and lock/cleanup lifetime need code review before implementation is accepted.

Environment/profile checks do not replace owned-path qualification, owner/DACL checks or retained-handle validation. No service/broker, source authentication claim or same-user malware boundary is introduced. Protected ACLs and sharing flags are not claimed to freeze namespace metadata or provide a production security proof.

## Fixture recipe and limits

Use only a fresh nonzero lowercase GUID-N marker. The reader already requires:

```text
<reviewed repository>/artifacts/windows-validation-fixtures-<marker>/
    .madclean-fixture-owner       exactly the 32 ASCII marker bytes, no newline
    <marker>/
        lease.bin                empty
        <marker>.jsonl           fixed synthetic snapshot plus LF
```

Current working directory must be that repository and contain `MadClean.sln`. Honor existing root length 160, final path 258, ancestor depth 16, fixed-local path, x64 Windows build at least 26100, NTFS/persistent-ACL and required export checks. Missing platform/API prerequisites mean unavailable/unverified, never a reader pass. Keep the pinned SDK/build dependencies; record the actual OS/SDK and immutable tested source.

Create new fixture objects only; collisions refuse without adopting, overwriting, repairing ACLs or deleting an existing tree. Use the fixed case list below, at most eight freshly marked roots including the optional reparse case, never caller-selected cases or paths. Setup may create/write only those marked scopes. No LocalAppData, real journal, startup, Downloads, application or user-data target is eligible. The setup profile must itself be independently reviewed for creation descriptors, ownership and safe teardown; modern fresh CreateDirectory2 execution is excluded. Setup/teardown needs its own retained owner for actual handles/buffers before calls; partial or uncertain setup cannot be treated as an unowned directory safe to remove.

Security must match the existing reader, not an approximate inherited ACL:

| Object | Owner | Protected DACL: exactly two explicit allow ACEs |
| --- | --- | --- |
| Marked root and GUID child | Current user SID | Current user and SYSTEM, each full access `0x1F01FF`, ObjectInherit + ContainerInherit |
| Lease and journal | Current user SID | Current user and SYSTEM, each full access `0x1F01FF`, AceFlags.None; separately protected leaves |

For these directory descriptors the existing recipe is `O:<sid>D:P(A;OICI;FA;;;<sid>)(A;OICI;FA;;;SY)`; leaves use the same two ACEs without OICI. A descriptor string is a recipe, not evidence: verify the actual opened object owner/protection/ACE set. Do not add Administrators/Everyone, inherit-only or extra ACEs to make setup succeed. The marker must independently satisfy the existing ordinary-file/path/size/content checks. Disclose that fixture creation and ACL writes are local test effects, not production provisioning.

Seed exactly one canonical Core record: matching marker GUID, DismCheckHealth, sequence 1, valid current UTC timestamp, DispatchIntent, null process/client-start/exit/native-error fields. Use `SystemToolJournal.TryEncode` and append exactly LF; enforce the store's 4096-byte bound including LF, then verify `DecodeSnapshots` and the fixed-seed policy before closing setup. Bare JSON lacks snapshot framing. No DISM/health command is executed: this intent is synthetic unresolved data, not a dispatch receipt.

## Exclusive-handle and ownership sequence

1. After validating the approved case/context, root the actual setup worker and resource owner before queueing/native effects; retain each real raw handle/buffer before dependent operations. Author the new fixture and independently observe expected leaf bytes/lengths, identities, security and kinds. Keep those observations in memory only. Setup uncertainty is not safe refusal or permission for deletion/retry; it blocks later cases.
2. Close all setup and baseline file handles/streams before the reader opens its exclusive lease/journal. Otherwise the test may only demonstrate sharing refusal. Do not hold a second baseline handle across the reader. Setup and post-observation are separate from the reader's read-only semantics.
3. Register/root the actual frame/proof before queueing or callbacks. Use existing phase/thread ownership; ancestors, parent, lease, journal, query buffers, descriptors and stream construction remain rooted. Read through the actual existing-only code, without fake native returns, provider injection or a replacement codec.
4. Capture the actual attempt observations and settlement. Names Complete proves only EOF plus the expected name/kind set; later security/codec failure remains failure/uncertainty. Sample verification never implies release; release never upgrades a refused sample.
5. Post-open for independent byte/identity/security comparison only after `ResourcesReleased=true`, confirmed Release callback, no pending work and no attempt/domain/global retention uncertainty. Compare to baseline; require exact nonmutation for a pass. Root/retain the actual post-observation/teardown worker and its resources before calls, then perform narrowly owned teardown. Uncertain ownership at setup, reader, post-observation or teardown blocks further post-opens, deletion, retries and subsequent cases. Fixture absence after teardown is a distinct fact, not rollback.

Use a finite monotonic observation deadline, proposed 60 seconds captured before queueing, plus a dedicated bounded job. The observation deadline does not cancel a synchronous native operation, kill the worker, free inputs, discard the owner or prove settlement. If later settlement occurs, preserve the initial deadline outcome separately; do not rewrite it as an on-time pass. External job/process termination proves neither safe cleanup nor rollback. Remaining cases after unsettled ownership are unverified. No automatic retry.

## Required boundary evidence per case

Each case gets a separate freshly marked scope. The harness must report actual callback outcomes, actual Names facts and resource settlement, not infer expected rejection from `SampleVerified=false`. Table notation: C=Confirmed, R=Refused, N=NotEntered. All qualified rows also require Release=C, ResourcesReleased=true, no pending/retained uncertainty, independent expected nonmutation and owned teardown. An acquisition refusal may be described only at its callback boundary; no absent native error code should be invented.

| Case | Prepare / Directory / Lease / Journal / Exercise | Names | Additional requirement |
| --- | --- | --- | --- |
| Valid canonical fixture | C / C / C / C / C | Complete | SampleVerified=true; two actual decoded seed reads agree |
| Omit lease at setup | C / C / R / N / N | NotObserved | Independently absent before and after; callback refusal alone is not filesystem absence proof |
| Omit journal at setup | C / C / C / R / N | NotObserved | Independently absent before and after; no replacement creation |
| Add ordinary extra file | C / C / C / C / R | Refused | Extra object remains untouched; real enumerator reached |
| Add extra directory | C / C / C / C / R | Refused | Wrong-kind extra, not replacing an expected leaf (which could fail earlier) |
| Actual case-mismatched expected name | C / C / C / C / R | Refused | Existing leaf opens must actually succeed; case-sensitive/unavailable behavior is not this negative-case pass |
| Canonical seed with final LF omitted | C / C / C / C / R | Complete | Demonstrates actual codec refusal after successful names observation, not a namespace negative |

A reparse-extra case is optional only within the same owned root and targeting another owned object. No external/network target or privilege/elevation request is allowed. Missing creation privileges or unsupported behavior remains unverified and cannot be counted as passing coverage. Do not inject duplicate/malformed native batches to label portable parser tests as native qualification; those remain existing controlled tests. Exceptions or earlier refusal make the intended case inconclusive even if resources later release. Names Complete plus Exercise=Uncertain is not sample success.

Evidence contains a fixed version/context, source SHA/ref, run/attempt/job, timestamps, fixed case labels, callback outcomes, Names state, boolean comparison/ownership/teardown facts and bounds. No full paths, marker/product/device IDs, SIDs, ACL text, journal bytes or exception text enter the report/logs. `StopObserved` means only the owner's existing between-call checkpoints; false does not exclude a requested or frame-observed cancellation. Capture initial timeout and eventual settlement separately. A passive evidence reader must distinguish setup unavailable, wrong-boundary rejection, actual expected refusal, verified sample, comparison failure and unsettled ownership. A skipped case is never a pass; source/run consistency alone is not authenticity.

## Review and authorization sequence

1. **Proposal review now:** architecture/security review of this exact exception, setup effects, authority/attachment lifetime, negative attribution and unknown-state reporting. Resolve objections without changing AGENTS or gates. No execution is approved by proposal review.
2. **Implementation scope decision:** explicitly approve amending only the quoted no-hook restriction for this candidate-only route and implementing the isolated test entry/bridge/workflow. Preserve every ordinary/fresh/Nt/production gate. This requires a concrete reviewed patch, meaningful portable contract/authority tests and independent combined review before any run. A successful local build is insufficient.
3. **Execution authorization:** record permission for the exact pinned disposable-runner existing-only fixture scope and its owned setup/ACL/teardown effects, including the listed negative cases and timeout behavior. Standard pushes/Actions authorization and the inert MSI exception do not by themselves authorize this new native route. Approval may name both implementation and later execution scope, but execution still waits for final patch review and pinned identity.
4. **Dispatch capability:** use an already authorized supported workflow dispatcher, or a deliberate manual GitHub dispatch after scope approval. Current CLI status reports invalid configured authentication and the available catalog has no dispatch connector. Do not extract/change credentials, create a replacement workflow event or exploit a push to bypass manual opt-in. Record the actual resulting source/run identity before reading results.

Success could qualify only that reviewed build's bounded existing-only synthetic reader and observed error/cleanup cases. It cannot open production recovery/repair, qualify FreshWrite/CreateDirectory2 or the Nt candidate, prove full-history admission, freeze namespace security, promise power-loss durability, establish exactly-once dispatch, qualify MSI/servicing, or justify a distributable. Any broader activation remains a distinct design, evidence and authorization decision.
