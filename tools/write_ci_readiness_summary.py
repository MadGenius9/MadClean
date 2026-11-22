"""Display source-bound build and qualification facts; never changes CI readiness."""
import argparse
import json
from pathlib import Path
import re
import stat

MAX_BYTES = 1_048_576
UNAVAILABLE = "Current build/qualification summary unavailable"
STAGES = (
    ("sdk", "SDK setup"), ("restore", "Dependency restore"),
    ("build", "Build"), ("publisher-tests", "Publisher tests"),
    ("core-tests", "Core tests"), ("publish", "Distributable publish"),
)
RESULTS = {"success", "failure", "cancelled", "skipped"}
CHECK_STATES = {"passed", "inconclusive", "failed"}


def require(condition):
    if not condition:
        raise ValueError("unavailable")


def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def read_json(path):
    path = Path(path).absolute()
    for component in (*reversed(path.parents), path):
        info = component.lstat()
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400)
    require(stat.S_ISREG(info.st_mode) and info.st_size <= MAX_BYTES)
    with path.open("rb") as stream:
        raw = stream.read(MAX_BYTES + 1)
    require(len(raw) <= MAX_BYTES)
    return json.loads(raw.decode("utf-8"), object_pairs_hook=unique_pairs,
                      parse_constant=lambda _: require(False))


def context(source, run, attempt):
    require(type(source) is str and re.fullmatch(r"[0-9a-f]{40}", source) is not None)
    require(type(run) is str and re.fullmatch(r"[1-9][0-9]{0,31}", run) is not None)
    require(type(attempt) is int and 0 < attempt <= 2_147_483_647)
    return source, run, attempt


def render_summary(latest, report, *, source, run, attempt):
    expected = context(source, run, attempt)
    validate_document(latest, 1, expected)
    require(type(latest.get("buildResult")) is str and latest["buildResult"] in RESULTS)
    require(type(latest.get("verificationResult")) is str and latest["verificationResult"] in RESULTS)
    stages = latest.get("stageOutcomes")
    require(type(stages) is dict)
    require(all(type(stages.get(key)) is str and stages[key] in RESULTS for key, _ in STAGES))
    if report is None:
        require(latest.get("reportPresent") is False and not latest["passed"])
        return with_scope_notice(summary_lines(
            "Windows qualification report unavailable", latest, stages) +
            ["Windows check counts are unavailable."])
    require(latest.get("reportPresent") is not False)
    validate_document(report, 7, expected)
    checks = report.get("checks")
    require(type(checks) is list and 0 < len(checks) <= 200)
    counts = dict.fromkeys(CHECK_STATES, 0)
    for check in checks:
        require(type(check) is dict and type(check.get("state")) is str and check["state"] in CHECK_STATES)
        require(type(check.get("name")) is str and type(check.get("detail")) is str)
        counts[check["state"]] += 1
    state = "failed" if counts["failed"] else "inconclusive" if counts["inconclusive"] else "passed"
    require(report.get("state") == state and report["passed"] == (state == "passed"))
    build_passed = latest["buildResult"] == "success" and all(
        stages[key] == "success" for key, _ in STAGES if key != "publish")
    if latest["passed"]:
        require(build_passed and latest["verificationResult"] == "success" and report["passed"])
    if latest["passed"]:
        heading = "Build and unit-test stages passed; Windows qualification passed for the reported scope"
    elif build_passed and state == "inconclusive":
        heading = "Build and unit-test stages passed; Windows qualification is incomplete"
    elif build_passed and state == "failed":
        heading = "Build and unit-test stages passed; Windows qualification found failed checks"
    elif build_passed:
        heading = "Build and unit-test stages passed; overall Windows qualification is not established"
    else:
        heading = "Build and unit-test stages have not all passed; Windows qualification is not established"
    lines = summary_lines(heading, latest, stages)
    lines.append(f"Windows checks: {counts['passed']} passed, {counts['inconclusive']} inconclusive, "
                 f"{counts['failed']} failed.")
    return with_scope_notice(lines)


def with_scope_notice(lines):
    return "\n".join(lines + ["", "This display does not change CI status or execution gates and is not release approval."])


def validate_document(data, version, expected):
    require(type(data) is dict and type(data.get("formatVersion")) is int and data["formatVersion"] == version)
    require(context(data.get("sourceCommit"), data.get("runId"), data.get("runAttempt")) == expected)
    require(type(data.get("passed")) is bool)


def summary_lines(heading, latest, stages):
    lines = ["## " + heading, "", "| Stage | Outcome |", "| --- | --- |"]
    lines.extend("| " + label + " | " + stages[key] + " |" for key, label in STAGES)
    lines.extend(("| Windows qualification job | " + latest["verificationResult"] + " |", ""))
    return lines


class FixedErrorParser(argparse.ArgumentParser):
    def error(self, message):
        raise ValueError("arguments")


def main(argv=None):
    try:
        parser = FixedErrorParser(description=__doc__)
        parser.add_argument("--latest", required=True)
        parser.add_argument("--report", required=True)
        parser.add_argument("--source", required=True)
        parser.add_argument("--run", required=True)
        parser.add_argument("--attempt", required=True, type=int)
        args = parser.parse_args(argv)
        latest = read_json(args.latest)
        try:
            report = read_json(args.report)
            require(report is not None)
        except FileNotFoundError:
            report = None
        text = render_summary(latest, report,
                              source=args.source, run=args.run, attempt=args.attempt)
    except (OSError, ValueError, TypeError, RecursionError, OverflowError):
        print(UNAVAILABLE)
        return 2
    print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
