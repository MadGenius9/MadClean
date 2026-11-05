"""Closed SFC client-lifecycle observations, never a Windows integrity verdict."""
import datetime as dt
from pathlib import Path
import re

FACTS = {
    "administratorObserved", "identityAccepted", "preCancellationVerified",
    "candidateAuthorizationTransferred", "candidateAuthorizationReleased", "requestMatched",
    "clientStartAttempted", "clientStartObserved", "clientExitObserved", "stdoutDrained",
    "stderrDrained", "ownershipReleased", "providerPending", "requiresRetention", "cleanupConfirmed",
}
TIMES = {"identityCheckedUtc", "identityExpiresUtc", "attemptedUtc", "completedUtc", "clientStartedUtc", "clientExitedUtc"}
FIELDS = FACTS | TIMES | {
    "formatVersion", "sourceCommit", "runId", "runAttempt", "startedUtc", "observedUtc", "state", "stage",
    "productionExecutionEnabled", "resultState", "integrity", "clientExitCode",
}
V2_FIELDS = FIELDS | {"uiRequired", "ui"}
UI_FACTS = {
    "controlBindingsObserved", "prepareObserved", "acknowledgementObserved", "confirmObserved",
    "resultObserved", "freshResultObserved", "settled", "windowClosed",
}
CANDIDATE_PROOF_FACTS = {"candidateAuthorizationTransferred", "candidateAuthorizationReleased"}
STAGES = {"Context", "Scope", "Identity", "Precancel", "Client", "Cleanup", "Completed"}
RESULT_STATES = {"Available", "Partial", "Unknown", "Failed", "Cancelled", "Unavailable", "Unsupported"}
PASS_FACTS = FACTS - {"providerPending", "requiresRetention"}


def require(condition, code):
    if not condition:
        raise ValueError(code)


def timestamp(value):
    require(type(value) is str and re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)", value) is not None, "sfc-client-time")
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        raise ValueError("sfc-client-time") from None


