"""Project fixed optional CI observations; never run MadClean or inspect a live SDK.

This consumes trusted current-run downloaded artifacts, not a hostile-filesystem broker.
Inner reports do not authenticate their source. The caller must already validate CI context
and download integrity; no accepted observation enables an action or a general pass.
"""
import datetime as dt
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import stat
import sys


def _load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# The existing passive readers import this exact sibling by its script name. Bind
# it only during their import, then restore the process import table unchanged.
_neutral = _load("madclean_scoped_neutral", "read_local_ui_report.py")
_missing = object()
_previous = sys.modules.get("read_local_ui_report", _missing)
try:
    sys.modules["read_local_ui_report"] = _neutral
    _repair = _load("madclean_scoped_repair", "read_local_repair_review_report.py")
    _history = _load("madclean_scoped_history", "read_local_update_history_report.py")
finally:
    if _previous is _missing:
        sys.modules.pop("read_local_ui_report", None)
    else:
        sys.modules["read_local_ui_report"] = _previous

SOURCE_REFS = {"refs/heads/feat/phase-one-foundation", "refs/heads/recovery/validated-20261112"}
SCOPES = {
    "localRepairReview": ("local-repair-review-validation", "local-repair-review.json", 16384),
    "localUpdateHistory": ("local-update-history-validation", "local-update-history.json", 8192),
    "sdkJournalReference": ("sdk-journal-reference", "sdk-journal-reference.json", 524288),
}
HEADERS = ("um/fileapi.h", "um/winbase.h", "um/handleapi.h", "um/winnt.h", "shared/sdkddkver.h")
SYMBOLS = ("CreateDirectory2W", "CreateFile3", "CREATEFILE3_EXTENDED_PARAMETERS",
           "DIRECTORY_FLAGS", "DISALLOW_PATH_REDIRECTS", "INVALID_HANDLE_VALUE")
HEADER_BASE = {"header", "state", "reason", "sha256", "byteCount", "fragments"}
HEADER_PARSED = {"encoding", "symbols", "fragmentsOmittedForReportBound", "lexicalInterpretation", "preprocessorInterpretation"}
ROOT_ISSUES = {"sdk-root-unavailable", "sdk-root-unreadable", "sdk-directory-enumeration-limit",
               "nonlocal-source", "linked-or-device-source", "wrong-source-kind"}
HEADER_ISSUES = {"header-size-limit", "header-line-limit", "unknown-source-encoding", "unsupported-source-text",
                 "source-unreadable", "source-changed", "linked-or-device-source", "wrong-source-kind", "nonlocal-source"}


def _require(value):
    if not value:
        raise ValueError("Invalid scoped observation.")


def _shape(value, keys):
    _require(type(value) is dict and set(value) == set(keys))


def _integer(value, low, high):
    return type(value) is int and low <= value <= high


def _label(value, choices):
    return type(value) is str and value in choices


def _safe(path, directory):
    path = Path(os.path.abspath(path))
    _require(not str(path).startswith(("\\\\", "//")))
    for item in reversed((path, *path.parents)):
        info = item.lstat()
        _require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & (0x400 | 0x40))
        _require(stat.S_ISDIR(info.st_mode) if item != path or directory else stat.S_ISREG(info.st_mode))
    return path


def _entries(directory, maximum):
    _safe(directory, True)
    names = []
    with os.scandir(directory) as entries:
        for entry in entries:
            _require(len(names) < maximum)
            names.append(entry.name)
    return names


def _report_path(directory, guid, repair_layout=False):
    if guid:
        names = _entries(directory, 1)
        _require(len(names) == 1 and re.fullmatch(r"[a-f0-9]{32}", names[0]) is not None and names[0] != "0" * 32)
        directory = directory / names[0]
    names = _entries(directory, 2 if repair_layout else 1)
    allowed = {"report.json", "layout-observations.json"} if repair_layout else {"report.json"}
    _require("report.json" in names and set(names) <= allowed)
    return directory / "report.json"


def _unique(pairs):
    value = {}
    for key, item in pairs:
        _require(key not in value)
        value[key] = item
    return value


def _constant(_):
    raise ValueError("Nonfinite JSON value.")


