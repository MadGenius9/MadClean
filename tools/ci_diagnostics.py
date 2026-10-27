"""Collect minimized, source-bound development CI excerpts; never production provider logs."""
from __future__ import annotations

import json
import os
from pathlib import Path
import re
import stat
import unicodedata

LABELS = ("restore", "build", "publisher-tests", "core-tests", "publish", "smoke")
OUTCOMES = {"success", "failure", "cancelled", "skipped"}
MAX_LOG_BYTES = 262_144
MAX_METADATA_BYTES = 8_192
MAX_EXCERPT_BYTES = 16_384
MAX_LINES = 40
CAPTURE_SCOPE = "Scrubbed development compiler/test output only; not production inventory."
SCOPE = ("Bounded, redacted development compiler/test command excerpts only; not production inventory or provider logs. "
         "A zero exit code is not a test count, Windows runtime result or health verdict. Missing stages were not observed; "
         "truncated includes capture/excerpt limits.")
FIELDS = {"formatVersion", "label", "sourceCommit", "runId", "runAttempt", "exitCode", "truncated", "scope"}
ANSI = re.compile(r"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|$))", re.DOTALL)
URL = re.compile(r"https?://[^\s<>\"']+", re.IGNORECASE)
SENSITIVE_KEY = r"(?:[A-Za-z0-9_-]*(?:token|password|passwd|secret|api[-_]?key)[A-Za-z0-9_-]*|pwd|signature|sig)"
ASSIGNMENT = re.compile(r"(\b" + SENSITIVE_KEY + r"\b[\"']?\s*[:=]\s*)[^\n]*", re.IGNORECASE)
ARGUMENT = re.compile(r"(--?" + SENSITIVE_KEY + r"\b(?:\s+|=))[^\n]*", re.IGNORECASE)
AUTH = re.compile(r"((?:proxy[-_ ]?)?authorization\s*[:=]\s*)[^\n]*", re.IGNORECASE)
BEARER = re.compile(r"\b(Bearer|Basic)\s+[A-Za-z0-9+/_.=-]{6,}", re.IGNORECASE)
KNOWN_TOKEN = re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9_]{10,}|github_pat_[A-Za-z0-9_]{10,}|eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})\b")
PRIVATE_KEY = re.compile(r"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|$)", re.DOTALL)
ERROR_LINE = re.compile(r"\b(?:error|failed|fatal|exception|traceback)\b", re.IGNORECASE)


def _require(condition, code):
    if not condition:
        raise ValueError(code)


def _safe(path: Path) -> Path:
    """Inspect existing ancestors root-to-leaf without resolving links first."""
    path = Path(os.path.abspath(path))
    for part in (*reversed(path.parents), path):
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        except OSError:
            raise ValueError("ci-path-access") from None
        _require(not stat.S_ISLNK(info.st_mode) and not (getattr(info, "st_file_attributes", 0) & 0x400), "ci-reparse-path")
        if part != path:
            _require(stat.S_ISDIR(info.st_mode), "ci-path-parent")
    return path


def _file(path: Path):
    path = _safe(path)
    try:
        info = path.lstat()
    except FileNotFoundError:
        return None
    except OSError:
        raise ValueError("ci-file-access") from None
    _require(stat.S_ISREG(info.st_mode), "ci-not-regular-file")
    return info


def _read(path: Path, maximum: int) -> bytes:
    info = _file(path)
    _require(info is not None and info.st_size <= maximum, "ci-file-size")
    try:
        with path.open("rb") as stream:
            data = stream.read(maximum + 1)
    except OSError:
        raise ValueError("ci-file-read") from None
    _require(len(data) <= maximum, "ci-file-size")
    return data


def _unique(pairs):
    result = {}
    for key, value in pairs:
        _require(key not in result, "ci-duplicate-json-property")
        result[key] = value
    return result


def _bad_number(_):
    raise ValueError("ci-invalid-json-number")


def _metadata(path: Path, label: str, context: tuple[str, str, int]) -> dict:
    try:
        value = json.loads(_read(path, MAX_METADATA_BYTES).decode("utf-8-sig"), object_pairs_hook=_unique, parse_constant=_bad_number)
    except (UnicodeError, json.JSONDecodeError, RecursionError):
        raise ValueError("ci-invalid-metadata-json") from None
    _require(isinstance(value, dict) and set(value) == FIELDS, "ci-metadata-schema")
    _require(type(value["formatVersion"]) is int and value["formatVersion"] == 1 and value["label"] == label, "ci-metadata-label")
    _require(value["sourceCommit"] == context[0] and value["runId"] == context[1] and
             type(value["runAttempt"]) is int and value["runAttempt"] == context[2], "ci-metadata-context")
    _require(type(value["exitCode"]) is int and -(2 ** 31) <= value["exitCode"] <= 2 ** 32 - 1 and
             type(value["truncated"]) is bool and value["scope"] == CAPTURE_SCOPE, "ci-metadata-values")
    return value


