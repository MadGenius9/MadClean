"""Publish bounded CI evidence to the existing private verification branch.

This is a development-only tool for trusted clean CI checkouts, not a hostile-filesystem broker.
It never exports production inventory, credentials or arbitrary runner files.
"""
from __future__ import annotations
import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import struct
import subprocess
import sys
import zlib
import xml.etree.ElementTree as ET

_diagnostic_spec = importlib.util.spec_from_file_location("madclean_ci_diagnostics", Path(__file__).with_name("ci_diagnostics.py"))
ci_diagnostics = importlib.util.module_from_spec(_diagnostic_spec)
_diagnostic_spec.loader.exec_module(ci_diagnostics)

_component_spec = importlib.util.spec_from_file_location("madclean_component_store_evidence", Path(__file__).with_name("component_store_evidence.py"))
component_store_evidence = importlib.util.module_from_spec(_component_spec)
_component_spec.loader.exec_module(component_store_evidence)

REPOSITORY = "MadGenius9/MadClean"
BRANCH = "madclean-verification"
SOURCE_REF = "refs/heads/feat/phase-one-foundation"
IMAGES = ("overview-wide.png", "overview-compact.png", "tool-11.png", "tool-12.png", "tool-13.png", "tool-14.png", "tool-15.png", "tool-16.png", "tool-17.png", "tool-18.png", "tool-19.png", "tool-20.png", "tool-21.png", "tool-22.png", "tool-23.png", "tool-25.png", "tool-26.png", "tool-27.png", "tool-28.png")
OWNED = ("latest.json", "report.json", "core-tests.json", "ci-diagnostics.json", "native-ci-diagnostics.json", "msi-request.json", "component-store.json", "component-scan.json", *IMAGES)
REQUIRED_CHECKS = {
    "ui-wide-size", "ui-initial-state", "ui-compact-layout", "ui-primary-navigation", "ui-tool-navigation", "ui-tool-popup", "ui-installed-app-search", "ui-packaged-app-search", "ui-packaged-settings-preview-interaction", "ui-component-store-preview-interaction", "ui-inspection-evidence", "ui-shortcut-file-evidence", "ui-duplicate-pair-recheck",
    "bounded-registration-fixture", "defender-status-contract", "defender-scan-history-contract", "storage-inspection-evidence-contract", "defender-inspection-evidence-contract", "installed-registration-contract", "msi-registration-contract", "msi-action-binding-contract", "packaged-application-contract", "leftover-content-fixture", "disk-identity-fixture", "wer-v1-managed-layout", "wer-store-read-contract", "wer-store-count-contract", "hardware-metadata-contract", "startup-fixture-inventory", "startup-fixture-preview", "startup-fixture-mutation-gate", "startup-fixture-scope", "startup-fixture-teardown", "startup-transaction-handle-probe", "startup-transaction-interference-probe", "system-tool-execution-gate", "system-tool-identity-contract", "system-tool-embedded-version-fixture", "system-tool-catalog-observation", "system-tool-held-catalog-observation",
    "duplicate-content-fixtures", "duplicate-hardlink-fixture", "duplicate-pair-review-fixture", "duplicate-pair-recheck-fixture", "shortcut-directory-sharing-observation", "system-tool-process-observation-fixture", "system-tool-journal-store-fixture", "system-tool-journal-cross-instance-fixture", "system-tool-journal-lifecycle-fixture", "system-tool-private-journal-scope", "owned-relative-directory-create-control", "owned-private-parent-security-preflight", "published-executable-startup", "update-discovery-provider-contract", "wua-offer-metadata-contract", "winget-query-observation-contract", "device-driver-properties-contract", "ui-device-driver-inventory-interaction", "dism-powershell-capability-contract", "dism-builtin-import-control-contract", "cleanup-file-evidence-fixture", "cleanup-disposition-owned-probe", "cleanup-mapped-disposition-owned-probe", "cleanup-confirmation-owned-protocol", "ui-cleanup-file-evidence", "ui-cleanup-file-evidence-interaction", "ui-cleanup-selection-review-interaction", "ui-cleanup-file-evidence-selection-race", "ui-cleanup-file-evidence-cancellation", "ui-diagnostic-channel-coverage-interaction", "ui-msi-action-binding-interaction", "startup-transaction-namespace-observation", "shortcut-existing-local-fixture",
    "shortcut-missing-local-fixture", "shortcut-no-mutation", "shortcut-rename-handle-probe", "shortcut-rename-win32-absolute-control", "shortcut-rename-cpp-control", "shortcut-rename-nt-control", "shortcut-rename-source-guard", "shortcut-rename-destination-guard", "shortcut-rename-ancestor-guard", "shortcut-mapped-write-witness", "shortcut-selected-recheck-fixture", "shortcut-file-evidence-fixture", "shortcut-dacl-fixture", "diagnostics-metadata-contract", "fixture-teardown",
}
RESULTS = {"success", "failure", "cancelled", "skipped"}
class EvidenceError(ValueError):
    pass

def reject_unless(condition, code):
    if not condition:
        raise EvidenceError(code)

def safe_path(path: Path, missing=False) -> Path:
    path = Path(os.path.abspath(path))
    for part in (*reversed(path.parents), path):
        try:
            data = part.lstat()
        except FileNotFoundError:
            reject_unless(missing, "missing-path")
            continue
        reject_unless(not stat.S_ISLNK(data.st_mode) and not (getattr(data, "st_file_attributes", 0) & 0x400), "reparse-path")
    return path

def unique_object(pairs):
    result = {}
    for key, value in pairs:
        reject_unless(key not in result, "duplicate-json-property")
        result[key] = value
    return result

def read_json(path, maximum):
    path = safe_path(path)
    reject_unless(path.is_file() and path.stat().st_size <= maximum, "invalid-json-file")
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)
    except (UnicodeError, json.JSONDecodeError, RecursionError):
        raise EvidenceError("invalid-json") from None

def git(root, *arguments):
    result = subprocess.run(["git", "-C", str(root), *arguments], text=True, capture_output=True)
    reject_unless(result.returncode == 0, "git-operation-failed")
    return result.stdout.strip()

def validate_context(environment):
    reject_unless(environment.get("GITHUB_REPOSITORY") == REPOSITORY, "repository-mismatch")
    reject_unless(environment.get("GITHUB_REF") == SOURCE_REF and environment.get("GITHUB_EVENT_NAME") in {"push", "workflow_dispatch"}, "untrusted-trigger")
    sha = environment.get("GITHUB_SHA", "")
    run = environment.get("GITHUB_RUN_ID", "")
    attempt = environment.get("GITHUB_RUN_ATTEMPT", "")
    reject_unless(re.fullmatch(r"[a-f0-9]{40}", sha) is not None, "invalid-source")
    reject_unless(re.fullmatch(r"[0-9]{1,32}", run) is not None and int(run) > 0, "invalid-run")
    reject_unless(re.fullmatch(r"[0-9]{1,10}", attempt) is not None and int(attempt) > 0, "invalid-attempt")
    return sha, run, int(attempt)

