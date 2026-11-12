"""Read a local neutral-UI report only; never launch MadClean or contact a service."""
import argparse
import datetime as dt
import json
from pathlib import Path
import re
import stat

FACTS = ("windowObserved", "initialNeutral", "wideLayout", "compactLayout",
         "navigationObserved", "finalNeutral", "windowClosed", "ownershipSettled")
FIELDS = {"formatVersion", "context", "startedUtc", "completedUtc", "state", "facts",
          "exceptionHResult", "windowsProviders", "maintenance", "physicalInput"}
TIME = re.compile(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)")


def ticks(value):
    if not isinstance(value, str) or not (match := TIME.fullmatch(value)):
        raise ValueError("Invalid UTC time.")
    clock = dt.datetime.strptime(match[1], "%Y-%m-%dT%H:%M:%S")
    seconds = (clock.toordinal() - 1) * 86400 + clock.hour * 3600 + clock.minute * 60 + clock.second
    return seconds * 10_000_000 + int((match[2] or "").ljust(7, "0"))


def validate(report, now=None):
    if not isinstance(report, dict) or set(report) != FIELDS:
        raise ValueError("Invalid report fields.")
    if type(report["formatVersion"]) is not int or report["formatVersion"] != 1 or report["context"] != "local-neutral-ui":
        raise ValueError("Invalid report context.")
    if any(report[key] != "unverified" for key in ("windowsProviders", "maintenance", "physicalInput")):
        raise ValueError("Unsupported qualification claim.")
    facts = report["facts"]
    if not isinstance(facts, dict) or set(facts) != set(FACTS) or any(value is not None and type(value) is not bool for value in facts.values()):
        raise ValueError("Invalid observation facts.")
    error = report["exceptionHResult"]
    if error is not None and (type(error) is not int or not -2_147_483_648 <= error <= 2_147_483_647):
        raise ValueError("Invalid exception observation.")
    expected = "failed" if error is not None or any(value is False for value in facts.values()) else (
        "passed" if all(value is True for value in facts.values()) else "inconclusive")
    if report["state"] != expected:
        raise ValueError("Contradictory outcome.")
    if any(facts[key] is True for key in FACTS[1:]) and facts["windowObserved"] is not True:
        raise ValueError("Window was not observed.")
    if facts["finalNeutral"] is True and facts["initialNeutral"] is not True:
        raise ValueError("Initial neutrality was not observed.")
    if facts["ownershipSettled"] is True and facts["windowClosed"] is not True:
        raise ValueError("Window closure was not observed.")
    start, end = ticks(report["startedUtc"]), ticks(report["completedUtc"])
    now = now if now is not None else dt.datetime.now(dt.timezone.utc)
    if now.tzinfo is None or now.utcoffset() != dt.timedelta(0):
        raise ValueError("Invalid observation clock.")
    current = ticks(now.isoformat().replace("+00:00", "Z"))
    if start <= 0 or end < start or end - start > 600 * 10_000_000 or end > current:
        raise ValueError("Invalid observation interval.")
    return report


def unique_fields(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate report field.")
        result[key] = value
    return result


def read_document(path):
    if str(path).startswith(("\\\\", "//")):
        raise ValueError("A local report file is required.")
    path = Path(path).absolute()
    for item in reversed((path, *path.parents)):
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked report path.")
        if not (stat.S_ISREG(info.st_mode) if item == path else stat.S_ISDIR(info.st_mode)):
            raise ValueError("A regular report file and directory ancestry are required.")
    with path.open("rb") as stream:
        data = stream.read(16_385)
    if not data or len(data) > 16_384:
        raise ValueError("Report exceeds its bounds.")
    return json.loads(data, object_pairs_hook=unique_fields)


def read(path):
    return validate(read_document(path))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    args = parser.parse_args(argv)
    try:
        report = read(args.report)
    except (OSError, ValueError, RecursionError):
        print("No accepted local neutral-UI report. No runtime qualification is claimed.")
        return 1
    print("Local neutral UI report: " + report["state"] + "; completed UTC " + report["completedUtc"] + ".")
    print("Report consistency only; source/build authenticity, providers, maintenance and physical input remain unverified.")
    return {"passed": 0, "failed": 1, "inconclusive": 2}[report["state"]]


if __name__ == "__main__":
    raise SystemExit(main())
