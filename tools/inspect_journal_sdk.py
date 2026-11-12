"""Collect bounded reference fragments from already-installed Windows SDK headers.

No compiler, native operation, SDK acquisition or ABI/availability qualification is run.
The command has no arguments or SDK-path override. Exported fragments need human review.
"""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

HEADERS = ("um/fileapi.h", "um/winbase.h", "um/handleapi.h", "um/winnt.h", "shared/sdkddkver.h")
SYMBOLS = ("CreateDirectory2W", "CreateFile3", "CREATEFILE3_EXTENDED_PARAMETERS",
           "DIRECTORY_FLAGS", "DISALLOW_PATH_REDIRECTS", "INVALID_HANDLE_VALUE")
MAX_HEADER_BYTES = 4 * 1024 * 1024
MAX_REPORT_BYTES = 512 * 1024
MAX_LINES = 120_000
TOKEN = re.compile(r"(?<![A-Za-z0-9_])(?:" + "|".join(SYMBOLS) +
                   r"|[A-Za-z_][A-Za-z0-9_]*_DISALLOW_PATH_REDIRECTS)(?![A-Za-z0-9_])")
VERSION = re.compile(r"[0-9]{1,10}(?:\.[0-9]{1,10}){3}")


class SourceError(ValueError):
    """Fixed reason only: do not export exception text or local paths."""


def mentioned_symbols(line):
    # Preserve an SDK's actual prefixed flag identifier instead of guessing its name.
    return {"DISALLOW_PATH_REDIRECTS" if token.endswith("_DISALLOW_PATH_REDIRECTS") else token
            for token in TOKEN.findall(line)}


def ordinary(path, directory):
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & (0x400 | 0x40):
        raise SourceError("linked-or-device-source")
    if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)):
        raise SourceError("wrong-source-kind")
    return info


def ancestry(path):
    if not path.is_absolute() or str(path).startswith(("\\\\", "//")):
        raise SourceError("nonlocal-source")
    for item in reversed((path, *path.parents)):
        ordinary(item, True)


def code_only(text):
    """Conservative lexical aid, not a C/preprocessor parser or definition verifier."""
    result = list(text)
    state, index = "code", 0
    while index < len(text):
        char = text[index]
        following = text[index + 1:index + 2]
        if state == "code":
            if char == "/" and following in ("/", "*"):
                state = "line" if following == "/" else "block"
                result[index] = result[index + 1] = " "
                index += 2
                continue
            if char in ('"', "'"):
                state = char
                result[index] = " "
        else:
            if char != "\n":
                result[index] = " "
            if state == "block" and char == "*" and following == "/":
                result[index + 1] = " "
                state = "code"
                index += 2
                continue
            if state == "line" and char == "\n" and (index == 0 or text[index - 1] != "\\"):
                state = "code"
            elif state in ('"', "'"):
                if char == "\\" and following:
                    if following != "\n":
                        result[index + 1] = " "
                    index += 2
                    continue
                if char == state:
                    state = "code"
        index += 1
    return "".join(result), state in ("code", "line")


def parse_header(data):
    if not isinstance(data, bytes) or len(data) > MAX_HEADER_BYTES:
        raise SourceError("header-size-limit")
    encoding = "utf-16" if data.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig"
    try:
        text = data.decode(encoding, errors="strict").replace("\r\n", "\n")
    except UnicodeError:
        raise SourceError("unknown-source-encoding") from None
    if "\x00" in text or "\r" in text:
        raise SourceError("unsupported-source-text")
    lines = text.split("\n")
    if len(lines) > MAX_LINES:
        raise SourceError("header-line-limit")
    masked, lexically_complete = code_only(text)
    code = masked.split("\n")
    matches = {symbol: [] for symbol in SYMBOLS}
    for number, line in enumerate(lines, 1):
        for symbol in mentioned_symbols(line):
            matches[symbol].append(number)
    selected = sorted({number for values in matches.values() for number in values[:4]})
    guards, contexts, guard_unknown = [], {}, not lexically_complete
    for number, line in enumerate(code, 1):
        directive = re.match(r"^\s*#\s*(if|ifdef|ifndef|elif|else|endif)\b", line)
        if directive:
            kind = directive[1]
            raw = lines[number - 1]
            fragment = {"line": number, "text": raw[:512]}
            if len(raw) > 512 or raw.rstrip().endswith("\\"):
                guard_unknown = True
            if kind in ("if", "ifdef", "ifndef"):
                guards.append([fragment])
            elif kind in ("elif", "else"):
                if guards:
                    guards[-1] = [guards[-1][0], fragment]
                else:
                    guard_unknown = True
            elif guards:
                guards.pop()
            else:
                guard_unknown = True
        if number in selected:
            context = [value for level in guards for value in level]
            contexts[number] = (context[-16:], guard_unknown or len(context) > 16)
    if guards:
        guard_unknown = True
    fragments = []
    for number in selected:
        start, end = max(1, number - 6), min(len(lines), number + 20)
        context, incomplete = contexts[number]
        fragments.append({"matchLine": number,
                          "mentions": [{"symbol": symbol, "kind": "code-token" if symbol in mentioned_symbols(code[number - 1]) else "comment-or-string-only"}
                                       for symbol in SYMBOLS if number in matches[symbol]],
                          "lines": [{"line": offset, "text": lines[offset - 1][:512]} for offset in range(start, end + 1)],
                          "fragmentTruncated": any(len(lines[offset - 1]) > 512 for offset in range(start, end + 1)),
                          "preprocessorContext": context,
                          "preprocessorContextUnknown": incomplete or guard_unknown})
    return {"state": "reference-fragments-only", "encoding": encoding,
            "symbols": [{"symbol": symbol, "matchingLineCount": len(matches[symbol]),
                         "state": "observed-reference-only" if matches[symbol] else "unknown-not-observed",
                         "definitionState": "unknown",
                         "matchingLinesOmitted": max(0, len(matches[symbol]) - 4)} for symbol in SYMBOLS],
            "fragments": fragments, "fragmentsOmittedForReportBound": 0,
            "lexicalInterpretation": "heuristic-only", "preprocessorInterpretation": "not-evaluated"}


