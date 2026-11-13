# Public MSI qualification decision

The user-facing dispatcher remains disabled: `MsiUninstallRequestLauncher.ProductionDispatchValidated` is still the literal `false`. No environment variable, setting or client control enables it.

The proposed next change is that single literal becoming `true`, followed by the already implemented real WPF qualification cycle on the existing feature branch. The context predicate remains **UserUnmanaged only**. Machine and managed-user products stay unsupported; existing read-only metadata `CanExecute=false` does not change. This is a development candidate, with no distribution ZIP or client-PC operation requested.

## Reviewable behavior

The user must select an actual installed registration, prepare a fresh exact-product preview, acknowledge its effects and confirm. The shared owner rechecks the three exact registry sources, actual current-user/machine MSI contexts, unchanged restrictions/metadata and the held qualified System32 client. One-shot reservation precedes the fixed direct `/x {ProductCode} /qf /norestart` request. No raw uninstall string, shell, silent/basic mode, `runas`, force deletion, automatic retry or restart is added.

Windows Installer controls package-defined changes and prompts. Not every package effect can be previewed, and a successful client exit is not global registration absence, complete removal or rollback proof. Enabling the development dispatcher would let someone running that build request removal of a qualifying, explicitly selected per-user MSI app before the public WPF test has passed. The CI test itself can operate only on its freshly generated inert owned product. While its observer is attached, a mismatched product request is explicitly blocked before scheduling, metadata reads, reservation or process start; it cannot fall through to an ordinary unrelated request. This restriction does not enable the production guard.

## Evidence already available

- Source `bc42d61198dc50d8d2f11ef4efdd81112ced0cd7`, [run 41713698292](https://github.com/MadGenius9/MadClean/actions/runs/41713698292), immutable evidence `3f67cf0769280aa893c7ab116bcf94ddb476b4ec`: actual authored Remove consent, client exit zero, exact product/payload absence, worker settlement and owned teardown passed.
- Independent review confirmed the current immutable arguments, fresh checks, reservation and common launch path match that tested backend. The observation-only UI hook grants no launch permission.
- The prepared WPF cycle uses actual registration refresh, exact owned-row selection, Prepare, acknowledgement and Confirm, then independently verifies the real client, fresh correlated result, window/worker settlement and post-state. It cannot substitute injected results or a reopened PID.
- The combined solution compiled with zero warnings/errors; 1,410 portable .NET tests passed. The tool suite passed 229 tests with one Windows-only skip. These checks do not qualify the public Windows workflow.

## Current block

Automatic approval review rejected the proposed literal activation because the public WPF workflow has not yet qualified and backend evidence alone does not authorize exposing an irreversible uninstall action to users. The rejected command did not run; source and remote branch retain the closed guard. No indirect activation or runtime bypass has been added.

Explicit approval is needed for that development-branch activation before requesting the real public qualification run. It does not authorize uninstalling anything on the user's PC, installing updates, distributing a ZIP or claiming the full roadmap finished.


On 2026-11-04 the user responded “Keep going” to the activation question. A normal approval-reviewed attempt was rejected again because that wording did not clearly authorize the exact irreversible feature. The command did not execute; the guard remains false. No indirect enablement was attempted. Read-only SDK adoption and qualification continued independently. Explicit approval naming the per-user MSI uninstall activation remains the unresolved decision.

## Separate recovery backend preparation

The later [manual recovery backend route](MSI-RECOVERY-BACKEND-QUALIFICATION.md) is default-off, restricted to the exact owned fixture and refuses when the actual production guard is true. It is not a retry of the denied literal activation. No public gate/UI/installer/consent behavior changed and no MSI execution occurred while preparing it. Its purpose is to obtain current backend evidence separately; any future public activation still needs the explicit decision described above.