def validate_checkout(root):
    root = safe_path(root)
    reject_unless(root.is_dir(), "invalid-checkout")
    # Git for Windows canonicalizes 8.3 aliases; normcase does not. Inspect both
    # ancestor chains first, then compare actual directory identity rather than spelling.
    reported_root = safe_path(Path(git(root, "rev-parse", "--show-toplevel")))
    try:
        same_root = os.path.samefile(reported_root, root)
    except OSError:
        raise EvidenceError("unexpected-worktree") from None
    reject_unless(same_root, "unexpected-worktree")
    reject_unless(git(root, "symbolic-ref", "--short", "HEAD") == BRANCH, "unexpected-branch")
    reject_unless(git(root, "remote", "get-url", "origin").rstrip("/") in {f"https://github.com/{REPOSITORY}", f"https://github.com/{REPOSITORY}.git"}, "unexpected-origin")
    manifest = read_json(root / "verification-manifest.json", 8192)
    reject_unless(isinstance(manifest, dict) and type(manifest.get("formatVersion")) is int and manifest["formatVersion"] == 1 and manifest.get("purpose") == "MadClean Windows validation" and manifest.get("sourceBranch") == "feat/phase-one-foundation", "unexpected-manifest")
    for name in OWNED:
        path = safe_path(root / name, missing=True)
        reject_unless(not path.exists() or path.is_file(), "invalid-destination")
    return root

def png_size(data):
    reject_unless(57 <= len(data) <= 8_388_608 and data[:8] == b"\x89PNG\r\n\x1a\n", "invalid-png")
    position = 8
    dimensions = None
    idat = False
    ended = False
    while position < len(data):
        reject_unless(position + 12 <= len(data), "truncated-png")
        length = struct.unpack_from(">I", data, position)[0]
        kind = data[position + 4:position + 8]
        end = position + 12 + length
        reject_unless(end <= len(data), "truncated-png")
        payload = data[position + 8:position + 8 + length]
        crc = struct.unpack_from(">I", data, position + 8 + length)[0]
        reject_unless(zlib.crc32(kind + payload) & 0xffffffff == crc, "png-checksum")
        if position == 8:
            reject_unless(kind == b"IHDR" and length == 13, "invalid-png-header")
            dimensions = struct.unpack_from(">II", payload)
            reject_unless(all(100 <= size <= 3000 for size in dimensions), "png-dimensions")
        elif kind == b"IHDR":
            raise EvidenceError("duplicate-png-header")
        if kind == b"IDAT":
            idat = True
        if kind == b"IEND":
            reject_unless(length == 0 and end == len(data), "invalid-png-end")
            ended = True
            break
        position = end
    reject_unless(ended and idat and dimensions is not None, "incomplete-png")
    return dimensions

def validate_report(download, context):
    sha, run, attempt = context
    path = download / "report.json"
    if not path.exists():
        return None, {}
    report = read_json(path, 262_144)
    fields = {"formatVersion", "sourceCommit", "runId", "runAttempt", "startedUtc", "completedUtc", "passed", "state", "checks", "screenshots", "limits"}
    reject_unless(isinstance(report, dict) and set(report) == fields and type(report.get("formatVersion")) is int and report["formatVersion"] in {1, 2, 3}, "report-schema")
    times = []
    for field in ("startedUtc", "completedUtc"):
        value = report[field]
        reject_unless(isinstance(value, str) and re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)", value) is not None, "report-time")
        try:
            times.append(dt.datetime.fromisoformat(value.replace("Z", "+00:00")))
        except ValueError:
            raise EvidenceError("report-time") from None
    reject_unless(times[0] <= times[1], "report-time-order")
    reject_unless(isinstance(report["limits"], str) and len(report["limits"]) <= 512, "report-limits")
    reject_unless(report.get("sourceCommit") == sha and report.get("runId") == run and type(report.get("runAttempt")) is int and report["runAttempt"] == attempt, "report-context")
    checks = report.get("checks")
    reject_unless(isinstance(checks, list) and 0 < len(checks) <= 200, "report-checks")
    names = set()
    for check in checks:
        reject_unless(isinstance(check, dict) and set(check) == {"name", "state", "detail"}, "check-schema")
        name, state, detail = check.get("name"), check.get("state"), check.get("detail")
        reject_unless(isinstance(name, str) and re.fullmatch(r"[a-z0-9-]{1,80}", name) is not None and name not in names, "check-name")
        names.add(name)
        reject_unless(isinstance(state, str) and state in {"passed", "failed", "inconclusive"} and isinstance(detail, str) and len(detail) <= 1000, "check-state")
    computed = "failed" if any(c["state"] == "failed" for c in checks) else "inconclusive" if any(c["state"] == "inconclusive" for c in checks) else "passed"
    reject_unless(type(report.get("passed")) is bool and report["passed"] == (computed == "passed") and report.get("state") == computed, "contradictory-report")
    if report["passed"]:
        required = REQUIRED_CHECKS | ({"structured-winget-discovery-contract"} if report["formatVersion"] >= 2 else set())
        if report["formatVersion"] >= 3:
            required |= {"structured-winget-published-query-contract"}
        reject_unless(required <= names, "missing-required-check")
    images = report.get("screenshots")
    reject_unless(isinstance(images, list) and len(images) <= 32, "report-images")
    found = set()
    admitted = {}
    for image in images:
        reject_unless(isinstance(image, dict) and set(image) == {"file", "width", "height", "sha256"}, "image-schema")
        name, digest = image.get("file"), image.get("sha256")
        reject_unless(isinstance(name, str) and re.fullmatch(r"(?:overview-(?:wide|compact)|tool-[0-9]{2})\.png", name) is not None and name not in found, "image-name")
        reject_unless(isinstance(digest, str) and re.fullmatch(r"[a-f0-9]{64}", digest) is not None, "image-hash-schema")
        found.add(name)
        source = safe_path(download / name)
        reject_unless(source.is_file() and source.stat().st_size <= 8_388_608, "image-file")
        data = source.read_bytes()
        reject_unless(hashlib.sha256(data).hexdigest() == digest, "image-hash")
        reject_unless(png_size(data) == (image.get("width"), image.get("height")), "image-size-mismatch")
        if name in IMAGES:
            admitted[name] = data
    if report["passed"]:
        reject_unless(set(IMAGES) <= found, "missing-required-capture")
    report["limits"] = "Real WPF client captures and isolated native fixture checks; no servicing, installation, real startup changes or personal-PC actions. No DWM/OS-DPI, exhaustive interaction, healthy/up-to-date or production-action readiness verdict."
    return report, admitted

