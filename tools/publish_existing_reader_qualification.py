"""Publish one fixed, validated fixture report to a clean verification checkout.

This is CI evidence transport, not native qualification, host authentication or a
hostile-filesystem broker. No ordinary validation snapshot is changed. A push is
explicit, single-attempt and never forced or retried after an uncertain outcome.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile

REPOSITORY = "MadGenius9/MadClean"
SOURCE_REF = "refs/heads/recovery/validated-20261112"
JOB = "publish-verification"
BRANCH = "madclean-verification"
TARGET = "existing-reader-qualification.json"
MAX_REPORT_BYTES = 65_536
_reader_module = None


class PublicationError(ValueError):
    """A fixed local diagnostic code; never contains Git or provider output."""


def require(condition, code):
    if not condition:
        raise PublicationError(code)


def validate_context(environment):
    require(environment.get("GITHUB_ACTIONS") == "true", "not-actions")
    require(environment.get("RUNNER_ENVIRONMENT") == "github-hosted", "not-hosted")
    require(environment.get("GITHUB_REPOSITORY") == REPOSITORY, "wrong-repository")
    require(environment.get("GITHUB_REF") == SOURCE_REF, "wrong-source-ref")
    require(environment.get("GITHUB_EVENT_NAME") == "workflow_dispatch", "wrong-event")
    require(environment.get("GITHUB_JOB") == JOB, "wrong-job")
    require(environment.get("MADCLEAN_EXISTING_READER_QUALIFICATION") == "1", "missing-reader-selection")
    source = environment.get("GITHUB_SHA", "")
    run = environment.get("GITHUB_RUN_ID", "")
    attempt = environment.get("GITHUB_RUN_ATTEMPT", "")
    require(isinstance(source, str) and re.fullmatch(r"[0-9a-f]{40}", source) is not None, "invalid-source")
    require(isinstance(run, str) and re.fullmatch(r"[1-9][0-9]{0,31}", run) is not None, "invalid-run")
    require(isinstance(attempt, str) and re.fullmatch(r"[1-9][0-9]{0,9}", attempt) is not None
            and int(attempt) <= 2_147_483_647, "invalid-attempt")
    return source, run, int(attempt)


def safe_path(path):
    """Reject observed links/reparse components; does not freeze the namespace."""
    path = Path(os.path.abspath(path))
    for component in (*reversed(path.parents), path):
        data = component.lstat()
        require(not stat.S_ISLNK(data.st_mode) and not (getattr(data, "st_file_attributes", 0) & 0x400),
                "linked-path")
    return path


def _git(root, *arguments):
    environment = os.environ.copy()
    for key in tuple(environment):
        if key.startswith("GIT_TRACE") or key.startswith("GIT_CURL_VERBOSE"):
            environment.pop(key)
    environment["GIT_TERMINAL_PROMPT"] = "0"
    try:
        result = subprocess.run(["git", "-C", str(root), *arguments], env=environment,
                                text=True, capture_output=True, timeout=50)
    except subprocess.TimeoutExpired:
        # The command may have executed. The caller must inspect the outcome,
        # not invoke another push or turn this into a known-not-executed result.
        raise PublicationError("git-outcome-uncertain") from None
    require(result.returncode == 0, "git-operation-failed")
    require(len(result.stdout) <= MAX_REPORT_BYTES, "git-output-bound")
    return result.stdout.strip()


def validate_checkout(path):
    root = safe_path(path)
    require(root.is_dir(), "not-checkout-directory")
    require(safe_path(root / ".git").is_dir(), "not-normal-checkout")
    # Git for Windows can expand a short directory name in its reported path.
    # Check both ancestor chains before comparing their filesystem identities.
    reported_root = safe_path(_git(root, "rev-parse", "--show-toplevel"))
    require(root.samefile(reported_root), "wrong-checkout-root")
    require(_git(root, "symbolic-ref", "--short", "HEAD") == BRANCH, "wrong-checkout-branch")
    require(_git(root, "remote", "get-url", "origin").rstrip("/") in {
        f"https://github.com/{REPOSITORY}", f"https://github.com/{REPOSITORY}.git"
    }, "wrong-origin")
    require(_git(root, "status", "--porcelain=v1", "--untracked-files=all") == "", "dirty-checkout")
    return root


def _load_report(path, source, run, attempt):
    global _reader_module
    if _reader_module is None:
        spec = importlib.util.spec_from_file_location(
            "madclean_existing_reader_report", Path(__file__).with_name("read_existing_reader_qualification.py"))
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)
        _reader_module = module
    try:
        return _reader_module.load_report(path, expected_source=source, expected_run=run,
                                          expected_attempt=attempt)
    except (OSError, ValueError, RecursionError, TypeError):
        raise PublicationError("invalid-source-bound-report") from None


def publish(verification_checkout, report_path, *, environment=None, push=False, fallback_report=None):
    require(type(push) is bool, "invalid-push-selection")
    source, run, attempt = validate_context(os.environ if environment is None else environment)
    primary = Path(os.path.abspath(report_path))
    safe_path(primary.parent)
    fallback_used = not os.path.lexists(primary)
    if fallback_used:
        require(fallback_report is not None, "missing-report")
        report_path = fallback_report
    report_path = safe_path(report_path)
    require(report_path.is_file() and report_path.stat().st_size <= MAX_REPORT_BYTES, "invalid-report-file")
    report = _load_report(report_path, source, run, attempt)
    if fallback_used:
        require(report["state"] == "inconclusive" and report["ownershipSettled"] is False and report["cases"] == [],
                "unsupported-fallback-claim")
    # Preserve the complete validated schema, including negative/uncertain facts.
    # Canonical serialization is transport only; no outcome is recalculated here.
    payload = (json.dumps(report, indent=2, sort_keys=True, ensure_ascii=True, allow_nan=False) + "\n").encode("utf-8")
    require(len(payload) <= MAX_REPORT_BYTES, "serialized-report-bound")
    root = validate_checkout(verification_checkout)
    require(not any(parent.samefile(root) for parent in report_path.parents), "report-inside-checkout")
    target = root / TARGET
    if target.exists() or target.is_symlink():
        target = safe_path(target)
        data = target.stat()
        require(stat.S_ISREG(data.st_mode) and data.st_nlink == 1 and data.st_size <= MAX_REPORT_BYTES,
                "unsafe-report-target")
        with target.open("rb") as previous:
            existing = previous.read(MAX_REPORT_BYTES + 1)
        require(len(existing) <= MAX_REPORT_BYTES, "existing-report-bound")
        if existing == payload:
            return {"changed": False, "pushed": False, "state": report["state"]}

    descriptor, temporary_name = tempfile.mkstemp(prefix=".existing-reader-publication-", suffix=".tmp", dir=root)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        # A symlink appearing here is replaced itself, never written through.
        os.replace(temporary, target)
    finally:
        if temporary.exists():
            temporary.unlink()
    _git(root, "add", "--", TARGET)
    require(_git(root, "diff", "--cached", "--name-only") == TARGET, "unexpected-staged-paths")
    _git(root, "-c", "user.name=MadClean verification", "-c",
         "user.email=41898282+github-actions[bot]@users.noreply.github.com",
         "commit", "-m", "ci: existing reader qualification for " + source)
    if push:
        _git(root, "push", "origin", "HEAD:refs/heads/" + BRANCH)
    return {"changed": True, "pushed": push, "state": report["state"]}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verification-checkout", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--fallback-report", type=Path, help="Conservative initial snapshot only when the primary is absent")
    parser.add_argument("--push", action="store_true", help="One non-force push; never retried")
    options = parser.parse_args(argv)
    try:
        result = publish(options.verification_checkout, options.report, push=options.push, fallback_report=options.fallback_report)
    except (OSError, ValueError, subprocess.SubprocessError):
        print("Existing reader evidence publication failed; inspect ownership/source/transport before retrying.", file=sys.stderr)
        return 2
    print("Existing reader evidence: " + result["state"] +
          "; changed=" + str(result["changed"]).lower() + "; pushed=" + str(result["pushed"]).lower())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
