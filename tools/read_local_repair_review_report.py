"""Read a prepare-only repair-review report; never run MadClean or a Windows check."""
import argparse
from pathlib import Path

import read_local_ui_report as neutral

REPAIR_FACTS = ("closedGates", "controlsBound", "draftsObserved", "acknowledgmentsObserved",
                "confirmationBlocked", "selectionInvalidation", "noRequestStateObserved")
FIELDS = neutral.FIELDS | {"repair"}


def outcome(values, error):
    if error is not None or any(value is False for value in values):
        return "failed"
    return "passed" if all(value is True for value in values) else "inconclusive"


def validate(report, now=None):
    if not isinstance(report, dict) or set(report) != FIELDS or report["context"] != "local-repair-review":
        raise ValueError("Invalid prepare-only report context or fields.")
    repair, facts = report["repair"], report["facts"]
    if not isinstance(repair, dict) or set(repair) != set(REPAIR_FACTS) or any(
            value is not None and type(value) is not bool for value in repair.values()):
        raise ValueError("Invalid prepare-only observation facts.")
    if not isinstance(facts, dict):
        raise ValueError("Invalid window observations.")
    # Reuse unchanged neutral time/coverage/closure checks with its own base outcome.
    # This projection is consistency logic, never source authentication or permission.
    base = {key: report[key] for key in neutral.FIELDS}
    base["context"] = "local-neutral-ui"
    base["state"] = outcome(tuple(facts.values()), report["exceptionHResult"])
    neutral.validate(base, now)
    expected = outcome((*facts.values(), *repair.values()), report["exceptionHResult"])
    if report["state"] != expected:
        raise ValueError("Contradictory prepare-only outcome.")
    if any(value is True for value in repair.values()) and any(
            facts[key] is not True for key in ("windowObserved", "initialNeutral", "navigationObserved")):
        raise ValueError("The prerequisite window/navigation was not observed.")
    prerequisites = {
        "controlsBound": ("closedGates",),
        "draftsObserved": ("controlsBound",),
        "acknowledgmentsObserved": ("draftsObserved",),
        "confirmationBlocked": ("acknowledgmentsObserved",),
        "selectionInvalidation": ("draftsObserved", "acknowledgmentsObserved", "confirmationBlocked"),
        "noRequestStateObserved": ("closedGates", "controlsBound"),
    }
    if any(repair[key] is True and any(repair[prior] is not True for prior in required)
           for key, required in prerequisites.items()):
        raise ValueError("A prerequisite repair-control observation is missing.")
    return report


def read(path):
    return validate(neutral.read_document(path))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    args = parser.parse_args(argv)
    try:
        report = read(args.report)
    except (OSError, ValueError, RecursionError):
        print("No accepted local repair-review report. No Windows qualification is claimed.")
        return 1
    print("Reported local repair-review state: " + report["state"] + "; completed UTC " + report["completedUtc"] + ".")
    print("Consistency only; source authenticity, providers, maintenance and physical input remain unverified.")
    return {"passed": 0, "failed": 1, "inconclusive": 2}[report["state"]]


if __name__ == "__main__":
    raise SystemExit(main())