def validate_sfc_client_report(value, context):
    require(type(value) is dict and type(value.get("formatVersion")) is int and value["formatVersion"] in {1, 2}, "sfc-client-schema")
    version = value["formatVersion"]
    require(set(value) == (FIELDS if version == 1 else V2_FIELDS), "sfc-client-schema")
    public_ui = False
    ui = None
    if version == 2:
        require(type(value["uiRequired"]) is bool, "sfc-client-ui-required")
        public_ui = value["uiRequired"]
        ui = value["ui"]
        if public_ui:
            # No private candidate proof was transferred for the sole actual
            # client in this path: it uses the normal confirmed App entrypoint.
            require(all(value[name] is None for name in CANDIDATE_PROOF_FACTS), "sfc-client-public-proof")
            require(ui is None or type(ui) is dict and set(ui) == UI_FACTS, "sfc-client-ui-schema")
            if ui is not None:
                require(all(fact is None or type(fact) is bool for fact in ui.values()), "sfc-client-ui-fact")
                require(ui["freshResultObserved"] is not True or ui["resultObserved"] is True, "sfc-client-ui-result")
        else:
            require(ui is None, "sfc-client-ui-schema")
    require(type(value["sourceCommit"]) is str and re.fullmatch(r"[a-f0-9]{40}", value["sourceCommit"]) is not None and
            type(value["runId"]) is str and re.fullmatch(r"[0-9]{1,32}", value["runId"]) is not None and int(value["runId"]) > 0 and
            type(value["runAttempt"]) is int and value["runAttempt"] > 0 and
            (value["sourceCommit"], value["runId"], value["runAttempt"]) == context, "sfc-client-context")
    require(type(value["state"]) is str and value["state"] in {"passed", "inconclusive", "failed"}, "sfc-client-state")
    require(type(value["stage"]) is str and value["stage"] in STAGES, "sfc-client-stage")
    require(value["productionExecutionEnabled"] is public_ui and value["integrity"] == "Unknown", "sfc-client-scope")
    require(value["resultState"] is None or type(value["resultState"]) is str and value["resultState"] in RESULT_STATES, "sfc-client-result-state")
    for name in FACTS:
        require(value[name] is None or type(value[name]) is bool, "sfc-client-fact")
    require(value["clientExitCode"] is None or type(value["clientExitCode"]) is int and -(2**31) <= value["clientExitCode"] < 2**31, "sfc-client-exit-code")
    started, observed = timestamp(value["startedUtc"]), timestamp(value["observedUtc"])
    require(started <= observed <= dt.datetime.now(dt.timezone.utc), "sfc-client-time-order")
    times = {name: None if value[name] is None else timestamp(value[name]) for name in TIMES}
    for name, actual in times.items():
        if actual is not None and name != "identityExpiresUtc":
            require(started <= actual <= observed, "sfc-client-observation-time")
    checked, expires = times["identityCheckedUtc"], times["identityExpiresUtc"]
    require((checked is None) == (expires is None), "sfc-client-identity-times")
    if checked is not None:
        require(checked < expires <= checked + dt.timedelta(minutes=2), "sfc-client-identity-lifetime")
    require(value["identityAccepted"] is not True or checked is not None, "sfc-client-identity-observed")
    chronological = [times[name] for name in ("identityCheckedUtc", "attemptedUtc", "clientStartedUtc", "clientExitedUtc", "completedUtc") if times[name] is not None]
    require(chronological == sorted(chronological), "sfc-client-observation-order")
    require(times["completedUtc"] is None or times["attemptedUtc"] is not None and times["completedUtc"] >= times["attemptedUtc"], "sfc-client-completion-time")
    if times["clientStartedUtc"] is not None:
        require(value["clientStartObserved"] is True, "sfc-client-start-time")
    require(value["clientStartObserved"] is not True or
            value["clientStartAttempted"] is True and value["administratorObserved"] is True and times["attemptedUtc"] is not None, "sfc-client-start")
    require(value["clientExitObserved"] is not True or value["clientStartObserved"] is True, "sfc-client-exit")
    require(value["clientExitCode"] is None or value["clientExitObserved"] is True, "sfc-client-exit")
    require(times["clientExitedUtc"] is None or value["clientExitObserved"] is True, "sfc-client-exit")
    for name in ("stdoutDrained", "stderrDrained"):
        require(value[name] is None or value["clientStartObserved"] is True, "sfc-client-drain")
    settled = value["clientExitObserved"] is True and value["stdoutDrained"] is True and value["stderrDrained"] is True
    require(value["ownershipReleased"] is not True or value["clientStartObserved"] is not True or settled, "sfc-client-live-release")
    require(value["ownershipReleased"] is not True or value["clientStartAttempted"] is not True or value["clientStartObserved"] is True, "sfc-client-ambiguous-release")
    if value["cleanupConfirmed"] is True:
        require(value["providerPending"] is False and value["requiresRetention"] is False and
                (value["clientStartObserved"] is not True or settled and value["ownershipReleased"] is True), "sfc-client-cleanup")
    require(value["requiresRetention"] is not True or value["ownershipReleased"] is not True, "sfc-client-retention")
    if ui is not None and ui["resultObserved"] is True:
        require(value["resultState"] is not None and times["attemptedUtc"] is not None and
                times["completedUtc"] is not None, "sfc-client-ui-result")
    if value["state"] == "passed":
        pass_facts = PASS_FACTS - CANDIDATE_PROOF_FACTS if public_ui else PASS_FACTS
        require(not public_ui or ui is not None and all(ui[name] is True for name in UI_FACTS), "sfc-client-ui-pass")
        require(value["stage"] == "Completed" and all(value[name] is True for name in pass_facts) and
                value["providerPending"] is False and value["requiresRetention"] is False and
                value["resultState"] == "Available" and value["clientExitCode"] == 0 and
                all(times[name] is not None for name in TIMES) and times["completedUtc"] > times["attemptedUtc"] and
                times["clientStartedUtc"] < expires, "sfc-client-pass")
    return value


def collect(directory, context, read_json, safe_path):
    if directory is None:
        return None
    root = safe_path(Path(directory), missing=True)
    if not root.exists():
        return None
    require(root.is_dir(), "sfc-client-directory")
    # This artifact has exactly one fixed payload, not a recursively searched tree.
    entries = []
    for entry in root.iterdir():
        entries.append(entry)
        require(len(entries) <= 1 and entry.name == "report.json", "sfc-client-artifact")
    if not entries:
        return None
    return validate_sfc_client_report(read_json(root / "report.json", 16384), context)
