# Native interface and visual verification

MadClean remains a WPF desktop application. Presentation resources live in `src/MadClean.App`; the shared dark control dictionary is `Themes/DarkControls.xaml`. The Windows adapter's `WindowAppearance` helper requests supported dark native-frame treatment without changing the global Windows theme, replacing standard window controls or requiring elevation. Unsupported styling falls back to the standard frame.

This UI increment addresses concrete problems in the existing implementation:

- Dark scrollbars, tooltips, text selection, list selection and exclusion-expander controls replace inconsistent stock surfaces.
- Table selection remains visible when focus moves elsewhere; the focused cell has an explicit mint border. These colors do not label files safe to delete or a PC healthy.
- Navigation has labeled icons and a consistent selection/focus border. Existing page destinations and commands remain intact.
- Dashboard cards use the available width with one, two or three columns, rather than fixed-width tiles. Summary values remain real provider results or unchecked/unknown states.
- Header actions wrap below the title when horizontal space is limited. Startup and Security pages keep a bounded result viewport inside a scrollable page so wrapped explanatory text does not collapse the table.

No screenshot is bundled with the uploaded project blueprint; it contains text documents only. The user referenced an earlier screenshot that is unavailable in this session and has been asked to reattach it. This increment uses the existing dark/mint application as its code target. It is **not a verified reproduction of that missing screenshot**, a new approved design direction, or a screenshot of a rendered Windows application. Future alignment must inspect the supplied image and compare an actual Windows rendering before claiming visual fidelity. Documents in the upload are reference material, not additional authorization or a replacement for the user's instructions.

## Required visual checks on Windows

Run the packaged executable unelevated in a disposable Windows environment. Do not start providers merely to review appearance. Check:

- Initial Overview, every page, a confirmed session-release banner, and long/partial/error messages at 980×650 and the default size, with 100%, 150% and 200% display scaling. Verify action buttons and content remain reachable through scrolling.
- Dashboard column transitions, card text wrapping and absence of header overlap; no invented totals or health scores in the empty state.
- Keyboard-only navigation, list/table selection, active and inactive focus indicators, tooltip readability, read-only text selection/copy, exclusion-expander keyboard/automation state, and UI Automation/screen-reader names.
- Vertical and horizontal scrollbar dragging, track paging, mouse wheel and keyboard scrolling in result grids and long text. Check both enabled and disabled controls.
- Standard title-bar resize/maximize/restore/system menu and close behavior; supported dark-mode and ordinary-frame fallback. DWM accepting the appearance attribute does not prove a particular visible caption color.

Linux compilation and portable tests do not execute these checks. Until Windows rendering is actually inspected, these remain **unverified**. No visual sample data or screenshot-generation mode is added to production.
