"""Bounded fixed-field evidence for the separate recorded component-store flag job.

This module does not execute PowerShell, inspect a PC, or interpret text output.
"""
from __future__ import annotations

import datetime as dt
import re
from pathlib import Path

TOP_BOOLEANS = {
    "administratorObserved", "precancelNoHostObserved", "precancelAuthorizationTransferred",
    "candidateAuthorizationTransferred", "candidateAuthorizationReleased", "requestMatched",
    "transportConsistent", "evidenceConsistent", "sessionStopRequested", "sessionPending",
    "providerPending", "sessionRetention", "providerRetention", "ownedTeardown",
}
TRANSPORT_BOOLEANS = {
    "administratorObserved", "hostStartAttempted", "hostStartObserved", "hostExitObserved", "ownershipReleased",
    "stdoutDrained", "stderrDrained", "outputBoundExceeded", "stopSignalSucceeded",
}
EVIDENCE_BOOLEANS = {
    "hostQualified", "moduleBound", "cmdletQualified", "outputContractQualified", "invocationStarted",
    "endInvokeReturned", "cancelEventObserved", "beginStopCalled", "endStopReturned", "powerShellDisposed",
    "runspaceClosed", "cancelHandleDisposed",
}
CHECK_STATES = {"Unknown", "Available", "Unavailable", "Unsupported", "Failed", "Partial", "Cancelled"}
FLAGS = {"Unknown", "NoRecordedCorruptionFlag", "RecordedRepairable", "RecordedNonRepairable"}
IDENTITY_STAGES = {"Unknown", "SystemDirectory", "FileLease", "Signature", "Fingerprint", "VersionMetadata", "Completed"}
IDENTITY_REASONS = {"Unspecified", "Observed", "NativeTrustNotEstablished", "SignerInspectionUnavailable", "SignerNotQualified", "OriginalFilenameMismatch", "IdentityBoundsOrPath", "OtherUnavailable", "WaitExpired", "PendingWorker", "UnsupportedPlatform", "TrustCleanupUnavailable"}
EVIDENCE_STAGES = {"NotStarted", "AdministratorCheck", "HostIdentity", "HostStart", "ModuleImport", "CmdletQualification", "Invocation", "StopRequested", "OutputQualification", "Cleanup", "Completed"}
OUTCOMES = {"NotStarted", "Unavailable", "MetadataUnqualified", "InvocationFailed", "PipelineStopped", "UnexpectedOutput", "Completed", "CleanupUncertain"}
PIPELINE_STATES = {"NotStarted", "Running", "Stopping", "Stopped", "Completed", "Failed", "Disconnected"}