def _url(match):
    value = match.group(0)
    scheme, tail = value.split("://", 1)
    boundary = min((position for marker in "/?#" if (position := tail.find(marker)) >= 0), default=len(tail))
    authority, rest = tail[:boundary], tail[boundary:]
    if "@" in authority:
        authority = "[redacted]@" + authority.rsplit("@", 1)[1]
    end = min((position for marker in "?#" if (position := rest.find(marker)) >= 0), default=len(rest))
    suffix = "?[redacted-query]" if end < len(rest) else ""
    return scheme + "://" + authority + rest[:end] + suffix


def scrub(text: str) -> str:
    text = text.replace("\r\n", "\n").replace("\r", "\n").replace("\u2028", "\n").replace("\u2029", "\n")
    text = ANSI.sub("", text)
    # Remove invalid-byte replacement characters and invisible controls before recognizing secret keys.
    text = "".join(c for c in text if c in "\n\t" or (c != "\ufffd" and unicodedata.category(c) not in {"Cc", "Cf", "Cs"}))
    text = PRIVATE_KEY.sub("[redacted-private-key]", text)
    text = URL.sub(_url, text)
    text = AUTH.sub(lambda match: match.group(1) + "[redacted]", text)
    text = ARGUMENT.sub(lambda match: match.group(1) + "[redacted]", text)
    text = ASSIGNMENT.sub(lambda match: match.group(1) + "[redacted]", text)
    text = BEARER.sub(lambda match: match.group(1) + " [redacted]", text)
    return KNOWN_TOKEN.sub("[redacted-token]", text)


def _excerpt(text: str, maximum: int) -> tuple[str, bool]:
    lines = text.splitlines()
    if not any(line.strip() for line in lines):
        text = "No command output was captured. Exit code alone does not establish tests or Windows verification."
        return text, False
    # unittest's per-test "... ERROR" lines precede the useful traceback and can consume
    # the entire excerpt. Prefer an actual traceback/error diagnostic when one exists.
    trace = next((i for i, line in enumerate(lines) if line.startswith("Traceback (most recent call last):")), None)
    if trace is not None:
        selected = set(range(max(0, trace - 1), min(len(lines), trace + 22)))
        selected.update(range(max(0, len(lines) - 8), len(lines)))
    else:
        selected = set(range(max(0, len(lines) - 10), len(lines)))
    for index, line in enumerate(lines):
        if trace is not None: break
        if ERROR_LINE.search(line):
            selected.update(range(max(0, index - 2), min(len(lines), index + 3)))
            if len(selected) >= MAX_LINES - 5:
                break
    if not any(ERROR_LINE.search(line) for line in lines):
        selected = set(range(max(0, len(lines) - MAX_LINES), len(lines)))
    indexes = sorted(selected)[:MAX_LINES]
    excerpt = "\n".join(lines[index] for index in indexes)
    limited = len(indexes) < len(lines)
    data = excerpt.encode("utf-8")
    if len(data) > maximum:
        marker = b"\n[Excerpt truncated.]"
        prefix = data[:max(0, maximum - len(marker))].decode("utf-8", errors="ignore")
        if len(prefix.splitlines()) >= MAX_LINES:
            prefix = "\n".join(prefix.splitlines()[:MAX_LINES - 1])
        excerpt = prefix + marker.decode()
        limited = True
    return excerpt, limited


def collect(directory: Path | None, context: tuple[str, str, int], download_outcome: str) -> dict | None:
    """Admit only complete fixed-stage pairs from an integrity-verified download.

    Files are development command text, not production/provider data. As with the parent publisher,
    lexical checks assume a trusted disposable CI checkout rather than a hostile concurrent writer.
    """
    _require(isinstance(download_outcome, str) and download_outcome in OUTCOMES, "ci-download-outcome")
    if download_outcome != "success" or directory is None:
        return None
    _require(isinstance(context, tuple) and len(context) == 3 and isinstance(context[0], str) and
             re.fullmatch(r"[a-f0-9]{40}", context[0]) is not None and isinstance(context[1], str) and
             re.fullmatch(r"[0-9]{1,32}", context[1]) is not None and int(context[1]) > 0 and
             type(context[2]) is int and context[2] > 0, "ci-context")
    root = _safe(directory)
    try:
        root_info = root.lstat()
    except FileNotFoundError:
        return None
    except OSError:
        raise ValueError("ci-directory-access") from None
    _require(stat.S_ISDIR(root_info.st_mode), "ci-not-directory")
    stages = []
    # Distribute the total excerpt budget across the fixed labels so later stages retain useful context.
    per_stage = MAX_EXCERPT_BYTES // len(LABELS)
    for label in LABELS:
        metadata_path, log_path = root / (label + ".json"), root / (label + ".log")
        metadata_info, log_info = _file(metadata_path), _file(log_path)
        if metadata_info is None and log_info is None:
            continue
        _require(metadata_info is not None and log_info is not None, "ci-incomplete-stage")
        metadata = _metadata(metadata_path, label, context)
        text = scrub(_read(log_path, MAX_LOG_BYTES).decode("utf-8", errors="replace"))
        excerpt, limited = _excerpt(text, per_stage)
        stages.append({"label": label, "exitCode": metadata["exitCode"], "truncated": metadata["truncated"] or limited, "excerpt": excerpt})
    return {"scope": SCOPE, "stages": stages} if stages else None