def header_record(version_root, relative):
    if relative not in HEADERS:
        raise SourceError("header-not-allowlisted")
    record = {"header": relative, "state": "unknown", "reason": None,
              "sha256": None, "byteCount": None, "fragments": []}
    try:
        file = version_root.joinpath(*relative.split("/"))
        ancestry(file.parent)
        before = ordinary(file, False)
        if before.st_size > MAX_HEADER_BYTES:
            raise SourceError("header-size-limit")
        with file.open("rb") as stream:
            opened = os.fstat(stream.fileno())
            if not stat.S_ISREG(opened.st_mode) or (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino):
                raise SourceError("source-changed")
            data = stream.read(MAX_HEADER_BYTES + 1)
            after = os.fstat(stream.fileno())
        ancestry(file.parent)
        final = ordinary(file, False)
        identity = lambda info: (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)
        if identity(before) != identity(after) or identity(before) != identity(final) or len(data) != before.st_size:
            raise SourceError("source-changed")
        record.update({"sha256": hashlib.sha256(data).hexdigest(), "byteCount": len(data)})
        record.update(parse_header(data))
    except SourceError as error:
        record["reason"] = str(error)
    except (OSError, ValueError):
        record["reason"] = "source-unreadable"
    return record


def collect(sdk_root):
    ancestry(sdk_root)
    versions = []
    with os.scandir(sdk_root) as entries:
        for index, entry in enumerate(entries):
            if index >= 256:
                raise SourceError("sdk-directory-enumeration-limit")
            if not VERSION.fullmatch(entry.name):
                continue
            parts = tuple(int(part) for part in entry.name.split("."))
            if any(part > 2_147_483_647 for part in parts) or ".".join(map(str, parts)) != entry.name:
                continue
            versions.append((parts, entry.name))
    result = []
    for _, version in sorted(versions, reverse=True)[:4]:
        folder = sdk_root / version
        try:
            ancestry(folder)
            records = [header_record(folder, relative) for relative in HEADERS]
            result.append({"sdkVersion": version, "state": "reference-only", "headers": records})
        except (SourceError, OSError, ValueError):
            result.append({"sdkVersion": version, "state": "unknown-source", "headers": []})
    return result


def encode_report(report):
    for _ in range(8):
        data = json.dumps(report, ensure_ascii=True, indent=2).encode("utf-8")
        if len(data) <= MAX_REPORT_BYTES:
            return data
        report["reportTruncated"] = True
        for version in report["versions"]:
            for header in version["headers"]:
                fragments = header["fragments"]
                keep = len(fragments) // 2
                header["fragmentsOmittedForReportBound"] = header.get("fragmentsOmittedForReportBound", 0) + len(fragments) - keep
                del fragments[keep:]
    raise SourceError("report-size-limit")


def write_report(repository, data):
    if len(data) > MAX_REPORT_BYTES:
        raise SourceError("report-size-limit")
    ancestry(repository)
    parent = repository
    for name in ("artifacts", "sdk-journal-reference"):
        ancestry(parent)
        child = parent / name
        try:
            ordinary(child, True)
        except FileNotFoundError:
            child.mkdir()
        ancestry(child)
        parent = child
    ancestry(parent)
    # Existing regular files and links both refuse; no overwrite or cleanup fallback.
    with (parent / "report.json").open("xb") as stream:
        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
            raise SourceError("wrong-report-kind")
        ancestry(parent)
        stream.write(data)


def main(argv=None):
    if (sys.argv[1:] if argv is None else argv) or sys.platform != "win32":
        print("SDK reference collection unavailable; no source qualification is claimed.")
        return 2
    try:
        repository = Path.cwd()
        ancestry(repository)
        ordinary(repository / "MadClean.sln", False)
        ancestry(repository / "src" / "MadClean.Windows")
        ordinary(repository / "src" / "MadClean.Windows" / "MadClean.Windows.csproj", False)
        report = {"formatVersion": 1, "context": "installed-windows-sdk-journal-reference",
                  "observedUtc": dt.datetime.now(dt.timezone.utc).isoformat(), "state": "unknown",
                  "sourceAuthenticity": "unverified", "abi": "unverified", "osAvailability": "unverified",
                  "namespaceSecurity": "unverified", "nativeQualification": "unverified",
                  "reportTruncated": False, "sourceIssue": None, "versions": []}
        try:
            program_files = os.environ.get("ProgramFiles(x86)", "")
            base = Path(program_files)
            if not program_files or not base.is_absolute() or not re.fullmatch(r"[A-Za-z]:", base.drive):
                raise SourceError("sdk-root-unavailable")
            report["versions"] = collect(base / "Windows Kits" / "10" / "Include")
            if report["versions"]:
                report["state"] = "reference-only"
        except SourceError as error:
            report["sourceIssue"] = str(error)
        except (OSError, ValueError):
            report["sourceIssue"] = "sdk-root-unreadable"
        write_report(repository, encode_report(report))
    except (SourceError, OSError, ValueError, UnicodeError):
        print("SDK reference collection failed; no source qualification is claimed.")
        return 1
    print("SDK reference evidence written; ABI, availability, security and native qualification remain unverified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