def _read(path, maximum):
    path = _safe(path, False)
    before = path.stat()
    _require(0 < before.st_size <= maximum)
    with path.open("rb") as stream:
        opened = os.fstat(stream.fileno())
        _require(stat.S_ISREG(opened.st_mode) and (before.st_dev, before.st_ino) == (opened.st_dev, opened.st_ino))
        data = stream.read(maximum + 1)
        after = os.fstat(stream.fileno())
    _safe(path, False)
    identity = lambda info: (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)
    _require(0 < len(data) <= maximum and len(data) == before.st_size and
             identity(before) == identity(after) == identity(path.stat()))
    return json.loads(data.decode("utf-8-sig", errors="strict"), object_pairs_hook=_unique, parse_constant=_constant), hashlib.sha256(data).hexdigest()


def _layout_number(value, low, high):
    return value is None or (type(value) in (int, float) and low <= value <= high and math.isfinite(value))


def _layout_snapshot(snapshot):
    if snapshot is None:
        return
    dimensions = {"requestedWidth", "requestedHeight", "actualWidth", "actualHeight", "clientWidth", "clientHeight",
                  "workAreaWidth", "workAreaHeight"}
    _shape(snapshot, dimensions | {"dpiScaleX", "dpiScaleY", "outerSizeMatched", "shellCount", "shellCountOverLimit", "shell"})
    for key in dimensions:
        _require(_layout_number(snapshot[key], 0, 32768) and
                 (key not in {"requestedWidth", "requestedHeight"} or snapshot[key] is None or snapshot[key] > 0))
    _require(all(_layout_number(snapshot[key], .125, 16) for key in ("dpiScaleX", "dpiScaleY")))
    outer_values = [snapshot[key] for key in ("requestedWidth", "requestedHeight", "actualWidth", "actualHeight")]
    outer = None if any(value is None for value in outer_values) else (
        abs(snapshot["actualWidth"] - snapshot["requestedWidth"]) <= 2 and
        abs(snapshot["actualHeight"] - snapshot["requestedHeight"]) <= 2)
    _require((snapshot["outerSizeMatched"] is None or type(snapshot["outerSizeMatched"]) is bool) and
             snapshot["outerSizeMatched"] is outer and type(snapshot["shellCountOverLimit"]) is bool)
    count, shell, over_limit = snapshot["shellCount"], snapshot["shell"], snapshot["shellCountOverLimit"]
    _require(count is None or _integer(count, 0, 3))
    _require(shell is None or type(shell) is list and len(shell) <= 3)
    if over_limit:
        _require(count is None and (shell is None or len(shell) == 3))
    elif count is None:
        _require(shell is None)
    else:
        _require(shell is not None and len(shell) == count)
    for item in shell or []:
        _shape(item, {"kind", "visible", "x", "y", "width", "height", "withinClient"})
        _require(_label(item["kind"], {"page-host", "navigation", "tools"}) and
                 (item["visible"] is None or type(item["visible"]) is bool) and
                 all(_layout_number(item[key], -32768, 32768) for key in ("x", "y")) and
                 all(_layout_number(item[key], 0, 32768) for key in ("width", "height")))
        sizes = [snapshot["clientWidth"], snapshot["clientHeight"], item["width"], item["height"]]
        if any(value == 0 for value in sizes if value is not None):
            within = False
        elif any(value is None for value in (*sizes, item["x"], item["y"])):
            within = None
        else:
            within = (item["x"] >= -1 and item["y"] >= -1 and item["x"] + item["width"] <= snapshot["clientWidth"] + 1 and
                      item["y"] + item["height"] <= snapshot["clientHeight"] + 1)
        _require((item["withinClient"] is None or type(item["withinClient"]) is bool) and item["withinClient"] is within)


def validate_layout(report, primary_digest):
    """Fixed geometry diagnostics only; never reinterpret the primary result."""
    _shape(report, {"formatVersion", "context", "primaryReportSha256", "sourceAuthenticity", "windowsProviders",
                    "maintenance", "physicalInput", "wide", "compact"})
    _require(type(report["formatVersion"]) is int and report["formatVersion"] == 1 and
             report["context"] == "local-repair-review-layout" and report["primaryReportSha256"] == primary_digest and
             all(report[key] == "unverified" for key in ("sourceAuthenticity", "windowsProviders", "maintenance", "physicalInput")))
    _layout_snapshot(report["wide"])
    _layout_snapshot(report["compact"])
    return report


def _layout_companion(primary_path, primary_digest):
    try:
        report, digest = _read(primary_path.with_name("layout-observations.json"), 16384)
        validate_layout(report, primary_digest)
        return report, digest, None
    except FileNotFoundError:
        return None, None, None
    except (OSError, ValueError, TypeError, KeyError, RecursionError):
        return None, None, "layout-evidence-rejected"


