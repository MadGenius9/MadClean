# First home test — Sunday, November 8

This checklist covers the read-only desktop workflows on your own Windows 11 x64 PC, using your normal account. It does not require running MadClean as administrator. The full roadmap is unfinished. A hosted runner's successful check does not establish that your PC's providers, display scaling or policies behave the same way.

Development is currently local only: no GitHub pushes, uploads or Actions runs. Use the exact source/build being reviewed; previous CI artifacts do not contain later local changes. No new distribution ZIP is being prepared. Build/run instructions are in [README](../README.md#build-and-run); this checklist does not authorize installing SDKs or other software automatically.

## Start and display

1. Record the build's source commit and app version. Start MadClean normally. Confirm that unchecked checks show neutral states and that no inspection or online query starts by itself.
2. Compare the main window with the supplied navy/blue concept. Try a narrow window and your usual Windows scaling. If practical, also check 100%, 150% and 200%, restoring your usual setting afterward.
3. Use Tab, Shift+Tab, arrows, Enter and Space. Check that focus is visible, labels remain readable, text is not clipped and every More tools page is reachable. A keyboard or screen-reader issue is a defect even if compilation passed.

## Local inventory

1. Choose **Inspect this PC**. Compare the selected drive's capacity/free space with Explorer close to the same time; quotas, other activity and units can cause differences. Check that changing the drive changes the ring's named scope.
2. Review cleanup categories, candidate paths, exclusions and errors. These are candidates, not reclaimable-space guarantees. Do not delete anything. If testing a saved exclusion, select only a harmless file/folder you created for testing; this changes MadClean's local preferences and can be removed afterward.
3. Review startup source/publisher details and Defender source/status. Missing publisher data, restricted providers and passive/unknown Defender status must stay explicit. Nothing should claim an infection or a healthy PC from missing data.
4. Repeat inspection and try Cancel. Completed sources should retain their own timestamps; unfinished checks should remain Cancelled/Unknown. A cancellation request is not proof that a Windows service stopped.

## Disk map and app search

Optional read-only startup review: in **Startup recovery previews**, choose **Read covered registrations**, select one row and request its preview. Check source, observation time, type, byte count and hypothetical-effect warnings. No raw command or preview token should appear. Changing selection or rereading should clear the old preview; a failed/cancelled read must not leave it current. **Read recovery record metadata** may return no records because this version creates none. Do not attempt a startup change or restoration; both remain unavailable.

1. In **Disk map**, choose a small ordinary folder you own, containing a few harmless files and nested folders. Avoid system/application folders for this first test. Select a tile/list row, use Drill, Up and Reset, then **Refresh original folder** while viewing a child.
2. Refresh should return to the original requested scope, clear old selection and optional identity totals, and show a new attempt/receipt. Try Cancel and retry. Logical bytes are not physical allocation or bytes safe to delete. Optional **Analyze snapshot file identities** can show reported default-stream allocation per observed file identity. Missing/change-conflicted allocation must stay Unknown; no allocation total or reclaimable-space claim should appear. Repeated alias rows must not be summed.
3. Read desktop installed apps and Windows packages separately. Search, select details and Clear. Searching should change only the displayed list, without a new inventory or combined-source count. Do not launch uninstallers or infer an app disappeared merely because a filter hides it.

## Leftover-review boundaries

You do not need to uninstall anything for this first test. With no complete baseline or no disappeared registration, the review should show no eligible candidate and leave the folder-preview command unavailable. If a legitimate in-session candidate is already available, preview only metadata: check the selected root and each request stage, change selection/reread, and ensure old results never appear for another candidate. Any excluded/shared/user-data path must remain a warning or Unknown, never proven junk. Do not remove leftovers or uninstall software to manufacture a test case.

## Diagnostics and hardware

1. In **Windows Diagnostics**, read event evidence, change the 1/7/30-day interval, toggle repeated observations and select source/finding details. Changing a range should invalidate old current results. Event counts are not unique-crash counts or a health score.
   Also try changing the interval while a read is still loading. Wait for that read to settle: old results must not reappear under the new interval, and the new interval must stay unchecked until you explicitly read it. Repeat with WER only if you choose its separate metadata read; an Unknown result is acceptable. A range change should not start another query or claim Windows work was forcibly stopped.
2. The session summary should show already-collected storage/Defender observations; the three integrity rows should remain unchecked during ordinary inspection. Selecting a row must start no command. Optional **Read WER metadata** may honestly remain Unknown, including when independent stored-report counts are zero.
3. In **Hardware Upgrades**, choose **Read hardware inventory**, select/copy source details, reread and try Cancel. Compare the PC model with Windows About. Reported board version, module count, memory form-factor names and interface labels do not verify PCB revision, free slots, replaceability or compatible upgrades. Form-factor names should retain the actual reported code; an unknown raw memory-type code must not be guessed into DDR generation.
4. If the prerequisite worksheet is included in this local build, try each area and your own budget/use case. All manufacturer/physical checks must remain Unknown. Editing an input must clear the old worksheet. **Clear planning inputs** should clear the entries. No query, recommendation or purchase should occur.

## BIOS identity

In Updates → **BIOS / Firmware**, choose **Read BIOS / PC identity**. Check that attempt and response UTC are separate and that a response does not imply manufacturer verification. You can compare the reported BIOS version/date with the built-in Windows System Information display; board version still is not proof of PCB revision. Reread and try Cancel: old facts, guidance and release selection must clear. Unsupported history coverage is acceptable and must not become up to date. Do not fetch firmware files or perform firmware actions.

## Explicit online checks

Optionally, after app discovery returns an Available offer, select it and choose **Read selected catalog metadata**. This requests online manifest metadata and may update WinGet's cache. Inspect the package/version/source/time and four plain-text fields; missing or unsupported fields should remain Unknown. Changing selection must clear the prior details. Stop/timeout may keep the app waiting for the actual SDK owner; it must never install a package or silently turn a stopped read into success. This new route still needs Windows runtime validation, so report the exact state if it fails.

1. Choose the update check yourself. Review Apps, Drivers and BIOS/Firmware separately. Available, Partial, Unknown, Unsupported, Unavailable and Failed must remain distinct. Unchecked/unmapped software must never be marked up to date.
2. Try cancellation. Optionally retry while offline using your usual network controls; restore connectivity afterward. Offline or private-source access must yield honest uncertainty, without credentials being requested or embedded by MadClean.
3. **MadClean updates** must say **Update checking not configured** when no trusted release source is configured. Choose **Use trusted GitHub source for this session**: status should become **Not checked**, with no automatic query. Try **Disable for this session** and **Use deployment setting**; comparisons/notes/reminders should reset on a changed choice, and the original loaded configuration/error should return. Only **Check MadClean updates** should query. A private source or no published release may legitimately be Unavailable. There should be no automatic download, installation, restart or external scheduled reminder.

## Keep the first test observational

Skip DISM/SFC confirmation, repair, Defender scan/quarantine, startup changes, uninstall, cleanup deletion, software/driver installs and firmware actions during this first pass. Some separately selected non-repair integrity tools can still write Windows diagnostic metadata and require existing administrator access. This checklist does not request those operations.

For a problem, note the source commit/version, exact clicked control, expected behavior, actual state/error and whether Cancel/close settled. Unknown/no rows may be legitimate coverage; a frozen UI, clipped controls, stale success, hidden failure or false up-to-date claim needs investigation. Share minimized text first. Redact personal paths, installed-app identities, hardware identifiers and security details from screenshots or logs. Do not send raw Event Log payloads, dumps, credentials or recovery keys.

In Diagnostics, detail times should show UTC plus a local date/time, numeric offset and zone ID. Compare a known observation with Windows while accounting for the offset on that observation's date. UTC table headings and start-inclusive/end-exclusive query bounds remain unchanged; daylight-saving transitions may give different offsets at the two bounds. Do not change the clock/time zone to manufacture test evidence. The [local validation helper](LOCAL-VALIDATION.md) can check builds/unit tests without Actions; it does not perform these UI/provider checks.

After an ordinary read-only check, the Activity page should show whether its latest log write succeeded. Earlier failed write attempts must remain reported after a later success, without claiming the missing records were replayed. The list keeps only the latest 200 events in memory. Do not edit permissions or create links in your real profile to manufacture a failure; linked-path and rotation failure cases belong in owned development fixtures.

In Installed apps, select an actual loaded row, then filter it out. Its details and reviews must clear; clearing the filter must not restore the old preview. Repeat separately for loaded Windows packages. Refreshing a source must discard prior selection/details while publication is incomplete. Reviews must stay tied to a currently visible row, and search must leave the complete source counts, coverage and captured baseline unchanged. Do not request an uninstall or open an uninstaller to test selection behavior.

If cleanup has an excluded/error row, selecting it should leave candidate recheck, worksheet-add and exact-file exclusion unavailable. Cancelling inspection during cleanup publication must clear its unfinished rows. Do not create personal files or errors just to force this case. For optional disk identity analysis of an already selected observational map, Cancel may end the caller wait while metadata work is still pending; the app should explain that normal close/retry waits for actual settlement. No physical/reclaimable-byte or successful late-result claim should appear. Do not kill a worker or change the PC to force a cancellation race.

The new installer requirements review is a separately chosen online metadata action for one actual Available app offer. It may cache a manifest but must never download/run an installer. A no-applicable result is scoped to User/Interactive options, not up-to-date status. For the remembered MSI recheck, use Remember and Recheck on an already installed supported MSI without uninstalling anything; verify source/context uncertainty and Clear behavior. Changing list selection must not silently retarget the remembered product. Do not uninstall personal software or create artificial registrations to test this feature. Both new workflows still need Windows qualification; the neutral-window tool does not exercise them.

Optional read-only Security check (outside the neutral UI tool): choose a history window and explicitly Read scan-event history. Verify actual retained source metadata and disclosed Partial/denied/disabled/missing/no-match states; change range to ensure previous rows clear without an automatic read. Do not start scans or create detections to fill the panel. Event records are not unique scans, running state, owned requests or clean results. Windows query/stop/close behavior is still unverified.

Optional driver worksheet check (outside the neutral UI tool): after a separately selected configured-service update check, select one real non-firmware driver offer and choose Review selected driver prerequisites. Compare declarations against its source details; mandatory compatibility/version/revision/elevation/dependency/restart/result checks must remain Unknown and there must be no Install button or approval switch. Changing the selected reference or refreshing/interruption must clear the worksheet. This is a snapshot review, not a new query or installation.

Optional hardware planning context check (outside the neutral UI tool): after explicitly reading local hardware, choose RAM/CPU/GPU/storage and Attach detected inventory. Compare displayed actual PC/baseboard/area observations and their sources with the inventory above. No total, free-slot, replaceability, supported-limit or compatibility claim may appear. Edit any planning input or refresh hardware to verify attachment clears; Clear attached context must preserve inventory. No new query or activity entry should occur from attachment. Existing manufacturer/manual prerequisites stay Unknown.
