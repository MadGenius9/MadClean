# First home test — Sunday, November 8

This checklist covers the read-only desktop workflows on your own Windows 11 x64 PC, using your normal account. It does not require running MadClean as administrator. The full roadmap is unfinished. A hosted runner's successful check does not establish that your PC's providers, display scaling or policies behave the same way.

Further GitHub pushes are paused at your request. Local development after commit `e8bc6e2` is not present in that GitHub checkout. Use the exact local source/build being reviewed when available; do not assume a previous CI artifact contains later changes. No new distribution ZIP is being prepared. Build/run instructions are in [README](../README.md#build-and-run); this checklist does not authorize installing SDKs or other software automatically.

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

1. In **Disk map**, choose a small ordinary folder you own, containing a few harmless files and nested folders. Avoid system/application folders for this first test. Select a tile/list row, use Drill, Up and Reset, then **Refresh original folder** while viewing a child.
2. Refresh should return to the original requested scope, clear old selection and optional identity totals, and show a new attempt/receipt. Try Cancel and retry. Logical bytes are not physical allocation or bytes safe to delete.
3. Read desktop installed apps and Windows packages separately. Search, select details and Clear. Searching should change only the displayed list, without a new inventory or combined-source count. Do not launch uninstallers or infer an app disappeared merely because a filter hides it.

## Diagnostics and hardware

1. In **Windows Diagnostics**, read event evidence, change the 1/7/30-day interval, toggle repeated observations and select source/finding details. Changing a range should invalidate old current results. Event counts are not unique-crash counts or a health score.
2. The session summary should show already-collected storage/Defender observations; the three integrity rows should remain unchecked during ordinary inspection. Selecting a row must start no command. Optional **Read WER metadata** may honestly remain Unknown, including when independent stored-report counts are zero.
3. In **Hardware Upgrades**, choose **Read hardware inventory**, select/copy source details, reread and try Cancel. Compare the PC model with Windows About. Reported board version, module count, memory form-factor names and interface labels do not verify PCB revision, free slots, replaceability or compatible upgrades. Form-factor names should retain the actual reported code; an unknown raw memory-type code must not be guessed into DDR generation.
4. If the prerequisite worksheet is included in this local build, try each area and your own budget/use case. All manufacturer/physical checks must remain Unknown. Editing an input must clear the old worksheet. **Clear planning inputs** should clear the entries. No query, recommendation or purchase should occur.

## Explicit online checks

1. Choose the update check yourself. Review Apps, Drivers and BIOS/Firmware separately. Available, Partial, Unknown, Unsupported, Unavailable and Failed must remain distinct. Unchecked/unmapped software must never be marked up to date.
2. Try cancellation. Optionally retry while offline using your usual network controls; restore connectivity afterward. Offline or private-source access must yield honest uncertainty, without credentials being requested or embedded by MadClean.
3. **MadClean updates** must say **Update checking not configured** when no trusted release source is configured. There should be no automatic download, installation, restart or external scheduled reminder.

## Keep the first test observational

Skip DISM/SFC confirmation, repair, Defender scan/quarantine, startup changes, uninstall, cleanup deletion, software/driver installs and firmware actions during this first pass. Some separately selected non-repair integrity tools can still write Windows diagnostic metadata and require existing administrator access. This checklist does not request those operations.

For a problem, note the source commit/version, exact clicked control, expected behavior, actual state/error and whether Cancel/close settled. Unknown/no rows may be legitimate coverage; a frozen UI, clipped controls, stale success, hidden failure or false up-to-date claim needs investigation. Share minimized text first. Redact personal paths, installed-app identities, hardware identifiers and security details from screenshots or logs. Do not send raw Event Log payloads, dumps, credentials or recovery keys.