def _source_line(value):
    _shape(value, {"line", "text"})
    _require(_integer(value["line"], 1, 120000) and type(value["text"]) is str and len(value["text"]) <= 512 and
             not any(char in "\x00\r\n" or 0xD800 <= ord(char) <= 0xDFFF for char in value["text"]))


def _fragment(value, seen_lines):
    _shape(value, {"matchLine", "mentions", "lines", "fragmentTruncated", "preprocessorContext", "preprocessorContextUnknown"})
    _require(_integer(value["matchLine"], 1, 120000) and type(value["fragmentTruncated"]) is bool and
             type(value["preprocessorContextUnknown"]) is bool)
    lines, mentions, guards = value["lines"], value["mentions"], value["preprocessorContext"]
    _require(type(lines) is list and 1 <= len(lines) <= 27 and type(guards) is list and len(guards) <= 16 and
             type(mentions) is list and 1 <= len(mentions) <= len(SYMBOLS))
    numbers = []
    for line in lines:
        _source_line(line)
        numbers.append(line["line"])
        _require(line["line"] not in seen_lines or seen_lines[line["line"]] == line["text"])
        seen_lines[line["line"]] = line["text"]
    _require(numbers == list(range(numbers[0], numbers[0] + len(numbers))) and value["matchLine"] in numbers)
    guard_numbers = []
    for line in guards:
        _source_line(line)
        guard_numbers.append(line["line"])
        _require(line["line"] <= value["matchLine"] and
                 (line["line"] not in seen_lines or seen_lines[line["line"]] == line["text"]))
        seen_lines[line["line"]] = line["text"]
    _require(guard_numbers == sorted(set(guard_numbers)))
    found = set()
    for mention in mentions:
        _shape(mention, {"symbol", "kind"})
        _require(_label(mention["symbol"], SYMBOLS) and mention["symbol"] not in found and
                 _label(mention["kind"], {"code-token", "comment-or-string-only"}))
        found.add(mention["symbol"])


def _header(value):
    _require(type(value) is dict and _label(value.get("header"), HEADERS))
    if value.get("state") == "unknown":
        _require(set(value) in (HEADER_BASE, HEADER_BASE | {"fragmentsOmittedForReportBound"}) and
                 _label(value["reason"], HEADER_ISSUES) and value["fragments"] == [])
        _require(value.get("fragmentsOmittedForReportBound", 0) == 0)
    else:
        _shape(value, HEADER_BASE | HEADER_PARSED)
        _require(value["state"] == "reference-fragments-only" and value["reason"] is None and
                 _label(value["encoding"], {"utf-8-sig", "utf-16"}) and
                 value["lexicalInterpretation"] == "heuristic-only" and value["preprocessorInterpretation"] == "not-evaluated")
        _require(type(value["symbols"]) is list and len(value["symbols"]) == len(SYMBOLS))
        for symbol, expected in zip(value["symbols"], SYMBOLS):
            _shape(symbol, {"symbol", "matchingLineCount", "state", "definitionState", "matchingLinesOmitted"})
            _require(symbol["symbol"] == expected and _integer(symbol["matchingLineCount"], 0, 120000) and
                     symbol["definitionState"] == "unknown" and type(symbol["matchingLinesOmitted"]) is int and
                     symbol["matchingLinesOmitted"] == max(0, symbol["matchingLineCount"] - 4) and
                     symbol["state"] == ("observed-reference-only" if symbol["matchingLineCount"] else "unknown-not-observed"))
        _require(type(value["fragments"]) is list and len(value["fragments"]) <= 24 and
                 _integer(value["fragmentsOmittedForReportBound"], 0, 24))
        seen_lines, positions = {}, []
        observed_counts = {symbol["symbol"]: symbol["matchingLineCount"] for symbol in value["symbols"]}
        mentioned_lines = {symbol: set() for symbol in SYMBOLS}
        for fragment in value["fragments"]:
            _fragment(fragment, seen_lines)
            positions.append(fragment["matchLine"])
            for mention in fragment["mentions"]:
                mentioned_lines[mention["symbol"]].add(fragment["matchLine"])
        _require(positions == sorted(set(positions)) and
                 len(positions) + value["fragmentsOmittedForReportBound"] <= 24 and
                 all(len(mentioned_lines[symbol]) <= observed_counts[symbol] for symbol in SYMBOLS) and
                 (not any(observed_counts.values()) or positions or value["fragmentsOmittedForReportBound"] > 0))
    digest, count = value["sha256"], value["byteCount"]
    _require((digest is None and count is None and value["state"] == "unknown") or
             (type(digest) is str and re.fullmatch(r"[a-f0-9]{64}", digest) is not None and _integer(count, 0, 4194304)))


