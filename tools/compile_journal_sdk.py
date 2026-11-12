"""Fixed Windows SDK compile-only entry point; accepts no command/program input."""

import os
from pathlib import Path
import subprocess
import sys


def main() -> int:
    if os.name != "nt" or len(sys.argv) != 1:
        print("Journal SDK compile-only validation requires Windows and no arguments.", file=sys.stderr)
        return 1
    system_root = os.environ.get("SystemRoot", "")
    if not system_root or not Path(system_root).is_absolute():
        print("Windows system PowerShell prerequisite is unavailable.", file=sys.stderr)
        return 1
    powershell = Path(system_root) / "System32" / "WindowsPowerShell" / "v1.0" / "powershell.exe"
    script = Path(__file__).resolve().with_name("compile_wer_sdk.ps1")
    if not powershell.is_file() or not script.is_file():
        print("Fixed SDK compile-only prerequisite is missing.", file=sys.stderr)
        return 1
    try:
        result = subprocess.run(
            [str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(script), "-Journal"],
            cwd=script.parent.parent,
            shell=False,
            timeout=120,
            check=False,
        )
        return result.returncode if 0 <= result.returncode <= 255 else 1
    except (OSError, subprocess.TimeoutExpired):
        print("SDK compile-only process did not complete; no executable was requested.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
