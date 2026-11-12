# Retained Defender lifecycle event history

Security offers **Read scan-event history**, an explicit local metadata query separate from Inspect this PC and Get-MpComputerStatus timestamps. Choose 24 hours, 7 days or 30 days. Changing the range clears the previous result and invalidates pending publication, including an away/back change; it starts no query. No scan, engine-scan cancellation, remediation, quarantine, signature update, elevation or policy change is requested.

## Source and interpretation

The fixed source is `Microsoft-Windows-Windows Defender/Operational`, exact provider `Microsoft-Windows-Windows Defender`, event IDs 1000 (started), 1001 (finished) and 1002 (stopped before completion). These meanings come from the coordinating task's current official [Microsoft event reference](https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-microsoft-defender-antivirus), recorded in [scan-contract research](DEFENDER-SCAN-REQUEST-RESEARCH.md). The integration reuses the existing inbox PowerShell/Get-WinEvent metadata pattern; the official [Get-WinEvent reference](https://learn.microsoft.com/powershell/module/microsoft.powershell.diagnostics/get-winevent) describes FilterXPath, MaxEvents and log configuration reads. No new retrieval of documentation bytes or Windows runtime qualification is claimed by this increment.

Only event ID, record reference and reported UTC time are retained. Log/provider identity is checked for every record. The adapter does not read messages, EventData, XML, Scan IDs, user names, paths or detections. A record reference is local history metadata, not a scan identifier or durable request identity. Records are not paired into scans; start/finish proximity, order, counts and timestamps do not establish running scans, unique scan counts, MadClean ownership, coverage, detections or a clean verdict. A finished notification is not an antivirus result.

The query uses an exact half-open UTC interval, returns at most 256 records and reads one additional sentinel to disclose truncation. Positive source evidence remains **Partial**, including an empty matching result, because retention, log clearing, provider behavior and clock uncertainty can hide events. The interval is a query scope, not proof that all events during that interval survived. The UI shows query bounds, provider observation and local response receipt separately. Current protection status, detection history and reported quick/full timestamps remain independent.

Disabled or missing logs are Unavailable; denied or failed reads remain Failed with fixed actionable explanations. Unexpected shapes, source mismatches, duplicate record references, unsupported event IDs, malformed/future times and contradictory flags withhold the report as Unknown. Unchecked and interrupted reads cannot become successful empty results. An unavailable or cleared event log never implies no scans occurred.

## Lifecycle and privacy

The query uses the absolute system Windows PowerShell executable, no profile, the inbox Diagnostics module and a fixed script; only locally generated UTC boundaries are substituted. No arbitrary script, user path, other log, online request or module fallback is accepted. Configuration and emitted event objects are disposed, including the truncation sentinel and records emitted before a pipeline error. The shared read-only process runner bounds output and owns its child through exit.

Cancel/timeout may stop this metadata-query child; it does **not** cancel any Defender engine scan. Normal App close waits while the query task remains unfinished; the busy owner awaits actual settlement even after display/notification failure. Event values, IDs and times are displayed in memory only and are excluded from the enum-only activity log. The new activity area records only attempt/outcome. This route is not invoked by startup, ordinary inspection, neutral UI validation or the local build/test helper.

## Validation limits

Portable tests exercise parsing, interval/source checks, contradictory failures, bounded records, cancellation and result consistency. Compilation does not execute the PowerShell script, WPF bindings or actual Defender/Event Log providers. Windows validation still needs real enabled/disabled/missing/denied/no-match/truncated behavior, cancellation/exit handling, range-change races, layout and accessibility. Do not create scans, detections or malware fixtures to populate this read-only panel. This feature does not complete the separately gated scan-request/lifecycle milestone.
