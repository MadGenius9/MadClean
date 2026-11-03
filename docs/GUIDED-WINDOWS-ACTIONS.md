# Explicit Windows workflow handoffs

This increment adds reviewed navigation into Windows-owned workflows and an official instruction page. It does **not** implement startup changes, application uninstall, Defender scan/quarantine/remediation, update installation, driver replacement, system-file verification or repair. Those roadmap execution stages retain their own prerequisites and validation gates.

The user selects a workflow and sees its exact effect, fixed destination, permissions, limitations and official source before choosing **Open Windows workflow**. Ordinary inventory never invokes these handoffs. No selected application/update identifiers, discovered executable paths, uninstall strings, shell arguments or credentials enter this generic catalog route. A separate [current-user package-family review](PACKAGED-APP-SETTINGS.md) uses its own typed, freshly revalidated descriptor for the documented family-level Settings page; it shares only the private STA owner/pending gate and does not widen this catalog.

## Fixed targets and effects

| Workflow | Exact allowlisted target | Navigation effect only |
| --- | --- | --- |
| Startup | `ms-settings:startupapps` | Open Startup apps settings. No entry is changed. |
| Installed apps | `ms-settings:appsfeatures` | Open Apps & Features / Installed apps settings. No app is selected or uninstalled. |
| Defender protection | `ms-settings:windowsdefender` | Open the Windows Security section of Settings. This is not a claimed direct scan-screen link and requests no scan or protection change. |
| Windows Update | `ms-settings:windowsupdate` | Open Windows Update settings. No update identity, installation or restart command is passed. |
| Optional drivers | `ms-settings:windowsupdate-optionalupdates` | Open Optional updates settings, which can contain more than driver offers. No driver is selected or installed. |
| System-file guide | `https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc` | Open Microsoft's instruction page in the registered browser. No SFC/DISM/tool command is executed. |

The current [Microsoft Settings URI reference](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings) was freshly checked before implementation and confirms all five Settings routes. Its former `windows/uwp/launch-resume/launch-settings-app` address currently redirects to this documented page. Guessed older Windows Security deep-link documentation returned 404, so this increment deliberately uses the documented `ms-settings:windowsdefender` Settings route rather than asserting an unverified `windowsdefender://threat/` scan-screen contract.

The exact [Microsoft SFC command reference](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc) was also checked: running the actual tool requires administrator membership, `/verifyonly` verifies without repair, and `/scannow` attempts repair. Opening its documentation executes none of these commands. See [Windows Diagnostics](WINDOWS-DIAGNOSTICS.md) for the still-separate integrity and repair stages.

## Permissions, trust and status

- The core catalog is immutable and accepts only the exact authored URI for the selected enum task. Alternate casing/escaping, queries, fragments, credentials, authority/default-port aliases, other tasks, action routes, repair/download endpoints and arbitrary handlers are rejected. The documented `windowsupdate-action`, seeker-on-demand and restart routes are deliberately absent.
- The Windows launcher revalidates the catalog immediately before dispatch and uses shell execution with the fixed URI, literal `open` verb and no arguments. It requests no elevation and never calls an installer, uninstaller, scanner, repair executable, arbitrary command shell, process termination or restart operation.
- Shell dispatch runs on one dedicated COM-initialized STA away from the WPF dispatcher and permits only one outstanding handoff. A 15-second caller wait is bounded, but Windows shell execution itself cannot safely be cancelled or rolled back after dispatch begins. A timeout/cancellation after that boundary reports Unknown and may still be followed by the page opening. It does not kill Windows Settings, Windows Security, the browser or any servicing process. A cancellation observed before dispatch prevents the request.
- **Available means only that the shell handoff returned.** The result explicitly says the requested operation was not executed or monitored by MadClean and its completion is Unknown. A returned process handle, no process handle, or an opened page is not proof of an uninstall, scan, install, successful repair or even verified window appearance.
- Windows may contact configured update sources or perform existing policy-driven background work. Windows may require separate user/admin consent for an action selected later inside its UI; organizational policy can restrict availability. The browser follows its own configuration when the official guide is opened. MadClean does not claim those external workflows are zero-write, monitor their contents, or force their completion/restart behavior.
- Errors expose bounded safe codes rather than raw handler paths, arguments or exception text. The existing activity log must remain operation/outcome metadata only.

## Validation limits and remaining work

Portable tests cover exact URI ownership, hostile/ambiguous aliases, unknown enum values, cross-task redirection, immutable catalog boundaries and honest navigation-only descriptors. They do not open any Windows page or run a repair.

Windows handlers, page availability, Windows Security UI placement, browser behavior, policy restrictions and cancellation-after-dispatch must be checked on a disposable Windows VM through explicit user selection. Compilation and core tests alone do not validate those behaviors; no personal PC needs to be accessed during development.

Direct cleanup/deletion and duplicate/shortcut removal, reversible startup actions, selected app uninstall, selected Defender execution, software/driver installation and system repairs remain separate execution milestones. This guidance does not complete or bypass their exact preview, identity/safety/permission, monitored outcome, recovery and Windows validation requirements.