def collect(directory, context, read_json, safe_path, reject):
    if directory is None:
        return None
    path = safe_path(Path(directory), missing=True) / "report.json"
    if not path.exists():
        return None
    value = read_json(path, 8192)
    fields = {"formatVersion", "sourceCommit", "runId", "runAttempt", "startedUtc", "observedUtc", "state", "stage",
              "precancelState", "resultState", "recordedFlag", "transport", "evidence", "fixtureExceptionHResult"} | TOP_BOOLEANS
    reject(isinstance(value, dict) and set(value) == fields and type(value["formatVersion"]) is int and value["formatVersion"] == 1, "component-report-schema")
    reject(type(value["runAttempt"]) is int and (value["sourceCommit"], value["runId"], value["runAttempt"]) == context, "component-report-context")
    reject(isinstance(value["state"], str) and value["state"] in {"passed", "failed", "inconclusive"}, "component-report-state")
    reject(isinstance(value["stage"], str) and value["stage"] in {"OwnedSetup", "AdministratorUnavailable", "Precancel", "PrecancelInvariantFailed", "Candidate", "Observed", "OwnershipUnsettled", "Complete"}, "component-report-stage")
    _booleans(value, TOP_BOOLEANS, reject)
    _label(value["precancelState"], CHECK_STATES, reject)
    _label(value["resultState"], CHECK_STATES, reject)
    _label(value["recordedFlag"], FLAGS, reject)
    _integer(value["fixtureExceptionHResult"], reject)
    times = []
    for field in ("startedUtc", "observedUtc"):
        text = value[field]
        reject(isinstance(text, str) and re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)", text) is not None, "component-report-time")
        try:
            times.append(dt.datetime.fromisoformat(text.replace("Z", "+00:00")))
        except ValueError:
            reject(False, "component-report-time")
    reject(times[0] <= times[1] <= dt.datetime.now(dt.timezone.utc) and times[1] - times[0] <= dt.timedelta(minutes=20), "component-report-time-order")
    transport, evidence = value["transport"], value["evidence"]
    if transport is not None:
        transport_fields = TRANSPORT_BOOLEANS | {"hostExitCode", "stderrCharacterCount", "stopSignalNativeError", "identityStage", "identityReason", "nativeTrustResult"}
        reject(isinstance(transport, dict) and set(transport) == transport_fields, "component-transport-schema")
        _booleans(transport, TRANSPORT_BOOLEANS, reject)
        for field in ("hostExitCode", "stopSignalNativeError", "nativeTrustResult"):
            _integer(transport[field], reject)
        _integer(transport["stderrCharacterCount"], reject, 0)
        _label(transport["identityStage"], IDENTITY_STAGES, reject)
        _label(transport["identityReason"], IDENTITY_REASONS, reject)
        reject((transport["identityStage"] is None) == (transport["identityReason"] is None), "component-identity-coherence")
        reject(transport["identityStage"] is not None or transport["nativeTrustResult"] is None, "component-identity-coherence")
        reject(not (transport["hostStartAttempted"] is False and transport["hostStartObserved"] is True), "component-start-coherence")
        reject(transport["hostExitObserved"] is not True or transport["hostStartObserved"] is True, "component-exit-coherence")
        reject(transport["hostExitCode"] is None or transport["hostExitObserved"] is True, "component-exit-coherence")
        reject(not (transport["hostStartObserved"] is True and transport["ownershipReleased"] is True) or transport["hostExitObserved"] is True, "component-ownership-coherence")
        reject(transport["stopSignalNativeError"] is None or transport["stopSignalSucceeded"] is False, "component-stop-coherence")
    if evidence is not None:
        evidence_fields = EVIDENCE_BOOLEANS | {"stage", "outcome", "pipelineState", "outputCount", "errorCount", "warningCount", "healthState", "managedExceptionHResult"}
        reject(isinstance(evidence, dict) and set(evidence) == evidence_fields, "component-evidence-schema")
        _booleans(evidence, EVIDENCE_BOOLEANS, reject)
        _label(evidence["stage"], EVIDENCE_STAGES, reject, required=True)
        _label(evidence["outcome"], OUTCOMES, reject, required=True)
        _label(evidence["pipelineState"], PIPELINE_STATES, reject)
        _label(evidence["healthState"], {"Healthy", "Repairable", "NonRepairable"}, reject)
        _integer(evidence["outputCount"], reject, 0, 16)
        _integer(evidence["errorCount"], reject, 0, 128)
        _integer(evidence["warningCount"], reject, 0, 128)
        _integer(evidence["managedExceptionHResult"], reject)
        reject(evidence["moduleBound"] is not True or evidence["hostQualified"] is True, "component-qualification-coherence")
        reject(evidence["cmdletQualified"] is not True or evidence["moduleBound"] is True, "component-qualification-coherence")
        reject(evidence["outputContractQualified"] is not True or evidence["cmdletQualified"] is True, "component-qualification-coherence")
        if evidence["invocationStarted"] is True:
            reject(all(evidence[name] is True for name in ("hostQualified", "moduleBound", "cmdletQualified", "outputContractQualified")), "component-qualification-coherence")
        reject(evidence["endInvokeReturned"] is None or evidence["invocationStarted"] is True, "component-pipeline-coherence")
        reject(evidence["pipelineState"] in (None, "NotStarted") or evidence["invocationStarted"] is True, "component-pipeline-coherence")
        reject(evidence["beginStopCalled"] is not True or evidence["cancelEventObserved"] is True and evidence["invocationStarted"] is True, "component-pipeline-coherence")
        reject(evidence["endStopReturned"] is None or evidence["beginStopCalled"] is True, "component-pipeline-coherence")
        if evidence["healthState"] is not None:
            reject(evidence["outputContractQualified"] is True and evidence["invocationStarted"] is True and evidence["outputCount"] is not None and evidence["outputCount"] >= 1, "component-output-coherence")
        reject(evidence["outcome"] != "MetadataUnqualified" or evidence["invocationStarted"] is not True, "component-pipeline-coherence")
        reject(evidence["outcome"] != "PipelineStopped" or evidence["pipelineState"] == "Stopped", "component-pipeline-coherence")
        if evidence["outcome"] == "Completed":
            reject(evidence["pipelineState"] == "Completed" and evidence["endInvokeReturned"] is True and evidence["outputCount"] == 1 and evidence["errorCount"] == 0 and evidence["healthState"] is not None, "component-output-coherence")
    if value["recordedFlag"] not in (None, "Unknown"):
        reject(value["resultState"] == "Available" and evidence is not None and evidence["errorCount"] == 0 and evidence["warningCount"] == 0, "component-flag-coherence")
    if value["resultState"] == "Available":
        reject(value["recordedFlag"] not in (None, "Unknown"), "component-flag-coherence")
    reject(value["evidenceConsistent"] is not True or evidence is not None, "component-evidence-coherence")
    reject(value["transportConsistent"] is not True or transport is not None, "component-transport-coherence")
    reject(value["requestMatched"] is not False or value["state"] == "failed", "component-request-mismatch")
    if value["precancelNoHostObserved"] is True:
        reject(value["precancelState"] == "Cancelled" and value["precancelAuthorizationTransferred"] is False, "component-precancel-coherence")
    if value["ownedTeardown"] is True:
        reject(value["candidateAuthorizationReleased"] is True and all(value[name] is False for name in ("sessionPending", "providerPending", "sessionRetention", "providerRetention")), "component-teardown-coherence")
    if value["state"] == "passed":
        required_true = {"administratorObserved", "precancelNoHostObserved", "candidateAuthorizationTransferred", "candidateAuthorizationReleased", "requestMatched", "transportConsistent", "evidenceConsistent", "ownedTeardown"}
        required_false = {"precancelAuthorizationTransferred", "sessionStopRequested", "sessionPending", "providerPending", "sessionRetention", "providerRetention"}
        reject(value["stage"] == "Complete" and value["resultState"] == "Available" and value["fixtureExceptionHResult"] is None and
               all(value[name] is True for name in required_true) and all(value[name] is False for name in required_false), "component-pass-coherence")
        reject(transport is not None and evidence is not None, "component-pass-coherence")
        reject(all(transport[name] is True for name in ("administratorObserved", "hostStartAttempted", "hostStartObserved", "hostExitObserved", "ownershipReleased", "stdoutDrained", "stderrDrained")) and
               transport["hostExitCode"] == 0 and transport["outputBoundExceeded"] is False and transport["stderrCharacterCount"] == 0 and
               transport["stopSignalSucceeded"] is None and transport["stopSignalNativeError"] is None and
               transport["identityStage"] == "Completed" and transport["identityReason"] == "Observed" and transport["nativeTrustResult"] == 0, "component-pass-transport")
        reject(evidence["stage"] == "Completed" and evidence["outcome"] == "Completed" and evidence["pipelineState"] == "Completed" and
               all(evidence[name] is True for name in ("hostQualified", "moduleBound", "cmdletQualified", "outputContractQualified", "invocationStarted", "endInvokeReturned", "powerShellDisposed", "runspaceClosed", "cancelHandleDisposed")) and
               evidence["cancelEventObserved"] is False and evidence["beginStopCalled"] is False and evidence["endStopReturned"] is None and
               evidence["outputCount"] == 1 and evidence["errorCount"] == 0 and evidence["warningCount"] == 0 and evidence["managedExceptionHResult"] is None, "component-pass-evidence")
        expected = {"Healthy": "NoRecordedCorruptionFlag", "Repairable": "RecordedRepairable", "NonRepairable": "RecordedNonRepairable"}
        reject(evidence["healthState"] in expected and value["recordedFlag"] == expected[evidence["healthState"]], "component-pass-flag")
    return value


def _booleans(value, fields, reject):
    for field in fields:
        reject(value[field] is None or type(value[field]) is bool, "component-report-boolean")


def _integer(value, reject, minimum=-(2**31), maximum=2**31 - 1):
    reject(value is None or type(value) is int and minimum <= value <= maximum, "component-report-integer")


def _label(value, allowed, reject, required=False):
    reject((value is None and not required) or isinstance(value, str) and value in allowed, "component-report-label")