class RejectDtdTreeBuilder(ET.TreeBuilder):
    def doctype(self, name, public_id, system_id):
        raise EvidenceError("unsupported-trx-markup")

def core_test_summary(directory):
    if directory is None:
        return None
    path = safe_path(directory / "core-windows.trx", missing=True)
    if not path.exists():
        return None
    reject_unless(path.is_file() and path.stat().st_size <= 5_242_880, "invalid-trx-file")
    data = path.read_bytes()
    reject_unless(b"<!DOCTYPE" not in data.upper() and b"<!ENTITY" not in data.upper(), "unsupported-trx-markup")
    try:
        document = ET.fromstring(data, parser=ET.XMLParser(target=RejectDtdTreeBuilder()))
    except ET.ParseError:
        raise EvidenceError("invalid-trx-xml") from None
    namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    counters = document.find("t:ResultSummary/t:Counters", namespace)
    reject_unless(counters is not None, "missing-trx-counters")
    result = {"scope": "Development portable tests on Windows; not production inventory or GUI/provider proof.", "counts": {}, "failures": []}
    for name in ("total", "executed", "passed", "failed", "notExecuted"):
        raw = counters.get(name, "")
        reject_unless(re.fullmatch(r"[0-9]{1,8}", raw) is not None, "invalid-trx-counter")
        result["counts"][name] = int(raw)
    # Preserve bounded numeric evidence independently of adapter accounting.
    # These names are the Microsoft VSTest TestRunSummary counter schema; unknown
    # attribute names/values and arbitrary outcome text are never forwarded.
    counter_names = ("total", "executed", "passed", "failed", "error", "timeout", "aborted",
                     "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted",
                     "disconnected", "warning", "completed", "inProgress", "pending")
    result["counterAttributes"] = {}
    for name in counter_names:
        if name in counters.attrib:
            raw = counters.get(name)
            reject_unless(re.fullmatch(r"[0-9]{1,8}", raw) is not None, "invalid-trx-counter")
            result["counterAttributes"][name] = int(raw)
    result["unrecognizedCounterAttributeCount"] = len(set(counters.attrib) - set(counter_names))
    outcome_names = {"Error", "Failed", "Timeout", "Aborted", "Inconclusive", "PassedButRunAborted",
                     "NotRunnable", "NotExecuted", "Disconnected", "Warning", "Passed", "Completed",
                     "InProgress", "Pending"}
    tests = document.findall("t:Results/t:UnitTestResult", namespace)
    reject_unless(len(tests) <= 100_000, "trx-result-limit")
    result["resultRecordCount"] = len(tests)
    result["resultOutcomeTally"] = {}
    for test in tests:
        outcome = test.get("outcome")
        key = outcome if outcome in outcome_names else "Unknown"
        result["resultOutcomeTally"][key] = result["resultOutcomeTally"].get(key, 0) + 1
    for test in tests:
        if test.get("outcome") != "Failed":
            continue
        name = test.get("testName", "Unknown test")
        message = test.findtext("t:Output/t:ErrorInfo/t:Message", default="No error message retained.", namespaces=namespace)
        result["failures"].append({"name": name[:256], "message": message[:2000]})
        if len(result["failures"]) == 32:
            break
    counts = result["counts"]
    tally = result["resultOutcomeTally"]
    not_executed = tally.get("NotExecuted", 0)
    # Microsoft VSTest's TestRunSummary serializes notExecuted as zero even
    # when individual results are NotExecuted. Reconcile every result instead
    # of guessing missing tests from total-executed or treating them as passes.
    # Converter also maps None/NotFound to NotExecuted: this is not proof of an
    # intentionally skipped test. Both raw counters and actual tally remain visible.
    result["accountingNote"] = "Counts reconciled against every result record. VSTest may leave the raw notExecuted counter zero; per-record NotExecuted means not executed, not necessarily an intentional skip or pass."
    other_counters = set(counter_names) - {"total", "executed", "passed", "failed", "notExecuted"}
    result["countsConsistent"] = (
        len(tests) == counts["total"] and
        set(tally) <= {"Passed", "Failed", "NotExecuted"} and
        tally.get("Passed", 0) == counts["passed"] and
        tally.get("Failed", 0) == counts["failed"] and
        counts["executed"] == counts["passed"] + counts["failed"] and
        counts["total"] == counts["executed"] + not_executed and
        counts["notExecuted"] in {0, not_executed} and
        all(result["counterAttributes"].get(name, 0) == 0 for name in other_counters) and
        result["unrecognizedCounterAttributeCount"] == 0
    )
    result["passed"] = result["countsConsistent"] and counts["executed"] > 0 and counts["failed"] == 0 and counts["passed"] == counts["executed"]
    return result


MSI_FACTS = {"packageBuilt", "databaseVerified", "preexistingCodeAbsent", "installReturned", "installedContextObserved", "payloadObserved", "visibleConsentObserved", "clientStarted", "clientExited", "postCodeAbsent", "payloadAbsent", "workerSettled", "ownedTeardown"}
MSI_STAGES = {"OwnedSetup", "BuildOwnedDatabase", "ProductCollision", "InstallOwnedProduct", "InstallUnverified", "InstalledEvidenceUnknown", "PrepareRequest", "SelectionUnknown", "VisibleRequest", "RequestUnsettled", "ObservePostState", "PostStateUnverified", "OwnershipUnverified", "Complete", "QualificationUnavailable", "VisibleConsentUnverified"}
MSI_TARGET_FIELDS = {
    "resultState", "state", "reason", "targetCoverageState", "lookupCount", "presentCount", "absentCount", "unknownCount",
    "sourceMatched", "systemComponent", "windowsInstaller", "noRemove", "nativeEnumerationComplete", "nativeIssueCount",
    "nativeInstanceCount", "observedContext", "historicalGlobalState", "currentGlobalWasRead", "currentGlobalState",
}
MSI_TARGET_REASONS = {"InvalidScope", "ArpLookupUnknown", "MultipleArpRegistrations", "SelectedRegistrationMissing",
    "RequiredArpMetadataUnknown", "SelectionChanged", "RemovalRestricted", "HiddenComponent", "NativeCoverageIncomplete",
    "MultipleNativeContexts", "NativeProductNotInstalled", "NativeMetadataMismatch", "ObservedMatch"}


