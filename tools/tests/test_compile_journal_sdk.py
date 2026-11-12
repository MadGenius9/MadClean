"""Launcher refusal and outcome tests; these do not compile or qualify a Windows ABI."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("journal_sdk_compile", Path(__file__).resolve().parents[1] / "compile_journal_sdk.py")
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)


class JournalCompilerLauncherTests(unittest.TestCase):
    def call(self, platform, arguments, system_root):
        with patch.object(c, "os", SimpleNamespace(name=platform, environ={"SystemRoot": system_root})), \
             patch.object(c.sys, "argv", arguments), contextlib.redirect_stderr(io.StringIO()):
            return c.main()

    def test_other_platform_or_extra_arguments_never_start_a_process(self):
        with patch.object(c.subprocess, "run") as run:
            self.assertEqual(1, self.call("posix", ["tool"], "/unused"))
            self.assertEqual(1, self.call("nt", ["tool", "user-command"], "/unused"))
            run.assert_not_called()

    def test_missing_or_relative_system_root_never_searches_path_for_powershell(self):
        with patch.object(c.subprocess, "run") as run:
            for root in ("", "relative-system-root"):
                self.assertEqual(1, self.call("nt", ["tool"], root))
            run.assert_not_called()

    def test_missing_fixed_executable_does_not_use_a_path_fallback(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(c.subprocess, "run") as run:
            self.assertEqual(1, self.call("nt", ["tool"], directory))
            run.assert_not_called()

    def test_exact_compiler_mode_and_exit_or_launch_failure_remain_observable(self):
        with tempfile.TemporaryDirectory() as directory:
            powershell = Path(directory) / "System32/WindowsPowerShell/v1.0/powershell.exe"
            powershell.parent.mkdir(parents=True)
            powershell.touch()  # Owned inert fixture only; never executed.
            script = Path(c.__file__).with_name("compile_wer_sdk.ps1").resolve()
            for code in (0, 1, 9, -1, 256):
                with patch.object(c.subprocess, "run", return_value=SimpleNamespace(returncode=code)) as run:
                    self.assertEqual(code if 0 <= code <= 255 else 1, self.call("nt", ["tool"], directory))
                    run.assert_called_once_with(
                        [str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(script), "-Journal"],
                        cwd=script.parent.parent, shell=False, timeout=120, check=False)
            for error in (OSError("fixture"), subprocess.TimeoutExpired("fixture", 120)):
                with patch.object(c.subprocess, "run", side_effect=error) as run:
                    self.assertEqual(1, self.call("nt", ["tool"], directory))
                    self.assertEqual(1, run.call_count)  # No retry after an uncertain process outcome.
