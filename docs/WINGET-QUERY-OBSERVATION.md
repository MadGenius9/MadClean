# Same-query WinGet structural diagnostics

The normal application still calls `AppsAsync` and receives the same `CheckResult<AppUpdate>` states, rows and messages. That method projects the result of one internal observed query. The development update-provider fixture calls that same observed query once instead of invoking a second WinGet process. The fixed alias, arguments (`list --source winget --disable-interactivity`), deadline, process-tree ownership, output bounds, cancellation and agreement policy are unchanged.

The immutable observation adds only fixed typed metadata:

- Profile or alias unavailable, a returned parsed stdout result, a returned nonzero exit, timeout, query failure or unexpected-response exception category.
- Actual returned process exit code, nullable when no `ProcessResult` returned. This is not a process-start observation: timeout/failure does not establish whether a child started.
- Whether actual stdout contains a non-whitespace character, nullable if no process result returned. Whitespace-only stdout is false. Stderr is still drained privately by the existing runner and is not summarized or exported.
- A parser issue only after exit zero: accepted table, empty/whitespace output, no accepted header layout, no accepted separator, unsupported display characters, short row, incomplete identifiers/fields, or a table without package rows.

These are structural observations, not diagnosed causes. `HeaderNotRecognized` means the parser found no accepted header/separator layout; it can include a final header without a following line. It does not prove localization, absent packages, source agreements, offline services or a specific version defect. A short row could be a changed table/footer; it does not identify one as the cause. No tolerance for unknown Unicode widths, trailing footer lines, malformed rows or new layouts is introduced. Previously plausible rows are still discarded if a later row is rejected. Missing offered versions remain Unknown even when the table was accepted.

`WingetParser.ParseObserved` provides the typed issue; the compatibility `Parse` method projects its result. The Windows exception categories and result messages retain the prior query policy. Cancellation propagates instead of becoming a completed diagnostic observation. No mutable last-result field, raw-message parsing or second query is used.

## Evidence boundary

The standalone development provider report writes version3 with one exact `winget` object containing fixed outcome/parser labels, nullable exit code and nullable stdout-content boolean. It retains source/run/attempt/time binding, strict field/duplicate rejection, canonical enums and coherent state/count/nullability checks. Version2 reports can still be read, but have no new diagnostic proof. Pending/cancelled/faulted tasks have null observations; a returned version3 task must have consistent diagnostics.

The separate `winget-query-observation-contract` check qualifies actual returned process/parser metadata only. It may pass for a returned nonzero exit or a rejected table while the existing provider check remains inconclusive/failed. It never promotes inventory coverage, invents available updates or means Up to date. Profile/alias/exception-only outcomes stay inconclusive for the actual-process observation contract. No package names, IDs, versions, source labels, paths, raw stdout/stderr, provider messages or exception text are exported.

Portable tests exercise malformed separators, trailing-line rejection after plausible rows, whitespace coherence, Unicode/layout refusal, unchecked versions and impossible transport/parser/state combinations. Compilation and synthetic cases do not identify the current disposable Windows runner's Unknown cause. A matching-source Windows report is required to observe that branch. No command, source, agreement, installation or production execution gate changes.

## Actual Windows observation

Source `61e34a3a`, run 41644441782, immutable evidence `8bef3129fa91e73432e62e7b3be9fb4f8c941cb3` passed this diagnostic contract with ParsedOutput, exit code 0, non-whitespace stdout and HeaderNotRecognized. The provider remained Unknown/zero rows. Process success and this structural enum do not establish the contents, language or cause of the unmatched output. Parent checks and actual standalone startup passed again; 86 checks had 76 passed/10 inconclusive/0 failed. No acceptance tolerance or successful update determination was substituted.

Bounded follow-up research retrieved the current official [WorkflowBase.cpp](https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerCLICore/Workflows/WorkflowBase.cpp) on 2026-11-03 (SHA256 `295f73ca3242f0f8f9f8fa591030a350d45d1ba06d4569f620993091f68d71a2`). Its ReportListResult ordinary-list path has no generic upgrade-count footer; the upgrade-only path has those summaries, while a truncated ordinary search can append a localized SearchTruncated message. Headers are localized resources and names come from installed registration properties. This master-source observation is not matched to the runner's WinGet version and establishes no cause for its HeaderNotRecognized result. No parser behavior changed from that research.
