"""Read local Windows Update history evidence only; never run an application or query Windows."""
import argparse
import datetime as dt
from pathlib import Path
import re

import read_local_ui_report as neutral

SCOPES = ("windowsServiceCompletion", "currentInstallation", "maintenance", "physicalInput", "sourceAuthenticity")
FACTS = {"queryTaskSettled", "observationBudgetExpired", "providerState", "responseShapeValid",
         "responseAccepted", "totalBefore", "requestedCount", "returnedEntryCount",
         "fieldProjectionObserved", "exceptionHResult", "queryTaskSettledWhenBudgetObserved"}
FIELDS = {"formatVersion", "context", "startedUtc", "completedUtc", "state", "facts", *SCOPES}
NEGATIVE = {"Unknown", "Unavailable", "Unsupported", "Failed", "Cancelled"}
PROVIDERS = NEGATIVE | {"Partial", "Available", "Unrecognized"}
TIME = re.compile(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)")


def ticks(value):
    if not isinstance(value, str) or not (match := TIME.fullmatch(value)):
        raise ValueError("Invalid explicit UTC time.")
    clock = dt.datetime.strptime(match[1], "%Y-%m-%dT%H:%M:%S")
    return ((clock.toordinal() - 1) * 86400 + clock.hour * 3600 + clock.minute * 60 + clock.second) * 10_000_000 + int((match[2] or "").ljust(7, "0"))


def outcome(facts):
    """Classify observed query facts, without inferring service completion or installation."""
    settled, provider = facts["queryTaskSettled"], facts["providerState"]
    shape, accepted = facts["responseShapeValid"], facts["responseAccepted"]
    total, requested, returned = (facts[key] for key in ("totalBefore", "requestedCount", "returnedEntryCount"))
    projection = facts["fieldProjectionObserved"]
    budget_snapshot = facts["queryTaskSettledWhenBudgetObserved"]
    if facts["exceptionHResult"] is not None or shape is False or provider in {"Available", "Unrecognized"}:
        return "failed"
    if budget_snapshot is not None and not facts["observationBudgetExpired"]:
        return "failed"
    if budget_snapshot is True and settled is False:
        return "failed"
    response = (provider, shape, accepted, total, requested, returned, projection)
    if settled is not True and any(value is not None for value in response):
        return "failed"
    if accepted is True and (provider != "Partial" or shape is not True):
        return "failed"
    if provider == "Partial" and accepted is False:
        return "failed"
    if any(value is not None for value in (total, requested, returned)) and accepted is not True:
        return "failed"
    if projection is not None and (accepted is not True or returned is None):
        return "failed"
    if projection is True and returned == 0:
        return "failed"
    if total is not None and requested is not None and requested != min(total, 128):
        return "failed"
    if returned is not None and requested is not None and returned > requested:
        return "failed"
    if returned is not None and total is not None and returned > min(total, 128):
        return "failed"
    if facts["observationBudgetExpired"]:
        return "inconclusive"
    if (settled is True and provider == "Partial" and shape is True and accepted is True
            and all(value is not None for value in (total, requested, returned, projection))):
        return "passed"
    return "inconclusive"


def validate(report, now=None):
    if not isinstance(report, dict) or set(report) != FIELDS:
        raise ValueError("Invalid history report fields.")
    if type(report["formatVersion"]) is not int or report["formatVersion"] != 1 or report["context"] != "local-update-history":
        raise ValueError("Invalid history report context.")
    if any(report[key] != "unverified" for key in SCOPES):
        raise ValueError("Unsupported qualification claim.")
    facts = report["facts"]
    if not isinstance(facts, dict) or set(facts) != FACTS:
        raise ValueError("Invalid history observation fields.")
    for key in ("queryTaskSettled", "responseShapeValid", "responseAccepted", "fieldProjectionObserved",
                "queryTaskSettledWhenBudgetObserved"):
        if facts[key] is not None and type(facts[key]) is not bool:
            raise ValueError("Invalid nullable observation.")
    if type(facts["observationBudgetExpired"]) is not bool:
        raise ValueError("Invalid observation budget.")
    if facts["providerState"] is not None and (type(facts["providerState"]) is not str or facts["providerState"] not in PROVIDERS):
        raise ValueError("Invalid provider state.")
    for key, low, high in (("totalBefore", 0, 2_147_483_647), ("requestedCount", 0, 128),
                           ("returnedEntryCount", 0, 128), ("exceptionHResult", -2_147_483_648, 2_147_483_647)):
        if facts[key] is not None and (type(facts[key]) is not int or not low <= facts[key] <= high):
            raise ValueError("Invalid bounded numeric observation.")
    if report["state"] != outcome(facts):
        raise ValueError("Contradictory history outcome.")
    start, end = ticks(report["startedUtc"]), ticks(report["completedUtc"])
    now = now if now is not None else dt.datetime.now(dt.timezone.utc)
    if now.tzinfo is None or now.utcoffset() != dt.timedelta(0):
        raise ValueError("Invalid observation clock.")
    if start <= 0 or end < start or end > ticks(now.isoformat()):
        raise ValueError("Invalid observation interval.")
    return report


def read(path):
    return validate(neutral.read_document(path, maximum_bytes=8192))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    args = parser.parse_args(argv)
    try:
        report = read(args.report)
    except (OSError, ValueError, RecursionError):
        print("No accepted local update-history report. No Windows qualification is claimed.")
        return 1
    print("Reported local update-history state: " + report["state"] + "; completed UTC " + report["completedUtc"] + ".")
    print("Provider state: " + (report["facts"]["providerState"] or "unobserved") + ".")
    if report["facts"]["observationBudgetExpired"]:
        snapshot = report["facts"]["queryTaskSettledWhenBudgetObserved"]
        label = "unobserved" if snapshot is None else "settled" if snapshot else "pending"
        print("Query task at first observed budget expiry: " + label + ".")
    print("Consistency only; Windows service completion, current installation, maintenance, physical input and source authenticity remain unverified.")
    return {"passed": 0, "failed": 1, "inconclusive": 2}[report["state"]]


if __name__ == "__main__":
    raise SystemExit(main())
