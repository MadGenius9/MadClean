# Selected driver prerequisite worksheet

The Drivers tab offers **Review selected driver prerequisites** for one explicitly selected actual Windows Update offer. This is a source-bound, in-memory worksheet. It performs no provider query, download, installation, device matching, elevation, restart, Settings handoff, log write or saved approval. It neither completes driver installation nor enables an executor.

## Snapshot contract

The selection must be the actual object exactly once in a positive Available/Partial source and its completely published Driver rows. A value-equal clone, foreign/stale reference, incomplete loaded list, duplicate row or ambiguous update GUID is refused. Identity ambiguity is checked across every covered source row, including software and firmware. Only nonzero canonical-format GUIDs are accepted; case changes do not create a different identity. Firmware-class offers are excluded, including inconsistent Driver labels with a Firmware class. No motherboard/OEM substitution or bulk driver operation is possible.

The attempt/receipt/review times must be explicit UTC and ordered. Both attempt and response must be at most 30 minutes old at review. This is MadClean's admission policy, not a Windows guarantee that the offer is unchanged. A worksheet describes its historical query snapshot, not a live applicability check. Refresh, interruption or a different selected reference clears it, even if replacement fields compare equal. No timer or action token makes it live again.

The report copies its bounded requirement collection. Source coverage remains visible; Partial inventory is never upgraded to a complete manufacturer catalogue. Valid optional Windows Update declarations retain their independent Available/Unknown/Unsupported/Failed states. Missing optional metadata permits a worksheet with Unknown declarations. Invalid metadata, unsupported shapes, oversized/unsafe display fields and invalid source/timing evidence are refused without IO.

## Evidence versus readiness

The worksheet displays declared manufacturer/provider/model, input/network requirements, installation impact and reboot behavior, plus the observed RebootRequired flag. These mean only what the already implemented read-only WUA metadata projection reports. A model string is not an exact device match; a provider declaration is not package authentication. NeverReboots or a false RebootRequired flag does not prove control over a future installation or permission to perform it.

Exact device/OEM compatibility, installed/offered versions and revision, official release notes/manufacturer prerequisites, elevation, dependencies/rollback, enforceable restart control and actual result verification remain Unknown. These requirements have no editable verification switches. The report's readiness is get-only Unknown and CanInstall is get-only false, regardless of declarations. No agreement, credential, consent or capability is created.

This feature uses only the existing [WUA offer projection](INTEGRATIONS.md) and portable policy; no new Windows interface or documentation semantics are introduced. The separate [history research](WINDOWS-UPDATE-HISTORY-RESEARCH.md) remains blocked and unimplemented.

## Validation limits

Portable boundary cases cover source coverage, exact references, GUID collisions across categories, stale/future clocks, malformed or missing metadata and the distinction between declarations and readiness. Independent source review and Linux build/tests cannot establish real WPF selection/refresh behavior, populated layout, keyboard/screen-reader access, or Windows/provider compatibility. No driver installation, download, firmware action or personal-PC test is performed.


Provider identity metadata is shown independently of installation characteristics. The provider revision describes update metadata, not a driver/BIOS version. The declared hardware or compatible ID is a provider prerequisite, not an observed device match or complete OEM compatibility proof. Missing/inconsistent optional identity fields remain Unknown without changing worksheet admission. Different revisions do not disambiguate duplicate update IDs in the current snapshot selector. Installation readiness remains Unknown and CanInstall stays false. See WUA-INSTALLATION-CONTRACT.md for exact official sources and local field-admission limits.
