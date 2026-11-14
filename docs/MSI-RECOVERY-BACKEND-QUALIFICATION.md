# Manual recovery-branch MSI backend qualification

This source preparation does not run an installer or enable public uninstall. `MsiUninstallRequestLauncher.ProductionDispatchValidated` remains the literal false; the existing public WPF qualification predicate and historical [activation decision](MSI-PUBLIC-QUALIFICATION-REVIEW.md) are unchanged.

## Exact selectable route

The Windows workflow has a default-false `qualify_recovery_msi_backend` input. It applies only to `recovery/validated-20261112` and a deliberate `workflow_dispatch`. Ordinary recovery pushes and pull requests still skip the MSI job. The existing feature-branch push/manual profile remains separate; this input does not suppress or redefine its behavior.

The dedicated job uses the existing standard `windows-latest` runner, 15-minute bound and read-only repository permission. Its exact manual recovery condition emits `MADCLEAN_MSI_BACKEND_ONLY_VALIDATION=1`; missing, zero, truthy aliases or whitespace variants do not qualify. Both fixture entry and the actual held-proof factory share `MsiQualificationSourceProfile`, requiring the exact ref, manual event and `windows-msi-request` job. Recovery additionally refuses if the actual production dispatch guard ever becomes true. Thus later public activation cannot silently turn this recovery route into a public UI cycle.

Existing hosted-runner/repository/source/run/workspace, fixed GUID/marker/database/payload, held-proof, client/consent and cleanup checks remain independently required. The new flag is only a development test-source fence, not host authentication, general installation consent or a production override. No arbitrary product/path/ref, fallback, package download, changed installer argument or action gate is added.

## Actual effects if explicitly run

Only the existing freshly generated inert current-user MSI is eligible. The fixture verifies its exact database and prior code absence, installs its owned payload, requests removal through the existing backend and observes its authored visible Remove consent, exact client lifetime, independent registration/payload absence and owned teardown. Windows Installer's cache/registration writes for that exact fresh product are expected. No unrelated application, driver/update, user-host maintenance, an elevation request, restart or final distribution is part of the test. Uncertain dispatch/cleanup retains ownership; job timeout is not native cancellation or rollback proof.

The resulting source/run/attempt-bound report must show `uiRequired=false` and no public UI result for recovery. A backend pass cannot qualify the public workflow, all real MSI packages, standard-user/policy coverage, complete removal of arbitrary application data or full roadmap completion. Current source includes post-start uncertainty handling newer than the historical backend pass; that current backend has not been Windows-qualified by this source preparation.

## Finite next action and evidence

A separately coordinated manual run may select the reviewed recovery branch, set `qualify_recovery_msi_backend=true`, and leave `prepare_package=false`. Check the actual run's source SHA against the reviewed checkpoint before interpreting evidence; the branch name alone is not an immutable source identity. This task has not dispatched that run, installed the fixture, operated a host/VM or created a release.

Inspect the fixed MSI report and both backend/public outcome fields independently. Require actual authored consent, client exit, independent exact product/payload absence and settled worker/teardown. Missing artifacts, ambiguous start, incomplete cleanup or absent observations remain unqualified. Do not activate public dispatch from backend evidence; the existing explicit public-activation decision remains separate.

The current tool catalog has no callable GitHub workflow-dispatch capability; supported Git transport can push source/read published evidence but is not a workflow-dispatch API. No fabricated URL, internal endpoint or host automation is used as a substitute. This is a concrete source-ready route for later coordinated execution, not a claim that the runtime test ran.

## Source validation

Independent code/workflow/permissions and documentation review found no blocking issue. The final Release solution build passed with zero warnings/errors. The 44 pure source-profile cases passed with no failures/skips; aggregate Core: 2,572 passed, zero failed, one Windows-only skipped. Python: 367 passed, zero failed, one skipped. Tests cover wrong/similar refs, pushes/PRs/other events, missing/truthy/whitespace flags, wrong jobs and later production activation. They execute no installer, native API or workflow, and do not qualify the existing backend's Windows behavior. No test package or final executable is delivered by this checkpoint.

## Current capability/authorization checkpoint, 2026-11-14

The latest ordinary push run skipped this job; it did not attempt and fail the installer. A read-only `gh auth status --hostname github.com` check reports its configured GH_TOKEN invalid (even though the command exits zero). Git transport works, but no callable workflow-dispatch connector is available. This task has not dispatched the manual route, changed credentials or used a different event to bypass it.

Before coordinated execution, verify authenticated standing authorization for installation/removal of this one fresh inert current-user MSI on the disposable hosted runner. AGENTS describes the exact constrained exception but is not, by itself, a user-authored grant; the latest coordinator message supplies no new permission. If that provenance is unavailable, the required approval is specifically that inert fixture test—not general GitHub connection, public uninstall, a personal-PC action or production activation. The prepared source/test route is already concrete and reviewable; the public activation decision remains separate.
