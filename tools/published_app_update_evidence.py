"""Fixed, source-bound published MadClean UI facts; no package data or process actions."""
import datetime as dt
import re
from pathlib import Path

FACTS = {"descriptorValidated", "executableLeased", "metadataSidecarLeased", "processStarted", "ownedWindowObserved",
    "neutralOverviewObserved", "overviewNoDataObserved", "ownedBundledRuntimeObserved", "updateCommandInvoked",
    "resultObserved", "freshResultObserved", "queryOwnershipSettled", "closeRequested", "exitObserved", "cleanupConfirmed"}
COUNTS = {"itemCount", "availableRows", "unknownRows"}
FIELDS = {"formatVersion", "sourceCommit", "runId", "runAttempt", "startedUtc", "observedUtc", "state",
    "requestedUpdateCommandInvocations", "resultState", "checkedUtc", "exitCode", "requiresRetention", "stage", "nativeError"} | FACTS | COUNTS
STATES = {"Available", "Partial", "Unknown", "Failed", "Cancelled", "Unavailable", "Unsupported"}


def collect(directory, context, read_json, safe_path, reject):
    if directory is None:
        return None
    path = safe_path(Path(directory), missing=True) / "report.json"
    if not path.exists():
        return None
    value = read_json(path, 16384)
    reject(isinstance(value, dict) and set(value) == FIELDS and type(value.get("formatVersion")) is int and value["formatVersion"] == 1, "published-app-schema")
    reject((value["sourceCommit"], value["runId"], value["runAttempt"]) == context and type(value["runAttempt"]) is int, "published-app-context")
    reject(type(value["state"]) is str and value["state"] in {"passed", "failed", "inconclusive"}, "published-app-state")
    reject(type(value["requiresRetention"]) is bool, "published-app-retention")
    for name in FACTS:
        reject(value[name] is None or type(value[name]) is bool, "published-app-fact")
    for name in COUNTS:
        reject(value[name] is None or type(value[name]) is int and 0 <= value[name] <= 256, "published-app-count")
    reject(value["requestedUpdateCommandInvocations"] is None or type(value["requestedUpdateCommandInvocations"]) is int and value["requestedUpdateCommandInvocations"] in {0, 1}, "published-app-command-count")
    for name in ("nativeError", "exitCode"):
        reject(value[name] is None or type(value[name]) is int and -(2**31) <= value[name] < 2**31, "published-app-code")
    reject(type(value["stage"]) is str and re.fullmatch(r"[a-zA-Z0-9-]{1,100}", value["stage"]) is not None, "published-app-stage")
    reject(value["resultState"] is None or type(value["resultState"]) is str and value["resultState"] in STATES, "published-app-result")

    def timestamp(text):
        reject(type(text) is str and re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)", text) is not None, "published-app-time")
        try:
            return dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
        except ValueError:
            reject(False, "published-app-time")
    started, observed = timestamp(value["startedUtc"]), timestamp(value["observedUtc"])
    reject(started <= observed <= dt.datetime.now(dt.timezone.utc) and observed - started <= dt.timedelta(minutes=10), "published-app-time-order")
    if value["checkedUtc"] is not None:
        reject(started <= timestamp(value["checkedUtc"]) <= observed, "published-app-check-time")
    prerequisites = {
        "processStarted": ("descriptorValidated", "executableLeased", "metadataSidecarLeased"),
        "ownedWindowObserved": ("processStarted",), "neutralOverviewObserved": ("ownedWindowObserved",),
        "overviewNoDataObserved": ("neutralOverviewObserved",), "ownedBundledRuntimeObserved": ("processStarted",),
        "updateCommandInvoked": ("ownedWindowObserved", "neutralOverviewObserved", "overviewNoDataObserved", "ownedBundledRuntimeObserved"),
        "resultObserved": ("updateCommandInvoked",), "freshResultObserved": ("resultObserved",),
        "queryOwnershipSettled": ("updateCommandInvoked", "resultObserved"), "closeRequested": ("processStarted",),
        "exitObserved": ("processStarted",),
    }
    for name, needed in prerequisites.items():
        reject(value[name] is not True or all(value[key] is True for key in needed), "published-app-order")
    reject(value["exitCode"] is None or value["exitObserved"] is True, "published-app-exit")
    reject(not value["requiresRetention"] or value["cleanupConfirmed"] is not True, "published-app-cleanup")
    reject(value["cleanupConfirmed"] is not True or value["processStarted"] is not True or value["exitObserved"] is True, "published-app-live-cleanup")
    reject(value["updateCommandInvoked"] is not True or value["requestedUpdateCommandInvocations"] == 1, "published-app-invoked")
    reject((value["resultObserved"] is True) == (value["resultState"] is not None) and
        (value["resultObserved"] is True) == (value["checkedUtc"] is not None), "published-app-result-observed")
    if value["resultState"] in {"Available", "Partial"}:
        reject(all(value[name] is not None for name in COUNTS) and value["availableRows"] + value["unknownRows"] == value["itemCount"], "published-app-row-total")
    else:
        reject(all(value[name] is None for name in COUNTS), "published-app-unchecked-rows")
    if value["state"] == "passed":
        reject(all(value[name] is True for name in FACTS) and value["requestedUpdateCommandInvocations"] == 1 and
            value["resultState"] in {"Available", "Partial"} and value["checkedUtc"] is not None and
            value["exitCode"] == 0 and value["nativeError"] is None and not value["requiresRetention"], "published-app-pass")
    return value