def validate_msi_target(value):
    if value is None:
        return False
    reject_unless(isinstance(value, dict) and set(value) == MSI_TARGET_FIELDS, "msi-target-schema")
    for name, allowed in {
        "resultState": {"Available", "Partial", "Unavailable", "Unknown", "Unsupported", "Failed", "Cancelled"},
        "state": {"ObservedMatch", "Blocked", "Unknown"}, "reason": MSI_TARGET_REASONS,
        "targetCoverageState": {"Available", "Partial"}, "historicalGlobalState": {"Available", "Partial"},
        "currentGlobalState": {"Unknown"},
    }.items():
        reject_unless(value[name] is None or isinstance(value[name], str) and value[name] in allowed, "msi-target-label")
    reject_unless(value["resultState"] is not None, "msi-target-result")
    for name in ("sourceMatched", "systemComponent", "windowsInstaller", "noRemove", "nativeEnumerationComplete", "currentGlobalWasRead"):
        reject_unless(value[name] is None or type(value[name]) is bool, "msi-target-fact")
    for name in ("lookupCount", "presentCount", "absentCount", "unknownCount", "nativeInstanceCount", "nativeIssueCount", "observedContext"):
        allowed = {1, 2, 4} if name == "observedContext" else range(33 if name == "nativeIssueCount" else 4)
        reject_unless(value[name] is None or type(value[name]) is int and value[name] in allowed, "msi-target-count")
    reject_unless(value["state"] is not None, "msi-target-unobserved")
    reject_unless(all(value[name] is not None for name in ("reason", "targetCoverageState", "lookupCount", "presentCount", "absentCount", "unknownCount", "nativeEnumerationComplete", "nativeIssueCount", "nativeInstanceCount", "historicalGlobalState", "currentGlobalWasRead", "currentGlobalState")), "msi-target-observed")
    reject_unless(value["lookupCount"] == 3 and value["presentCount"] + value["absentCount"] + value["unknownCount"] == 3, "msi-target-lookup-coherence")
    reject_unless((value["nativeInstanceCount"] == 1) == (value["observedContext"] is not None), "msi-target-context-coherence")
    reject_unless(value["currentGlobalWasRead"] is False and value["currentGlobalState"] == "Unknown", "msi-target-global-coherence")
    if value["presentCount"] != 1:
        reject_unless(all(value[name] is None for name in ("sourceMatched", "systemComponent", "windowsInstaller", "noRemove")), "msi-target-single-coherence")
    else:
        reject_unless(value["sourceMatched"] is not None, "msi-target-single-coherence")
    return (value["state"] == "ObservedMatch" and value["reason"] == "ObservedMatch" and value["targetCoverageState"] == "Available"
        and value["resultState"] == value["historicalGlobalState"] and value["presentCount"] == 1 and value["absentCount"] == 2
        and value["unknownCount"] == 0 and value["sourceMatched"] is True and value["systemComponent"] is False
        and value["windowsInstaller"] is True and value["noRemove"] is False and value["nativeEnumerationComplete"] is True
        and value["nativeIssueCount"] == 0 and value["nativeInstanceCount"] == 1 and value["observedContext"] == 2)


MSI_CONSENT_BOOLEANS = {
    "ownClientPidObserved", "ownedWindowSeen", "authoredTitleSeen", "authoredWindowVisibleEnabled", "removeButtonSeen",
    "removeButtonVisibleEnabled", "removePatternSeen", "removeInvoked", "genericTitleSeen", "genericWindowVisibleEnabled",
    "genericPromptMatched", "yesControlSeen", "yesControlVisibleEnabled", "yesPatternSeen", "yesInvoked", "enumerationFailed",
}


def validate_msi_consent(value, version=4):
    if value is None:
        return False
    fields = MSI_CONSENT_BOOLEANS | {"pollCount", "managedExceptionHResult"}
    if version >= 5:
        fields |= {"yesSelectionKind", "yesCandidateCount"}
    reject_unless(isinstance(value, dict) and set(value) == fields, "msi-consent-schema")
    for field in MSI_CONSENT_BOOLEANS:
        reject_unless(value[field] is None or type(value[field]) is bool, "msi-consent-fact")
    reject_unless(value["pollCount"] is None or type(value["pollCount"]) is int and 0 <= value["pollCount"] <= 4096, "msi-consent-count")
    reject_unless(value["managedExceptionHResult"] is None or type(value["managedExceptionHResult"]) is int and -(2**31) <= value["managedExceptionHResult"] < 2**31, "msi-consent-code")
    prerequisites = {
        "ownedWindowSeen": ("ownClientPidObserved",), "authoredTitleSeen": ("ownedWindowSeen",),
        "authoredWindowVisibleEnabled": ("authoredTitleSeen",), "removeButtonSeen": ("authoredWindowVisibleEnabled",),
        "removeButtonVisibleEnabled": ("removeButtonSeen",), "removePatternSeen": ("removeButtonVisibleEnabled",),
        "removeInvoked": ("removePatternSeen",), "genericTitleSeen": ("ownedWindowSeen",),
        "genericWindowVisibleEnabled": ("genericTitleSeen",), "genericPromptMatched": ("genericWindowVisibleEnabled",),
        "yesControlSeen": ("genericWindowVisibleEnabled",), "yesControlVisibleEnabled": ("yesControlSeen", "genericPromptMatched"),
        "yesPatternSeen": ("yesControlVisibleEnabled",), "yesInvoked": ("yesPatternSeen",),
    }
    for field, previous in prerequisites.items():
        reject_unless(value[field] is not True or all(value[name] is True for name in previous), "msi-consent-order")
    if version >= 5:
        selection, count = value["yesSelectionKind"], value["yesCandidateCount"]
        reject_unless(selection is None or type(selection) is str and selection == "UniqueButtonName", "msi-consent-selector")
        reject_unless(count is None or type(count) is int and 0 <= count <= 64, "msi-consent-candidates")
        yes_fields = ("yesControlSeen", "yesControlVisibleEnabled", "yesPatternSeen", "yesInvoked")
        if selection is None:
            reject_unless(count is None and all(value[name] is None for name in yes_fields), "msi-consent-selector-unobserved")
        else:
            reject_unless(value["genericPromptMatched"] is True and value["genericWindowVisibleEnabled"] is True, "msi-consent-selector-scope")
        if count is not None:
            reject_unless(selection == "UniqueButtonName" and value["yesControlSeen"] is not None, "msi-consent-candidate-scope")
            reject_unless(count == 0 or value["yesControlSeen"] is True, "msi-consent-candidate-presence")
        if value["yesInvoked"] is True:
            reject_unless(selection == "UniqueButtonName" and count == 1, "msi-consent-unique-yes")
    if value["pollCount"] == 0:
        reject_unless(all(value[field] is None for field in MSI_CONSENT_BOOLEANS - {"ownClientPidObserved"}), "msi-consent-unpolled")
    return value["enumerationFailed"] is False and value["managedExceptionHResult"] is None and all(value[field] is True for field in ("ownClientPidObserved", "ownedWindowSeen", "authoredTitleSeen", "authoredWindowVisibleEnabled", "removeButtonSeen", "removeButtonVisibleEnabled", "removePatternSeen", "removeInvoked"))


