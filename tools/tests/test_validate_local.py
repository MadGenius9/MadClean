import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("validate_local", Path(__file__).parents[1] / "validate_local.py")
local = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(local)


class LocalValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = local.reserve_output(self.root)
        self.calls = []

    def runner(self, command, **kwargs):
        self.calls.append((command, kwargs))
        return subprocess.CompletedProcess(command, 0, stdout="10.0.100\n")

    def test_commands_are_fixed_argument_arrays_and_report_contains_only_summary(self):
        report = local.validate(self.root, self.output, "fixed sdk", runner=self.runner)
        self.assertEqual(report["outcome"], "commands_succeeded")
        self.assertEqual(len(self.calls), 5)
        for command, kwargs in self.calls:
            self.assertIsInstance(command, list)
            self.assertIs(kwargs["shell"], False)
            self.assertIs(kwargs["check"], False)
        self.assertEqual(self.calls[1][0], ["fixed sdk", "restore", "MadClean.sln", "--locked-mode"])
        self.assertEqual(self.calls[2][0], ["fixed sdk", "build", "MadClean.sln", "-c", "Release", "--no-restore", "-m:1", "--disable-build-servers"])
        self.assertIn("--no-build", self.calls[3][0])
        self.assertIn("--no-restore", self.calls[3][0])
        self.assertEqual(self.calls[4][0][1:], ["-m", "unittest", "discover", "-s", "tools/tests"])
        serialized = (self.output / "report.json").read_text()
        self.assertNotIn(str(self.root), serialized)
        self.assertNotIn("fixed sdk", serialized)
        self.assertEqual(json.loads(serialized), report)
        for key in ("windows_runtime", "windows_providers", "ui", "maintenance"):
            self.assertEqual(report[key], "unverified")

    def test_each_failed_stage_stops_later_commands(self):
        for failed_index in range(5):
            with self.subTest(stage=failed_index):
                output = local.reserve_output(self.root)
                calls = []

                def fail(command, **kwargs):
                    calls.append(command)
                    return subprocess.CompletedProcess(command, 7 if len(calls) == failed_index + 1 else 0,
                                                       stdout="10.0.100")

                report = local.validate(self.root, output, runner=fail)
                self.assertEqual(len(calls), failed_index + 1)
                self.assertEqual(report["outcome"], "failed")
                self.assertEqual(report["stages"][failed_index]["exit_code"], 7)
                self.assertTrue(all(s["status"] == "not_run" for s in report["stages"][failed_index + 1:]))
                self.assertEqual(report["windows_runtime"], "unverified")

    def test_missing_sdk_records_unavailable_without_later_commands(self):
        def missing(command, **kwargs):
            self.calls.append(command)
            raise FileNotFoundError("secret provider path")

        report = local.validate(self.root, self.output, runner=missing)
        self.assertEqual(len(self.calls), 1)
        self.assertEqual(report["stages"][0]["status"], "unavailable")
        self.assertEqual(report["outcome"], "failed")
        self.assertNotIn("secret", (self.output / "report.json").read_text())

    def test_no_restore_skips_only_explicit_restore(self):
        report = local.validate(self.root, self.output, no_restore=True, runner=self.runner)
        self.assertEqual(len(self.calls), 4)
        self.assertEqual(report["stages"][1]["status"], "skipped")
        self.assertFalse(report["restore_requested"])
        self.assertNotIn("restore", [command[1] for command, _ in self.calls])
        self.assertIn("--no-restore", self.calls[1][0])
        self.assertIn("--no-restore", self.calls[2][0])

    def test_malformed_sdk_response_is_not_success_or_retained_text(self):
        def malformed(command, **kwargs):
            return subprocess.CompletedProcess(command, 0, stdout="credential-shaped arbitrary provider text")

        report = local.validate(self.root, self.output, runner=malformed)
        self.assertEqual(report["outcome"], "failed")
        self.assertIsNone(report["sdk"])
        self.assertEqual(report["stages"][0]["status"], "invalid_response")
        self.assertNotIn("credential-shaped", (self.output / "report.json").read_text())

    def test_existing_output_is_rejected_without_overwrite(self):
        identifier = "a" * 32
        output = local.reserve_output(self.root, identifier)
        marker = output / "report.json"
        marker.write_text("preserve")
        with self.assertRaises(FileExistsError):
            local.reserve_output(self.root, identifier)
        self.assertEqual(marker.read_text(), "preserve")

    def test_report_file_is_exclusive(self):
        (self.output / "report.json").write_text("preserve")
        with self.assertRaises(ValueError):
            local.validate(self.root, self.output, runner=self.runner)
        self.assertEqual(self.calls, [])
        self.assertEqual((self.output / "report.json").read_text(), "preserve")

    def test_symlink_output_ancestor_and_symlink_root_are_rejected(self):
        target = self.root / "target"
        target.mkdir()
        linked_root = self.root / "linked"
        try:
            linked_root.symlink_to(target, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest("Directory symlink creation unavailable")
        with self.assertRaises(ValueError):
            local.reserve_output(linked_root)
        other = self.root / "other"
        other.mkdir()
        (other / "artifacts").symlink_to(target, target_is_directory=True)
        with self.assertRaises(ValueError):
            local.reserve_output(other)
        self.assertFalse((target / "local-validation").exists())

    def test_reparse_attribute_is_rejected_even_if_not_a_symlink(self):
        real_lstat = Path.lstat

        def reparse(path):
            if path == self.root:
                class Info:
                    st_mode = real_lstat(path).st_mode
                    st_file_attributes = 0x400
                return Info()
            return real_lstat(path)

        with patch.object(Path, "lstat", reparse):
            with self.assertRaises(ValueError):
                local.check_directory(self.root)

    def test_dangerous_cli_modes_are_not_supported(self):
        for option in ("--publish", "--package", "--github", "--native", "--launch", "--maintenance"):
            with self.subTest(option=option), patch("sys.stderr"), self.assertRaises(SystemExit) as raised:
                local.main([option])
            self.assertEqual(raised.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
