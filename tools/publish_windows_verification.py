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

REPOSITORY = "MadGenius9/MadClean"
BRANCH = "madclean-verification"
SOURCE_REF = "refs/heads/feat/phase-one-foundation"
IMAGES = ("overview-wide.png", "overview-compact.png", "tool-11.png", "tool-12.png", "tool-13.png", "tool-14.png", "tool-15.png", "tool-16.png", "tool-17.png", "tool-18.png", "tool-19.png", "tool-20.png", "tool-21.png", "tool-22.png", "tool-23.png", "tool-25.png", "tool-26.png")
OWNED = ("latest.json", "report.json", "core-tests.json", "ci-diagnostics.json", "native-ci-diagnostics.json", *IMAGES)
REQUIRED_CHECKS = {
    "ui-wide-size", "ui-initial-state", "ui-compact-layout", "ui-primary-navigation", "ui-tool-navigation", "ui-tool-popup", "ui-installed-app-search", "ui-packaged-app-search", "ui-inspection-evidence", "ui-shortcut-file-evidence",
    "bounded-registration-fixture", "defender-status-contract", "storage-inspection-evidence-contract", "defender-inspection-evidence-contract", "installed-registration-contract", "msi-registration-contract", "packaged-application-contract", "leftover-content-fixture", "disk-identity-fixture", "wer-v1-managed-layout", "wer-store-read-contract", "wer-store-count-contract", "hardware-metadata-contract", "startup-fixture-inventory", "startup-fixture-preview", "startup-fixture-mutation-gate", "startup-fixture-scope", "startup-fixture-teardown", "startup-transaction-handle-probe", "startup-transaction-interference-probe", "system-tool-execution-gate", "system-tool-identity-contract", "system-tool-embedded-version-fixture", "system-tool-catalog-observation", "system-tool-held-catalog-observation",
    "duplicate-content-fixtures", "duplicate-hardlink-fixture", "duplicate-pair-review-fixture", "shortcut-existing-local-fixture",
    "shortcut-missing-local-fixture", "shortcut-no-mutation", "shortcut-rename-handle-probe", "shortcut-rename-cpp-control", "shortcut-rename-nt-control", "shortcut-rename-source-guard", "shortcut-rename-destination-guard", "shortcut-rename-ancestor-guard", "shortcut-mapped-write-witness", "shortcut-selected-recheck-fixture", "shortcut-file-evidence-fixture", "shortcut-dacl-fixture", "diagnostics-metadata-contract", "fixture-teardown",
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
    reject_unless(isinstance(report, dict) and set(report) == fields and type(report.get("formatVersion")) is int and report["formatVersion"] == 1, "report-schema")
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
        reject_unless(REQUIRED_CHECKS <= names, "missing-required-check")
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


def prepare(root, download, build_result, verification_result, environment, core_directory=None, report_download_result="success", core_download_result="success", command_logs=None, log_download_result="skipped", native_command_logs=None, native_log_download_result="skipped"):
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
        "scope": "Real WPF client captures and isolated native fixture checks; no servicing, installation, real startup changes or personal-PC actions.",
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
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    try:
        root, summary, files = prepare(args.verification_checkout, args.downloaded_reports, args.build_result, args.verification_result, os.environ, args.core_test_results, args.report_download_result, args.core_download_result, args.command_logs, args.log_download_result, args.native_command_logs, args.native_log_download_result)
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