def validate_sdk(report, now=None):
    _shape(report, {"formatVersion", "context", "observedUtc", "state", "sourceAuthenticity", "abi", "osAvailability",
                    "namespaceSecurity", "nativeQualification", "reportTruncated", "sourceIssue", "versions"})
    _require(type(report["formatVersion"]) is int and report["formatVersion"] == 1 and
             report["context"] == "installed-windows-sdk-journal-reference" and type(report["reportTruncated"]) is bool and
             all(report[key] == "unverified" for key in ("sourceAuthenticity", "abi", "osAvailability", "namespaceSecurity", "nativeQualification")))
    now = now if now is not None else dt.datetime.now(dt.timezone.utc)
    _require(now.tzinfo is not None and now.utcoffset() == dt.timedelta(0) and 0 < _history.ticks(report["observedUtc"]) <= _history.ticks(now.isoformat()))
    versions = report["versions"]
    _require(type(versions) is list and len(versions) <= 4 and report["state"] == ("reference-only" if versions else "unknown") and
             (report["sourceIssue"] is None or _label(report["sourceIssue"], ROOT_ISSUES) and not versions))
    seen_versions, omitted = [], False
    for version in versions:
        _shape(version, {"sdkVersion", "state", "headers"})
        number = version["sdkVersion"]
        _require(type(number) is str and re.fullmatch(r"[0-9]{1,10}(?:\.[0-9]{1,10}){3}", number) is not None)
        parts = tuple(int(part) for part in number.split("."))
        _require(all(part <= 2147483647 for part in parts) and ".".join(map(str, parts)) == number)
        seen_versions.append(parts)
        headers = version["headers"]
        _require(type(headers) is list and _label(version["state"], {"reference-only", "unknown-source"}))
        if version["state"] == "unknown-source":
            _require(headers == [])
            continue
        _require(len(headers) == len(HEADERS))
        for header, expected in zip(headers, HEADERS):
            _header(header)
            _require(header["header"] == expected)
            omitted |= header.get("fragmentsOmittedForReportBound", 0) > 0
    _require(seen_versions == sorted(set(seen_versions), reverse=True) and (not omitted or report["reportTruncated"]))
    return report


def collect(directory, context, source_ref, download_result="skipped"):
    """Return fixed per-scope status and wrapped bytes; do not publish or write files."""
    _require(type(context) is tuple and len(context) == 3 and type(context[0]) is str and
             re.fullmatch(r"[a-f0-9]{40}", context[0]) is not None and type(context[1]) is str and
             re.fullmatch(r"[0-9]{1,32}", context[1]) is not None and int(context[1]) > 0 and
             _integer(context[2], 1, 9999999999) and _label(source_ref, SOURCE_REFS) and
             _label(download_result, {"success", "failure", "cancelled", "skipped"}))
    observations = {scope: {"present": False, "reportedState": None, "originalReportSha256": None, "error": None} for scope in SCOPES}
    files = {}
    if download_result != "success" or directory is None:
        return observations, files
    try:
        directory = _safe(directory, True)
    except FileNotFoundError:
        return observations, files
    except (OSError, ValueError):
        for observation in observations.values():
            observation["error"] = "unsafe-artifact-root"
        return observations, files
    for scope, (folder, output, maximum) in SCOPES.items():
        try:
            path = _report_path(directory / folder, guid=scope != "sdkJournalReference", repair_layout=scope == "localRepairReview")
            report, digest = _read(path, maximum)
            if scope == "localRepairReview":
                _repair.validate(report)
            elif scope == "localUpdateHistory":
                _history.validate(report)
            else:
                validate_sdk(report)
            wrapper = {"formatVersion": 1, "context": "MadClean scoped development observation",
                       "sourceCommit": context[0], "runId": context[1], "runAttempt": context[2], "sourceRef": source_ref,
                       "scope": scope, "reportSha256": digest, "sourceAuthenticity": "unverified", "report": report}
            if scope == "localRepairReview":
                layout, layout_digest, layout_error = _layout_companion(path, digest)
                wrapper.update(layoutObservation=layout, layoutObservationSha256=layout_digest, layoutObservationError=layout_error)
            files[output] = (json.dumps(wrapper, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
            observations[scope].update(present=True, reportedState=report["state"], originalReportSha256=digest)
        except FileNotFoundError:
            pass
        except (OSError, ValueError, TypeError, KeyError, RecursionError):
            observations[scope]["error"] = "scope-evidence-rejected"
    return observations, files