MSI_UI_FACTS = {"installReturned", "installedContextObserved", "payloadObserved", "inventoryRefreshObserved",
    "ownedSelectionObserved", "prepareObserved", "acknowledgementObserved", "confirmObserved", "resultReturned",
    "freshResultObserved", "clientObserverClaimed", "settled", "windowClosed", "visibleConsentObserved",
    "clientStarted", "clientExited", "postCodeAbsent", "payloadAbsent"}
MSI_UI_CODES = {"installCode", "clientExitCode", "exceptionHResult", "toolNativeTrustResult",
    "toolFileTrustResult", "toolCatalogTrustResult", "toolCleanupTrustResult"}
MSI_UI_LABELS = {
    "clientRequestState": {"NotStarted", "Blocked", "Preparing", "ClientStarted", "ClientExited", "MonitoringUnknown"},
    "toolIdentityStage": {"Unknown", "SystemDirectory", "FileLease", "Signature", "Fingerprint", "VersionMetadata", "Completed"},
    "toolIdentityReason": {"Unspecified", "Observed", "NativeTrustNotEstablished", "SignerInspectionUnavailable", "SignerNotQualified",
        "OriginalFilenameMismatch", "IdentityBoundsOrPath", "OtherUnavailable", "WaitExpired", "PendingWorker", "UnsupportedPlatform", "TrustCleanupUnavailable"},
}


def validate_msi_ui(value):
    if value is None:
        return False
    reject_unless(isinstance(value, dict) and set(value) == MSI_UI_FACTS | MSI_UI_CODES | set(MSI_UI_LABELS) | {"consentObservation"}, "msi-ui-schema")
    for name in MSI_UI_FACTS:
        reject_unless(value[name] is None or type(value[name]) is bool, "msi-ui-fact")
    for name in MSI_UI_CODES:
        reject_unless(value[name] is None or type(value[name]) is int and -(2**31) <= value[name] < 2**31, "msi-ui-code")
    for name, allowed in MSI_UI_LABELS.items():
        reject_unless(value[name] is None or type(value[name]) is str and value[name] in allowed, "msi-ui-label")
    consent = validate_msi_consent(value["consentObservation"], 5)
    reject_unless((value["installReturned"] is True) == (value["installCode"] is not None), "msi-ui-install")
    reject_unless(value["clientExitCode"] is None or value["clientExited"] is True, "msi-ui-exit")
    prerequisites = {
        "installedContextObserved": ("installReturned",), "payloadObserved": ("installReturned",),
        "ownedSelectionObserved": ("inventoryRefreshObserved",), "prepareObserved": ("ownedSelectionObserved",),
        "acknowledgementObserved": ("prepareObserved",), "confirmObserved": ("acknowledgementObserved",),
        "resultReturned": ("confirmObserved",), "freshResultObserved": ("resultReturned", "clientObserverClaimed"),
        "clientStarted": ("confirmObserved", "clientObserverClaimed", "installedContextObserved", "payloadObserved"),
        "clientExited": ("clientStarted",), "visibleConsentObserved": ("clientStarted",),
        "postCodeAbsent": ("clientExited",), "payloadAbsent": ("clientExited",),
    }
    for name, required in prerequisites.items():
        reject_unless(value[name] is not True or all(value[item] is True for item in required), "msi-ui-order")
    reject_unless(value["consentObservation"] is None or value["clientStarted"] is True, "msi-ui-consent-client")
    reject_unless(value["visibleConsentObserved"] is not True or consent, "msi-ui-consent")
    if value["consentObservation"] is not None:
        reject_unless(value["consentObservation"]["removeInvoked"] is not True or value["visibleConsentObserved"] is True, "msi-ui-consent-invoked")
    reject_unless((value["toolIdentityStage"] is None) == (value["toolIdentityReason"] is None), "msi-ui-tool")
    if value["toolIdentityStage"] is None:
        reject_unless(all(value[name] is None for name in MSI_UI_CODES - {"installCode", "clientExitCode", "exceptionHResult"}), "msi-ui-tool")
    return (all(value[name] is True for name in MSI_UI_FACTS) and consent
        and value["installCode"] == 0 and value["clientExitCode"] == 0 and value["exceptionHResult"] is None
        and value["clientRequestState"] == "ClientExited" and value["toolIdentityStage"] == "Completed"
        and value["toolIdentityReason"] == "Observed" and value["toolNativeTrustResult"] == 0)


