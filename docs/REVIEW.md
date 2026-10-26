# Initial foundation review

MadClean starts from an empty repository with a C#/.NET 10 WPF application, a portable core, separate Windows adapters and core boundary tests. It provides a dark six-page read-only workspace with user-initiated local inventory, online update discovery, cancellation, per-provider result states and privacy-limited local activity events.

The Windows adapters read fixed disk capacity, two supported temporary-file roots, Run/RunOnce and Startup-folder entries, Defender provider/history data, WinGet list results, and Windows Update software/driver offers. No mutation APIs, fake production data, elevation, installation or reboot operations are included. Limited startup/provider coverage and Unknown/unavailable/partial results are explicit.

Validation: SDK 10.0.403; locked restore; full Release build with zero warnings/errors; **35 core tests passed on Linux (0 failed/skipped)**; locked self-contained Windows x64 publishing passed. A PE32+ GUI executable and ZIP/checksum were produced. Windows UI and live integrations remain unverified and require the disposable-VM matrix in VALIDATION.md. Windows CI is configured but has not run. The branch is committed locally; no push or release is performed.

Review focus: conservative cleanup scope and filesystem races; localized WinGet table compatibility; actual Defender/WUA responses and permissions; Windows GUI/DPI/accessibility; executable signing before distribution. See INTEGRATIONS.md for official documentation and boundaries, and README.md/AGENTS.md for reproducible commands and safety invariants.
