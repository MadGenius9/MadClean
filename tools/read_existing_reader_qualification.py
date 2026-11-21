"""Passive consistency reader for fixed existing-only fixture facts; starts no work."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import stat
import sys

MAX_BYTES = 65_536
LABELS = ("valid", "missing-lease", "missing-journal", "extra-file", "extra-directory", "case-mismatch", "missing-lf")
PHASES = ("prepare", "directory", "lease", "journal", "exercise", "release")
OUTCOMES = {"NotEntered", "Running", "Confirmed", "Refused", "Uncertain"}
NAMES = {"NotObserved", "InProgress", "Refused", "Complete", "Uncertain"}
FLAGS = ("sampleVerified", "resourcesReleased", "hasPendingWork", "requiresRetention", "boundaryMatched", "independentComparison", "teardownConfirmed")
TOP = {"formatVersion", "context", "sourceCommit", "sourceRef", "runId", "runAttempt", "job", "startedUtc", "completedUtc", "state", "timeoutObserved", "ownershipSettled", "windowsBuild", "processX64", "cases", "sourceAuthenticity", "reparseCoverage"}
CASE = {"label", "state", "setupConfirmed", "observation", *FLAGS}


def require(value, code):
    if not value:
        raise ValueError(code)


def keys(value, expected):
    require(type(value) is dict and set(value) == expected, "unsupported-fields")


def utc(value):
    require(type(value) is str and len(value) <= 40 and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|\+00:00)", value), "invalid-time")
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(result.year > 1 and result.utcoffset().total_seconds() == 0, "invalid-time")
    fraction = re.search(r"\.(\d+)", value)
    extra_tick = int(fraction.group(1).ljust(7, "0")[6]) if fraction else 0
    return result, extra_tick


def matches(case):
    o = case["observation"]
    if case["resourcesReleased"] is not True or case["hasPendingWork"] is not False:
        return False
    if o is None or o["stopObserved"] or o["prepare"] != "Confirmed" or o["directory"] != "Confirmed" or o["release"] != "Confirmed":
        return False
    label = case["label"]
    if label == "missing-lease":
        return case["sampleVerified"] is False and (o["lease"], o["journal"], o["exercise"], o["names"]) == ("Refused", "NotEntered", "NotEntered", "NotObserved")
    if o["lease"] != "Confirmed":
        return False
    if label == "missing-journal":
        return case["sampleVerified"] is False and (o["journal"], o["exercise"], o["names"]) == ("Refused", "NotEntered", "NotObserved")
    if o["journal"] != "Confirmed":
        return False
    if label == "valid":
        return case["sampleVerified"] is True and (o["exercise"], o["names"]) == ("Confirmed", "Complete")
    return case["sampleVerified"] is False and o["exercise"] == "Refused" and o["names"] == ("Complete" if label == "missing-lf" else "Refused")


def validate_report(data, *, expected_source=None, expected_run=None, expected_attempt=None):
    keys(data, TOP)
    require(type(data["formatVersion"]) is int and data["formatVersion"] == 1 and data["context"] == "existing-journal-reader-qualification", "unsupported-context")
    require(type(data["sourceCommit"]) is str and re.fullmatch(r"[0-9a-f]{40}", data["sourceCommit"]), "invalid-source")
    require(data["sourceRef"] == "refs/heads/recovery/validated-20261112" and data["job"] == "windows-existing-journal-reader", "wrong-scope")
    require(type(data["runId"]) is str and re.fullmatch(r"[1-9][0-9]{0,31}", data["runId"]), "invalid-run")
    require(type(data["runAttempt"]) is int and 0 < data["runAttempt"] <= 2_147_483_647, "invalid-attempt")
    for expected, actual in ((expected_source, data["sourceCommit"]), (expected_run, data["runId"]), (expected_attempt, data["runAttempt"])):
        require(expected is None or expected == actual, "context-mismatch")
    require(utc(data["startedUtc"]) <= utc(data["completedUtc"]) <= (datetime.now(timezone.utc), 0), "inconsistent-times")
    require(data["state"] in ("passed", "inconclusive", "failed"), "invalid-state")
    for name in ("timeoutObserved", "ownershipSettled", "processX64"):
        require(type(data[name]) is bool, "invalid-flag")
    require(data["windowsBuild"] is None or type(data["windowsBuild"]) is int and 0 <= data["windowsBuild"] <= 2_147_483_647, "invalid-build")
    require(data["sourceAuthenticity"] == "unverified" and data["reparseCoverage"] == "unverified", "unsupported-claim")
    cases = data["cases"]
    require(type(cases) is list and len(cases) <= len(LABELS), "case-bound")
    for i, case in enumerate(cases):
        keys(case, CASE)
        require(case["label"] == LABELS[i] and case["state"] in ("passed", "inconclusive", "failed"), "invalid-case")
        require(type(case["setupConfirmed"]) is bool, "invalid-setup")
        for name in FLAGS:
            require(case[name] is None or type(case[name]) is bool, "invalid-case-flag")
        o = case["observation"]
        if o is not None:
            keys(o, {*PHASES, "stopObserved", "names"})
            require(all(o[p] in OUTCOMES for p in PHASES) and o["names"] in NAMES and type(o["stopObserved"]) is bool, "invalid-observation")
        if case["boundaryMatched"] is True or case["state"] == "passed":
            require(case["setupConfirmed"] and matches(case), "wrong-boundary")
        if case["state"] == "passed":
            require(all(case[p] is True for p in ("resourcesReleased", "boundaryMatched", "independentComparison", "teardownConfirmed")) and case["hasPendingWork"] is False and case["requiresRetention"] is False, "unsettled-pass")
        if case["independentComparison"] is False or (case["setupConfirmed"] and case["label"] != "valid" and case["sampleVerified"] is True):
            require(case["state"] == "failed", "hidden-known-failure")
        if case["teardownConfirmed"] is True:
            require(o is not None and o["release"] == "Confirmed", "unconfirmed-release")
            require(case["setupConfirmed"] and case["resourcesReleased"] is True and case["hasPendingWork"] is False and case["requiresRetention"] is False, "unsafe-teardown-claim")
        if i < len(cases) - 1:
            require(case["state"] == "passed", "continued-after-refusal")
    if data["ownershipSettled"]:
        require(all(c["teardownConfirmed"] is True and c["hasPendingWork"] is False and c["requiresRetention"] is False for c in cases), "unsettled-ownership")
    if data["state"] == "passed":
        require(not data["timeoutObserved"] and data["ownershipSettled"] and data["processX64"] and data["windowsBuild"] is not None and data["windowsBuild"] >= 26100 and len(cases) == len(LABELS) and all(c["state"] == "passed" for c in cases), "unsupported-pass")
    if any(c["state"] == "failed" for c in cases):
        require(data["state"] == "failed", "hidden-failure")
    return data


def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate-key")
        result[key] = value
    return result


def load_report(path, **expected):
    path = Path(path)
    for component in (*reversed(path.absolute().parents), path.absolute()):
        info = component.lstat()
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400, "linked-input")
    require(path.is_file() and path.stat().st_size <= MAX_BYTES, "report-bound")
    with path.open("rb") as stream:
        raw = stream.read(MAX_BYTES + 1)
    require(len(raw) <= MAX_BYTES, "report-bound")
    data = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_pairs, parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite-number")))
    return validate_report(data, **expected)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--source")
    parser.add_argument("--run")
    parser.add_argument("--attempt", type=int)
    options = parser.parse_args(argv)
    try:
        data = load_report(options.report, expected_source=options.source, expected_run=options.run, expected_attempt=options.attempt)
    except (OSError, ValueError, TypeError, RecursionError):
        print("Existing-reader evidence invalid or unavailable.", file=sys.stderr)
        return 2
    print("Existing-reader evidence: " + data["state"] + "; cases=" + str(len(data["cases"])) + "; native scope only; source authenticity unverified.")
    return 0 if data["state"] == "passed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