def validate_msi_report(directory, context):
    if directory is None:
        return None
    path = safe_path(Path(directory), missing=True) / "report.json"
    if not path.exists():
        return None
    value = read_json(path, 8192)
    fields = {"formatVersion", "sourceCommit", "runId", "runAttempt", "startedUtc", "observedUtc", "state", "stage", "installCode", "clientExitCode", "selectionSourceState", "bindingResultState", "bindingReportState", "currentSnapshotState", "previewPrepared", "clientRequestState", "failureKind", "failureCode", "databaseOperation", "toolIdentityStage", "toolIdentityReason", "toolNativeTrustResult", "toolFileTrustResult", "toolCatalogTrustResult", "toolCleanupTrustResult", "bindingReason", "bindingChangedFields", "selectionNoRemove", "bindingNoRemove", "bindingInstanceCount", "expectedMask", "observedContext"} | MSI_FACTS
    reject_unless(isinstance(value, dict) and type(value.get("formatVersion")) is int and value["formatVersion"] in {2, 3, 4, 5, 6}, "msi-report-schema")
    version = value["formatVersion"]
    reject_unless(set(value) == fields | ({"targetPreparation"} if version >= 3 else set()) | ({"consentObservation"} if version >= 4 else set()) | ({"uiRequired", "ui"} if version >= 6 else set()), "msi-report-schema")
    if version >= 6:
        reject_unless(type(value["uiRequired"]) is bool and (value["uiRequired"] or value["ui"] is None), "msi-ui-mode")
        ui_qualified = validate_msi_ui(value["ui"])
        reject_unless(value["state"] != "passed" or not value["uiRequired"] or ui_qualified, "msi-ui-pass")
    target_qualified = validate_msi_target(value["targetPreparation"]) if version >= 3 else False
    consent_qualified = validate_msi_consent(value["consentObservation"], version) if version >= 4 else False
    reject_unless((value["sourceCommit"], value["runId"], value["runAttempt"]) == context and type(value["runAttempt"]) is int, "msi-report-context")
    reject_unless(isinstance(value["state"], str) and value["state"] in {"passed", "failed", "inconclusive"} and isinstance(value["stage"], str) and value["stage"] in MSI_STAGES, "msi-report-state")
    for name in MSI_FACTS:
        reject_unless(value[name] is None or type(value[name]) is bool, "msi-report-fact")
    for name in ("selectionNoRemove", "bindingNoRemove"):
        reject_unless(value[name] is None or type(value[name]) is bool, "msi-restriction-fact")
    for name, allowed in (("bindingInstanceCount", {0, 1, 2, 3}), ("expectedMask", {3, 4}), ("observedContext", {1, 2, 4})):
        reject_unless(value[name] is None or type(value[name]) is int and value[name] in allowed, "msi-binding-count-mask")
    reject_unless(value["bindingChangedFields"] is None or type(value["bindingChangedFields"]) is int and 0 <= value["bindingChangedFields"] <= 1023, "msi-binding-flags")
    reject_unless(value["previewPrepared"] is None or type(value["previewPrepared"]) is bool, "msi-preview-fact")
    labels = {
        "selectionSourceState": {"Available", "Partial", "Unknown", "Unavailable", "Unsupported", "Failed", "Cancelled"},
        "bindingResultState": {"Available", "Partial", "Unknown", "Unavailable", "Unsupported", "Failed", "Cancelled"},
        "currentSnapshotState": {"Available", "Partial", "Unknown", "Unavailable", "Unsupported", "Failed", "Cancelled"},
        "bindingReportState": {"Bound", "Blocked", "Unknown"},
        "bindingReason": {"Unknown", "SourceUnavailable", "SelectionChanged", "RemovalRestricted", "RemovalRestrictionUnknown", "CoverageIncomplete", "MultipleContexts", "SourceContextMismatch", "MetadataNotMatched", "Bound"},
        "clientRequestState": {"NotStarted", "Blocked", "Preparing", "ClientStarted", "ClientExited", "MonitoringUnknown"},
        "failureKind": {"NativeWin32", "OwnedEvidenceUnavailable", "ObservationUnavailable", "OwnedInvariantFailed"},
        "toolIdentityStage": {"Unknown", "SystemDirectory", "FileLease", "Signature", "Fingerprint", "VersionMetadata", "Completed"},
        "toolIdentityReason": {"Unspecified", "Observed", "NativeTrustNotEstablished", "SignerInspectionUnavailable", "SignerNotQualified", "OriginalFilenameMismatch", "IdentityBoundsOrPath", "OtherUnavailable", "WaitExpired", "PendingWorker", "UnsupportedPlatform", "TrustCleanupUnavailable"},
        "databaseOperation": {"OpenDatabase", "Commit", "SummarySet", "SummaryRead", "ReadTableNames", "HandleClose"} | {prefix + table for prefix in ("Create", "Insert", "Read") for table in ("Property", "Directory", "Feature", "Component", "FeatureComponents", "File", "Media", "InstallExecuteSequence", "InstallUISequence", "Dialog", "Control", "ControlEvent", "TextStyle")},
    }
    for name, allowed in labels.items():
        reject_unless(value[name] is None or isinstance(value[name], str) and value[name] in allowed, "msi-report-label")
    for name in ("installCode", "clientExitCode", "failureCode", "toolNativeTrustResult", "toolFileTrustResult", "toolCatalogTrustResult", "toolCleanupTrustResult"):
        reject_unless(value[name] is None or type(value[name]) is int and -(2**31) <= value[name] < 2**31, "msi-report-code")
    times = []
    for name in ("startedUtc", "observedUtc"):
        text = value[name]
        reject_unless(isinstance(text, str) and re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)", text) is not None, "msi-report-time")
        try:
            times.append(dt.datetime.fromisoformat(text.replace("Z", "+00:00")))
        except ValueError:
            raise EvidenceError("msi-report-time") from None
    reject_unless(times[0] <= times[1] <= dt.datetime.now(dt.timezone.utc) and times[1] - times[0] <= dt.timedelta(minutes=20), "msi-report-time-order")
    if value["bindingReportState"] is None:
        reject_unless(all(value[name] is None for name in ("bindingReason", "bindingChangedFields", "bindingNoRemove", "bindingInstanceCount", "observedContext")), "msi-binding-unobserved")
    else:
        reject_unless(value["bindingReason"] is not None and value["bindingInstanceCount"] is not None and value["expectedMask"] is not None, "msi-binding-observed")
        reject_unless((value["bindingInstanceCount"] == 1) == (value["observedContext"] is not None), "msi-binding-context-coherence")
        if value["bindingReason"] == "SelectionChanged":
            reject_unless(value["bindingReportState"] == "Blocked" and value["bindingChangedFields"] is not None and value["bindingChangedFields"] > 0, "msi-binding-change-coherence")
        elif value["bindingChangedFields"] is not None:
            reject_unless(value["bindingChangedFields"] == 0, "msi-binding-change-coherence")
    reject_unless((value["toolIdentityStage"] is None) == (value["toolIdentityReason"] is None), "msi-tool-coherence")
    if value["toolIdentityStage"] is None:
        reject_unless(all(value[name] is None for name in ("toolNativeTrustResult", "toolFileTrustResult", "toolCatalogTrustResult", "toolCleanupTrustResult")), "msi-tool-coherence")
    reject_unless((value["installReturned"] is True) == (value["installCode"] is not None), "msi-install-coherence")
    reject_unless(value["clientExitCode"] is None or value["clientExited"] is True, "msi-client-coherence")
    if value["clientExited"] is True or value["visibleConsentObserved"] is True:
        reject_unless(value["clientStarted"] is True, "msi-client-not-started")
    if value["clientStarted"] is True:
        reject_unless(all(value[name] is True for name in ("packageBuilt", "databaseVerified", "preexistingCodeAbsent", "installReturned", "installedContextObserved", "payloadObserved")), "msi-client-setup-coherence")
    if value["postCodeAbsent"] is not None or value["payloadAbsent"] is not None:
        reject_unless(value["clientExited"] is True, "msi-postcheck-coherence")
    if value["ownedTeardown"] is True:
        reject_unless(value["workerSettled"] is True and value["postCodeAbsent"] is True and value["payloadAbsent"] is True, "msi-teardown-coherence")
        if version >= 6 and value["uiRequired"]:
            reject_unless(value["ui"] is not None and all(value["ui"][name] is True for name in
                ("settled", "windowClosed", "postCodeAbsent", "payloadAbsent")), "msi-ui-teardown")
    if value["state"] == "passed" and version == 2:
        reject_unless(value["stage"] == "Complete" and all(value[name] is True for name in MSI_FACTS) and value["installCode"] == 0 and value["clientExitCode"] == 0 and value["previewPrepared"] is True and value["clientRequestState"] == "ClientExited" and value["toolIdentityStage"] == "Completed" and value["toolIdentityReason"] == "Observed" and value["toolNativeTrustResult"] == 0 and value["bindingReportState"] == "Bound" and value["bindingReason"] == "Bound" and value["bindingChangedFields"] == 0 and value["selectionNoRemove"] is False and value["bindingNoRemove"] is False and value["bindingInstanceCount"] == 1 and value["expectedMask"] == 3 and value["observedContext"] == 2 and value["currentSnapshotState"] == "Available" and value["failureKind"] is None and value["failureCode"] is None, "msi-pass-coherence")
    if value["state"] == "passed" and version >= 3:
        reject_unless(target_qualified and value["selectionSourceState"] == value["targetPreparation"]["historicalGlobalState"]
            and value["stage"] == "Complete" and all(value[name] is True for name in MSI_FACTS)
            and value["installCode"] == 0 and value["clientExitCode"] == 0 and value["previewPrepared"] is True
            and value["clientRequestState"] == "ClientExited" and value["toolIdentityStage"] == "Completed"
            and value["toolIdentityReason"] == "Observed" and value["toolNativeTrustResult"] == 0
            and value["failureKind"] is None and value["failureCode"] is None, "msi-pass-coherence")
    if version >= 4:
        consent = value["consentObservation"]
        if consent is not None:
            reject_unless(value["clientStarted"] is True, "msi-consent-client")
            reject_unless(consent["removeInvoked"] is not True or value["visibleConsentObserved"] is True, "msi-consent-invoked")
        if value["visibleConsentObserved"] is True or value["state"] == "passed":
            reject_unless(consent_qualified, "msi-pass-consent")
    return value

