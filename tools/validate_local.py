#!/usr/bin/env python3
"""Local compilation/unit validation only. Restore may contact configured NuGet sources."""
import argparse
import json
import os
from pathlib import Path
import platform
import re
import stat
import subprocess
import sys
import uuid

STAGES = ("sdk", "restore", "build", "core_tests", "python_tests")
SDK_VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?")


def check_directory(path):
    """Reject links/reparse points in every existing ancestor, including filesystem root.

    This is a local development fence, not protection against concurrent namespace swaps.
    """
    path = Path(os.path.abspath(path))
    for part in reversed((path, *path.parents)):
        info = part.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked or reparse output ancestry is not permitted.")
        if not stat.S_ISDIR(info.st_mode):
            raise ValueError("Output ancestry must contain directories only.")


def reserve_output(root, run_id=None):
    root = Path(os.path.abspath(root))
    check_directory(root)
    current = root
    for name in ("artifacts", "local-validation"):
        current = current / name
        try:
            current.mkdir()
        except FileExistsError:
            pass
        check_directory(current)
    # No caller-specified directory is accepted by the CLI; run_id supports isolated tests.
    name = run_id if run_id is not None else uuid.uuid4().hex
    if not re.fullmatch(r"[0-9a-f]{32}", name):
        raise ValueError("Invalid local run identifier.")
    current = current / name
    current.mkdir()  # Exclusive: never reuse or overwrite a prior run.
    check_directory(current)
    return current


def validate(root, output, dotnet="dotnet", no_restore=False, runner=subprocess.run):
    root, output = Path(root), Path(output)
    check_directory(output)
    if any(output.iterdir()):
        raise ValueError("Validation output must be fresh and empty.")
    report = {
        "schema_version": 1,
        "context": "local-build-and-unit-tests-only",
        "platform": platform.system(),
        "sdk": None,
        "restore_requested": not no_restore,
        "stages": [{"stage": name, "status": "not_run", "exit_code": None} for name in STAGES],
        "outcome": "unknown",
        "windows_runtime": "unverified",
        "windows_providers": "unverified",
        "ui": "unverified",
        "maintenance": "unverified",
    }
    commands = [
        [dotnet, "--version"],
        [dotnet, "restore", "MadClean.sln", "--locked-mode"],
        [dotnet, "build", "MadClean.sln", "-c", "Release", "--no-restore", "-m:1", "--disable-build-servers"],
        [dotnet, "test", "tests/MadClean.Tests", "-c", "Release", "--no-build", "--no-restore",
         "--logger", "trx;LogFileName=core-local.trx", "--results-directory", str(output)],
        [sys.executable, "-m", "unittest", "discover", "-s", "tools/tests"],
    ]
    for index, command in enumerate(commands):
        stage = report["stages"][index]
        if index == 1 and no_restore:
            stage["status"] = "skipped"
            continue
        print("Local validation: " + stage["stage"], flush=True)
        try:
            result = runner(command, cwd=str(root), shell=False, check=False,
                            **({"capture_output": True, "text": True} if index == 0 else {}))
        except OSError:
            stage["status"] = "unavailable"
            report["outcome"] = "failed"
            break
        stage["exit_code"] = result.returncode
        if result.returncode != 0:
            stage["status"] = "failed"
            report["outcome"] = "failed"
            break
        if index == 0:
            version = result.stdout.strip()
            if len(version) > 128 or not SDK_VERSION.fullmatch(version):
                stage["status"] = "invalid_response"
                report["outcome"] = "failed"
                break
            report["sdk"] = version
        # This records process outcomes only; no test-count or Windows qualification claim.
        stage["status"] = "process_succeeded"
    else:
        report["outcome"] = "commands_succeeded"
    check_directory(output)
    with (output / "report.json").open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet", help="One .NET executable path/name; no command arguments.")
    parser.add_argument("--no-restore", action="store_true", help="Skip script-level restore; builds/tests use --no-restore.")
    args = parser.parse_args(argv)
    if not args.dotnet or "\x00" in args.dotnet:
        parser.error("--dotnet requires one executable.")
    root = Path(os.path.abspath(__file__)).parent.parent
    print("Local-only build/unit validation; restore may contact configured NuGet sources." if not args.no_restore
          else "Local-only build/unit validation; explicit restore skipped.")
    try:
        output = reserve_output(root)
        report = validate(root, output, args.dotnet, args.no_restore)
    except (OSError, ValueError):
        print("Local validation could not safely create or write its fresh output.", file=sys.stderr)
        return 2
    print("Report: " + str(output / "report.json"))
    print("Windows runtime, providers, UI and maintenance remain unverified.")
    return 0 if report["outcome"] == "commands_succeeded" else 1


if __name__ == "__main__":
    raise SystemExit(main())