def prepare(root, download, build_result, verification_result, environment, core_directory=None, report_download_result="success", core_download_result="success", command_logs=None, log_download_result="skipped", native_command_logs=None, native_log_download_result="skipped", msi_directory=None, msi_download_result="skipped", msi_result="skipped", component_directory=None, component_download_result="skipped", component_result="skipped", scan_directory=None, scan_download_result="skipped", scan_result="skipped"):
    context = validate_context(environment)
    reject_unless(build_result in RESULTS and verification_result in RESULTS, "job-result")
    root = validate_checkout(root)
    reject_unless(report_download_result in RESULTS and core_download_result in RESULTS, "download-result")
    reject_unless(log_download_result in RESULTS, "log-download-result")
    stages = {}
    for label in ("SDK", "RESTORE", "BUILD", "PUBLISHER_TESTS", "CORE_TESTS", "PUBLISH"):
        outcome = environment.get("MADCLEAN_STAGE_" + label) or "unknown"
        reject_unless(outcome in RESULTS | {"unknown"}, "stage-result")
        stages[label.lower().replace("_", "-")] = outcome
    diagnostics_error = None
    try:
        diagnostics = ci_diagnostics.collect(command_logs, context, log_download_result)
    except (ValueError, OSError):
        # Reject the entire log payload while retaining trusted job outcomes for diagnosis.
        diagnostics, diagnostics_error = None, "log-evidence-rejected"
    reject_unless(native_log_download_result in RESULTS, "native-log-download-result")
    native_diagnostics_error = None
    try:
        native_diagnostics = ci_diagnostics.collect(native_command_logs, context, native_log_download_result)
    except (ValueError, OSError):
        native_diagnostics, native_diagnostics_error = None, "log-evidence-rejected"
    reject_unless(msi_download_result in RESULTS and msi_result in RESULTS, "msi-job-result")
    msi_error = None
    try:
        msi_report = validate_msi_report(msi_directory, context) if msi_download_result == "success" else None
    except (ValueError, OSError):
        # A rejected optional action qualification report must not suppress independent
        # passive/core evidence or turn absence into a successful qualification.
        msi_report, msi_error = None, "msi-evidence-rejected"
    reject_unless(component_download_result in RESULTS and component_result in RESULTS, "component-job-result")
    component_error = None
    try:
        component_report = component_store_evidence.collect(component_directory, context, read_json, safe_path, reject_unless) if component_download_result == "success" else None
    except (ValueError, OSError):
        # Optional D2 qualification cannot suppress independent passive/MSI/core facts.
        component_report, component_error = None, "component-evidence-rejected"
    reject_unless(scan_download_result in RESULTS and scan_result in RESULTS, "component-scan-job-result")
    scan_error = None
    try:
        scan_report = component_store_evidence.collect(scan_directory, context, read_json, safe_path, reject_unless, expected_kind="ScanHealth") if scan_download_result == "success" else None
    except (ValueError, OSError):
        scan_report, scan_error = None, "component-scan-evidence-rejected"
    latest = root / "latest.json"
    if latest.exists():
        previous = read_json(latest, 262_144)
        reject_unless(isinstance(previous, dict) and previous.get("formatVersion") == 1 and re.fullmatch(r"[0-9]{1,32}", str(previous.get("runId", ""))) is not None, "invalid-previous-report")
        previous_identity = (int(previous["runId"]), int(previous.get("runAttempt", 0)))
        reject_unless(previous_identity <= (int(context[1]), context[2]), "newer-report-exists")
    report, images = validate_report(safe_path(download, missing=True), context) if report_download_result == "success" else (None, {})
    core = core_test_summary(core_directory) if core_download_result == "success" else None
    summary = {
        "formatVersion": 1, "sourceCommit": context[0], "runId": context[1], "runAttempt": context[2],
        "buildResult": build_result, "verificationResult": verification_result,
        "passed": build_result == "success" and verification_result == "success" and report is not None and report["passed"] and core is not None and core["passed"],
        "updatedUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "scope": "Passive WPF/native report scope only: no servicing, installation, real startup changes or personal-PC actions. Separate optional inert MSI qualification is reported independently.",
        "reportPresent": report is not None,
        "coreTestsPresent": core is not None,
        "reportArtifactOutcome": report_download_result,
        "coreArtifactOutcome": core_download_result,
        "stageOutcomes": stages,
        "logArtifactOutcome": log_download_result,
        "diagnosticsPresent": diagnostics is not None,
        "diagnosticsError": diagnostics_error,
        "nativeLogArtifactOutcome": native_log_download_result,
        "nativeDiagnosticsPresent": native_diagnostics is not None,
        "nativeDiagnosticsError": native_diagnostics_error,
        "componentStoreStatusScope": "Separate explicit disposable-runner recorded-flag check; Windows-managed DISM log/archive writes, no fresh scan or repair.",
        "componentStoreStatusResult": component_result,
        "componentStoreStatusArtifactOutcome": component_download_result,
        "componentStoreStatusReportPresent": component_report is not None,
        "componentStoreStatusQualified": component_result == "success" and component_report is not None and component_report["state"] == "passed",
        "componentStoreStatusError": component_error,
        "componentStoreScanScope": "Separate explicit disposable-runner fresh component-store scan; Windows-managed health metadata/log writes, no repair or restart.",
        "componentStoreScanResult": scan_result,
        "componentStoreScanArtifactOutcome": scan_download_result,
        "componentStoreScanReportPresent": scan_report is not None,
        "componentStoreScanBackendQualified": scan_result == "success" and scan_report is not None and scan_report["state"] == "passed",
        "componentStoreScanPublicUiQualified": scan_result == "success" and scan_report is not None and scan_report["state"] == "passed" and scan_report["uiRequired"] is True,
        "componentStoreScanError": scan_error,
        "msiRequestScope": "Separate disposable-runner qualification of one fresh inert current-user MSI product only; not the passive report or production permission.",
        "msiRequestResult": msi_result,
        "msiRequestArtifactOutcome": msi_download_result,
        "msiRequestReportPresent": msi_report is not None,
        "msiRequestQualified": msi_result == "success" and msi_report is not None and msi_report["state"] == "passed",
        "msiRequestPublicUiQualified": msi_result == "success" and msi_report is not None and msi_report["state"] == "passed" and msi_report.get("uiRequired") is True,
        "msiRequestError": msi_error,
        "images": [{"name": name, "sha256": hashlib.sha256(data).hexdigest()} for name, data in images.items()],
    }
    files = {"latest.json": (json.dumps(summary, indent=2) + "\n").encode(), **images}
    if report is not None:
        files["report.json"] = (json.dumps(report, indent=2) + "\n").encode()
    if core is not None:
        files["core-tests.json"] = (json.dumps(core, indent=2) + "\n").encode()
    if diagnostics is not None:
        files["ci-diagnostics.json"] = (json.dumps(diagnostics, indent=2) + "\n").encode()
    if native_diagnostics is not None:
        files["native-ci-diagnostics.json"] = (json.dumps(native_diagnostics, indent=2) + "\n").encode()
    if msi_report is not None:
        files["msi-request.json"] = (json.dumps(msi_report, indent=2) + "\n").encode()
    if component_report is not None:
        files["component-store.json"] = (json.dumps(component_report, indent=2) + "\n").encode()
    if scan_report is not None:
        files["component-scan.json"] = (json.dumps(scan_report, indent=2) + "\n").encode()
    return root, summary, files

def publish(root, files, source_commit):
    # All source evidence is validated before changing the generated, fixed destination names.
    for name in OWNED:
        path = safe_path(root / name, missing=True)
        if path.exists():
            path.unlink()
    for name, data in files.items():
        reject_unless(name in OWNED, "unexpected-output")
        safe_path(root / name, missing=True).write_bytes(data)
    git(root, "config", "user.name", "MadClean verification")
    git(root, "config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com")
    tracked = set(git(root, "ls-files", "--", *OWNED).splitlines())
    git(root, "add", "-A", "--", *sorted(tracked | set(files)))
    changed = subprocess.run(["git", "-C", str(root), "diff", "--cached", "--quiet"], capture_output=True)
    reject_unless(changed.returncode in (0, 1), "git-inspect-failed")
    if changed.returncode == 0:
        return
    git(root, "commit", "-m", "ci: Windows validation for " + source_commit)
    # No force push: concurrent or unexpected branch updates remain a visible failure.
    git(root, "push", "origin", "HEAD:refs/heads/" + BRANCH)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--verification-checkout", type=Path, required=True)
    parser.add_argument("--downloaded-reports", type=Path, required=True)
    parser.add_argument("--core-test-results", type=Path)
    parser.add_argument("--build-result", required=True)
    parser.add_argument("--verification-result", required=True)
    parser.add_argument("--report-download-result", choices=sorted(RESULTS), required=True)
    parser.add_argument("--core-download-result", choices=sorted(RESULTS), required=True)
    parser.add_argument("--command-logs", type=Path)
    parser.add_argument("--log-download-result", choices=sorted(RESULTS), default="skipped")
    parser.add_argument("--native-command-logs", type=Path)
    parser.add_argument("--native-log-download-result", choices=sorted(RESULTS), default="skipped")
    parser.add_argument("--msi-request-reports", type=Path)
    parser.add_argument("--msi-download-result", choices=sorted(RESULTS), default="skipped")
    parser.add_argument("--msi-result", choices=sorted(RESULTS), default="skipped")
    parser.add_argument("--component-store-reports", type=Path)
    parser.add_argument("--component-store-download-result", choices=sorted(RESULTS), default="skipped")
    parser.add_argument("--component-store-result", choices=sorted(RESULTS), default="skipped")
    parser.add_argument("--component-scan-reports", type=Path)
    parser.add_argument("--component-scan-download-result", choices=sorted(RESULTS), default="skipped")
    parser.add_argument("--component-scan-result", choices=sorted(RESULTS), default="skipped")
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    try:
        root, summary, files = prepare(args.verification_checkout, args.downloaded_reports, args.build_result, args.verification_result, os.environ, args.core_test_results, args.report_download_result, args.core_download_result, args.command_logs, args.log_download_result, args.native_command_logs, args.native_log_download_result, args.msi_request_reports, args.msi_download_result, args.msi_result, args.component_store_reports, args.component_store_download_result, args.component_store_result, args.component_scan_reports, args.component_scan_download_result, args.component_scan_result)
        if not args.validate_only:
            publish(root, files, summary["sourceCommit"])
        print("Windows evidence " + ("validated" if args.validate_only else "published") + "; scoped pass: " + str(summary["passed"]).lower())
        return 0
    except (EvidenceError, OSError, ValueError) as error:
        code = str(error) if isinstance(error, EvidenceError) and re.fullmatch(r"[a-z0-9-]{1,80}", str(error)) else "publication-failed"
        print("Windows evidence could not be safely validated/published (" + code + "). No force push or broad file export was attempted.", file=sys.stderr)
        return 1
if __name__ == "__main__":
    raise SystemExit(main())
